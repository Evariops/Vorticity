// PHASE1-CONTRACTS.md §13.1. The fluent front door of the library.
//
// WHERE `Where` GOES. docs/03-architecture.md §3.4 fixes the order of application as WHERE THEN
// PROJECT: the filter sees columns the projection does not. Phase 1 has no expressions
// (docs/01-scope.md §3) and this class therefore has no Where method - a public
// `throw new InvalidOperationException("not implemented")` would ship in the 1.0 surface, which is
// worse than the method not existing. When Phase 2 adds it, it belongs BETWEEN Rows and the
// projection compilation in ExecuteAsync: the filter's own field mask is unioned into the mask
// handed to RegisterSegments, and the projection is applied to the result.
using System;
using System.Collections.Generic;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Scan;

/// <summary>Builds and launches one scan over an open <see cref="VortexFile"/>.</summary>
/// <remarks>
/// A builder is not thread-safe and is meant to be used and discarded. The
/// <see cref="IAsyncEnumerable{T}"/> it produces is independent of it: mutating the builder
/// afterwards does not change an enumeration already handed out.
/// </remarks>
public sealed class ScanBuilder
{
    private readonly VortexFile _file;
    private FieldMaskBuilder? _fields;
    private RowRange _rows;
    private bool _rowsSet;
    private int _maxBatchRows;
    private int _degree = 1;

    internal ScanBuilder(VortexFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
    }

    /// <summary>
    /// Adds <paramref name="paths"/> to the projection. Calling it twice unions the projections.
    /// </summary>
    /// <param name="paths">
    /// <c>.</c>-separated field names: <c>"id"</c>, <c>"payload.size"</c>.
    /// <b>A Vortex field name may itself contain a <c>.</c> or be empty</b> - corpus
    /// <c>types/struct_field_names</c> has fields named <c>"a.b"</c> and <c>""</c> - so this
    /// overload cannot address every column and no escaping syntax is invented for it. Use
    /// <see cref="ProjectFields(ReadOnlySpan{int})"/> with
    /// <see cref="StructColumn.GetField(int)"/> for those (§13.2).
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> or an element is null.</exception>
    /// <exception cref="ArgumentException">A path does not resolve against the file's schema.</exception>
    public ScanBuilder Project(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Project(new ReadOnlySpan<string>(paths));
    }

