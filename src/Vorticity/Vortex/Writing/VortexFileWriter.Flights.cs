using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types.Variants;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>The batches an ingest across the lanes takes, each with what its items read beside its rows.</summary>
public sealed partial class VortexFileWriter
{
    /// <summary>The batch the ingest across the lanes takes, kept from one batch to the next.</summary>
    private Flight? _across;

    /// <summary>
    /// One batch on its way through an ingest across the lanes: its rows, and everything its items read beside them, the
    /// batch's own rather than the writer's, so that an item reads its batch's whatever the writer does with the next.
    /// </summary>
    private sealed class Flight
    {
        /// <summary>The arena the batch's rows lie in.</summary>
        internal CanonicalArena Arena = null!;

        /// <summary>The batch's rows.</summary>
        internal int Rows;

        /// <summary>The rows of the open block before the batch's.</summary>
        internal int Filled;

        /// <summary>Each column's node, by its place.</summary>
        internal int[] FieldNodes = [];

        /// <summary>What the batch's counts read beside the columns; null for a file that keeps no counts.</summary>
        internal CountedRows? Counted;

        /// <summary>Whether the variant columns' keys are fed apart, in groups, which their columns' own items then leave out.</summary>
        internal bool KeysApart;

        /// <summary>Per variant column whose keys are fed apart, the batch's pass over them; null for another column.</summary>
        internal IndexWriter.KeyPass?[] Keys = [];

        // ---------------------------------------------------------------- on the writer's line (FlightLine)

        /// <summary>The staged block the flight sends through, which its assembly concatenates.</summary>
        internal Staging? Staging;

        /// <summary>The flight's number among the writer's, from 0.</summary>
        internal long Index;

        /// <summary>The node its block was assembled into.</summary>
        internal int Node;

        /// <summary>Where the flight is on the line.</summary>
        internal FlightPhase Phase;

        /// <summary>Whether the rows held once it lands complete a chunk of the file, which the writer emits before the next flight ingests.</summary>
        internal bool EndsChunk;

        /// <summary>Whether its items copy its block's columns in with what each gathers, its block a whole chunk of the file, which its emission then finds gathered.</summary>
        internal bool Gathers;

        /// <summary>
        /// Whether its chunk is cut column by column on the line (<see cref="CutsOnLine"/>): each column's cut an item of its
        /// own (<see cref="CloseItem"/>), the owner emitting each column once cut, the flights after it going through meanwhile.
        /// </summary>
        internal bool Cuts;

        /// <summary>Whether the flight before it was cut column by column, whose cuts its own then follow (<see cref="OverItem"/>).</summary>
        internal bool AfterCut;

        /// <summary>The column that writes its chunk early in the chunk it cuts, -1 for none (<see cref="Over"/>), set by its <see cref="OverItem"/> or its prologue.</summary>
        internal int OverField = -1;

        /// <summary>Per column, the chunk its cut took from the column's gathering to emission; null for none.</summary>
        internal ColumnEmission?[] Detached = [];

        /// <summary>Per column, whether its cut ran, which the owner emits the column after.</summary>
        internal bool[] CutDone = [];

        /// <summary>Per column, whether its cut closed its builders' chunk, which all close at once (<see cref="IndexWriter.ClosesAtOnce"/>).</summary>
        internal bool[] ClosedAtCut = [];

        /// <summary>
        /// Per column, whether its cut left the owner nothing to do for it that the next flight waits for: its builders closed,
        /// and no chunk of it taken, so none to land, which would hand its ingest what the emission decided. Its owned state
        /// is posed as the cut ends.
        /// </summary>
        internal bool[] OwnedAtCut = [];

        /// <summary>The columns its owner emitted, in their order.</summary>
        internal int Owned;

        /// <summary>Whether its owner emitted every column and closed its chunk, which it then lands with, its epilogue run.</summary>
        internal bool Finished;

        /// <summary>Whether it ends a chunk and its epilogue failed, so that it lands without its emission.</summary>
        internal bool LandsFailed;

        /// <summary>Its counted rows, whose wide columns' distinct values its owner attaches as it emits each column; null once attached.</summary>
        internal CountedRows? OwnCounted;

        /// <summary>The blocks and the rows of the file before its chunk, in the order of the flights.</summary>
        internal int ChunkFirstBlock;

        /// <inheritdoc cref="ChunkFirstBlock"/>
        internal long ChunkFirstRow;

        // By column, the items its cut waits for, kept from one flight to the next: a flight lays the same items each time.
        private int[][] _cutWaits = [];

