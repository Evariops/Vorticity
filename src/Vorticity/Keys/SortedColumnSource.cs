using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Keys;

/// <summary>
/// One column of one file, walked in key order. A column the statistics call sorted is already a
/// key index: row order is key order, and the zone map's per-zone minimum and maximum act as
/// separator keys, so a seek bisects the bounds in memory and then bisects inside a single decoded
/// zone, with no index on disk. Nulls sort below every value and are not entries, so the entries
/// are the contiguous rows after the null count, which makes rank and select arithmetic rather
/// than a search. Zone bounds may be widened rather than exact, so a zone that turns out to hold
/// nothing costs a decode and never an answer.
/// </summary>
internal sealed class SortedColumnSource : IAsyncDisposable
{
    private readonly VortexFile _file;
    private readonly LayoutTree _tree;
    private readonly FieldExpr _field;
    private readonly FieldMask _mask;
    private readonly ZoneColumn _zones;
    private readonly long _firstRow;
    private readonly long _rowCount;
    private readonly int _zoneCount;

    private ScanContext? _context;
    private ScanSegments? _held;
    private RetainedChunks? _retained;
    private bool _ownsHeld = true;
    private long _loadedStart = -1;
    private long _loadedEnd = -1;
    private int _column = -1;

    private SortedColumnSource(
        VortexFile file, LayoutTree tree, FieldExpr field, FieldMask mask, ZoneColumn zones,
        FilterLiteralKind kind, long firstRow, long rowCount)
    {
        _file = file;
        _tree = tree;
        _field = field;
        _mask = mask;
        _zones = zones;
        _firstRow = firstRow;
        _rowCount = rowCount;
        _zoneCount = zones.Zones(new RowRange(0, rowCount)).End;
        KeyKind = kind;
    }

    /// <summary>The column's comparison domain, which every seek key must be in.</summary>
    internal FilterLiteralKind KeyKind { get; }

    /// <summary>The non-null rows: every entry, exactly.</summary>
    internal long EntryCount => _rowCount - _firstRow;

    /// <summary>The file row an entry came from.</summary>
    /// <param name="entry">A zero-based entry index below <see cref="EntryCount"/>.</param>
    internal long RowOf(long entry) => _firstRow + entry;

