using System;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;

namespace Vorticity.Scan;

/// <summary>File-level pruning, before a scan is even built.</summary>
/// <remarks>
/// An extension for the same reason <see cref="VortexFileScanExtensions"/> is one: file-open does
/// not depend on the scan and pruning machinery (contract §13.1), and a caller that holds a
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
    /// <see langword="false"/> only when the file statistics PROVE that no row matches -- the
    /// same rule the zone maps prune under (docs/08-semantics.md §1). <see langword="true"/> is
    /// "read it and see", and it is what a file without a statistics segment, a non-struct root,
    /// a nested field or a bound of an unusable kind all answer.
    /// </returns>
    /// <remarks>
    /// An engine over many files calls this before opening a scan on each (docs/11 §6.3). The
    /// answer is a superset test: a scan of a file this returns <see langword="true"/> for may
    /// still yield nothing.
    /// </remarks>
    public static bool MayMatch(this VortexFile file, VortexExpr filter)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(filter);
        return FileStatisticsPruner.MayMatch(file, filter);
    }
}