        /// <summary>
        /// Column <paramref name="field"/>'s cut's items to wait for, <paramref name="items"/> each once, in an array kept for the
        /// flights this one carries next: an item laid for two columns' cuts may be laid twice for one, and an item done
        /// counts once toward what waits for it, so a cut that counted it twice would wait for ever.
        /// </summary>
        internal int[] CutWaits(int field, System.Collections.Generic.List<int> items)
        {
            if (_cutWaits.Length <= field)
            {
                System.Array.Resize(ref _cutWaits, field + 1);
            }

            items.Sort();
            int distinct = 0;
            for (int i = 0; i < items.Count; i++)
            {
                distinct += i == 0 || items[i] != items[i - 1] ? 1 : 0;
            }

            int[]? kept = _cutWaits[field];
            if (kept is null || kept.Length != distinct)
            {
                kept = _cutWaits[field] = new int[distinct];
            }

            for (int i = 0, at = 0; i < items.Count; i++)
            {
                if (i == 0 || items[i] != items[i - 1])
                {
                    kept[at++] = items[i];
                }
            }

            return kept;
        }

        /// <summary>Room for the cuts of <paramref name="fields"/> columns, emptied.</summary>
        internal void SizeCuts(int fields)
        {
            if (Detached.Length != fields)
            {
                Detached = new ColumnEmission?[fields];
                CutDone = new bool[fields];
                ClosedAtCut = new bool[fields];
                OwnedAtCut = new bool[fields];
            }

            System.Array.Clear(Detached);
            System.Array.Clear(CutDone);
            System.Array.Clear(ClosedAtCut);
            System.Array.Clear(OwnedAtCut);
            Owned = 0;
            Finished = false;
        }

        /// <summary>The counted rows of the flight before, whose wide columns' distinct values this flight's counts attach first; null for none.</summary>
        internal CountedRows? AttachFrom;

        /// <summary>The rows of the flight before, which <see cref="AttachFrom"/> holds.</summary>
        internal int AttachRows;

        /// <summary>Each variant column's paths counted for its census, made ready for the flight's rows; null for a writer that counts none.</summary>
        internal PathCensus.Counting[]? Census;

        /// <summary>
        /// Per shredded column whose rows the line takes block by block (<see cref="ShredChain"/>), the flight's rows made ready
        /// and where they fall in the column's chunk, -1 for a column whose chain does not take them.
        /// </summary>
        internal VariantShredder.Shredding.Rows[] Shred = [];

        /// <inheritdoc cref="Shred"/>
        internal int[] ShredAt = [];

        /// <summary>
        /// Per shredded column whose rows the line takes, what the flight's rows hold of the column's shredding, read alone
        /// beside the chain (<see cref="TakeBlock"/>), and the arena it is carved from: made on the column's first block of
        /// the flight and kept, null for a column the flight never took.
        /// </summary>
        internal VariantShredder.Shredding?[] Blocks = [];

        /// <inheritdoc cref="Blocks"/>
        internal CanonicalArena?[] BlockStores = [];

        /// <summary>
        /// Per shredded column, whether the flight's assembly filled <see cref="Blocks"/> from what the merge's inputs took of
        /// its runs' rows (<see cref="TakeRuns"/>), which <see cref="TakeBlock"/> then leaves as it is; until the flight lands.
        /// </summary>
        internal bool[] RunsTaken = [];

        /// <summary>
        /// Room for the takes of <paramref name="fields"/> columns, on the thread that assembles the flight's block, then on
        /// the one that runs its prologue, which follows it: what a flight before it took of a column kept.
        /// </summary>
        internal void SizeShreds(int fields)
        {
            if (ShredAt.Length != fields)
            {
                ShredAt = new int[fields];
                Shred = new VariantShredder.Shredding.Rows[fields];
            }

            if (Blocks.Length != fields)
            {
                System.Array.Resize(ref Blocks, fields);
                System.Array.Resize(ref BlockStores, fields);
                RunsTaken = new bool[fields];
            }
        }

        /// <summary>
        /// The shredding of <paramref name="plan"/> the flight's rows of column <paramref name="field"/> are taken into,
        /// begun for them alone, numbered from the block's first: made on the column's first block of the flight and kept.
        /// </summary>
        internal VariantShredder.Shredding BeginBlock(int field, VariantShredding plan, MemoryAccount account)
        {
            VariantShredder.Shredding block = Blocks[field] ??= new VariantShredder.Shredding(plan);
            CanonicalArena store = BlockStores[field] ??= new CanonicalArena(64, account);
            store.ResetKeepingBlocks();
            block.Begin(store, null, counted: true);
            return block;
        }

