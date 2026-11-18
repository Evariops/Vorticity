using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Vorticity.Arrays;
using Vorticity.Execution;
using Vorticity.Types.Variants;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>A merge's blocks sent through several at once, each column's work on them in the order of the blocks.</summary>
public sealed partial class VortexFileWriter
{
    /// <summary>
    /// L, the blocks a merge's writer sends through ahead of the one whose landing its size is read at: a merge reads the
    /// size the writer had when the block L before the one it fills landed, with the rows staged since at its rate then
    /// (G1 and G3 of the parallel flows). The same at any degree, so that a merge's objects end at the same rows at any
    /// degree: the degree changes only how many of those blocks go through at once. Four, so that the columns quick to
    /// ingest run up to three blocks ahead of the slowest while it takes one.
    /// </summary>
    internal const int FlightsAhead = 4;

    /// <summary>
    /// The blocks an object is aimed at, at least, for its writer to read its size <see cref="FlightsAhead"/> blocks back:
    /// four blocks of lag in thirty-two. An object aimed at fewer reads it one block back, as before the line, and sends its
    /// blocks through one at a time (G1 of the parallel flows).
    /// </summary>
    internal const int LineObjectBlocks = 32;

    /// <summary>
    /// The blocks back the size a merge reads is the writer's, as that block landed: <see cref="FlightsAhead"/> for an object
    /// aimed at many blocks, which sends that many through at once, 1 otherwise. Set before the first row, the same at any
    /// degree, so that a merge's objects end at the same rows at any degree.
    /// </summary>
    internal int LandLag { get; set; } = 1;

    /// <summary>The ring of the flights not landed and of what was recorded as each landed: the flights FlightsAhead back and since.</summary>
    private const int LandRing = FlightsAhead + 2;

    // What the writer's size read as each flight landed, and at what rate, by flight; the bytes each flight staged; the
    // flights flown; the first flight since the size was last read as it stood, and what it read then.
    private Landing[] _lands = [];
    private long[] _flownBytes = [];
    private long _flown;
    private long _landBase;
    private Landing _landStart;

    /// <summary>
    /// What the writer's size read as a flight landed, in the parts <see cref="Held"/> sums: the sink's position, what the
    /// columns hold at their rates and the chunks compressed ahead, the bytes in transit, and the rate the rows not yet
    /// written count at; summed with the rows staged since as <see cref="Held"/> sums the staged ones, to the byte.
    /// </summary>
    private readonly record struct Landing(long Sink, double Fixed, long Pending, double Rate);

    /// <summary>What the writer's size reads now, the rows staged left out.</summary>
    private Landing Landed()
    {
        long canonical = 0;
        double encoded = 0;
        double rates = 0;
        int columns = 0;
        if (_gathering is { } gathering)
        {
            foreach (Gathering? column in gathering)
            {
                if (column is not null)
                {
                    canonical += column.Bytes;
                    encoded += column.Bytes * column.Rate;
                    rates += column.Rate;
                    columns++;
                }
            }
        }

        double rate = canonical > 0 ? encoded / canonical : columns > 0 ? rates / columns : Gathering.FirstRate;
        return new Landing(_sink.Position, encoded + _emittingBytes, _pendingBytes, rate);
    }

    // The line the flights go through when the writer sends a merge's blocks several at once; null otherwise.
    private FlightLine? _line;

    // The stagings the flights landed left, for the blocks staged next; made with the first one left.
    private Stack<Staging>? _spareStagings;

    /// <summary>The blocks that went through the line beside others, each column's work on them in order.</summary>
    internal int BlocksOnLine { get; private set; }

    /// <summary>The blocks a writer that sends its blocks through its line sent alone, as every block went before the line.</summary>
    internal int BlocksAlone { get; private set; }

    /// <summary>The chunks of the file the line's blocks ended, each emitted once every block of it landed and before the next one ingested.</summary>
    internal int ChunksEndedOnLine { get; private set; }

    /// <summary>Records what the writer's size reads once flight <paramref name="flight"/> landed: its rows in transit or emitted.</summary>
    private void RecordLand(long flight) => _lands[flight % LandRing] = Landed();

    /// <summary>
    /// Sets the size a merge reads while flight <paramref name="flight"/> is the last flown: what the writer read as the
    /// flight <see cref="FlightsAhead"/> before it landed, or as the flights began, with the bytes staged by every flight
    /// since at the rate read then.
    /// </summary>
    private void SizeAtFlight(long flight)
    {
        long from = flight - LandLag;
        Landing land = from < _landBase ? _landStart : _lands[from % LandRing];
        long staged = 0;
        for (long i = Math.Max(from + 1, _landBase); i <= flight; i++)
        {
            staged += _flownBytes[i % LandRing];
        }

        _sizedBytes = land.Sink + (long)(land.Fixed + ((land.Pending + staged) * land.Rate));
        _sizedRate = land.Rate;
        _sized = true;
    }

    /// <summary>
    /// The number of the flight of the block staged now, which stages <paramref name="bytes"/>: a size read as it stood, every
    /// flight before landed, begins the flights anew from what it reads.
    /// </summary>
    private long NextFlight(long bytes)
    {
        if (_lands.Length == 0)
        {
            // Made with the writer's first flight: a writer that stages nothing makes none.
            _lands = new Landing[LandRing];
            _flownBytes = new long[LandRing];
        }

        if (!_sized)
        {
            _landBase = _flown;
            _landStart = Landed();
        }

        long flight = _flown++;
        _flownBytes[flight % LandRing] = bytes;
        return flight;
    }

    /// <summary>A staging for the block staged next: one a flight landed left, or a new one.</summary>
    private Staging SpareStaging()
    {
        if (Volatile.Read(ref _spareStagings) is { } spare)
        {
            lock (spare)
            {
                if (spare.TryPop(out Staging? staging))
                {
                    return staging;
                }
            }
        }

        return new Staging(TransitContexts.Rent(Account));
    }

    /// <summary>Keeps <paramref name="staging"/>, its rows let go, for a block staged later.</summary>
    private void SpareStaging(Staging staging)
    {
        staging.Flight = null;
        staging.Ready = false;
        Stack<Staging> spare = LazyInitializer.EnsureInitialized(ref _spareStagings);
        lock (spare)
        {
            spare.Push(staging);
        }
    }

    /// <summary>
    /// Whether the writer sends a merge's blocks through its line, several at once: a writer that stages blocks exactly, on
    /// more than one lane, of a table of several columns cut into chunks by their bytes. A block the line does not take
    /// goes through alone (<see cref="LineTakes"/>).
    /// </summary>
    /// <remarks>
    /// A writer that takes its caller's rows a block at a time makes its line from its <see cref="FlightsAhead"/>-th block on,
    /// its first ones going through one at a time: one of a few blocks, an append's object of a few, makes none of its state,
    /// some 11 KB (its flights, their stagings, the states of its items). A merge's writer, whose lag its merge chose for the
    /// object it aims at, makes it at its first block.
    /// </remarks>
    private bool UsesLine =>
        LandLag > 1 && _lanes > 1 && _rowBlock > 0 && _stagedRows == _rowBlock && _blockRows == _rowBlock && _automaticChunks && _isTabular
        && _fieldCount > 1 && _rowBlock < 2 * CensusSliceRows && (_line is not null || !_blocksExactly || _flown >= FlightsAhead)
        && ColumnCompressor.Differential.Value is null && ArrayBlobWriter.ElementsHanded.Value is null;

    /// <summary>The line, made with the writer's first flight.</summary>
    private FlightLine Line => _line ??= new FlightLine(this, _core);

    // ------------------------------------------------------------------------------------------------- the line's steps

    // The rows in transit the flights prologued count on; the counted rows of the last flight on the line whose wide columns'
    // distinct values no flight after it attached yet, and its rows.
    private long _planPending;
    private CountedRows? _attachDue;
    private int _attachDueRows;

    // The counted rows the flights landed left, for the next ones; made with the line.
    private Stack<CountedRows>? _spareCounted;

    // By item, the states it touches, made once; by column, the wide columns its counts wait for, made once; each group's
    // first state by column. Made with the line.
    private Dictionary<int, int[]>? _ownStates;
    private int[][]? _wideAfter;
    private int[]? _groupBase;
    private int _stateCount;

