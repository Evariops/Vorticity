using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Indexes;

/// <summary>One segment of a run, as its fence says.</summary>
/// <param name="Index">Its place among the run's segments.</param>
/// <param name="Start">Its first entry's position in the run.</param>
/// <param name="Bounds">Its entry count and first and last key.</param>
/// <param name="Regions">Its arrays' regions, <c>stride</c> of them.</param>
internal readonly record struct Fence(long Index, long Start, KeySegment Bounds, IndexSegment[] Regions);

/// <summary>What a descent compares a fence's last key with.</summary>
internal interface IFenceProbe
{
    /// <summary>Whether every key up to <paramref name="max"/> comes before the probe.</summary>
    /// <param name="max">A fence's last key.</param>
    bool Below(ReadOnlySpan<byte> max);
}

/// <summary>
/// A page of fences, decoded: a file region like any payload, holding a protobuf message small
/// enough to be read whole, whose fences are either segments or the pages one level down.
/// </summary>
/// <remarks>
/// A page is trusted only after its checksum and then its shape -- the level its parent says, fences
/// in key order, counts that add up to what the parent claims -- since a page that lies would
/// otherwise cost rows. What fails is a format exception, which a pruner turns into no claim and a
/// key source into a refusal.
/// </remarks>
internal sealed class FencePage
{
    private const int PageLevel = 1;
    private const int PageFences = 2;
    private const int FenceEntries = 1;
    private const int FenceMin = 2;
    private const int FenceMax = 3;
    private const int FenceRegions = 4;
    private const int FenceSegments = 5;

    /// <summary>The places a page read keeps of each fence: its message's start and length, its first key's, its last key's.</summary>
    private const int Places = 6;

    // A page a writer builds holds its fences. One read holds its bytes, copied once, and where each fence's message and keys
    // lie in them, a fence's regions parsed when asked: a page of a thousand fences parsed whole was as many arrays of keys
    // and of regions, half of what a question that leaves one identifier out allocated over three objects.
    private readonly KeySegment[]? _bounds;
    private readonly IndexSegment[][]? _regions;
    private readonly byte[]? _bytes;
    private readonly int[]? _places;
    private readonly int _width;

    // The fences of a page read that a caller took whole, made once each: a cursor that steps from segment to segment
    // takes the same fences again, and made them at each step.
    private KeySegment?[]? _boundsMade;
    private IndexSegment[]?[]? _regionsMade;

    private FencePage(
        int level, long[] entryStarts, long[] segmentStarts, KeySegment[]? bounds, IndexSegment[][]? regions, byte[]? bytes, int[]? places, int width)
    {
        Level = level;
        EntryStarts = entryStarts;
        SegmentStarts = segmentStarts;
        _bounds = bounds;
        _regions = regions;
        _bytes = bytes;
        _places = places;
        _width = width;
    }

    /// <summary>0 when its fences are segments; the child pages' level plus one otherwise.</summary>
    internal int Level { get; }

    /// <summary>Where each fence's entries start in the page's subtree, and the subtree's entries last.</summary>
    internal long[] EntryStarts { get; }

    /// <summary>Where each fence's segments start in the page's subtree, and the subtree's segments last.</summary>
    internal long[] SegmentStarts { get; }

    /// <summary>The page's fences.</summary>
    internal int Count => EntryStarts.Length - 1;

    /// <summary>
    /// The deepest level a reader takes. The writer's levels at least halve and its root holds at
    /// least one fence, so level L needs 2^L segments: 64 covers every run a 64-bit count can.
    /// </summary>
    internal const int MaxLevel = 64;

    /// <summary>The entries of fence <paramref name="fence"/>.</summary>
    internal ulong EntriesOf(int fence) => (ulong)(EntryStarts[fence + 1] - EntryStarts[fence]);

    /// <summary>The first key of fence <paramref name="fence"/>.</summary>
    internal ReadOnlySpan<byte> MinOf(int fence) =>
        _bounds is { } bounds ? bounds[fence].Min : _bytes.AsSpan(_places![(Places * fence) + 2], _places[(Places * fence) + 3]);

    /// <summary>The last key of fence <paramref name="fence"/>.</summary>
    internal ReadOnlySpan<byte> MaxOf(int fence) =>
        _bounds is { } bounds ? bounds[fence].Max : _bytes.AsSpan(_places![(Places * fence) + 4], _places[(Places * fence) + 5]);

