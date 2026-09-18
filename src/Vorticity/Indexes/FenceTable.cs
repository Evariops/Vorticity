// The segment table of a locating run, bounded whatever the run's length - docs/13-dataset.md §6.3.
//
// A RUN'S FENCES ARE ITS SEGMENTS' BOUNDS: for each segment its entry count, its first and last key
// and the regions of its arrays. A run of a few segments keeps them where it always did -- the
// bounds in the run's options, the regions in the directory -- and nothing about those files moves.
// A run of more (one merged run over a ten-gibibyte object has thousands) keeps them in FENCE PAGES:
// file regions like any payload, each a protobuf message of at most about 64 KiB, arranged as a
// tree whose top page is inlined in the run's options. A lookup reads the pages on its way down --
// two for any run this format can hold -- and the directory stays a few kilobytes whatever the
// run's length.
//
//   message KeyRunOptions {              // version 2, a paged run
//     uint32 version = 1;                 // 2
//     FencePage root = 3;                 // the top page, inline
//     repeated bytes stride_dtypes = 4;   // the serialized dtype of each array of a segment
//   }
//   message FencePage {
//     uint32 level = 1;                   // 0: its fences are segments; > 0: pages of level - 1
//     repeated Fence fences = 2;          // in key order
//   }
//   message Fence {
//     uint64 entries = 1;  bytes min = 2;  bytes max = 3;
//     repeated Segment regions = 4;       // level 0: the segment's `stride` arrays; above: the child page
//     uint64 segments = 5;                // above level 0: the segments under the child
//   }
//
// A PAGE IS READ LIKE EVERY REGION: its checksum first (13 §7), then its shape -- the level its
// parent says, fences in order, counts that add up to what the parent claims. A page that fails is
// a `VortexFormatException`, which a pruner turns into "no claim" and a key source into a refusal.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
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

/// <summary>A page of fences, decoded.</summary>
internal sealed class FencePage
{
    /// <summary>0 when its fences are segments; the child pages' level plus one otherwise.</summary>
    internal required int Level { get; init; }

    /// <summary>Each fence's entries and first and last key.</summary>
    internal required KeySegment[] Bounds { get; init; }

    /// <summary>Each fence's regions: a segment's arrays, or the child page.</summary>
    internal required IndexSegment[][] Regions { get; init; }

    /// <summary>Where each fence's entries start in the page's subtree, and the subtree's entries last.</summary>
    internal required long[] EntryStarts { get; init; }

    /// <summary>Where each fence's segments start in the page's subtree, and the subtree's segments last.</summary>
    internal required long[] SegmentStarts { get; init; }

    /// <summary>The page's fences.</summary>
    internal int Count => Bounds.Length;

    private const int PageLevel = 1;
    private const int PageFences = 2;
    private const int FenceEntries = 1;
    private const int FenceMin = 2;
    private const int FenceMax = 3;
    private const int FenceRegions = 4;
    private const int FenceSegments = 5;

    /// <summary>
    /// The deepest level a reader takes. The writer's levels at least halve and its root holds at
    /// least one fence, so level L needs 2^L segments: 64 covers every run a 64-bit count can.
    /// </summary>
    internal const int MaxLevel = 64;

    /// <summary>The bytes one fence takes in a page of <paramref name="level"/>, its tag and length included.</summary>
    /// <param name="bounds">The fence's entries and keys.</param>
    /// <param name="regions">Its regions.</param>
    /// <param name="segments">The segments under it, written above level 0.</param>
    /// <param name="level">The page's level.</param>
    internal static int FenceBytes(KeySegment bounds, IndexSegment[] regions, long segments, int level)
    {
        int body = 1 + ProtoWire.VarintSize(bounds.Entries) + Field(bounds.Min.Length) + Field(bounds.Max.Length);
        foreach (IndexSegment region in regions)
        {
            body += IndexDirectory.SegmentBytes(region);
        }

        if (level > 0)
        {
            body += 1 + ProtoWire.VarintSize((ulong)segments);
        }

        return Field(body);

        static int Field(int length) => 1 + ProtoWire.VarintSize((ulong)length) + length;
    }