    /// <inheritdoc cref="Project(string[])"/>
    public ScanBuilder Project(ReadOnlySpan<string> paths)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            Projection.IncludePath(_file.Schema, paths[i], Fields(), nameof(paths));
        }

        return this;
    }

    /// <summary>Adds root-level field indices to the projection, for callers that resolved names themselves.</summary>
    /// <param name="fieldIndices">0-based indices into the root struct's fields.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The file's root dtype is not a struct.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the root struct.</exception>
    public ScanBuilder ProjectFields(ReadOnlySpan<int> fieldIndices)
    {
        if (fieldIndices.Length == 0)
        {
            return this;
        }

        DType schema = _file.Schema;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            ScanThrow.NonStructRoot(nameof(fieldIndices));
        }

        int fieldCount = schema.FieldCount;
        FieldMaskBuilder builder = Fields();
        for (int i = 0; i < fieldIndices.Length; i++)
        {
            int field = fieldIndices[i];
            if ((uint)field >= (uint)fieldCount)
            {
                ScanThrow.FieldIndexOutOfRange(field, fieldCount, nameof(fieldIndices));
            }

            builder.IncludeField(field);
        }

        return this;
    }

    /// <summary>Restricts the scan to <paramref name="range"/>.</summary>
    /// <param name="range">The wanted rows, in file coordinates.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The range is <b>intersected</b> with the file's rows rather than rejected: a caller asking
    /// for <c>RowRange.FromLength(0, 1_000_000)</c> of a 4096-row file gets 4096 rows, and one
    /// asking wholly past the end gets no batches. <see cref="RowRange"/>'s own constructor already
    /// rejects a negative or inverted range.
    /// </remarks>
    public ScanBuilder Rows(RowRange range)
    {
        _rows = range;
        _rowsSet = true;
        return this;
    }

    /// <summary>Caps the batch size, so a memory-constrained consumer is not at the writer's mercy.</summary>
    /// <param name="maxRows">The largest batch this scan may produce. Must be positive.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <b>It caps; it does not set.</b> The natural batch size is the file's zone length, or
    /// <c>8192</c> when it has no zone map, and a cap above that changes nothing (§13 traps).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxRows"/> is not positive.</exception>
    public ScanBuilder WithMaxBatchRows(int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        _maxBatchRows = maxRows;
        return this;
    }

    /// <summary>Opts in to decoding independent splits concurrently.</summary>
    /// <param name="degree">How many splits may be in flight at once. Must be positive.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// Default <c>1</c>: a library must not appropriate the host's thread pool
    /// (docs/09-contracts.md §2). Each concurrent split gets its <b>own</b>
    /// <see cref="Vorticity.Arrays.ScanContext"/> with its own arenas (contract §2.2); nothing is
    /// shared. Batches are still delivered in row order.
    /// </para>
    /// <para>
    /// I/O concurrency is separate and always on: one <c>ReadManyAsync</c> per batch issues
    /// overlapping reads whatever this is set to. A degree above 1 allocates a task per split,
    /// which the degree-1 path does not.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degree"/> is not positive.</exception>
    public ScanBuilder WithDegreeOfParallelism(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(degree);
        _degree = degree;
        return this;
    }

    /// <summary>Compiles the scan and returns its batches.</summary>
    /// <returns>
    /// A re-enumerable sequence: every <c>GetAsyncEnumerator</c> call starts a fresh scan with its
    /// own <see cref="Vorticity.Arrays.ScanContext"/>.
    /// </returns>
    /// <remarks>
    /// The layout tree is parsed here, once, and shared by every enumerator this call produces: it
    /// is immutable after parsing and therefore safe for concurrent scans (docs/09-contracts.md §1).
    /// </remarks>
    /// <exception cref="VortexFormatException">The file's layout tree is malformed.</exception>
    public IAsyncEnumerable<RecordBatch> ExecuteAsync()
    {
        LayoutTree tree = LayoutTree.Parse(_file);

        long rootRows = tree.Root.RowCount;
        RowRange whole = new RowRange(0, rootRows);
        RowRange rows = _rowsSet ? _rows.Intersect(whole) : whole;

        // WHERE GOES HERE (docs/03-architecture.md §3.4): the filter's field mask is unioned into
        // `mask` below, so RegisterSegments materializes the filter's columns too, and the
        // projection is applied to the filtered result.
        Projection projection = _fields is null ? Projection.All : Projection.Create(_fields.Build());

        long natural = SplitPlan.NaturalBatchRows(tree);
        if (natural > int.MaxValue)
        {
            natural = int.MaxValue;
        }

        long cap = _maxBatchRows > 0 && _maxBatchRows < natural ? _maxBatchRows : natural;
        SplitPlan plan = SplitPlan.Compute(tree, rows, projection.RootMask, cap);

        return new BatchAsyncEnumerable(_file, tree, projection, plan, _degree);
    }

    private FieldMaskBuilder Fields() => _fields ??= new FieldMaskBuilder();
}

/// <summary>The scan entry point.</summary>
public static class VortexFileScanExtensions
{
    /// <summary>Starts building a scan over <paramref name="file"/>.</summary>
    /// <param name="file">An open file.</param>
    /// <returns>A fresh builder.</returns>
    /// <remarks>
    /// An extension rather than a method on <see cref="VortexFile"/> so that file-open does not
    /// depend on scan (contract §13.1).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public static ScanBuilder Scan(this VortexFile file) => new ScanBuilder(file);
}