    /// <summary>An item that reads a variant column's keys into the flight's pass, its field in the low twenty bits.</summary>
    private const int ReadItem = 1 << 28;

    /// <summary>An item that feeds the builders of a variant column's nested paths, its field in the low twenty bits.</summary>
    private const int NestedItem = 1 << 27;

    /// <summary>The item that counts the flight's paths into each variant column's census.</summary>
    private const int CensusItem = 1 << 26;

    /// <summary>An item that takes the flight's rows of a shredded column along its chain (<see cref="ShredChain"/>), its field in the low twenty bits.</summary>
    private const int ShredItem = 1 << 25;

    /// <summary>
    /// An item that cuts a column's chunk, its field in the low twenty bits, in a flight cut column by column
    /// (<see cref="Flight.Cuts"/>). Below the bits of a group of keys, which a keys item sets: told apart after it.
    /// </summary>
    private const int CloseItem = 1 << 23;

    /// <summary>The item that decides which column writes its chunk early in a flight cut after one cut too (<see cref="Over"/>); told apart after a keys item.</summary>
    private const int OverItem = 1 << 22;

    /// <summary>
    /// Whether the line takes the block of <paramref name="rows"/> rows assembled at node <paramref name="node"/> of
    /// <paramref name="arena"/> beside others, <paramref name="pending"/> rows in transit before it: a whole block from a
    /// block's start, the file's first block posed, whose rows go into transit without crossing a chunk of the file; and
    /// whether they end a chunk (<paramref name="endsChunk"/>). A block that starts a chunk past the rows in transit or
    /// crosses one goes through alone, as the write of a batch cuts it (<see cref="WriteInChunksAsync"/>).
    /// </summary>
    /// <remarks>
    /// A block that makes a whole chunk, its rows the chunk's to the last, ends it on the line as a block that completes one
    /// does: it went through alone, and a consolidation of metric records, each of whose blocks makes a chunk, ingested its
    /// 6 328 blocks one at a time on its flow, 7.45 s of its 9.6 s of writing at degree 14, behind 9.2 s at four lanes and
    /// 11.6 at fourteen; on the line, 9.2 s at fourteen, the same bytes (2026-11-18).
    /// </remarks>
    private bool LineTakes(CanonicalArena arena, int node, int rows, long pending, out bool endsChunk)
    {
        endsChunk = false;
        if (rows != _blockRows || _blockFilled != 0 || Fan is null || !(_indexes is null or { AsksNothing: true } || _indexes.FeedsAcross))
        {
            return false;
        }

        // The chunk the write of the block alone would cut (WriteDecodedAsync), under columns that gather too.
        (long widest, long total) = ColumnBytes(arena, node);
        long chunk = AutomaticChunkRows(widest, total, rows);
        if (_gatheredBytes > 0)
        {
            chunk = Math.Min(chunk, GatheredChunkRows(arena, node, rows));
        }

        if (pending >= chunk || rows > chunk - pending)
        {
            return false;
        }

        endsChunk = pending + rows >= chunk;
        return true;
    }

    /// <summary>
    /// The prologue of <paramref name="flight"/>, its block assembled, in the order of the flights: whether the line takes
    /// it, and then what its items read made ready, its measures, its census's paths and its keys' passes, and its items laid
    /// out with the states each touches. False for a block that goes through alone.
    /// </summary>
    /// <param name="flight">The flight.</param>
    /// <param name="first">Whether every flight before it landed, so that the rows in transit are those the writer holds.</param>
    private bool Prologue(Flight flight, bool first)
    {
        Staging staging = flight.Staging!;
        CanonicalArena arena = staging.Context.Canonical;
        int node = staging.Assembled;
        int rows = staging.Rows;
        long pending = first ? _pendingRows : _planPending;
        if (first)
        {
            // Every flight before landed: the chunks emitted are those the writer counted.
            _planBlocks = _emittedBlocks;
            _planRows = _rowCount;
            _planGroupTaken = _groupTaken;
        }

        // The flight before cut column by column, which its prologue let this one start before it landed: what the columns
        // hold now is the lanes' to change, and this flight decides only from what is its own and what the cuts recorded.
        bool afterCut = _lastCut;
        _lastCut = false;
        flight.Gathers = false;
        flight.Cuts = false;
        flight.AfterCut = false;
        flight.OverField = -1;
        if (node < 0 || !LineTakes(arena, node, rows, pending, out bool endsChunk))
        {
            return false;
        }

        flight.Node = node;
        flight.EndsChunk = endsChunk;
        _planPending = endsChunk ? 0 : pending + rows;

        // A block that makes a whole chunk alone copies its columns in with what each gathers in its items, the column that
        // writes early chosen first, as the emission chooses it before it gathers: no rows wait in transit, so every flight
        // before it landed, and its chunk's emission with them, or was cut column by column. After a cut, no column can have
        // gathered past int.MaxValue rows, the cut being taken only while the object's blocks could not hold that many
        // (CutsAt), and a block that would end its chunk without being cut goes into transit, its chunk gathered in its
        // emission once every flight before landed.
        flight.Gathers = endsChunk && pending == 0 && (afterCut ? _gathering is not null && EmitsApart : GathersOnLine(rows));
        // Cut column by column only while the job's memory has room for what cuts keep beside the next flights: the chunks cut
        // and not yet emitted, their owner behind, and the columns the flights after them gather meanwhile, up to a gathering
        // ceiling past two blocks. Cut on every block, a consolidation of 74 appends under 2 GiB held 400 MiB in its writer
        // and was refused every time, with or without a room of two blocks asked (2026-11-18). Cut or not, the bytes are the
        // same.
        flight.Cuts = flight.Gathers && CutsAt(flight.Index, rows)
            && (_admitsAhead is null || _admitsAhead((2 * staging.Bytes) + _gatheredCeiling));
        flight.AfterCut = afterCut;
        if (flight.Gathers)
        {
            // Where its chunk starts, in the order of the flights: after a cut, the owner counts the chunks before as it goes.
            flight.ChunkFirstBlock = _planBlocks;
            flight.ChunkFirstRow = _planRows;
        }

        if (flight.Cuts)
        {
            MakeCutRecords();
            flight.SizeCuts(_fieldCount);
            _planBlocks += ChunkBlocks(rows, _blockRows);
            _planRows += rows;
            _planGroupTaken += rows;
            _lastCut = true;
        }

        // The column that writes early: after a cut, decided once that cut ran (OverItem); otherwise now, every flight before
        // landed.
        if (flight.Gathers && !afterCut)
        {
            Over();
            flight.OverField = _over is { } over ? Array.IndexOf(_gathering!, over) : -1;
        }

        CanonicalNode root = arena.GetNode(node);
        Span<int> nodes = stackalloc int[_fieldCount];
        for (int field = 0; field < _fieldCount; field++)
        {
            nodes[field] = root.GetFieldIndex(field);
        }

        CountedRows? counted = null;
        if (_indexes is { KeepsCounts: true })
        {
            Stack<CountedRows> spares = LazyInitializer.EnsureInitialized(ref _spareCounted);
            lock (spares)
            {
                counted = spares.TryPop(out CountedRows? spare) ? spare : new CountedRows();
            }
        }

        flight.Take(arena, nodes, rows, filled: 0, counted);
        _indexes?.PrepareCounts(counted, arena, flight.FieldNodes, rows);
        if (afterCut)
        {
            PlanShredsAfterCut(flight);
        }
        else
        {
            PlanShreds(flight, arena, rows, pending);
        }

        // The wide columns' distinct values of the flight before, which this flight's counts attach first: none when a chunk's
        // emission or a block alone attached them since. A flight cut column by column has its own attached by its owner as
        // it emits each column, as a chunk's emission attaches the last flight's, and hands none to the next.
        flight.AttachFrom = _attachDue;
        flight.AttachRows = _attachDueRows;
        flight.OwnCounted = flight.Cuts ? counted : null;
        _attachDue = flight.Cuts ? null : counted;
        _attachDueRows = rows;

        if (_census is { } census)
        {
            PathCensus.Counting[] ready = new PathCensus.Counting[census.Length];
            for (int field = 0; field < census.Length; field++)
            {
                if (census[field] is not null)
                {
                    ready[field] = PathCensus.Prepare(arena, flight.FieldNodes[field], rows);
                }
            }

            flight.Census = ready;
        }

        LayItems(flight);
        return true;
    }