    /// <summary>The entries and keys of fence <paramref name="fence"/>, its keys copied out of a page read once.</summary>
    internal KeySegment BoundsOf(int fence)
    {
        if (_bounds is { } bounds)
        {
            return bounds[fence];
        }

        _boundsMade ??= new KeySegment?[Count];
        return _boundsMade[fence] ??= new KeySegment(EntriesOf(fence), MinOf(fence).ToArray(), MaxOf(fence).ToArray());
    }

    /// <summary>The regions of fence <paramref name="fence"/>: a segment's arrays, or the child page; parsed once from a page read.</summary>
    internal IndexSegment[] RegionsOf(int fence)
    {
        if (_regions is { } regions)
        {
            return regions[fence];
        }

        _regionsMade ??= new IndexSegment[]?[Count];
        if (_regionsMade[fence] is { } made)
        {
            return made;
        }

        IndexSegment[] named = new IndexSegment[_width];
        int next = 0;
        ProtoReader reader = new ProtoReader(_bytes.AsSpan(_places![Places * fence], _places[(Places * fence) + 1]));
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == FenceRegions && wire == ProtoWireType.LengthDelimited)
            {
                named[next++] = IndexDirectory.ReadSegment(reader.ReadMessage());
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        _regionsMade[fence] = named;
        return named;
    }

    /// <summary>The first region of fence <paramref name="fence"/>: the child page of a fence above level 0.</summary>
    internal IndexSegment FirstRegionOf(int fence)
    {
        if (_regions is { } regions)
        {
            return regions[fence][0];
        }

        ProtoReader reader = new ProtoReader(_bytes.AsSpan(_places![Places * fence], _places[(Places * fence) + 1]));
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == FenceRegions && wire == ProtoWireType.LengthDelimited)
            {
                return IndexDirectory.ReadSegment(reader.ReadMessage());
            }

