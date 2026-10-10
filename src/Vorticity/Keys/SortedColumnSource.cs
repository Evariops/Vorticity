using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        VortexFile file, string path, CancellationToken cancellationToken, ZoneColumn? known = null, ScanCounters? metrics = null)
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
        ScanProjection.IncludePath(schema, path, builder, nameof(path));
        FieldMask mask = ScanProjection.Create(builder.Build()).RootMask;

        return (
            new SortedColumnSource(file, tree, field, mask, zones, kind, firstRow, file.RowCount) { Metrics = metrics },
            null);
    }

    /// <summary>The scan's sink, to which the zones this source decodes are added; null when nobody asks.</summary>
    private ScanCounters? Metrics { get; init; }

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
    /// The entry just past <paramref name="entry"/>'s key, going forward or back -- the first after
    /// its entries, or the last before them -- found in the loaded zone, which stays loaded for it;
    /// -1 when the key reaches the zone's edge that way, where the zone cannot tell.
    /// </summary>
    /// <param name="entry">An entry whose zone is loaded, as every positioning call leaves it.</param>
    /// <param name="forward">Whether to go past the key's last entry rather than before its first.</param>
    /// <remarks>
    /// O(log k) comparisons for a key held by k entries, against the two bisections of a bound, each
    /// made where the keys lie: a primitive's values as they are, a string's bytes in place.
    /// </remarks>
    internal long PastKeyInZone(long entry, bool forward)
    {
        CanonicalArena arena = _context!.Canonical;
        CanonicalNode node = arena.GetNode(ComparisonKernels.Unwrap(arena, _column));
        int at = (int)(RowOf(entry) - _loadedStart);
        int edge = (int)((forward ? Math.Min(_loadedEnd, _rowCount) - 1 : Math.Max(_loadedStart, _firstRow)) - _loadedStart);
        int past = node.Kind switch
        {
            CanonicalKind.Primitive => node.PType switch
            {
                PType.F16 => PastValue<Half>(node.Values.Span, at, edge, forward),
                PType.F32 => PastValue<float>(node.Values.Span, at, edge, forward),
                PType.F64 => PastValue<double>(node.Values.Span, at, edge, forward),
                _ => node.PType.ByteWidth() switch
                {
                    1 => PastValue<byte>(node.Values.Span, at, edge, forward),
                    2 => PastValue<ushort>(node.Values.Span, at, edge, forward),
                    4 => PastValue<uint>(node.Values.Span, at, edge, forward),
                    _ => PastValue<ulong>(node.Values.Span, at, edge, forward),
                },
            },
            CanonicalKind.VarBinView => PastView(node, at, edge, forward),

            // Any other form is left to the bound: a constant, whose one key fills the zone, or one
            // this source does not read in place.
            _ => -1,
        };

        return past < 0 ? -1 : _loadedStart + past - _firstRow;
    }

    /// <summary>A primitive zone's gallop, one per width.</summary>
    /// <remarks>
    /// Out of line so that the dispatch stays small: each gallop inlined into it gets stack slots of
    /// its own, which every call then clears, whichever width it takes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PastValue<T>(ReadOnlySpan<byte> values, int at, int edge, bool forward)
        where T : unmanaged, IEquatable<T> =>
        KeyGallop.Past(new Values<T>(values, at), at, edge, forward);

    /// <summary>A string zone's gallop.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PastView(CanonicalNode node, int at, int edge, bool forward) =>
        KeyGallop.Past(new Views(node, at), at, edge, forward);

    /// <summary>
    /// The number of entries whose key is below <paramref name="key"/>, which is also the index of
    /// the first entry at or after it: a lower bound, and the key's rank.
    /// </summary>
    /// <param name="key">The sought key.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    internal ValueTask<long> LowerBoundAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(new LiteralKey(this, key), strict: false, cancellationToken);

    /// <summary>The index of the first entry whose key is strictly after <paramref name="key"/>.</summary>
    /// <param name="key">The sought key.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    internal ValueTask<long> UpperBoundAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        BoundAsync(new LiteralKey(this, key), strict: true, cancellationToken);

    /// <summary>
    /// <see cref="LowerBoundAsync"/>, or <see cref="UpperBoundAsync"/> when <paramref name="strict"/>,
    /// of the byte key <paramref name="lender"/> is on, compared where it lends it.
    /// </summary>
    /// <param name="lender">A source on an entry, whose keys are bytes.</param>
    /// <param name="strict">Whether the bound is past the key's entries.</param>
    /// <param name="cancellationToken">Cancels the decodes this makes.</param>
    internal ValueTask<long> BoundOfAsync(KeySource lender, bool strict, CancellationToken cancellationToken) =>
        BoundAsync(new LentKey(this, lender), strict, cancellationToken);

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
    private async ValueTask<long> BoundAsync<TKey>(TKey key, bool strict, CancellationToken cancellationToken)
        where TKey : struct, ISoughtKey
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
                int order = key.OrderLoaded(mid);
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
    private int FirstZoneThatMayHold<TKey>(TKey key, bool strict, int first, int last)
        where TKey : struct, ISoughtKey
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
    private bool Below<TKey>(int zone, TKey key, bool strict)
        where TKey : struct, ISoughtKey
    {
        ZoneBounds bounds = _zones.Bounds(zone);
        if (!bounds.HasMax || bounds.Max.Kind != key.Kind)
        {
            return false;
        }

        int order = key.Order(bounds.Max);
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

    /// <summary>Orders the loaded zone's row against a byte key.</summary>
    private int CompareLoaded(long row, ReadOnlySpan<byte> key)
    {
        CanonicalNode node = _context!.Canonical.GetNode(_column);
        ReadOnlySpan<byte> value = node.Kind == CanonicalKind.VarBinView
            ? LiteralReader.ViewAt(node, (int)(row - _loadedStart))
            : Literal(row).BytesValue;
        return Math.Sign(value.SequenceCompareTo(key));
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
        VortexFile.FillFromTail(_file.Segments, _context.Segments);
        held.Claim(_context.Segments, ticket, waiter: null);
        try
        {
            if (ScanCounters.Note(Metrics, _context.Segments))
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

        ScanCounters.Served(Metrics, _context.Segments);
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

    /// <summary>
    /// A primitive zone's values as they lie, against the key of one row: an integer's bits, a
    /// float's IEEE value, which holds <c>-0.0</c> and <c>+0.0</c> as one key the way
    /// <see cref="Compare"/> does.
    /// </summary>
    private readonly ref struct Values<T> : KeyGallop.IKeyed
        where T : unmanaged, IEquatable<T>
    {
        private readonly ReadOnlySpan<T> _values;
        private readonly T _key;

        internal Values(ReadOnlySpan<byte> bytes, int row)
        {
            _values = MemoryMarshal.Cast<byte, T>(bytes);
            _key = _values[row];
        }

        public bool Holds(int index) => _values[index].Equals(_key);
    }

    /// <summary>A string zone's views against the key of one row, their bytes compared in place.</summary>
    private readonly ref struct Views : KeyGallop.IKeyed
    {
        private readonly CanonicalNode _node;
        private readonly ReadOnlySpan<byte> _key;

        internal Views(CanonicalNode node, int row)
        {
            _node = node;
            _key = LiteralReader.ViewAt(node, row);
        }

        public bool Holds(int index) => LiteralReader.ViewAt(_node, index).SequenceEqual(_key);
    }

    /// <summary>A key a bound is sought for, ordered against the zones' bounds and the loaded rows.</summary>
    private interface ISoughtKey
    {
        /// <summary>The key's domain, which a bound must share to say anything.</summary>
        FilterLiteralKind Kind { get; }

        /// <summary>The sign of a zone's bound less the key.</summary>
        int Order(FilterLiteral bound);

        /// <summary>The sign of the loaded zone's row less the key.</summary>
        int OrderLoaded(long row);
    }

    /// <summary>A key sought as a literal, as a seek names it.</summary>
    private readonly struct LiteralKey(SortedColumnSource source, FilterLiteral key) : ISoughtKey
    {
        public FilterLiteralKind Kind => key.Kind;

        public int Order(FilterLiteral bound) => Compare(bound, key);

        public int OrderLoaded(long row) => source.CompareLoaded(row, key);
    }

    /// <summary>A byte key sought where another source lends it, on its current entry.</summary>
    private readonly struct LentKey(SortedColumnSource source, KeySource lender) : ISoughtKey
    {
        public FilterLiteralKind Kind => FilterLiteralKind.Bytes;

        public int Order(FilterLiteral bound) => Math.Sign(bound.BytesValue.SequenceCompareTo(lender.KeyBytes));

        public int OrderLoaded(long row) => source.CompareLoaded(row, lender.KeyBytes);
    }
}