    /// <summary>
    /// Plans the take of <paramref name="flight"/>'s <paramref name="rows"/> rows along the chain of each shredded column whose
    /// chunks are compressed ahead: at their place in the column's chunk, its rows gathered so far and the
    /// <paramref name="pending"/> rows in transit before them, made ready for its item to take them.
    /// </summary>
    /// <remarks>
    /// In the order of the flights, after every chunk of the file before this one was emitted: the line runs the prologue of
    /// a flight after one that ends a chunk once that one landed, so the rows a column gathered, and the chunk they are of,
    /// are those its block's rows follow.
    /// </remarks>
    private void PlanShreds(Flight flight, CanonicalArena arena, int rows, long pending)
    {
        if (Chains() is not { } chains)
        {
            return;
        }

        if (flight.ShredAt.Length != _fieldCount)
        {
            flight.ShredAt = new int[_fieldCount];
            flight.Shred = new VariantShredder.Shredding.Rows[_fieldCount];
        }

        for (int field = 0; field < _fieldCount; field++)
        {
            flight.ShredAt[field] = -1;
            if (chains[field] is { } chain && _gathering![field] is { } gathering)
            {
                long position = gathering.Rows + pending;
                if (chain.Plan(gathering.Generation, position, rows))
                {
                    flight.Shred[field] = VariantShredder.Shredding.Prepare(arena, flight.FieldNodes[field]);
                    flight.ShredAt[field] = checked((int)position);
                }
            }
        }
    }

    /// <summary>
    /// Marks which of <paramref name="flight"/>'s shredded columns take its rows along their chains, after a flight cut column
    /// by column: each column with a chain when the flight is cut too, placed by its item from that flight's cut
    /// (<see cref="TakeOnLine"/>), none otherwise, the chain then broken and the chunk shredded as it is emitted.
    /// </summary>
    private void PlanShredsAfterCut(Flight flight)
    {
        if (Chains() is not { } chains)
        {
            return;
        }

        if (flight.ShredAt.Length != _fieldCount)
        {
            flight.ShredAt = new int[_fieldCount];
            flight.Shred = new VariantShredder.Shredding.Rows[_fieldCount];
        }

        for (int field = 0; field < _fieldCount; field++)
        {
            flight.ShredAt[field] = -1;
            if (flight.Gathers && chains[field] is not null && _gathering?[field] is not null)
            {
                // Made ready here, in the order of the flights: it materializes the column's children in the block's arena,
                // which the flight's other items read, its gathering among them.
                flight.Shred[field] = VariantShredder.Shredding.Prepare(flight.Arena, flight.FieldNodes[field]);
                flight.ShredAt[field] = PlacedByCut;
            }
        }
    }

    /// <summary>A take placed by its item from the cut of the flight before (<see cref="TakeOnLine"/>), in <see cref="Flight.ShredAt"/>.</summary>
    private const int PlacedByCut = -2;

    /// <summary>
    /// Takes the rows of <paramref name="staging"/>, a block that goes through alone, along each shredded column's chain, at
    /// their place in the column's chunk after the rows in transit: on the owner, every flight before it landed.
    /// </summary>
    private void TakeAlone(Staging staging)
    {
        if (Chains() is not { } chains || staging.Assembled < 0)
        {
            return;
        }

        CanonicalArena arena = staging.Context.Canonical;
        CanonicalNode root = arena.GetNode(staging.Assembled);
        for (int field = 0; field < _fieldCount; field++)
        {
            if (chains[field] is { } chain && _gathering![field] is { } gathering)
            {
                long position = gathering.Rows + _pendingRows;
                if (chain.Plan(gathering.Generation, position, staging.Rows))
                {
                    VariantShredder.Shredding.Rows taken = VariantShredder.Shredding.Prepare(arena, root.GetFieldIndex(field));
                    chain.Shredding.TakeMore(in taken, checked((int)position));
                }
            }
        }
    }

    // The numbers of the states the items touch: four for each column (its statistics, its bounds, its distinct table, its
    // builders), then the composite keys, one no item touches, each variant column's nested paths, each group of its keys,
    // each shredded column's chain, each column's gathering, and each variant column's census.
    private int StateBuilders(int field) => (field * 4) + 3;

    private int StateShred(int field) => (_fieldCount * 5) + 2 + _groupTotal + field;

    private int StateGather(int field) => (_fieldCount * 6) + 2 + _groupTotal + field;

    private int StateComposite => _fieldCount * 4;

    private int StateCensus(int field) => (_fieldCount * 7) + 2 + _groupTotal + field;

    /// <summary>A column's cut on the line (<see cref="CloseItem"/>), which the flight after waits for before it decides its own.</summary>
    private int StateCut(int field) => (_fieldCount * 8) + 2 + _groupTotal + field;

    /// <summary>
    /// A column's chunk emitted by the owner as its flight was cut (<see cref="EmitCutColumnsAsync"/>), posed by the owner
    /// alone, for every flight in its order, never by an epilogue: the next flight's ingest and builders of the column wait
    /// for it, since a landing hands the column what its emission decided, and the next cut, for the rate it reads.
    /// </summary>
    private int StateOwned(int field) => (_fieldCount * 9) + 2 + _groupTotal + field;

    /// <summary>Whether <paramref name="state"/> is a column's <see cref="StateOwned"/>, which only the owner poses.</summary>
    private bool IsOwnedState(int state) => state >= (_fieldCount * 9) + 2 + _groupTotal;

    private int StateNested(int field) => (_fieldCount * 4) + 2 + field;

    private int StateGroup(int field, int group) => (_fieldCount * 5) + 2 + _groupBase![field] + group;

    /// <summary>The states the line numbers, the groups of keys numbered first.</summary>
    private int StateCount()
    {
        if (_groupBase is null)
        {
            _groupBase = new int[_fieldCount];
            int groups = 0;
            for (int field = 0; field < _fieldCount; field++)
            {
                _groupBase[field] = groups;
                groups += _indexes?.KeyGroups(field) ?? 0;
            }

            _groupTotal = groups;
            _stateCount = (_fieldCount * 10) + 2 + groups;
        }

        return _stateCount;
    }

    // The groups of keys every column's numbered together.
    private int _groupTotal;

    /// <summary>The states item <paramref name="entry"/> touches, made once for the writer.</summary>
    private int[] OwnStates(int entry)
    {
        _ownStates ??= [];
        if (_ownStates.TryGetValue(entry, out int[]? own))
        {
            return own;
        }

        if ((entry & ReadItem) != 0)
        {
            own = [];
        }
        else if ((entry & NestedItem) != 0)
        {
            own = [StateNested(entry & 0xF_FFFF)];
        }
        else if ((entry & CensusItem) != 0)
        {
            own = [StateCensus(entry & 0xF_FFFF)];
        }
        else if ((entry & KeysItem) != 0)
        {
            own = [StateGroup(entry & 0xF_FFFF, ((entry >> 20) & 0x3FF) - 1)];
        }
        else if ((entry & GatherItem) != 0)
        {
            own = [StateGather(entry & 0xF_FFFF)];
        }
        else if ((entry & ShredItem) != 0)
        {
            own = [StateShred(entry & 0xF_FFFF)];
        }
        else if ((entry & CloseItem) != 0)
        {
            // What a column's cut reads and leaves: its ingest's summaries (Freeze), its gathering, its chain, and its cut.
            int field = entry & 0xF_FFFF;
            own = [field * 4, (field * 4) + 1, (field * 4) + 2, StateGather(field), StateShred(field), StateCut(field)];
        }
        else if ((entry & OverItem) != 0)
        {
            own = [];
        }
        else if ((entry & 7) == 0)
        {
            int field = entry >> 3;
            own = [field == _fieldCount ? StateComposite : StateBuilders(field)];
        }
        else
        {
            int field = entry >> 3;
            ColumnWriter.IngestParts parts = (ColumnWriter.IngestParts)(entry & 7);
            own = new int[System.Numerics.BitOperations.PopCount((uint)parts)];
            int at = 0;
            if ((parts & ColumnWriter.IngestParts.Statistics) != 0)
            {
                own[at++] = field * 4;
            }

            if ((parts & ColumnWriter.IngestParts.Bounds) != 0)
            {
                own[at++] = (field * 4) + 1;
            }

            if ((parts & ColumnWriter.IngestParts.Table) != 0)
            {
                own[at++] = (field * 4) + 2;
            }
        }

        _ownStates[entry] = own;
        return own;
    }

