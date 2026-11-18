using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// The chunks a writer's line cuts column by column: each column's cut an item of the flight that ends the chunk, in the
/// column's own chain, and the owner emitting each column once cut, while the flights after it go through.
/// </summary>
/// <remarks>
/// A block of a consolidation of metric records makes a chunk of its own, and the next block's prologue waited for the
/// block's chunk to be emitted, its items all run first: the line went at the pace of a block's slowest item plus its
/// emission, 0.75 ms a block, on some seven of fourteen lanes (2026-11-18). Cut column by column, a column goes on to the
/// next block once its own cut and emission ran, and the slowest column paces only itself.
/// </remarks>
public sealed partial class VortexFileWriter
{
    // By flight modulo LandRing, then by column, what the column held once the flight's cut of it ran (CloseItem): what the
    // next flight's cut decides from and its takes are placed by, in place of the column's state as it stands, which the
    // flights after it change as they go.
    private CutRecord[][]? _cutRecords;

    // The blocks and the rows of the file before the chunks the flights prologued since every flight landed, in their order:
    // where the next flight's chunk starts, before the owner counts the chunks emitted.
    private int _planBlocks;
    private long _planRows;
    private long _planGroupTaken;

    // Whether the last flight prologued is cut column by column, which lets the next start before it lands.
    private bool _lastCut;

    private int _cutFlights;

    /// <summary>The flights of the writer's line whose chunks were cut column by column (<see cref="CutsOnLine"/>).</summary>
    internal int ChunksCutOnLine => Volatile.Read(ref _cutFlights);

    /// <summary>
    /// Whether flight <paramref name="flight"/>, a block of <paramref name="rows"/> rows that gathers on the line, is cut
    /// column by column (<see cref="CutsOnLine"/>): also only while the object's blocks up to the next flight could not hold
    /// int.MaxValue rows, so that the next flight, which starts before this one lands, knows that no column it gathers into
    /// passes them (<see cref="GatheringOverflows"/>) without reading what the columns hold.
    /// </summary>
    private bool CutsAt(long flight, int rows) => (flight + 2) * (long)_blockRows <= int.MaxValue && CutsOnLine(rows);

    /// <summary>
    /// What a column held once its cut ran: its gathering's canonical bytes, the bytes it holds, its rows and the chunks it
    /// began, what it and its chunks in emission count toward the writer's ceiling (<see cref="Over"/>), and for a shredded
    /// column, whether its chain takes the next flight's rows (<see cref="ShredChain.Accepts"/>), which that flight then
    /// gathers thin.
    /// </summary>
    private readonly record struct CutRecord(long Bytes, long Held, long Rows, long Generation, long Counted, bool Takes);

    /// <summary>The columns' records of flight <paramref name="flight"/>'s cuts, made by the prologue of the first flight cut (<see cref="MakeCutRecords"/>).</summary>
    private CutRecord[] CutRecordsOf(long flight) => _cutRecords![flight % LandRing];

    /// <summary>
    /// Makes the records of the cuts, whole, in the prologue of the first flight cut, before any cut: made by the first of
    /// the columns' cuts, which run side by side, another cut found the ring published before its slots were.
    /// </summary>
    private void MakeCutRecords()
    {
        if (_cutRecords is not null)
        {
            return;
        }

        MakeSlotsAtCut();

        CutRecord[][] records = new CutRecord[LandRing][];
        for (int slot = 0; slot < LandRing; slot++)
        {
            records[slot] = new CutRecord[_fieldCount];
        }

        Volatile.Write(ref _cutRecords, records);
    }