        /// <summary>
        /// Reads the flight's rows of shredded column <paramref name="field"/>, made ready in <see cref="Shred"/>, into a
        /// shredding of <paramref name="plan"/> of the block's own, its rows numbered from the block's first: what the chain
        /// then adds to the column's chunk (<see cref="VariantShredder.Shredding.Append"/>), and touches nothing else.
        /// </summary>
        /// <remarks>
        /// Each column's item touches its own slot alone, of arrays the flight's prologue sized (<see cref="SizeShreds"/>): the
        /// items of two variant columns of a flight run beside each other. A column whose runs the flight's assembly took
        /// from what the merge's inputs took of them (<see cref="RunsTaken"/>) holds its rows already, and none is read.
        /// </remarks>
        internal void TakeBlock(int field, VariantShredding plan, MemoryAccount account)
        {
            if (!RunsTaken[field])
            {
                BeginBlock(field, plan, account).TakeMore(in Shred[field], 0);
            }
        }

        /// <summary>The flight's items, as the ingest across the lanes numbers them.</summary>
        internal int[] Entries = [];

        /// <summary>Per item, the states it touches, each of which it waits for the flight before to have left.</summary>
        internal int[][] Own = [];

        /// <summary>Per item, the states of other columns it waits for the flight before to have left: the wide columns, for an attachment.</summary>
        internal int[][] After = [];

        /// <summary>Per item, the item of the same flight it waits for, -1 for none: a group of keys waits for its column's read.</summary>
        internal int[] Before = [];

        /// <summary>
        /// Per item, the items of the same flight it waits for besides <see cref="Before"/>, empty for none: a column's cut
        /// waits for the column's ingest, gathering and take of the flight, which touch the states it touches.
        /// </summary>
        internal int[][] Befores = [];

        /// <summary>Per item, how many states and items it still waits for.</summary>
        internal int[] Waiting = [];

        /// <summary>The items.</summary>
        internal int Count;

        /// <summary>The items not yet run.</summary>
        internal int Remaining;

        /// <summary>Takes a batch of <paramref name="rows"/> rows of <paramref name="arena"/>, whose columns' nodes are <paramref name="fieldNodes"/>.</summary>
        internal void Take(CanonicalArena arena, System.ReadOnlySpan<int> fieldNodes, int rows, int filled, CountedRows? counted)
        {
            Arena = arena;
            Rows = rows;
            Filled = filled;
            Counted = counted;
            if (FieldNodes.Length != fieldNodes.Length)
            {
                FieldNodes = new int[fieldNodes.Length];
            }

            fieldNodes.CopyTo(FieldNodes);
        }

        /// <summary>The pass over the keys of variant column <paramref name="field"/>, made on its first batch and kept.</summary>
        internal IndexWriter.KeyPass PassOf(IndexWriter indexes, int field)
        {
            if (Keys.Length <= field)
            {
                System.Array.Resize(ref Keys, field + 1);
            }

            return Keys[field] ??= indexes.NewKeyPass(field);
        }

        /// <summary>Room for <paramref name="count"/> items, kept from one flight to the next.</summary>
        internal void Size(int count)
        {
            if (Entries.Length < count)
            {
                Entries = new int[count];
                Own = new int[count][];
                After = new int[count][];
                Before = new int[count];
                Befores = new int[count][];
                Waiting = new int[count];
            }

            Count = 0;
        }

        /// <summary>Lets go of what the batch held: its rows, which are the caller's, and its counted rows, which are the writer's.</summary>
        internal void Land()
        {
            Arena = null!;
            Counted = null;
            KeysApart = false;
            Staging = null;
            Census = null;
            AttachFrom = null;
            System.Array.Clear(RunsTaken);
        }

        /// <summary>Gives back the key passes' arenas, for a writer done.</summary>
        internal void Release()
        {
            foreach (IndexWriter.KeyPass? pass in Keys)
            {
                pass?.Dispose();
            }

            Keys = [];
            foreach (CanonicalArena? store in BlockStores)
            {
                store?.Reset();
            }

            Blocks = [];
            BlockStores = [];
        }
    }

    /// <summary>Where a flight is on the writer's line.</summary>
    private enum FlightPhase
    {
        /// <summary>Admitted: its block is being assembled, or waits for its prologue.</summary>
        Admitted,

        /// <summary>Its prologue ran: its items wait for their states, or run.</summary>
        Running,

        /// <summary>Its prologue found a block the line does not take: the writer sends it through alone, once every flight before it landed.</summary>
        Alone,

        /// <summary>Every item ran: it waits for its epilogue, in order.</summary>
        Ran,

        /// <summary>Its epilogue ran: its rows wait in transit; a flight that ends a chunk waits for the chunk's emission.</summary>
        Held,

        /// <summary>Landed: what the writer's size reads of it is recorded.</summary>
        Landed,
    }
}