    /// <summary>The builders' states of the wide columns column <paramref name="field"/>'s counts attach, itself aside, made once.</summary>
    private int[] WideAfter(int field)
    {
        if (_wideAfter is null)
        {
            List<int> wide = [.. _indexes?.WideFields ?? []];
            _wideAfter = new int[_fieldCount][];
            for (int f = 0; f < _fieldCount; f++)
            {
                List<int> after = [];
                foreach (int w in wide)
                {
                    if (w != f)
                    {
                        after.Add(StateBuilders(w));
                    }
                }

                _wideAfter[f] = [.. after];
            }
        }

        return _wideAfter[field];
    }

    /// <summary>
    /// Lays out <paramref name="flight"/>'s items, the longest first: the shredded columns' takes, the keys' reads, the census,
    /// the columns' gatherings, their accumulators, their builders, then the groups of keys.
    /// </summary>
    /// <remarks>
    /// The core runs a flight's items in the order they are laid out, as lanes come free. Laid out behind the reads, the
    /// census and the gatherings, the take of a consolidation of metric records' widest variant column, 0.79 ms of a block,
    /// started once ten items before it had started, on lanes the emissions of the chunks before also took, and the items of
    /// a block spanned 1.03 ms (2026-11-18).
    /// </remarks>
    private void LayItems(Flight flight)
    {
        StateCount();
        IndexWriter? indexes = _indexes is { AsksNothing: false, Streams: true } streaming ? streaming : null;
        int groups = indexes is null ? 0 : KeysApart(indexes);
        flight.Size((_fieldCount * 11) + groups + 4);
        flight.KeysApart = groups > 0;
        bool attaches = flight.AttachFrom is not null;

        // After a flight cut column by column, the columns' ingest and builders wait for the owner to have emitted their
        // column of it (StateOwned), and the column that writes early is decided once every cut of it ran.
        bool afterCut = flight.AfterCut;
        int over = afterCut && flight.Gathers ? Add(flight, OverItem, CutStates(), -1) : -1;
        List<int>[]? before = flight.Cuts ? CutBefores() : null;

        // Each shredded column's rows taken along its chain, after the flight before's.
        for (int field = 0; field < flight.ShredAt.Length; field++)
        {
            if (flight.ShredAt[field] != -1)
            {
                // Laid first, then noted for the cut: a null-conditional call would skip laying it too.
                int item = before is null ? Add(flight, ShredItem | field, [], -1) : AddBeforeCut(flight, ShredItem | field, false);
                before?[field].Add(item);
            }
        }

        // Each column's copy in with what it gathers, after the flight before's, for a block that makes a whole chunk alone.
        for (int field = 0; flight.Gathers && field < _fieldCount; field++)
        {
            if (_gathering![field] is not null)
            {
                int item = before is null ? Add(flight, GatherItem | field, [], -1) : AddBeforeCut(flight, GatherItem | field, false);
                before?[field].Add(item);
            }
        }

        Span<int> reads = stackalloc int[_fieldCount];
        reads.Fill(-1);
        for (int field = 0; groups > 0 && field < _fieldCount; field++)
        {
            if (indexes!.KeyGroups(field) > 0)
            {
                flight.PassOf(indexes, field);
                reads[field] = Add(flight, ReadItem | field, [], -1);
            }
        }

        // Each variant column's census an item of its own: counted in one, the two variant columns of a metric record took
        // 0.44 ms of a block, the longest of its items once the shredded columns' takes were quicker (2026-11-18).
        for (int field = 0; flight.Census is not null && field < _fieldCount; field++)
        {
            if (_census![field] is not null)
            {
                Add(flight, CensusItem | field, [], -1);
            }
        }

        Span<int> ingest = stackalloc int[(_fieldCount * 3) + 1];
        int count = IngestItems(flight.Arena, flight.FieldNodes, ingest);
        for (int i = 0; i < count; i++)
        {
            int field = ingest[i] >> 3;
            int item = before is null ? Add(flight, ingest[i], afterCut ? OwnedOf(field) : [], -1) : AddBeforeCut(flight, ingest[i], afterCut);
            before?[field].Add(item);
        }

        if (indexes is not null)
        {
            int last = _fieldCount - 1;
            for (int field = 0; field < _fieldCount; field++)
            {
                if (indexes.Feeds(field))
                {
                    int[] after = attaches ? (afterCut ? WideAfterOwned(field) : WideAfter(field)) : afterCut ? OwnedOf(field) : [];
                    int item = Add(flight, field << 3, after, -1);
                    before?[field].Add(item);

                    // A wide column's counts of the flight are what the owner attaches to every column as it emits it: each
                    // column's cut waits for them, as the chunk's emission came once every item of the flight ran.
                    for (int other = 0; before is not null && other < _fieldCount; other++)
                    {
                        if (other != field && IsWide(field))
                        {
                            before[other].Add(item);
                        }
                    }
                }
            }

            // The builders past the columns close once the owner emitted the last column.
            if (indexes.FeedsKeys)
            {
                int item = Add(flight, _fieldCount << 3, afterCut ? OwnedOf(last) : [], -1);
                before?[last].Add(item);
            }

            for (int field = 0; field < _fieldCount; field++)
            {
                if (reads[field] < 0)
                {
                    continue;
                }

                if (indexes.FeedsNested(field))
                {
                    int item = Add(flight, NestedItem | field, afterCut ? OwnedOf(last) : [], reads[field]);
                    before?[field].Add(item);
                    before?[last].Add(item);
                }

                for (int group = 0; group < indexes.KeyGroups(field); group++)
                {
                    int item = Add(flight, KeysItem | ((group + 1) << 20) | field, afterCut ? OwnedOf(last) : [], reads[field]);
                    before?[field].Add(item);
                    before?[last].Add(item);
                }
            }
        }

        // Each column's cut, once the column's items of the flight ran and the owner emitted the column of the flight before.
        for (int field = 0; before is not null && field < _fieldCount; field++)
        {
            int item = Add(flight, CloseItem | field, OwnedOf(field), over);
            flight.Befores[item] = flight.CutWaits(field, before[field]);
        }

        flight.Remaining = flight.Count;
    }

    /// <summary>
    /// Adds item <paramref name="entry"/>, a column's ingest, gathering or take, to <paramref name="flight"/>, cut column by
    /// column: the states it would touch it only waits for, the column's cut touching them after it, so that the next
    /// flight's items of the column wait for the cut and not for the first of them to end; and after a flight cut too, the
    /// column's owned state as well (<paramref name="afterCut"/>).
    /// </summary>
    private int AddBeforeCut(Flight flight, int entry, bool afterCut)
    {
        int index = Add(flight, entry, [], -1);
        flight.Own[index] = [];
        flight.After[index] = WaitsBeforeCut(entry, afterCut);
        return index;
    }

    // By item and whether the flight before was cut, the states an item that comes before a column's cut waits for; made once.
    private Dictionary<(int Entry, bool AfterCut), int[]>? _waitsBeforeCut;

    private int[] WaitsBeforeCut(int entry, bool afterCut)
    {
        _waitsBeforeCut ??= [];
        if (!_waitsBeforeCut.TryGetValue((entry, afterCut), out int[]? waits))
        {
            int[] own = OwnStates(entry);
            int field = (entry & (GatherItem | ShredItem)) != 0 ? entry & 0xF_FFFF : entry >> 3;
            waits = afterCut ? [.. own, StateOwned(field)] : own;
            _waitsBeforeCut[(entry, afterCut)] = waits;
        }

        return waits;
    }

    // By column, the items of a flight cut column by column its cut waits for; made once, emptied for each flight.
    private List<int>[]? _cutBefores;

    // The columns' cut states, which a flight's decision of the column that writes early waits for; and by column, its
    // owned state alone. Made once.
    private int[]? _cutStates;
    private int[][]? _ownedOf;