            reader.SkipField(wire);
        }

        throw Malformed($"fence {fence} names no region");
    }

    /// <summary>The bytes one fence takes in a page of <paramref name="level"/>, its tag and length included.</summary>
    /// <param name="bounds">The fence's entries and keys.</param>
    /// <param name="regions">Its regions.</param>
    /// <param name="segments">The segments under it, written above level 0.</param>
    /// <param name="level">The page's level.</param>
    internal static int FenceBytes(KeySegment bounds, IndexSegment[] regions, long segments, int level)
    {
        int body = 1 + Varint.Size(bounds.Entries) + Field(bounds.Min.Length) + Field(bounds.Max.Length);
        foreach (IndexSegment region in regions)
        {
            body += IndexDirectory.SegmentBytes(region);
        }

        if (level > 0)
        {
            body += 1 + Varint.Size((ulong)segments);
        }

        return Field(body);

        static int Field(int length) => 1 + Varint.Size((ulong)length) + length;
    }

    /// <summary>The bytes a page of <paramref name="level"/> takes besides its fences.</summary>
    /// <param name="level">The page's level.</param>
    internal static int HeaderBytes(int level) => level == 0 ? 0 : 1 + Varint.Size((ulong)level);

    /// <summary>A page over <paramref name="bounds"/>.</summary>
    internal static FencePage Of(int level, KeySegment[] bounds, IndexSegment[][] regions, long[] segments)
    {
        long[] entryStarts = new long[bounds.Length + 1];
        long[] segmentStarts = new long[bounds.Length + 1];
        for (int i = 0; i < bounds.Length; i++)
        {
            entryStarts[i + 1] = checked(entryStarts[i] + (long)bounds[i].Entries);
            segmentStarts[i + 1] = checked(segmentStarts[i] + segments[i]);
        }

        return new FencePage(level, entryStarts, segmentStarts, bounds, regions, null, null, 0);
    }

    /// <summary>Writes the page's fields into the message <paramref name="writer"/> has open.</summary>
    internal void Write(ref ProtoWriter writer)
    {
        writer.WriteUInt32(PageLevel, (uint)Level);
        for (int i = 0; i < Count; i++)
        {
            using ProtoWriter.MessageScope fence = writer.BeginMessage(PageFences);
            writer.WriteUInt64Always(FenceEntries, EntriesOf(i));
            writer.WriteBytesAlways(FenceMin, MinOf(i));
            writer.WriteBytesAlways(FenceMax, MaxOf(i));
            foreach (IndexSegment region in RegionsOf(i))
            {
                IndexDirectory.WriteSegment(ref writer, FenceRegions, region);
            }

            if (Level > 0)
            {
                writer.WriteUInt64Always(FenceSegments, (ulong)(SegmentStarts[i + 1] - SegmentStarts[i]));
            }
        }
    }

    /// <summary>The page as its own bytes.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            Write(ref writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Reads a page and checks its shape: the level expected, <paramref name="stride"/> regions per
    /// segment or one per child, and fences whose keys never go down. Its bytes are copied once and its
    /// fences found in them, each fence's regions parsed again only when asked.
    /// </summary>
    /// <exception cref="VortexFormatException">The page is not one.</exception>
    internal static FencePage Read(ReadOnlySpan<byte> page, int stride, int? level, KeyLayout layout)
    {
        byte[] bytes = page.ToArray();

        // The level, and the fences counted, for arrays of their size.
        uint read = 0;
        int count = 0;
        ProtoReader reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case PageLevel when wire == ProtoWireType.Varint:
                    read = reader.ReadVarint32();
                    break;
                case PageFences when wire == ProtoWireType.LengthDelimited:
                    reader.ReadLengthDelimited();
                    count++;
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (read > MaxLevel || (level is { } expected && read != expected))
        {
            throw Malformed($"a page of level {read} where {level} was expected");
        }

        if (count == 0)
        {
            throw Malformed("a page with no fence");
        }

        int width = read == 0 ? stride : 1;
        int[] places = new int[Places * count];
        long[] entryStarts = new long[count + 1];
        long[] segmentStarts = new long[count + 1];
        int fence = 0;
        reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field != PageFences || wire != ProtoWireType.LengthDelimited)
            {
                reader.SkipField(wire);
                continue;
            }

            int length = reader.ReadLengthDelimited().Length;
            int start = reader.Position - length;
            places[Places * fence] = start;
            places[(Places * fence) + 1] = length;
            (ulong entries, long segments) = ReadFence(bytes, start, length, places, fence, width);
            if (read == 0)
            {
                segments = 1;
            }
            else if (segments <= 0)
            {
                throw Malformed($"fence {fence} covers no segment");
            }

            entryStarts[fence + 1] = checked(entryStarts[fence] + (long)entries);
            segmentStarts[fence + 1] = checked(segmentStarts[fence] + segments);
            fence++;
        }

        FencePage built = new FencePage((int)read, entryStarts, segmentStarts, null, null, bytes, places, width);
        for (int i = 0; i < count; i++)
        {
            ulong entries = built.EntriesOf(i);
            if (layout.Shape != KeyShape.Bytes && entries > 0
                && (built.MinOf(i).Length != layout.Width || built.MaxOf(i).Length != layout.Width))
            {
                throw Malformed($"fence {i} has bounds of the wrong width");
            }

            if (i > 0 && built.EntriesOf(i - 1) > 0 && entries > 0
                && layout.Compare(built.MaxOf(i - 1), built.MinOf(i)) > 0)
            {
                throw Malformed($"fences {i - 1} and {i} are out of key order");
            }
        }

        return built;
    }

    /// <summary>
    /// Finds the keys of the fence at <paramref name="start"/> in <paramref name="bytes"/> and checks its regions, each
    /// read and let go; its entries and the segments it says it covers.
    /// </summary>
    private static (ulong Entries, long Segments) ReadFence(byte[] bytes, int start, int length, int[] places, int fence, int width)
    {
        ulong entries = 0;
        ulong count = 0;
        int regions = 0;
        int at = Places * fence;
        ProtoReader reader = new ProtoReader(bytes.AsSpan(start, length));
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case FenceEntries when wire == ProtoWireType.Varint:
                    entries = reader.ReadVarint();
                    break;
                case FenceMin when wire == ProtoWireType.LengthDelimited:
                {
                    int keyLength = reader.ReadLengthDelimited().Length;
                    places[at + 2] = start + reader.Position - keyLength;
                    places[at + 3] = keyLength;
                    break;
                }

                case FenceMax when wire == ProtoWireType.LengthDelimited:
                {
                    int keyLength = reader.ReadLengthDelimited().Length;
                    places[at + 4] = start + reader.Position - keyLength;
                    places[at + 5] = keyLength;
                    break;
                }

                case FenceRegions when wire == ProtoWireType.LengthDelimited:
                    IndexDirectory.ReadSegment(reader.ReadMessage());
                    regions++;
                    break;
                case FenceSegments when wire == ProtoWireType.Varint:
                    count = reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (entries > long.MaxValue || count > long.MaxValue)
        {
            throw Malformed("a fence's counts overflow");
        }

        if (regions != width)
        {
            throw Malformed($"fence {fence} names {regions} regions where {width} were expected");
        }

        return (entries, (long)count);
    }

    internal static VortexFormatException Malformed(string what) =>
        new VortexFormatException($"A key index fence page is malformed: {what}.");
}