    /// <summary>
    /// Whether a block of <paramref name="rows"/> rows that gathers on the line (<see cref="GathersOnLine"/>) has its chunk
    /// cut column by column: every column gathers, so that nothing of the chunk waits in transit for its emission; its rows
    /// end no group of rows, whose end writes what every column holds as the chunk's emission does it, counted in the order
    /// of the flights; and no list shares its elements with the batch it was cut from.
    /// </summary>
    private bool CutsOnLine(int rows)
    {
        if ((_groupRows > 0 && _planGroupTaken + rows >= _groupRows) || _mayShareChildren || _gathering is not { } gathering)
        {
            return false;
        }

        foreach (Gathering? column in gathering)
        {
            if (column is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decides which column of <paramref name="flight"/>, cut after a flight cut too, writes its chunk early
    /// (<see cref="Over"/>), from what each column held once that flight's cut of it ran: what <see cref="Over"/> reads of
    /// the columns as they stand once every flight before landed, the chunks the owner lands at each cut uncounted at the cut.
    /// </summary>
    private void DecideOver(Flight flight)
    {
        flight.OverField = -1;
        if (_gatheredCeiling > 0)
        {
            CutRecord[] before = CutRecordsOf(flight.Index - 1);
            long total = 0;
            for (int field = 0; field < _fieldCount; field++)
            {
                total += before[field].Counted;
            }

            for (int field = 0; total > _gatheredCeiling && field < _fieldCount; field++)
            {
                if (before[field].Rows > 0 && (flight.OverField < 0 || before[field].Held > before[flight.OverField].Held))
                {
                    flight.OverField = field;
                }
            }
        }

        // A flight that gathers without being cut, its chunk ending a group of rows, has its chunk emitted by its owner once
        // every flight before landed, as a chunk's emission decides: from the column the writer holds (Over).
        if (!flight.Cuts)
        {
            _over = flight.OverField >= 0 ? _gathering![flight.OverField] : null;
        }
    }

    /// <summary>
    /// Cuts column <paramref name="field"/>'s chunk in <paramref name="flight"/>, the column's items of the flight run: the
    /// column taken from its gathering when due or the one that writes early (<see cref="Detach"/>), its chunks the owner
    /// lands before the new one uncounted now, at this point of the rows' order, and what it then holds recorded for the next
    /// flight's cut and takes.
    /// </summary>
    private void CutColumn(Flight flight, int field)
    {
        Gathering column = _gathering![field]!;
        ColumnEmission? detached = null;
        if (column.Due || field == flight.OverField)
        {
            detached = Detach(field, column);
            if (_emissions?[field] is { } chunks)
            {
                for (int i = 0; i < chunks.Count - (EmissionDepth - 1); i++)
                {
                    Uncount(chunks[i]);
                }
            }
        }

        long counted = column.Held + (detached?.Held ?? 0);
        if (_emissions?[field] is { } inEmission)
        {
            foreach (ColumnEmission chunk in inEmission)
            {
                counted += chunk.Held;
            }
        }

        flight.Detached[field] = detached;
        // Whether the chain takes the next flight's rows, decided here, where its state stands as the next take finds it: the
        // next flight's take and gathering both wait for this cut, and the take alone changes the chain.
        bool takes = _chains is { Length: > 0 } chains && chains[field] is { } chain && chain.Accepts(column.Generation, column.Rows);
        CutRecordsOf(flight.Index)[field] = new CutRecord(column.Bytes, column.Held, column.Rows, column.Generation, counted, takes);

        // A column whose builders all close at once has its chunk closed here, the flight's wide counts attached first, as the
        // owner does it for one that closes later: the next flight's builders of the column then wait for this cut rather
        // than for the owner, who goes through the columns in their order.
        if (_indexes is { } indexes && ClosesAtCut(field))
        {
            if (flight.OwnCounted is { } attached)
            {
                indexes.Attach(field, attached, flight.Rows);
            }

            foreach (int slot in SlotsAtCut(field))
            {
                indexes.CloseChunkAtOnce(slot);
            }

            flight.ClosedAtCut[field] = true;
        }

        flight.OwnedAtCut[field] = detached is null && (_indexes is null || flight.ClosedAtCut[field]);
    }

    // By column, the builders' slots its cut closes (IndexWriter.FieldOfSlot), and whether they all close at once; made once.
    private int[][]? _slotsAtCut;
    private bool[]? _closesAtCut;

    /// <summary>The slots of builders column <paramref name="field"/>'s cut closes: its own, those of the keys and nested paths it holds, and the composite keys' for the last column.</summary>
    private int[] SlotsAtCut(int field)
    {
        MakeSlotsAtCut();
        return _slotsAtCut![field];
    }

    /// <summary>Whether column <paramref name="field"/>'s cut closes its builders' chunk itself, every one of them closing at once.</summary>
    private bool ClosesAtCut(int field)
    {
        MakeSlotsAtCut();
        return _closesAtCut![field];
    }

    private void MakeSlotsAtCut()
    {
        if (_slotsAtCut is not null || _indexes is not { } indexes)
        {
            return;
        }

        System.Collections.Generic.List<int>[] slots = new System.Collections.Generic.List<int>[_fieldCount];
        bool[] atOnce = new bool[_fieldCount];
        for (int field = 0; field < _fieldCount; field++)
        {
            slots[field] = [];
            atOnce[field] = true;
        }

        for (int slot = 0; slot < indexes.Slots; slot++)
        {
            int field = indexes.FieldOfSlot(slot);
            slots[field].Add(slot);
            atOnce[field] &= indexes.ClosesAtOnce(slot);
        }

        _closesAtCut = atOnce;
        _slotsAtCut = Array.ConvertAll(slots, static list => list.ToArray());
    }

    /// <summary>
    /// Takes <paramref name="flight"/>'s rows of shredded column <paramref name="field"/> along its chain: after a flight cut
    /// column by column, placed where that flight's cut left the column, which the item finds once that cut ran, its chain
    /// following the cut's.
    /// </summary>
    private void TakeOnLine(Flight flight, int field)
    {
        ShredChain chain = _chains![field]!;
        if (flight.AfterCut)
        {
            CutRecord before = CutRecordsOf(flight.Index - 1)[field];
            if (!chain.Plan(before.Generation, before.Rows, flight.Rows))
            {
                return;
            }

            flight.ShredAt[field] = checked((int)before.Rows);
        }

        chain.Shredding.TakeMore(in flight.Shred[field], flight.ShredAt[field]);
    }

    /// <summary>
    /// Emits the columns of <paramref name="flight"/>, cut column by column, that its cuts reached, in the columns' order:
    /// each column's chunks before landing, the one cut leaving for an emission of its own, the flight's distinct values of
    /// the column attached and the chunk's builders of the column closed, then the column given back to the flights after
    /// (<see cref="StateOwned"/>). Once every column is emitted, the chunk's indexes are judged and their payloads written,
    /// and the chunk counted, as the chunk's emission does it. Whether every column was.
    /// </summary>
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> EmitCutColumnsAsync(Flight flight, CancellationToken cancellationToken)
    {
        int blocks = ChunkBlocks(flight.Rows, _blockRows);
        while (flight.Owned < _fieldCount && Volatile.Read(ref flight.CutDone[flight.Owned]))
        {
            int field = flight.Owned;
            if (flight.Detached[field] is { } detached)
            {
                flight.Detached[field] = null;
                await LandEmissionsAsync(field, cancellationToken, keep: EmissionDepth - 1).ConfigureAwait(false);
                StartEmission(detached);
            }

            if (_indexes is { } indexes)
            {
                // A column whose cut closed its builders leaves them be; the others close here, in the slots' order.
                if (!flight.ClosedAtCut[field])
                {
                    if (flight.OwnCounted is { } counted)
                    {
                        indexes.Attach(field, counted, flight.Rows);
                    }

                    await indexes.CloseChunkAsync(field, flight.ChunkFirstBlock, blocks, flight.ChunkFirstRow, flight.Rows, cancellationToken).ConfigureAwait(false);
                }

                if (field == _fieldCount - 1)
                {
                    // Past the columns, the builders of the composite keys and of the variant columns' groups.
                    for (int slot = _fieldCount; slot < indexes.Slots; slot++)
                    {
                        if (!flight.ClosedAtCut[indexes.FieldOfSlot(slot)])
                        {
                            await indexes.CloseChunkAsync(slot, flight.ChunkFirstBlock, blocks, flight.ChunkFirstRow, flight.Rows, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            flight.Owned++;
            _line!.Owned(flight, field);
        }

        if (flight.Owned < _fieldCount)
        {
            return false;
        }

        if (flight.OwnCounted is { } attached)
        {
            flight.OwnCounted = null;
            SpareCounted(attached);
        }

        _indexes?.Judge();
        _emittedBlocks += blocks;
        _chunkRows.Add(flight.Rows);
        _rowCount += flight.Rows;
        if (_groupRows > 0)
        {
            // Short of the group's end, which a chunk cut column by column never reaches (CutsOnLine).
            _groupTaken += flight.Rows;
        }

        await FlushIndexesAsync(cancellationToken).ConfigureAwait(false);
        ChunksEndedOnLine++;
        _lands[flight.Index % LandRing] = LandedAtCut(flight.Index);
        return true;
    }

    /// <summary>What the writer's size reads as flight <paramref name="flight"/>, cut column by column, lands: its columns at the bytes they held once cut.</summary>
    private Landing LandedAtCut(long flight)
    {
        CutRecord[] cut = CutRecordsOf(flight);
        long canonical = 0;
        double encoded = 0;
        double rates = 0;
        int columns = 0;
        for (int field = 0; field < _fieldCount; field++)
        {
            Gathering column = _gathering![field]!;
            canonical += cut[field].Bytes;
            encoded += cut[field].Bytes * column.Rate;
            rates += column.Rate;
            columns++;
        }

        double rate = canonical > 0 ? encoded / canonical : columns > 0 ? rates / columns : Gathering.FirstRate;
        return new Landing(_sink.Position, encoded + _emittingBytes, _pendingBytes, rate);
    }

    /// <summary>Gives back the chunks <paramref name="flight"/>'s cuts took that a failure kept from leaving for their emissions.</summary>
    private void DropCut(Flight flight)
    {
        for (int field = 0; field < flight.Detached.Length; field++)
        {
            if (flight.Detached[field] is { } detached)
            {
                flight.Detached[field] = null;
                Release(detached);
                Uncount(detached);
                ReturnTaken(detached);
            }
        }

        if (flight.OwnCounted is { } counted)
        {
            flight.OwnCounted = null;
            SpareCounted(counted);
        }
    }
}