    private List<int>[] CutBefores()
    {
        if (_cutBefores is null)
        {
            _cutBefores = new List<int>[_fieldCount];
            for (int field = 0; field < _fieldCount; field++)
            {
                _cutBefores[field] = [];
            }
        }

        foreach (List<int> items in _cutBefores)
        {
            items.Clear();
        }

        return _cutBefores;
    }

    private int[] CutStates()
    {
        if (_cutStates is null)
        {
            _cutStates = new int[_fieldCount];
            for (int field = 0; field < _fieldCount; field++)
            {
                _cutStates[field] = StateCut(field);
            }
        }

        return _cutStates;
    }

    // By column, whether its counts are wide (IndexWriter.WideFields), made once.
    private bool[]? _wide;

    private bool IsWide(int field)
    {
        if (_wide is null)
        {
            _wide = new bool[_fieldCount];
            foreach (int wide in _indexes?.WideFields ?? [])
            {
                if (wide < _fieldCount)
                {
                    _wide[wide] = true;
                }
            }
        }

        return _wide[field];
    }

    // By column, the wide columns' builders its counts' attachment waits for and its owned state, made once.
    private int[][]? _wideAfterOwned;

    private int[] WideAfterOwned(int field)
    {
        if (_wideAfterOwned is null)
        {
            _wideAfterOwned = new int[_fieldCount][];
            for (int f = 0; f < _fieldCount; f++)
            {
                _wideAfterOwned[f] = [.. WideAfter(f), StateOwned(f)];
            }
        }

        return _wideAfterOwned[field];
    }

    private int[] OwnedOf(int field)
    {
        if (_ownedOf is null)
        {
            _ownedOf = new int[_fieldCount][];
            for (int f = 0; f < _fieldCount; f++)
            {
                _ownedOf[f] = [StateOwned(f)];
            }
        }

        return _ownedOf[field];
    }

    /// <summary>Adds item <paramref name="entry"/> to <paramref name="flight"/>, the states of other columns it waits for, and the item of the flight before it.</summary>
    private int Add(Flight flight, int entry, int[] after, int before)
    {
        int index = flight.Count++;
        flight.Entries[index] = entry;
        flight.Own[index] = OwnStates(entry);
        flight.After[index] = after;
        flight.Before[index] = before;
        flight.Befores[index] = [];
        return index;
    }

    /// <summary>Runs item <paramref name="entry"/> of <paramref name="flight"/> on the line.</summary>
    private void RunLineItem(Flight flight, int entry)
    {
        int rows = flight.Rows;
        if ((entry & ReadItem) != 0)
        {
            int field = entry & 0xF_FFFF;
            _indexes!.TakeKeys(field, flight.Keys[field]!, flight.Arena, flight.FieldNodes[field], rows);
            return;
        }

        if ((entry & NestedItem) != 0)
        {
            int field = entry & 0xF_FFFF;
            _indexes!.FeedNested(field, flight.Keys[field]!, rows, 0, _blockRows);
            return;
        }

        if ((entry & CensusItem) != 0)
        {
            int field = entry & 0xF_FFFF;
            _census![field]!.Add(in flight.Census![field], 0, rows);
            return;
        }

        if ((entry & KeysItem) != 0)
        {
            int field = entry & 0xF_FFFF;
            int group = ((entry >> 20) & 0x3FF) - 1;
            _indexes!.FeedGroup(field, group, flight.Keys[field]!, rows, 0, _blockRows);
            return;
        }

        if ((entry & GatherItem) != 0)
        {
            int field = entry & 0xF_FFFF;
            Gather(field, _gathering![field]!, flight.Arena, flight.FieldNodes[field], rows, ChunkBlocks(rows, _blockRows), flight.Cuts || flight.AfterCut ? flight.ChunkFirstBlock : -1);
            return;
        }

        if ((entry & ShredItem) != 0)
        {
            TakeOnLine(flight, entry & 0xF_FFFF);
            return;
        }

        if ((entry & CloseItem) != 0)
        {
            CutColumn(flight, entry & 0xF_FFFF);
            return;
        }

        if ((entry & OverItem) != 0)
        {
            DecideOver(flight);
            return;
        }

        if ((entry & 7) != 0)
        {
            int accumulated = entry >> 3;
            IngestPart(accumulated, (ColumnWriter.IngestParts)(entry & 7), flight.Arena, flight.FieldNodes[accumulated], rows, 0);
            return;
        }

        int fed = entry >> 3;
        IndexWriter indexes = _indexes!;
        if (fed < _fieldCount && flight.AttachFrom is { } before)
        {
            // The flight before's distinct values of the wide columns, each wide column past that flight: an attachment is a
            // maximum of registers, which no later batch's rows change.
            indexes.Attach(fed, before, flight.AttachRows);
        }

        FeedPart(fed, flight.Arena, flight.FieldNodes, rows, 0, flight.Counted, flight.KeysApart);
        if (fed < _fieldCount)
        {
            // A wide column settles on its own sheet once the batch is in, at the points it settled at when every column's
            // batch was in.
            indexes.SettleWide(fed);
        }
    }

    /// <summary>
    /// The epilogue of <paramref name="flight"/>, its items run, in the order of the flights: its keys' passes ended, its
    /// rows into transit, the counted rows its counts attached given back, its staging kept for a block staged later; and,
    /// unless it ends a chunk, what the writer's size reads of it recorded.
    /// </summary>
    private void Epilogue(Flight flight, bool failed)
    {
        Staging staging = flight.Staging!;
        foreach (IndexWriter.KeyPass? pass in flight.Keys)
        {
            pass?.End();
        }

        if (!failed)
        {
            // A block that makes its chunk alone is adopted where it was assembled; one that joins rows in transit is copied.
            // One cut column by column goes to no transit: its columns all gathered it in its items.
            if (flight.Cuts)
            {
                Interlocked.Increment(ref _cutFlights);
            }
            else if (flight.EndsChunk && _pendingRows == 0 && !_mayShareChildren)
            {
                Adopt(staging, flight.Node);
            }
            else
            {
                Hold(flight.Arena, flight.Node);
            }

            BatchesIngestedAcross++;
            BlocksOnLine++;
            if (flight.KeysApart)
            {
                BatchesKeysApart++;
            }
        }

        if (flight.AttachFrom is { } attached)
        {
            SpareCounted(attached);
        }

        staging.Assembled = -1;
        staging.Rows = 0;
        staging.Bytes = 0;

        // A block cut gives its blocks back, as the transit that adopts one gives them back at its emission: kept by each of
        // the four to six stagings a writer makes, they held a block's rows each (2026-11-18).
        if (flight.Cuts)
        {
            staging.Context.Canonical.Reset();
        }
        else
        {
            staging.Context.Canonical.ResetKeepingBlocks();
        }

        SpareStaging(staging);
        bool ends = flight.EndsChunk;
        long index = flight.Index;
        flight.Land();
        if (!failed && !ends)
        {
            RecordLand(index);
        }
    }

    private void SpareCounted(CountedRows counted)
    {
        Stack<CountedRows> spares = LazyInitializer.EnsureInitialized(ref _spareCounted);
        lock (spares)
        {
            spares.Push(counted);
        }
    }

    /// <summary>
    /// Attaches the wide columns' distinct values of the last flight on the line, which no flight after it attached: before
    /// a chunk's emission judges the builders, before a block goes through alone, and once the flights are done.
    /// </summary>
    private void FlushAttach()
    {
        if (_attachDue is not { } due)
        {
            return;
        }

        _attachDue = null;
        if (_indexes is { } indexes)
        {
            for (int field = 0; field < _fieldCount; field++)
            {
                indexes.Attach(field, due, _attachDueRows);
            }
        }

        SpareCounted(due);
    }

    /// <summary>Emits the chunk the rows of <paramref name="flight"/> ended, every flight before it landed, then records its landing.</summary>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask EmitAfterAsync(Flight flight, CancellationToken cancellationToken)
    {
        FlushAttach();
        _planPending = 0;
        _gatheredInIngest = flight.Gathers;
        try
        {
            await EmitBlockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gatheredInIngest = false;
        }

        ChunksEndedOnLine++;
        RecordLand(flight.Index);
    }