/// <summary>
/// A run's segment table -- for each segment its entry count, its first and last key and the regions
/// of its arrays -- held in memory for a run of few segments and in pages read on the way down for a
/// longer one, so that the directory stays a few kilobytes whatever the run's length.
/// </summary>
internal sealed class FenceTable
{
    private readonly IndexRun? _run;
    private readonly int _stride;
    private readonly KeyLayout _layout;
    private readonly KeySegment[]? _inline;
    private readonly long[]? _inlineStarts;
    private readonly FencePage? _root;
    private readonly byte[][] _dtypes;
    private readonly Dictionary<ulong, FencePage> _pages = [];

    /// <summary>The pages a table keeps once read: two levels of a lookup, many times over.</summary>
    private const int PageCache = 64;

    private FenceTable(IndexRun? run, int stride, KeyLayout layout, KeySegment[]? inline, FencePage? root, byte[][] dtypes)
    {
        _run = run;
        _stride = stride;
        _layout = layout;
        _inline = inline;
        _root = root;
        _dtypes = dtypes;
        if (inline is not null)
        {
            _inlineStarts = new long[inline.Length + 1];
            for (int i = 0; i < inline.Length; i++)
            {
                _inlineStarts[i + 1] = checked(_inlineStarts[i] + (long)inline[i].Entries);
            }

            Entries = _inlineStarts[^1];
            SegmentCount = inline.Length;
        }
        else
        {
            Entries = root!.EntryStarts[^1];
            SegmentCount = root.SegmentStarts[^1];
        }

        FirstMin = [];
        LastMax = [];
        int top = inline?.Length ?? root!.Count;
        for (int i = 0; i < top; i++)
        {
            if ((inline?[i].Entries ?? root!.EntriesOf(i)) > 0)
            {
                FirstMin = inline?[i].Min ?? root!.MinOf(i).ToArray();
                break;
            }
        }

        for (int i = top - 1; i >= 0; i--)
        {
            if ((inline?[i].Entries ?? root!.EntriesOf(i)) > 0)
            {
                LastMax = inline?[i].Max ?? root!.MaxOf(i).ToArray();
                break;
            }
        }
    }

    /// <summary>The run's entries.</summary>
    internal long Entries { get; }

    /// <summary>The run's segments.</summary>
    internal long SegmentCount { get; }

    /// <summary>The run's first key, or empty for an empty run.</summary>
    internal byte[] FirstMin { get; }

    /// <summary>The run's last key, or empty for an empty run.</summary>
    internal byte[] LastMax { get; }

    /// <summary>Whether the table is in pages.</summary>
    internal bool Paged => _root is not null;

    /// <summary>The pages asked of the source so far, for the tests and <c>Explain</c>: a page the file holds in its tail is not one.</summary>
    internal int PagesRead { get; private set; }

    /// <summary>A table over bounds held in memory, with no regions: a dictionary's run.</summary>
    internal static FenceTable InMemory(KeySegment[] bounds, KeyLayout layout) =>
        new FenceTable(null, 0, layout, bounds, null, []);

