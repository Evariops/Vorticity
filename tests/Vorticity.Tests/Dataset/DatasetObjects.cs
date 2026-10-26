using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Dataset;

namespace Vorticity.Tests.Dataset;

/// <summary>The objects of a dataset the tests look into.</summary>
internal static class DatasetObjects
{
    /// <summary>The entries of every object above level 0, level by level, each level's in the order of its keys: what the jobs wrote.</summary>
    internal static async IAsyncEnumerable<TreeEntry> AboveLevelZeroAsync(this VortexDataset dataset, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int level = 1; level < dataset.Levels.Count; level++)
        {
            await foreach (TreeEntry entry in dataset.Levels[level].EnumerateAsync(dataset.Pages, cancellationToken))
            {
                yield return entry;
            }
        }
    }
}