    /// <summary>The bytes a page of <paramref name="level"/> takes besides its fences.</summary>
    /// <param name="level">The page's level.</param>
    internal static int HeaderBytes(int level) => level == 0 ? 0 : 1 + ProtoWire.VarintSize((ulong)level);

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

        return new FencePage
        {
            Level = level,
            Bounds = bounds,
            Regions = regions,
            EntryStarts = entryStarts,
            SegmentStarts = segmentStarts,
        };
    }

    /// <summary>Writes the page's fields into the message <paramref name="writer"/> has open.</summary>
    internal void Write(ref ProtoWriter writer)
    {
        writer.WriteUInt32(PageLevel, (uint)Level);
        for (int i = 0; i < Count; i++)
        {
            using ProtoWriter.MessageScope fence = writer.BeginMessage(PageFences);
            writer.WriteUInt64Always(FenceEntries, Bounds[i].Entries);
            writer.WriteBytesAlways(FenceMin, Bounds[i].Min);
            writer.WriteBytesAlways(FenceMax, Bounds[i].Max);
            foreach (IndexSegment region in Regions[i])
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
    /// Parses a page and checks its shape: the level expected, <paramref name="stride"/> regions per
    /// segment or one per child, and fences whose keys never go down.
    /// </summary>
    /// <exception cref="VortexFormatException">The page is not one.</exception>
    internal static FencePage Read(ProtoReader reader, int stride, int? level, KeyLayout layout)
    {
        uint read = 0;
        List<KeySegment> bounds = [];
        List<IndexSegment[]> regions = [];
        List<long> segments = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case PageLevel when wire == ProtoWireType.Varint:
                    read = reader.ReadVarint32();
                    break;
                case PageFences when wire == ProtoWireType.LengthDelimited:
                    ReadFence(reader.ReadMessage(), bounds, regions, segments);
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

        if (bounds.Count == 0)
        {
            throw Malformed("a page with no fence");
        }

        int width = read == 0 ? stride : 1;
        for (int i = 0; i < bounds.Count; i++)
        {
            if (regions[i].Length != width)
            {
                throw Malformed($"fence {i} names {regions[i].Length} regions where {width} were expected");
            }

            if (read == 0)
            {
                segments[i] = 1;
            }
            else if (segments[i] <= 0)
            {
                throw Malformed($"fence {i} covers no segment");
            }

            if (layout.Shape != KeyShape.Bytes && bounds[i].Entries > 0
                && (bounds[i].Min.Length != layout.Width || bounds[i].Max.Length != layout.Width))
            {
                throw Malformed($"fence {i} has bounds of the wrong width");
            }

            if (i > 0 && bounds[i - 1].Entries > 0 && bounds[i].Entries > 0
                && layout.Compare(bounds[i - 1].Max, bounds[i].Min) > 0)
            {
                throw Malformed($"fences {i - 1} and {i} are out of key order");
            }
        }

        return Of((int)read, [.. bounds], [.. regions], [.. segments]);
    }

    private static void ReadFence(ProtoReader reader, List<KeySegment> bounds, List<IndexSegment[]> regions, List<long> segments)
    {
        ulong entries = 0;
        byte[] min = [];
        byte[] max = [];
        ulong count = 0;
        List<IndexSegment> named = [];
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case FenceEntries when wire == ProtoWireType.Varint:
                    entries = reader.ReadVarint();
                    break;
                case FenceMin when wire == ProtoWireType.LengthDelimited:
                    min = reader.ReadLengthDelimited().ToArray();
                    break;
                case FenceMax when wire == ProtoWireType.LengthDelimited:
                    max = reader.ReadLengthDelimited().ToArray();
                    break;
                case FenceRegions when wire == ProtoWireType.LengthDelimited:
                    named.Add(IndexDirectory.ReadSegment(reader.ReadMessage()));
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

        bounds.Add(new KeySegment(entries, min, max));
        regions.Add([.. named]);
        segments.Add((long)count);
    }

    internal static VortexFormatException Malformed(string what) =>
        new VortexFormatException($"A key index fence page is malformed: {what}.");
}

/// <summary>
/// A run's segment table, in memory for a run of few segments and in pages read on the way down
/// for a longer one.
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
        KeySegment[] top = inline ?? root!.Bounds;
        foreach (KeySegment bounds in top)
        {
            if (bounds.Entries > 0)
            {
                FirstMin = bounds.Min;
                break;
            }
        }

        for (int i = top.Length - 1; i >= 0; i--)
        {
            if (top[i].Entries > 0)
            {
                LastMax = top[i].Max;
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

    /// <summary>The pages read so far, for the tests and <c>Explain</c>.</summary>
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
                if (root.Regions[i][0] != run.Payload[i])
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

    /// <summary>Whether a sorted run's rows are 64-bit (13 §6.1).</summary>
    internal bool WideRows => _stride == KeyRunOptions.SortedStride && KeyRunOptions.IsWideRowsDType(DTypeOf(1));

    /// <summary>Segment <paramref name="index"/>.</summary>
    /// <param name="source">Where the run's regions are read: <c>VortexFile.IndexSourceOf(run)</c>.</param>
    /// <param name="index">The segment's place in the run.</param>
    /// <param name="cancellationToken">Cancels the page reads.</param>
    internal ValueTask<Fence> GetAsync(ISegmentSource source, long index, CancellationToken cancellationToken)
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
    internal ValueTask<Fence> OfPositionAsync(ISegmentSource source, long position, CancellationToken cancellationToken)
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
    internal async ValueTask<long> LowerBoundAsync<TProbe>(ISegmentSource source, TProbe probe, CancellationToken cancellationToken)
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
            int i = LowerBound(page.Bounds, probe);
            if (i == page.Count)
            {
                return baseSegment + page.SegmentStarts[i];
            }

            if (page.Level == 0)
            {
                return baseSegment + i;
            }

            baseSegment += page.SegmentStarts[i];
            page = await PageAsync(source, page.Regions[i][0], page.Level - 1, page.SegmentStarts[i + 1] - page.SegmentStarts[i], cancellationToken).ConfigureAwait(false);
        }
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

    private async ValueTask<Fence> DescendAsync(ISegmentSource source, long target, bool bySegment, CancellationToken cancellationToken)
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
                return new Fence(baseSegment + i, baseEntry + page.EntryStarts[i], page.Bounds[i], page.Regions[i]);
            }

            target -= starts[i];
            baseSegment += page.SegmentStarts[i];
            baseEntry += page.EntryStarts[i];
            page = await PageAsync(source, page.Regions[i][0], page.Level - 1, page.SegmentStarts[i + 1] - page.SegmentStarts[i], cancellationToken).ConfigureAwait(false);
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
    /// A segment's regions, built once per segment: a step that crosses into a loaded segment
    /// allocates nothing (12 §11).
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

    /// <summary>A child page, from the table's own cache or the file.</summary>
    private async ValueTask<FencePage> PageAsync(
        ISegmentSource source, IndexSegment region, int level, long segments, CancellationToken cancellationToken)
    {
        if (_pages.TryGetValue(region.Offset, out FencePage? cached))
        {
            return cached;
        }

        FencePage page;
        using (SegmentRequestSet requests = new SegmentRequestSet(1))
        {
            int slot = requests.Add(new SegmentSpec(region.Offset, region.Length, region.AlignmentExponent, 0, 0));
            await source.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            VortexBuffer bytes = requests.GetBuffer(slot);
            if (!region.Holds(bytes.Span))
            {
                throw FencePage.Malformed("a page's bytes do not match its checksum");
            }

            page = FencePage.Read(new ProtoReader(bytes.Span), _stride, level, _layout);
        }

        if (page.SegmentStarts[^1] != segments)
        {
            throw FencePage.Malformed($"a page covers {page.SegmentStarts[^1]} segments where its parent says {segments}");
        }

        PagesRead++;
        Diagnostics.VortexEventSource.RunsRead(1);
        if (_pages.Count >= PageCache)
        {
            _pages.Clear();
        }

        _pages[region.Offset] = page;
        return page;
    }
}