    /// <summary>
    /// The table of <paramref name="run"/>, or why it has none: inline bounds that match the payload,
    /// or an inline root whose counts agree with the run.
    /// </summary>
    internal static bool TryOpen(IndexRun run, int stride, KeyLayout layout, out FenceTable? table, out string? reason)
    {
        table = null;
        if (KeyRunOptions.TryParseRun(run.OptionBytes, out List<KeySegment> segments))
        {
            if (run.Payload.Count != stride * segments.Count)
            {
                reason = "its segment table does not match its payload";
                return false;
            }

            byte[][] dtypes = new byte[stride][];
            for (int a = 0; a < stride; a++)
            {
                dtypes[a] = a < run.PayloadDTypes.Count ? run.PayloadDTypes[a] : [];
            }

            table = new FenceTable(run, stride, layout, [.. segments], null, dtypes);
        }
        else if (KeyRunOptions.TryParsePagedRun(run.OptionBytes, stride, layout, out FencePage? root, out byte[][]? paged))
        {
            if (root!.Level == 0 || run.Payload.Count != root.Count || paged!.Length != stride)
            {
                reason = "its fence root does not match its payload";
                return false;
            }

            // The directory lists the root's children, and bounds-checked them: they must be the same.
            for (int i = 0; i < root.Count; i++)
            {
                if (root.FirstRegionOf(i) != run.Payload[i])
                {
                    reason = "its fence root names pages its payload does not list";
                    return false;
                }
            }

            table = new FenceTable(run, stride, layout, null, root, paged);
        }
        else
        {
            reason = "its segment table does not parse";
            return false;
        }

        if (run.EntryCount != 0 && run.EntryCount != (ulong)table.Entries)
        {
            reason = $"it lists {run.EntryCount} entries and its segments {table.Entries}";
            table = null;
            return false;
        }

        foreach (KeySegment bounds in table._inline ?? [])
        {
            bool widths = layout.Shape == KeyShape.Bytes
                || bounds.Entries == 0
                || (bounds.Min.Length == layout.Width && bounds.Max.Length == layout.Width);
            if (!widths || bounds.Entries > int.MaxValue)
            {
                reason = "a segment's bounds or size do not fit the column";
                table = null;
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>The serialized dtype of array <paramref name="array"/> of every segment.</summary>
    internal ReadOnlySpan<byte> DTypeOf(int array) => array < _dtypes.Length ? _dtypes[array] : [];

    /// <summary>Whether a sorted run's rows are 64-bit.</summary>
    internal bool WideRows => _stride == KeyRunOptions.SortedStride && KeyRunOptions.IsWideRowsDType(DTypeOf(1));

    /// <summary>Segment <paramref name="index"/>.</summary>
    /// <param name="source">Where the run's regions are read: <c>VortexFile.IndexSourceOf(run)</c>.</param>
    /// <param name="index">The segment's place in the run.</param>
    /// <param name="cancellationToken">Cancels the page reads.</param>
    internal ValueTask<Fence> GetAsync(ISegmentReader source, long index, CancellationToken cancellationToken)
    {
        if (_inline is not null)
        {
            int i = checked((int)index);
            return new ValueTask<Fence>(new Fence(index, _inlineStarts![i], _inline[i], InlineRegions(i)));
        }

        return DescendAsync(source, index, bySegment: true, cancellationToken);
    }

    /// <summary>The segment holding the entry at <paramref name="position"/>.</summary>
    /// <param name="source">Where the file's index regions are read.</param>
    /// <param name="position">An entry's place in the run.</param>
    /// <param name="cancellationToken">Cancels the page reads.</param>
    internal ValueTask<Fence> OfPositionAsync(ISegmentReader source, long position, CancellationToken cancellationToken)
    {
        if (_inline is not null)
        {
            int s = Upper(_inlineStarts!, position);
            return new ValueTask<Fence>(new Fence(s, _inlineStarts![s], _inline[s], InlineRegions(s)));
        }

        return DescendAsync(source, position, bySegment: false, cancellationToken);
    }

    /// <summary>
    /// The first segment, with entries, whose last key does not come before <paramref name="probe"/>;
    /// <see cref="SegmentCount"/> when there is none.
    /// </summary>
    /// <param name="source">Where the file's index regions are read.</param>
    /// <param name="probe">What a fence's last key is compared with.</param>
    /// <param name="cancellationToken">Cancels the page reads.</param>
    internal async ValueTask<long> LowerBoundAsync<TProbe>(ISegmentReader source, TProbe probe, CancellationToken cancellationToken)
        where TProbe : IFenceProbe
    {
        if (_inline is not null)
        {
            return LowerBound(_inline, probe);
        }

        FencePage page = _root!;
        long baseSegment = 0;
        while (true)
        {
            int i = LowerBound(page, probe);
            if (i == page.Count)
            {
                return baseSegment + page.SegmentStarts[i];
            }

            if (page.Level == 0)
            {
                return baseSegment + i;
            }

            baseSegment += page.SegmentStarts[i];
            page = await PageAsync(source, page.FirstRegionOf(i), page.Level - 1, page.SegmentStarts[i + 1] - page.SegmentStarts[i], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The first fence of <paramref name="page"/> whose last key the probe does not pass, its keys read where the page holds them.</summary>
    private static int LowerBound<TProbe>(FencePage page, TProbe probe)
        where TProbe : IFenceProbe
    {
        int low = 0;
        int high = page.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (page.EntriesOf(mid) == 0 || probe.Below(page.MaxOf(mid)))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static int LowerBound<TProbe>(KeySegment[] bounds, TProbe probe)
        where TProbe : IFenceProbe
    {
        int low = 0;
        int high = bounds.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (bounds[mid].Entries == 0 || probe.Below(bounds[mid].Max))
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private async ValueTask<Fence> DescendAsync(ISegmentReader source, long target, bool bySegment, CancellationToken cancellationToken)
    {
        FencePage page = _root!;
        long baseSegment = 0;
        long baseEntry = 0;
        while (true)
        {
            long[] starts = bySegment ? page.SegmentStarts : page.EntryStarts;
            int i = Upper(starts, target);
            if (page.Level == 0)
            {
                return new Fence(baseSegment + i, baseEntry + page.EntryStarts[i], page.BoundsOf(i), page.RegionsOf(i));
            }

            target -= starts[i];
            baseSegment += page.SegmentStarts[i];
            baseEntry += page.EntryStarts[i];
            page = await PageAsync(source, page.FirstRegionOf(i), page.Level - 1, page.SegmentStarts[i + 1] - page.SegmentStarts[i], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The last slot whose start is at or before <paramref name="target"/>, past empty ones.</summary>
    private static int Upper(long[] starts, long target)
    {
        int low = 0;
        int high = starts.Length - 2;
        while (low < high)
        {
            int mid = (low + high + 1) >>> 1;
            if (starts[mid] <= target)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        while (low + 1 < starts.Length - 1 && starts[low + 1] <= target)
        {
            low++;
        }

        return Math.Max(low, 0);
    }

    /// <summary>
    /// A segment's regions, built once per segment, so that a step crossing into a segment already
    /// loaded allocates nothing.
    /// </summary>
    private IndexSegment[] InlineRegions(int segment)
    {
        if (_run is null || _stride == 0)
        {
            return [];
        }

        _inlineRegions ??= new IndexSegment[]?[_inline!.Length];
        if (_inlineRegions[segment] is { } built)
        {
            return built;
        }

        IndexSegment[] regions = new IndexSegment[_stride];
        for (int a = 0; a < _stride; a++)
        {
            regions[a] = _run.Payload[(segment * _stride) + a];
        }

        _inlineRegions[segment] = regions;
        return regions;
    }

    private IndexSegment[]?[]? _inlineRegions;

    /// <summary>The region of the page a read names, which a reader that keeps index regions keeps; one for the table's reads.</summary>
    private IndexSegment[]? _named;

    /// <summary>A child page, from the table's own cache or the file.</summary>
    private async ValueTask<FencePage> PageAsync(
        ISegmentReader source, IndexSegment region, int level, long segments, CancellationToken cancellationToken)
    {
        if (_pages.TryGetValue(region.Offset, out FencePage? cached))
        {
            return cached;
        }

        FencePage page;
        bool asked;
        using (SegmentRequestSet requests = new SegmentRequestSet(1))
        {
            int slot = requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            asked = Scanning.ScanCounters.Unread(requests, source, out _) > 0;
            IndexSegment[] named = _named ??= new IndexSegment[1];
            named[0] = region;
            await source.ReadRegionsAsync(requests, named, cancellationToken).ConfigureAwait(false);
            VortexBuffer bytes = requests.GetBuffer(slot);
            if (!region.Holds(bytes.Span))
            {
                throw FencePage.Malformed("a page's bytes do not match its checksum");
            }

            page = FencePage.Read(bytes.Span, _stride, level, _layout);
        }

        if (page.SegmentStarts[^1] != segments)
        {
            throw FencePage.Malformed($"a page covers {page.SegmentStarts[^1]} segments where its parent says {segments}");
        }

        PagesRead += asked ? 1 : 0;
        Diagnostics.VortexEventSource.RunsRead(1);
        if (_pages.Count >= PageCache)
        {
            _pages.Clear();
        }

        _pages[region.Offset] = page;
        return page;
    }
}
