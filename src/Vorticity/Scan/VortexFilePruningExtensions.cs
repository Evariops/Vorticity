using System;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Scan;

/// <summary>File-level pruning, before a scan is even built.</summary>
/// <remarks>
/// An extension for the same reason <see cref="VortexFileScanExtensions"/> is one: file-open does
/// not depend on the scan and pruning machinery, and a caller that holds a
/// <see cref="VortexFile"/> gets the method by importing this namespace.
/// </remarks>
public static class VortexFilePruningExtensions
{
    /// <summary>
    /// Whether <paramref name="file"/> may contain a row <paramref name="filter"/> selects,
    /// answered from the footer's file statistics alone, without reading a data segment.
    /// </summary>
    /// <param name="file">An open file.</param>
    /// <param name="filter">The predicate a scan would run.</param>
    /// <returns>
    /// <see langword="false"/> only when the file statistics prove that no row matches, under the
    /// same rule the zone maps prune by. <see langword="true"/> is "read it and see", and it is
    /// what a file without a statistics segment, a non-struct root, a nested field or a bound of
    /// an unusable kind all answer.
    /// </returns>
    /// <remarks>
    /// An engine over many files calls this before opening a scan on each. The answer is a
    /// superset test: a scan of a file this returns <see langword="true"/> for may still yield
    /// nothing.
    /// </remarks>
    public static bool MayMatch(this VortexFile file, VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(filter);
        return FileStatisticsPruner.MayMatch(file, filter);
    }

    /// <summary>
    /// <see cref="MayMatch"/>, then the file-level Bloom filters the file's index directory
    /// carries.
    /// </summary>
    /// <param name="file">An open file.</param>
    /// <param name="filter">The predicate a scan would run.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>
    /// <see langword="false"/> when the file statistics or a file-level filter prove no row matches.
    /// </returns>
    /// <remarks>
    /// The statistics are answered first and read nothing. A filter costs the directory's segment
    /// and one segment per equality-tested column that has one; a file written without the
    /// file-level resolution answers exactly as <see cref="MayMatch"/> does.
    /// </remarks>
    public static async System.Threading.Tasks.ValueTask<bool> MayMatchAsync(
        this VortexFile file, VortexExpr filter, System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(filter);
        return FileStatisticsPruner.MayMatch(file, filter)
            && await Indexes.BloomPruner.FileMayMatchAsync(file, filter, cancellationToken).ConfigureAwait(false);
    }
}