    /// <summary>
    /// Sends <paramref name="flight"/>'s block through alone, every flight before it landed, as a merge's writer sent every
    /// block before the line: its rows written as a batch of their own, its assembly done.
    /// </summary>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AloneAsync(Flight flight, CancellationToken cancellationToken)
    {
        FlushAttach();
        Staging staging = flight.Staging!;
        try
        {
            TakeAlone(staging);
            await WriteStagedAsync(staging, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SpareStaging(staging);
            flight.Land();
        }

        BlocksAlone++;
        _planPending = _pendingRows;
        RecordLand(flight.Index);
    }

    /// <summary>Gives back the stagings and counted rows the line kept, for a writer done.</summary>
    private void ReleaseLine()
    {
        if (_spareStagings is { } stagings)
        {
            lock (stagings)
            {
                while (stagings.TryPop(out Staging? staging))
                {
                    Ledger?.Release(staging, ref staging.FirstRun);
                    TransitContexts.Return(staging.Context);
                }
            }
        }

        _attachDue?.Dispose();
        _attachDue = null;
        if (_spareCounted is { } spares)
        {
            lock (spares)
            {
                while (spares.TryPop(out CountedRows? counted))
                {
                    counted.Dispose();
                }
            }
        }

        _line?.Release();
    }

    /// <summary>
    /// The flights of a merge's writer that go through at once: each a block, its prologue run in the order of the flights
    /// once it is assembled, its items run on the session's lanes, each as soon as every state it touches is done with the
    /// flight before, and its epilogue run in the order of the flights once its items ran. What waits, the emission of a chunk
    /// and a block that goes through alone, falls to its owner, the writer's caller, which runs the entries ready as it waits.
    /// </summary>
    /// <remarks>
    /// A work of the core that offers itself for as many pumps as it has entries ready: an entry is a flight's prologue, one
    /// of its items, or its epilogue. Under its own lock, which the core's lock may hold, never the other way.
    /// </remarks>
    private sealed class FlightLine(VortexFileWriter writer, FlowCore core) : CoreWork, IValueTaskSource
    {
        private const int Prologues = 0xF_FFFF;
        private const int Epilogues = 0xF_FFFE;
        private const int TicketBits = 20;

        /// <summary>What stops the line of a writer given up, which no one throws.</summary>
        private static readonly OperationCanceledException GivenUp = new OperationCanceledException("The writer was given up.");

        private readonly Lock _gate = new Lock();
        private readonly Flight?[] _ring = new Flight?[LandRing];
        private readonly Queue<long> _ready = new Queue<long>();
        private readonly Stack<Flight> _spare = new Stack<Flight>();
        private int[] _posed = [];

        // By state, whether the epilogue being counted moved it past its flight (PoseUntouched); cleared after.
        private bool[] _raised = [];

        // The flights admitted, prologued, whose epilogue ran, and landed, each a count of the writer's flights; and the
        // flights the owner is through with, which it goes through in their order, ahead of the landings for flights cut column
        // by column, whose landing waits for their epilogue too.
        private long _admitted;
        private long _prologued;
        private long _epilogued;
        private long _landed;
        private long _owning;

        // Whether a prologue or an epilogue is queued or running: each runs one at a time, in order.
        private bool _prologuing;
        private bool _epiloguing;
        private Exception? _error;

        // What the owner awaits: the flights landed it waits for, or an action of its own.
        private ManualResetValueTaskSourceCore<bool> _wake;
        private bool _ownerWaits;
        private long _ownerTarget;

        /// <summary>Whether a flight is admitted and not landed.</summary>
        internal bool Flying
        {
            get
            {
                lock (_gate)
                {
                    return _landed < _admitted;
                }
            }
        }

        /// <summary>Admits the flight of <paramref name="staging"/>, number <paramref name="index"/>, whose assembly is offered next.</summary>
        internal void Admit(Staging staging, long index)
        {
            lock (_gate)
            {
                if (_posed.Length == 0)
                {
                    // The line made at a later block than the writer's first: the blocks before it went through one at a
                    // time, and count as landed, every state past them.
                    _posed = new int[writer.StateCount()];
                    _raised = new bool[_posed.Length];
                    Array.Fill(_posed, (int)Math.Min(index, int.MaxValue));
                    _prologued = _epilogued = _landed = _owning = index;
                }

                Flight flight = _spare.Count > 0 ? _spare.Pop() : new Flight();
                flight.Staging = staging;
                flight.Index = index;
                flight.Phase = FlightPhase.Admitted;
                flight.Cuts = false;
                flight.LandsFailed = false;
                staging.Flight = flight;
                staging.Ready = false;
                _ring[index % LandRing] = flight;
                _admitted = index + 1;
            }
        }

        /// <summary>
        /// The block of <paramref name="staging"/> is assembled: its prologue may run, in order. A notice that comes late, its
        /// staging since taken by a flight whose block is not yet assembled, is let go.
        /// </summary>
        internal void Assembled(Staging staging)
        {
            int ready;
            lock (_gate)
            {
                if (staging.Flight is not { Phase: FlightPhase.Admitted } || (staging.Assembled < 0 && Volatile.Read(ref _error) is null))
                {
                    return;
                }

                staging.Ready = true;
                ready = QueuePrologue() ? 1 : 0;
            }

            if (ready > 0)
            {
                core.Offer(this, Flow.Write, ready);
            }
        }

        /// <summary>
        /// Waits, running the entries ready meanwhile and doing what falls to the owner, until the flights before
        /// <paramref name="target"/> landed; rethrows what failed a flight, unless <paramref name="quietly"/>.
        /// </summary>
        [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
        internal async ValueTask SettleAsync(long target, bool quietly, CancellationToken cancellationToken)
        {
            // A writer given up does no more work on the line: its flights land as a failed one's do.
            if (quietly)
            {
                Interlocked.CompareExchange(ref _error, GivenUp, null);
            }

            while (true)
            {
                Flight? owned = null;
                Staging? assemble = null;
                long ticket = -1;
                bool done = false;
                lock (_gate)
                {
                    if (OwnerFlight() is { } acting)
                    {
                        owned = acting;
                    }
                    else if (_landed >= target)
                    {
                        done = true;
                    }
                    else if (!_ready.TryDequeue(out ticket))
                    {
                        ticket = -1;

                        // Nothing ready: the block the next prologue waits for, assembled here when no pump took it. What holds a
                        // lane never waits for work a free lane would have to run.
                        if (!_prologuing && _prologued < _admitted && _ring[_prologued % LandRing] is { Staging: { Ready: false } waiting })
                        {
                            assemble = waiting;
                        }
                        else
                        {
                            _ownerWaits = true;
                            _ownerTarget = target;
                            _wake.Reset();
                        }
                    }
                }

                if (done)
                {
                    if (!quietly && Volatile.Read(ref _error) is { } failure)
                    {
                        ExceptionDispatchInfo.Throw(failure);
                    }

                    return;
                }

                if (owned is not null)
                {
                    await OwnAsync(owned, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (assemble is not null)
                {
                    try
                    {
                        await assemble.Assembly!.RunOrWaitAsync(core).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        // Its prologue finds the line failed, and lets its block go.
                        Interlocked.CompareExchange(ref _error, error, null);
                    }

                    Assembled(assemble);
                    continue;
                }

                if (ticket >= 0)
                {
                    Complete(ticket, Execute(ticket));
                    continue;
                }

                await new ValueTask(this, _wake.Version).ConfigureAwait(false);
            }
        }

        /// <summary>Does what falls to the owner for the oldest flight not landed: the emission of the chunk it ends, or its block alone.</summary>
        [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask OwnAsync(Flight flight, CancellationToken cancellationToken)
        {
            if (flight.Cuts)
            {
                await OwnCutAsync(flight, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool alone = flight.Phase == FlightPhase.Alone;
            try
            {
                if (Volatile.Read(ref _error) is not null)
                {
                    if (alone)
                    {
                        writer.SpareStaging(flight.Staging!);
                        flight.Land();
                    }
                }
                else if (alone)
                {
                    await writer.AloneAsync(flight, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await writer.EmitAfterAsync(flight, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
            }

            int ready;
            lock (_gate)
            {
                if (alone)
                {
                    // A block alone touched every state, and is past its epilogue as it went through.
                    _epilogued = flight.Index + 1;
                    for (int s = 0; s < _posed.Length; s++)
                    {
                        _posed[s] = (int)Math.Min(flight.Index + 1, int.MaxValue);
                    }
                }

                ready = Land(flight);
                ready += LandInOrder();
                ready += (QueuePrologue() ? 1 : 0) + (QueueEpilogue() ? 1 : 0);
            }

            if (ready > 0)
            {
                core.Offer(this, Flow.Write, ready);
            }
        }

        /// <summary>
        /// Emits the columns of <paramref name="flight"/>, cut column by column, that its cuts reached, then, every column
        /// emitted, closes its chunk and is through with it, landing it if its epilogue ran. Whatever fails, every column is
        /// given back to the flights after, which would wait for it forever otherwise.
        /// </summary>
        [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask OwnCutAsync(Flight flight, CancellationToken cancellationToken)
        {
            bool finished;
            try
            {
                finished = Volatile.Read(ref _error) is null && await writer.EmitCutColumnsAsync(flight, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
                finished = false;
            }

            bool failed = Volatile.Read(ref _error) is not null;
            if (!finished && !failed)
            {
                // Its next column is not cut yet: the owner comes back once it is.
                return;
            }

            if (failed)
            {
                writer.DropCut(flight);
            }

            int ready;
            lock (_gate)
            {
                ready = failed ? PoseOwned(flight, flight.Owned) : 0;
                flight.Finished = true;
                _owning = flight.Index + 1;
                ready += LandInOrder();
                ready += (QueuePrologue() ? 1 : 0) + (QueueEpilogue() ? 1 : 0);
            }

            if (ready > 0)
            {
                core.Offer(this, Flow.Write, ready);
            }
        }

        /// <summary>
        /// Lands the flights not landed, oldest first, under the lock, as long as the oldest may: its epilogue run, and its rows
        /// in transit, or, cut column by column, its owner through with it, or its line failed. After a flight cut column by
        /// column, the next one may run its epilogue first: it lands once that one did. The items made ready: how many.
        /// </summary>
        private int LandInOrder()
        {
            int ready = 0;
            while (_landed < _prologued && _ring[_landed % LandRing] is { Phase: FlightPhase.Held } head
                && (head.Cuts ? head.Finished : !head.EndsChunk || head.LandsFailed || Volatile.Read(ref _error) is not null))
            {
                ready += Land(head);
            }

            return ready;
        }

        /// <summary>
        /// Lands <paramref name="flight"/>, the oldest not landed, under the lock: its slot freed for a flight after it. A flight
        /// not cut column by column is one the owner is through with as it lands, and its columns' owned states are posed past
        /// it then, the items of the next flight that waited for them made ready: how many.
        /// </summary>
        private int Land(Flight flight)
        {
            int ready = 0;
            if (!flight.Cuts)
            {
                ready = PoseOwned(flight, 0);
                _owning = Math.Max(_owning, flight.Index + 1);
            }

            flight.Phase = FlightPhase.Landed;
            _landed = flight.Index + 1;
            _ring[flight.Index % LandRing] = null;
            _spare.Push(flight);
            return ready;
        }

        /// <summary>
        /// Poses the owned states of <paramref name="flight"/>'s columns from <paramref name="from"/> on past it, under the lock
        /// (<see cref="VortexFileWriter.StateOwned"/>), and makes ready the items of the next flight that waited for them: how many.
        /// </summary>
        private int PoseOwned(Flight flight, int from) => _posed.Length == 0 ? 0 : PoseOwned(flight, from, writer._fieldCount);

        /// <summary>
        /// Poses column <paramref name="field"/>'s owned state past <paramref name="flight"/>, cut column by column, once its
        /// owner emitted the column (<see cref="VortexFileWriter.EmitCutColumnsAsync"/>): the next flight's items of the
        /// column that waited for it are offered.
        /// </summary>
        internal void Owned(Flight flight, int field)
        {
            int ready;
            lock (_gate)
            {
                ready = PoseOwned(flight, field, field + 1);
            }

            if (ready > 0)
            {
                core.Offer(this, Flow.Write, ready);
            }
        }

        /// <summary>Poses the owned states of columns <paramref name="from"/> to <paramref name="to"/> past <paramref name="flight"/>, under the lock.</summary>
        private int PoseOwned(Flight flight, int from, int to)
        {
            int ready = 0;
            int past = (int)Math.Min(flight.Index + 1, int.MaxValue);
            for (int field = from; field < to; field++)
            {
                int state = writer.StateOwned(field);
                if (_posed[state] >= past)
                {
                    continue;
                }

                _posed[state] = past;
                if (flight.Index + 1 < _prologued && _ring[(flight.Index + 1) % LandRing] is { Phase: FlightPhase.Running } next)
                {
                    for (int i = 0; i < next.Count; i++)
                    {
                        if (next.Waiting[i] > 0 && (Array.IndexOf(next.After[i], state) >= 0 || Array.IndexOf(next.Own[i], state) >= 0)
                            && --next.Waiting[i] == 0)
                        {
                            _ready.Enqueue((next.Index << TicketBits) | (uint)i);
                            ready++;
                        }
                    }
                }
            }

            return ready;
        }

        internal override bool TryClaim(out bool more, out long ticket)
        {
            lock (_gate)
            {
                if (_ready.TryDequeue(out ticket))
                {
                    more = _ready.Count > 0;
                    return true;
                }
            }

            more = false;
            return false;
        }

        internal override bool Run(long ticket) => Complete(ticket, Execute(ticket));

        internal override void Signal() => _wake.SetResult(true);

        /// <summary>Runs the entry <paramref name="ticket"/> names, catching what fails it: whether it failed.</summary>
        private bool Execute(long ticket)
        {
            Flight flight = _ring[(ticket >> TicketBits) % LandRing]!;
            int code = (int)(ticket & ((1 << TicketBits) - 1));
            bool failed = Volatile.Read(ref _error) is not null;
            try
            {
                if (code == Prologues)
                {
                    if (failed || !writer.Prologue(flight, first: flight.Index == Volatile.Read(ref _landed)))
                    {
                        flight.Phase = FlightPhase.Alone;
                        flight.Cuts = false;
                    }
                }
                else if (code == Epilogues)
                {
                    writer.Epilogue(flight, failed);
                }
                else if (!failed)
                {
                    writer.RunLineItem(flight, flight.Entries[code]);
                }

                return false;
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
                if (code == Prologues)
                {
                    flight.Phase = FlightPhase.Alone;
                    flight.Cuts = false;
                }

                return true;
            }
        }

        /// <summary>
        /// Counts the entry <paramref name="ticket"/> names done: the entries it made ready queued, and offered once the lock
        /// is left. Whether the owner awaits what it brought, which the pump that ran it then signals.
        /// </summary>
        private bool Complete(long ticket, bool failed)
        {
            long index = ticket >> TicketBits;
            int code = (int)(ticket & ((1 << TicketBits) - 1));
            int ready = 0;
            bool signal;
            lock (_gate)
            {
                Flight flight = _ring[index % LandRing]!;
                if (code == Prologues)
                {
                    _prologuing = false;
                    _prologued = index + 1;
                    if (flight.Phase != FlightPhase.Alone)
                    {
                        flight.Phase = FlightPhase.Running;
                        ready += Register(flight);
                    }

                    ready += QueuePrologue() ? 1 : 0;
                }
                else if (code == Epilogues)
                {
                    _epiloguing = false;
                    _epilogued = index + 1;
                    flight.Phase = FlightPhase.Held;
                    ready += PoseUntouched(flight);
                    if (failed && flight.EndsChunk && !flight.Cuts)
                    {
                        flight.LandsFailed = true;
                    }

                    long landed = _landed;
                    ready += LandInOrder();
                    if (_landed > landed)
                    {
                        ready += QueuePrologue() ? 1 : 0;
                    }

                    ready += QueueEpilogue() ? 1 : 0;
                }
                else
                {
                    ready += Done(flight, code);
                }

                signal = _ownerWaits && (_landed >= _ownerTarget || OwnerActs());
                if (signal)
                {
                    _ownerWaits = false;
                }
            }

            if (ready > 0)
            {
                core.Offer(this, Flow.Write, ready);
            }

            return signal;
        }

        /// <summary>Whether a flight waits for its owner, under the lock (<see cref="OwnerFlight"/>).</summary>
        private bool OwnerActs() => OwnerFlight() is not null;

        /// <summary>
        /// The flight that waits for its owner, under the lock; null for none. The first the owner is not through with: cut
        /// column by column, once its next column's cut ran; otherwise once it is the oldest not landed, for the chunk it ends
        /// or its block alone.
        /// </summary>
        private Flight? OwnerFlight()
        {
            if (_owning >= _prologued || _ring[_owning % LandRing] is not { } flight)
            {
                return null;
            }

            if (flight.Cuts && flight.Phase is FlightPhase.Running or FlightPhase.Ran or FlightPhase.Held)
            {
                return !flight.Finished && (flight.Owned == flight.CutDone.Length || flight.CutDone[flight.Owned]) ? flight : null;
            }

            return _owning == _landed && ((flight.Phase == FlightPhase.Held && flight.EndsChunk) || flight.Phase == FlightPhase.Alone)
                ? flight
                : null;
        }

        /// <summary>
        /// Queues the prologue of the next flight, under the lock, when it may run: its block assembled, the prologue before it
        /// done, and the flight before it neither ending a chunk nor going alone, unless it landed.
        /// </summary>
        private bool QueuePrologue()
        {
            if (_prologuing || _prologued >= _admitted || _ring[_prologued % LandRing] is not { Staging.Ready: true } flight)
            {
                return false;
            }

            // A flight cut column by column lets the next start once its prologue ran: the next one's columns follow its cuts.
            if (_prologued > _landed && _ring[(_prologued - 1) % LandRing] is { } before
                && ((before.EndsChunk && !before.Cuts) || before.Phase == FlightPhase.Alone))
            {
                return false;
            }

            _prologuing = true;
            _ready.Enqueue((flight.Index << TicketBits) | Prologues);
            return true;
        }

        /// <summary>Queues the epilogue of the next flight whose items all ran, under the lock.</summary>
        private bool QueueEpilogue()
        {
            if (_epiloguing || _epilogued >= _prologued || _ring[_epilogued % LandRing] is not { Phase: FlightPhase.Ran } flight)
            {
                return false;
            }

            _epiloguing = true;
            _ready.Enqueue((flight.Index << TicketBits) | Epilogues);
            return true;
        }

        /// <summary>Registers <paramref name="flight"/>'s items, under the lock: how many it queued ready.</summary>
        private int Register(Flight flight)
        {
            int ready = 0;
            int index = (int)Math.Min(flight.Index, int.MaxValue);
            for (int i = 0; i < flight.Count; i++)
            {
                int waiting = (flight.Before[i] >= 0 ? 1 : 0) + flight.Befores[i].Length;
                foreach (int state in flight.Own[i])
                {
                    waiting += _posed[state] < index ? 1 : 0;
                }

                foreach (int state in flight.After[i])
                {
                    waiting += _posed[state] < index ? 1 : 0;
                }

                flight.Waiting[i] = waiting;
                if (waiting == 0)
                {
                    _ready.Enqueue((flight.Index << TicketBits) | (uint)i);
                    ready++;
                }
            }

            if (flight.Count == 0)
            {
                flight.Phase = FlightPhase.Ran;
                ready += QueueEpilogue() ? 1 : 0;
            }

            return ready;
        }

        /// <summary>
        /// Counts item <paramref name="item"/> of <paramref name="flight"/> done, under the lock: its states past the flight,
        /// and the items of the next flight that waited for them and those of this flight that waited for it made ready. How
        /// many.
        /// </summary>
        private int Done(Flight flight, int item)
        {
            int ready = 0;
            int past = (int)Math.Min(flight.Index + 1, int.MaxValue);
            int entry = flight.Entries[item];
            if ((entry & CloseItem) != 0 && (entry & KeysItem) == 0)
            {
                // The column's cut ran: its owner may emit it.
                flight.CutDone[entry & 0xF_FFFF] = true;
            }

            int[] states = flight.Own[item];
            foreach (int state in states)
            {
                _posed[state] = past;
            }

            if (states.Length > 0 && flight.Index + 1 < _prologued && _ring[(flight.Index + 1) % LandRing] is { Phase: FlightPhase.Running } next)
            {
                for (int i = 0; i < next.Count; i++)
                {
                    if (next.Waiting[i] == 0)
                    {
                        continue;
                    }

                    int freed = Count(next.Own[i], states) + Count(next.After[i], states);
                    if (freed > 0 && (next.Waiting[i] -= freed) == 0)
                    {
                        _ready.Enqueue((next.Index << TicketBits) | (uint)i);
                        ready++;
                    }
                }
            }

            for (int i = 0; i < flight.Count; i++)
            {
                int freed = (flight.Before[i] == item ? 1 : 0) + (Array.IndexOf(flight.Befores[i], item) >= 0 ? 1 : 0);
                if (freed > 0 && (flight.Waiting[i] -= freed) == 0)
                {
                    _ready.Enqueue((flight.Index << TicketBits) | (uint)i);
                    ready++;
                }
            }

            if (--flight.Remaining == 0)
            {
                flight.Phase = FlightPhase.Ran;
                ready += QueueEpilogue() ? 1 : 0;
            }

            return ready;
        }

        /// <summary>
        /// Moves every state past <paramref name="flight"/> whose epilogue ran, under the lock, and makes ready the items of the
        /// next flight that waited for those it moved: how many. Its epilogue runs once every item of it ran, after the
        /// epilogues of the flights before it, so no flight up to it has work left on any state, those it did not touch
        /// included.
        /// </summary>
        /// <remarks>
        /// A state moved only as an item that touched it ended. A flight that touched a state no flight since it did waited for
        /// them forever: a shredded column's chain broken for three blocks, then taken again at a chunk's start, left the next
        /// flight's item waiting for the state the three never moved, and a writer of 30 000 rows hung (2026-11-18). Hidden
        /// while a block that made a whole chunk went through alone, which moves every state.
        /// </remarks>
        private int PoseUntouched(Flight flight)
        {
            int past = (int)Math.Min(flight.Index + 1, int.MaxValue);
            bool any = false;
            for (int state = 0; state < _posed.Length; state++)
            {
                // A column's owned state is the owner's to pose, as it is through with each flight (PoseOwned).
                if (_posed[state] < past && !writer.IsOwnedState(state))
                {
                    _posed[state] = past;
                    _raised[state] = true;
                    any = true;
                }
            }

            if (!any)
            {
                return 0;
            }

            int ready = 0;
            if (flight.Index + 1 < _prologued && _ring[(flight.Index + 1) % LandRing] is { Phase: FlightPhase.Running } next)
            {
                for (int i = 0; i < next.Count; i++)
                {
                    if (next.Waiting[i] == 0)
                    {
                        continue;
                    }

                    int freed = Raised(next.Own[i]) + Raised(next.After[i]);
                    if (freed > 0 && (next.Waiting[i] -= freed) == 0)
                    {
                        _ready.Enqueue((next.Index << TicketBits) | (uint)i);
                        ready++;
                    }
                }
            }

            Array.Clear(_raised);
            return ready;
        }

        /// <summary>How many of <paramref name="states"/> the epilogue being counted moved (<see cref="PoseUntouched"/>).</summary>
        private int Raised(int[] states)
        {
            int count = 0;
            foreach (int state in states)
            {
                count += _raised[state] ? 1 : 0;
            }

            return count;
        }

        /// <summary>How many of <paramref name="states"/> are among <paramref name="past"/>.</summary>
        private static int Count(int[] states, int[] past)
        {
            int count = 0;
            foreach (int state in states)
            {
                count += Array.IndexOf(past, state) >= 0 ? 1 : 0;
            }

            return count;
        }

        /// <summary>Gives back the key passes the line's flights kept.</summary>
        internal void Release()
        {
            lock (_gate)
            {
                foreach (Flight flight in _spare)
                {
                    flight.Release();
                }

                _spare.Clear();
            }
        }

        void IValueTaskSource.GetResult(short token) => _wake.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _wake.GetStatus(token);

        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _wake.OnCompleted(continuation, state, token, flags);
    }
}
