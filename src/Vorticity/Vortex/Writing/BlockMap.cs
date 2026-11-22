using System.Collections.Generic;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>
/// The runs of one block of a merge's output, in the key's order, where they lie in the merge's inputs' batches: what the
/// merge hands its writer, a block at a time, rather than a run at a time.
/// </summary>
/// <remarks>
/// The merge fills a map up to the writer's block (<see cref="VortexFileWriter.MapRows"/>), cutting the run that crosses
/// it, and notes each run in its ledger as it adds it, the map holding them there until the writer takes them over
/// (<see cref="VortexFileWriter.WriteBlockAsync"/>). The rows a run names are sized at the bytes a row of its batch names,
/// as the writer's staging sized them, so that the block's staging counts the same bytes either way.
/// </remarks>
internal sealed class BlockMap
{
    /// <summary>Its runs, in order.</summary>
    internal List<MapRun> Runs { get; } = [];

    /// <summary>The rows its runs name.</summary>
    internal int Rows { get; private set; }

    /// <summary>The bytes its runs name, each run's rows at the bytes a row of its batch names.</summary>
    internal long Bytes { get; private set; }

    /// <summary>The number its first run took in the merge's ledger, -1 while it holds none.</summary>
    internal long FirstRun = -1;

    /// <summary>Adds rows <paramref name="row"/> on of node <paramref name="root"/> of <paramref name="source"/>, a merge's input batch.</summary>
    internal void Add(CanonicalArena source, int root, int row, int rows, double rowBytes)
    {
        Runs.Add(new MapRun(source, root, row, rows));
        Rows += rows;
        Bytes += (long)(rows * rowBytes);
    }

    /// <summary>Forgets its runs, once the writer took them over.</summary>
    internal void Clear()
    {
        Runs.Clear();
        Rows = 0;
        Bytes = 0;
    }
}

/// <summary>Rows <see cref="Row"/> on of node <see cref="Root"/> of <see cref="Source"/>, a merge's input batch.</summary>
internal readonly record struct MapRun(CanonicalArena Source, int Root, int Row, int Rows);
