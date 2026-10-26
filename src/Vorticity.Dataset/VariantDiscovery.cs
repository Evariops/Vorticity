using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.Scanning;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

/// <summary>One path and class a dataset's variant column holds, and how many values of that class it holds there.</summary>
/// <param name="Path">The keys from the variant's root, none for the root itself.</param>
/// <param name="Class">The class a question reads the values as.</param>
/// <param name="Values">How many values of that class the path holds, in every object.</param>
internal readonly record struct DiscoveredPath(string[] Path, PathClass Class, long Values);

/// <summary>
/// The paths a dataset's variant column holds, with their counts: the union of its objects' path
/// directories, an object without one, or whose directory the budget trimmed, counted from its rows.
/// </summary>
/// <remarks>
/// The counts are those of the rows as their objects were written: a row a delete marks is still
/// counted, as a directory counts it.
/// </remarks>
internal static class VariantDiscovery
{
    /// <summary>Every path and class of <paramref name="column"/> in <paramref name="dataset"/>'s current snapshot, in no order.</summary>
    /// <exception cref="ArgumentException"><paramref name="column"/> is no top-level variant column.</exception>
    internal static async ValueTask<IReadOnlyList<DiscoveredPath>> PathsAsync(VortexDataset dataset, string column, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        Types.DType schema = dataset.Snapshot.Schema.DType;
        int field = schema.IndexOfField(column);
        if (field < 0 || schema.GetField(field).Kind != Types.DTypeKind.Variant)
        {
            throw new ArgumentException($"'{column}' is no top-level variant column of the dataset.", nameof(column));
        }

        Dictionary<(string Path, PathClass Class), (string[] Keys, long Values)> found = new(StringTupleComparer.Instance);
        foreach ((int _, DatasetTree tree) in dataset.Levels.Occupied())
        {
            await foreach (TreeEntry leaf in tree.EnumerateAsync(dataset.Pages, cancellationToken).ConfigureAwait(false))
            {
                ObjectLease lease = await dataset.RentAsync(ObjectEntry.FromBytes(leaf.Value.Span), cancellationToken).ConfigureAwait(false);
                await using (lease.ConfigureAwait(false))
                {
                    VortexFile file = lease.File;
                    int at = file.DType.IndexOfField(column);
                    if (at < 0)
                    {
                        continue;
                    }

                    (VariantDirectory? directory, _, _) = await VariantDirectory.ReadAsync(file, at, cancellationToken).ConfigureAwait(false);
                    if (directory is not { Partial: false })
                    {
                        directory = await CountAsync(file, column, cancellationToken).ConfigureAwait(false);
                    }

                    foreach (PathEntry entry in directory.Entries)
                    {
                        (string, PathClass) key = (string.Join('\u0000', entry.Path), entry.Class);
                        found[key] = found.TryGetValue(key, out (string[] Keys, long Values) held)
                            ? (held.Keys, held.Values + entry.Values)
                            : (entry.Path, entry.Values);
                    }
                }
            }
        }

        List<DiscoveredPath> paths = new List<DiscoveredPath>(found.Count);
        foreach (KeyValuePair<(string Path, PathClass Class), (string[] Keys, long Values)> path in found)
        {
            paths.Add(new DiscoveredPath(path.Value.Keys, path.Key.Class, path.Value.Values));
        }

        return paths;
    }

    /// <summary>The directory of <paramref name="column"/> in <paramref name="file"/>, counted from its rows.</summary>
    private static async ValueTask<VariantDirectory> CountAsync(VortexFile file, string column, CancellationToken cancellationToken)
    {
        PathCensus census = new PathCensus();
        await foreach (RecordBatch batch in file.ScanBuilder().Project(column).ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            census.Add(batch, 0);
        }

        return VariantDirectory.From(census, null);
    }

    /// <summary>Ordinal equality of a joined path and its class.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string Path, PathClass Class)>
    {
        internal static readonly StringTupleComparer Instance = new StringTupleComparer();

        public bool Equals((string Path, PathClass Class) x, (string Path, PathClass Class) y) =>
            x.Class == y.Class && string.Equals(x.Path, y.Path, StringComparison.Ordinal);

        public int GetHashCode((string Path, PathClass Class) obj) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Path), obj.Class);
    }
}
