using Vorticity.Arrays;
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

        /// <summary>Lets go of what the batch held: its rows, which are the caller's, and its counted rows, which are the writer's.</summary>
        internal void Land()
        {
            Arena = null!;
            Counted = null;
            KeysApart = false;
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
}