    /// <summary>
    /// Opens a source over <paramref name="path"/>, or says why the column cannot have one.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="path">The column, <c>.</c>-separated for a nested field.</param>
    /// <param name="cancellationToken">Cancels the zone-map read.</param>
    /// <param name="known">The column's zone map, when a pruning pass already read it; null to read it.</param>
    /// <param name="metrics">The scan's sink, to which the zone map and the zones this source decodes are added; null when nobody asks.</param>
    /// <returns>The source, or null with the reason it is not available.</returns>
    internal static async ValueTask<(SortedColumnSource? Source, string? Reason)> OpenAsync(
        VortexFile file, string path, CancellationToken cancellationToken, ZoneColumn? known = null, ScanMetrics? metrics = null)
    {
        DType schema = file.DType;
        if (schema.IsDefault || schema.Kind != DTypeKind.Struct)
        {
            return (null, "the file's root is not a struct, so it has no named columns");
        }

        if (path.Contains('.', StringComparison.Ordinal))
        {
            return (null, "the file statistics are shallow, one entry per top-level field, so a nested column has no is_sorted");
        }

        int index = schema.IndexOfField(path);
        if (index < 0)
        {
            return (null, "the file's schema has no such column");
        }

        if (!file.HasFileStatistics)
        {
            return (null, "the file carries no statistics segment, so nothing says the column is sorted");
        }

        FileStatistics statistics = file.FileStatistics;
        if (index >= statistics.FieldCount)
        {
            return (null, "the statistics segment has no entry for this column");
        }

        FieldStatistics stats = statistics.GetField(index);
        if (!stats.TryGetIsSorted(out bool sorted))
        {
            return (null, "the file statistics do not state is_sorted for this column");
        }

        if (!sorted)
        {
            return (null, "the file statistics say the column is not sorted");
        }

        DType column = schema.GetField(index);
        if (!TryKeyKind(column, out FilterLiteralKind kind))
        {
            return (null, $"a {column.Kind} column has no key order a cursor can walk");
        }

        // The nulls are the leading rows and are not entries, so the source has to know how many
        // there are. A non-nullable column has none by construction; a nullable one whose count is
        // not recorded cannot be walked without finding the boundary by hand, which is a scan.
        long firstRow = 0;
        if (column.IsNullable)
        {
            if (!stats.TryGetStoredNullCount(out ulong nulls))
            {
                return (null, "the column is nullable and its null count is not recorded, so where its entries begin is unknown");
            }

            firstRow = (long)Math.Min(nulls, (ulong)file.RowCount);
        }

        // The zone map is the separator level of the search. It is read the way the pruning pass
        // reads one, so a file without one for this column is refused rather than scanned.
        FieldExpr field = Expr.Field(path);
        LayoutTree tree = file.LayoutTree;
        ZoneColumn? zones = known;
        if (zones is null)
        {
            ZonePruningPlan.PruningPlan plan = await ZonePruningPlan
                .PlanAsync(file, tree, Expr.IsNotNull(field), cancellationToken, steps: null, metrics)
                .ConfigureAwait(false);
            zones = plan.Zones?.Column(path);
        }

        if (zones is null || zones.ZoneLength <= 0)
        {
            return (null, "the column carries no zone map, so a seek would have to decode the whole column");
        }

        FieldMaskBuilder builder = new FieldMaskBuilder();
        Projection.IncludePath(schema, path, builder, nameof(path));
        FieldMask mask = Projection.Create(builder.Build()).RootMask;

        return (
            new SortedColumnSource(file, tree, field, mask, zones, kind, firstRow, file.RowCount) { Metrics = metrics },
            null);
    }

    /// <summary>The scan's sink, to which the zones this source decodes are added; null when nobody asks.</summary>
    private ScanMetrics? Metrics { get; init; }

    /// <summary>
    /// Makes the source read through what a scan holds, rather than through segments and chunks of
    /// its own: the scan then releases what the two no longer need.
    /// </summary>
    /// <param name="held">The scan's segments.</param>
    /// <param name="retained">The scan's decoded chunks.</param>
    internal void Share(ScanSegments held, RetainedChunks retained)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(retained);
        if (_context is not null || _held is not null)
        {
            throw new InvalidOperationException("The source has read already; it shares before its first seek.");
        }

