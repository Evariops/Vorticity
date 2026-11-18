using Vorticity.Arrays;
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

        /// <summary>The flight's items, as the ingest across the lanes numbers them.</summary>
        internal int[] Entries = [];

        /// <summary>Per item, the states it touches, each of which it waits for the flight before to have left.</summary>
        internal int[][] Own = [];

        /// <summary>Per item, the states of other columns it waits for the flight before to have left: the wide columns, for an attachment.</summary>
        internal int[][] After = [];

        /// <summary>Per item, the item of the same flight it waits for, -1 for none: a group of keys waits for its column's read.</summary>
        internal int[] Before = [];

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
        }

        /// <summary>Gives back the key passes' arenas, for a writer done.</summary>
        internal void Release()
        {
            foreach (IndexWriter.KeyPass? pass in Keys)
            {
                pass?.Dispose();
            }

            Keys = [];
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