        _held = held;
        _retained = retained;
        _ownsHeld = false;
    }

    /// <summary>
    /// Orders two keys the way this source's own entries are ordered: IEEE for floats, because
    /// <c>is_sorted</c> is computed with IEEE comparisons, so <c>-0.0</c> and <c>+0.0</c> are one
    /// key here (see <see cref="KeyOrder"/>).
    /// </summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    internal static int Compare(FilterLiteral left, FilterLiteral right) => KeyOrder.Ieee(left, right);

    /// <summary>Decodes the zone holding <paramref name="entry"/>, unless it already is.</summary>
    /// <param name="entry">A zero-based entry index below <see cref="EntryCount"/>.</param>
    /// <param name="cancellationToken">Cancels the zone decode this may make.</param>
    /// <remarks>
    /// The whole of what a step costs. When the entry is in the loaded zone this returns without
    /// awaiting anything, and an <c>async ValueTask</c> that never suspends allocates nothing,
    /// which is what keeps a step free of allocation.
    /// </remarks>
    internal ValueTask EnsureEntryAsync(long entry, CancellationToken cancellationToken) =>
        EnsureLoadedAsync(RowOf(entry), cancellationToken);

    /// <summary>
    /// The key of an entry whose zone is loaded, which every positioning call leaves it. Copies a
    /// byte key, as <see cref="FilterLiteral"/> owns its array; <see cref="BytesAt"/> lends it.
    /// </summary>
    /// <param name="entry">A zero-based entry index below <see cref="EntryCount"/>.</param>
    internal FilterLiteral LoadedKey(long entry) => Literal(RowOf(entry));

    /// <summary>
    /// The bytes of entry <paramref name="entry"/>, borrowed from the loaded zone and valid until
    /// the next positioning call. The entry must already be loaded, which every positioning call
    /// leaves it.
    /// </summary>
    /// <param name="entry">A zero-based entry index below <see cref="EntryCount"/>.</param>
    internal ReadOnlySpan<byte> BytesAt(long entry)
    {
        long row = RowOf(entry);
        if (_context is null || row < _loadedStart || row >= _loadedEnd)
        {
            return default;
        }

        CanonicalNode node = _context.Canonical.GetNode(_column);
        return node.Kind == CanonicalKind.VarBinView
            ? LiteralReader.ViewAt(node, (int)(row - _loadedStart))
            : default;
    }

    /// <summary>
    /// The number of entries whose key is below <paramref name="key"/>, which is also the index of
    /// the first entry at or after it: a lower bound, and the key's rank.
    /// </summary>
    /// <param name="key">The sought key.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    internal ValueTask<long> LowerBoundAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(key, strict: false, cancellationToken);

    /// <summary>The index of the first entry whose key is strictly after <paramref name="key"/>.</summary>
    /// <param name="key">The sought key.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    internal ValueTask<long> UpperBoundAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(key, strict: true, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _context?.Dispose();
        _context = null;
        if (_ownsHeld)
        {
            _held?.Dispose();
        }

        _held = null;
        _column = -1;
        _loadedStart = -1;
        _loadedEnd = -1;
        return default;
    }

    /// <summary>
    /// The first entry index whose key is at or after <paramref name="key"/>, or strictly after it.
    /// </summary>
    /// <remarks>
    /// Two levels. The zone bounds are walked first, in memory, for the first zone that may hold
    /// such an entry; they are monotone over a sorted column, so the walk is a bisection, and the
    /// step back afterwards costs nothing and covers a map whose leading zones carry no bound at
    /// all. Then that zone is decoded and bisected. A zone whose stated bound was wider than its
    /// values yields nothing and the search moves to the next one, which is the only case that
    /// decodes twice.
    /// </remarks>
    private async ValueTask<long> BoundAsync(
        FilterLiteral key, bool strict, CancellationToken cancellationToken)
    {
        long entries = EntryCount;
        if (entries <= 0)
        {
            return 0;
        }

        int first = ZoneOf(_firstRow);
        int last = ZoneOf(_rowCount - 1);
        int zone = FirstZoneThatMayHold(key, strict, first, last);
        for (; zone <= last; zone++)
        {
            (long start, long end) = ZoneRows(zone);
            if (end <= start)
            {
                continue;
            }

            await EnsureLoadedAsync(start, cancellationToken).ConfigureAwait(false);
            long low = start;
            long high = end;
            while (low < high)
            {
                long mid = low + ((high - low) >> 1);
                int order = CompareLoaded(mid, key);
                bool before = strict ? order <= 0 : order < 0;
                if (before)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            if (low < end)
            {
                return low - _firstRow;
            }
        }

        return entries;
    }

    /// <summary>
    /// The first zone that may hold an entry at or after <paramref name="key"/>, from the bounds
    /// alone.
    /// </summary>
    private int FirstZoneThatMayHold(FilterLiteral key, bool strict, int first, int last)
    {
        int low = first;
        int high = last;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (Below(mid, key, strict))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        // A zone with no bound claims nothing, so it breaks the monotonicity the bisection
        // assumed; stepping back over such zones restores the answer and stops at the first one.
        while (low > first && !Below(low - 1, key, strict))
        {
            low--;
        }

        return low;
    }

    /// <summary>Whether zone <paramref name="zone"/>'s every value is below the sought key.</summary>
    private bool Below(int zone, FilterLiteral key, bool strict)
    {
        ZoneBounds bounds = _zones.Bounds(zone);
        if (!bounds.HasMax || bounds.Max.Kind != key.Kind)
        {
            return false;
        }

        int order = Compare(bounds.Max, key);
        return strict ? order <= 0 : order < 0;
    }

    private int ZoneOf(long row) =>
        (int)Math.Min(row / _zones.ZoneLength, Math.Max(_zoneCount - 1, 0));

    /// <summary>The rows of a zone that are entries: the zone, clipped to the non-null rows.</summary>
    private (long Start, long End) ZoneRows(int zone)
    {
        long start = Math.Max((long)zone * _zones.ZoneLength, _firstRow);
        long end = Math.Min(((long)zone + 1) * _zones.ZoneLength, _rowCount);
        return (start, Math.Max(end, start));
    }

    /// <summary>
    /// Orders the loaded zone's row against a key without materializing a byte key, so a bisection
    /// over a string column allocates nothing.
    /// </summary>
    private int CompareLoaded(long row, FilterLiteral key)
    {
        CanonicalNode node = _context!.Canonical.GetNode(_column);
        int at = (int)(row - _loadedStart);
        if (node.Kind == CanonicalKind.VarBinView && key.Kind == FilterLiteralKind.Bytes)
        {
            return Math.Sign(LiteralReader.ViewAt(node, at).SequenceCompareTo(key.BytesValue));
        }

        return Compare(Literal(row), key);
    }

    private FilterLiteral Literal(long row) =>
        LiteralReader.TryRead(_context!.Canonical, _column, (int)(row - _loadedStart), out FilterLiteral value)
            ? value
            : FilterLiteral.Null;

    /// <summary>Decodes the zone holding <paramref name="row"/>, unless it already is.</summary>
    private async ValueTask EnsureLoadedAsync(long row, CancellationToken cancellationToken)
    {
        if (_context is not null && row >= _loadedStart && row < _loadedEnd)
        {
            return;
        }

        int zone = ZoneOf(row);
        long start = (long)zone * _zones.ZoneLength;
        long end = Math.Min(start + _zones.ZoneLength, _rowCount);

        if (_context is null)
        {
            _context = new ScanContext(_file);
            if (_retained is not null)
            {
                _context.ShareRetained(_retained);
            }
        }

        _context.ResetBatch();
        _loadedStart = -1;
        _loadedEnd = -1;
        _column = -1;

        // A zone is a range of a chunk, and a search lands on zones that are rarely neighbours: the
        // segment is read once for the chunk, whether the holder is this source's own or the
        // scan's that walks it, and each zone decodes its own rows and nothing around them.
        ScanSegments held = _held ??= new ScanSegments(1);
        long ticket = held.NextTicket();
        _context.Batch = ticket;
        RowRange range = new RowRange(start, end);
        SplitExecution.Alone(_context, range);
        SplitExecution.Register(_context, _tree, in _mask, range);
        held.Claim(_context.Segments, ticket, waiter: null);
        try
        {
            if (ScanMetrics.Note(Metrics, _context.Segments))
            {
                await _file.Segments.ReadManyAsync(_context.Segments, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _context.Segments.Complete();
            }

            held.Publish(_context.Segments, ticket);
        }
        catch
        {
            held.Abandon(ticket);
            throw;
        }

        ScanMetrics.Served(Metrics, _context.Segments);
        int root = SplitExecution.Execute(_context, _tree, in _mask, range, take: null);
        if (_ownsHeld)
        {
            held.Release(ticket);
        }

        _column = FilterEvaluator.Resolve(_context.Canonical, root, _field, (int)(end - start));
        _loadedStart = start;
        _loadedEnd = end;

        if (_file.ReadOptions.VerifyStatistics)
        {
            Verify(zone, start, end);
        }
    }

    /// <summary>
    /// The checks <see cref="VortexReadOptions.VerifyStatistics"/> asks for: the loaded zone's
    /// leading rows null exactly as the null count says, its entries non-null and in IEEE key
    /// order, inside the zone's own stated bounds, and ordered against its neighbours' — at or
    /// after the previous zone's minimum, at or before the next zone's maximum, which holds
    /// however much a bound was widened. Without the option a lie gives a wrong walk and never a
    /// fault; with it, the walk refuses to go on.
    /// </summary>
    private void Verify(int zone, long start, long end)
    {
        ZoneBounds own = _zones.Bounds(zone);
        FilterLiteral previous = FilterLiteral.Null;
        for (long row = start; row < end; row++)
        {
            FilterLiteral key = Literal(row);
            if (row < _firstRow)
            {
                if (key.Kind != FilterLiteralKind.Null)
                {
                    throw Lie($"row {row} holds a value where the null count puts a null");
                }

                continue;
            }

            if (key.Kind != KeyKind || (key.Kind == FilterLiteralKind.Float && double.IsNaN(key.FloatValue)))
            {
                throw Lie($"row {row} holds a null or a NaN, which no sorted column has among its entries");
            }

            if (previous.Kind != FilterLiteralKind.Null && Compare(previous, key) > 0)
            {
                throw Lie($"row {row} is below row {row - 1}");
            }

            if (Outside(own, key))
            {
                throw Lie($"row {row} lies outside the bounds its zone states");
            }

            previous = key;
        }

        long first = Math.Max(start, _firstRow);
        if (first >= end)
        {
            return;
        }

        if (zone > 0 && zone - 1 >= ZoneOf(_firstRow))
        {
            ZoneBounds before = _zones.Bounds(zone - 1);
            if (before.HasMin && before.Min.Kind == KeyKind && Compare(before.Min, Literal(first)) > 0)
            {
                throw Lie($"row {first} is below the minimum zone {zone - 1} states");
            }
        }

        if (zone + 1 < _zoneCount)
        {
            ZoneBounds after = _zones.Bounds(zone + 1);
            if (after.HasMax && after.Max.Kind == KeyKind && Compare(Literal(end - 1), after.Max) > 0)
            {
                throw Lie($"row {end - 1} is above the maximum zone {zone + 1} states");
            }
        }
    }

    private static bool Outside(ZoneBounds bounds, FilterLiteral key) =>
        (bounds.HasMin && bounds.Min.Kind == key.Kind && Compare(key, bounds.Min) < 0)
        || (bounds.HasMax && bounds.Max.Kind == key.Kind && Compare(key, bounds.Max) > 0);

    private static VortexFormatException Lie(string what) =>
        new VortexFormatException($"A column whose statistics say it is sorted does not hold what they claim: {what}.");

    /// <summary>The comparison domain of a column, or none when it has no key order.</summary>
    /// <remarks>
    /// A bool is not a key source, a decimal has no comparison kernel here, and an extension
    /// orders as its storage type -- a timestamp as its integer.
    /// </remarks>
    /// <param name="dtype">The column's dtype.</param>
    /// <param name="kind">Its comparison domain.</param>
    /// <returns>Whether it has one.</returns>
    internal static bool TryKeyKind(DType dtype, out FilterLiteralKind kind)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Primitive when dtype.PType.IsSignedInteger():
                kind = FilterLiteralKind.Signed;
                return true;
            case DTypeKind.Primitive when dtype.PType.IsUnsignedInteger():
                kind = FilterLiteralKind.Unsigned;
                return true;
            case DTypeKind.Primitive when dtype.PType.IsFloat():
                kind = FilterLiteralKind.Float;
                return true;
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                kind = FilterLiteralKind.Bytes;
                return true;
            default:
                kind = FilterLiteralKind.Null;
                return false;
        }
    }
}
