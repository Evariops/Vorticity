using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Parquet.Metadata;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// What the read of a row group asks of its source, chunk by chunk: the whole chunk, or, from a
/// source that does not read in place, the pages the batches the scan reads need, placed by the
/// chunk's offset index, and what precedes the first page, the dictionary's. A mapped file is read
/// whole, since its pages cost nothing until they are touched. From a source that does not read in
/// place, a group of more than <see cref="WindowedBytes"/> is read in windows of batches, so that
/// its first batch waits for its own pages and not the group's. The scan and its plan count the same
/// ranges.
/// </summary>
internal static class ChunkReads
{
    /// <summary>The bytes of a group's chunks past which a read that does not read in place is cut into windows.</summary>
    internal const long WindowedBytes = 4L << 20;

    /// <summary>
    /// The bytes past which a window's batches stop doubling: the windows after it take as many
    /// batches, so that what a group holds of its reads, a window decoded and the next read, stays
    /// within a few of them whatever the group's size.
    /// </summary>
    internal const long MaxWindowBytes = 8L << 20;
    /// <summary>
    /// The rows of a group of <paramref name="rows"/> rows starting at <paramref name="firstRow"/> that a
    /// scan of <paramref name="range"/> reads, from its first batch it reaches to the end of its last.
    /// </summary>
    internal static (long First, long End) Window(RowRange? range, long firstRow, long rows, int batchRows)
    {
        long first = 0;
        long end = rows;
        if (range is { } asked)
        {
            if (asked.End < firstRow + rows)
            {
                end = Math.Min(rows, (asked.End - firstRow + batchRows - 1) / batchRows * batchRows);
            }

            first = Math.Max(0, (asked.Start - firstRow) / batchRows * batchRows);
        }

        return (first, end);
    }

    /// <summary>
    /// Whether a group of <paramref name="rows"/> rows of which the scan reads those from
    /// <paramref name="first"/> to <paramref name="end"/>, the batches <paramref name="live"/> leaves,
    /// is read a page at a time: from a source that does not read in place, when some of its rows are
    /// not read.
    /// </summary>
    internal static bool Sparse(ParquetFile file, long first, long end, long rows, BlockMask? live) =>
        !file.Reader.ReadsInPlace && (first > 0 || end < rows || live is { } mask && mask.LiveCount < mask.BlockCount);

    /// <summary>
    /// Reads the offset index of the chunk of each of <paramref name="leaves"/> in row group
    /// <paramref name="group"/> that <paramref name="locations"/> does not already hold, in one request:
    /// a chunk whose index is missing or malformed keeps none, and is read whole.
    /// </summary>
    internal static async ValueTask OffsetsAsync(
        ParquetFile file, int group, int[] leaves, PageLocation[]?[] locations, SegmentRequestSet requests, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        ParquetFooter footer = file.Footer;
        requests.Release();
        int[] slots = new int[leaves.Length];
        int asked = 0;
        long bytes = 0;
        for (int i = 0; i < leaves.Length; i++)
        {
            ColumnChunkMetadata chunk = footer.Chunk(group, leaves[i]);
            slots[i] = -1;
            if (locations[leaves[i]] is not null || chunk.IsEncrypted || !file.Holds(chunk.OffsetIndexOffset, chunk.OffsetIndexLength))
            {
                continue;
            }

            slots[i] = requests.Add(new SegmentSpec((ulong)chunk.OffsetIndexOffset, (uint)chunk.OffsetIndexLength, 0, 0, 0));
            bytes += chunk.OffsetIndexLength;
            asked++;
        }

        if (asked == 0)
        {
            return;
        }

        ScanCounters.Note(metrics, asked, bytes);
        await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        long rows = footer.RowGroups[group].RowCount;
        for (int i = 0; i < leaves.Length; i++)
        {
            if (slots[i] < 0)
            {
                continue;
            }

            try
            {
                locations[leaves[i]] = OffsetIndex.Read(requests.GetBuffer(slots[i]).Span, rows);
            }
            catch (ParquetFormatException)
            {
                // An index the reader may not trust: its chunk is read whole, as without one.
            }
        }
    }

    /// <summary>
    /// Appends the ranges of <paramref name="chunk"/> the read asks for to <paramref name="runs"/>, as
    /// offsets of the file: the whole chunk, or, when <paramref name="sparse"/> and
    /// <paramref name="pages"/> tile it, what precedes its first data page and the pages the batches
    /// read need, those that touch as one range. The chunk's pages placed from its start when only some
    /// are read, null when it is read whole.
    /// </summary>
    internal static PageLocation[]? Plan(
        ParquetFile file,
        ColumnChunkMetadata chunk,
        PageLocation[]? pages,
        bool sparse,
        BlockMask? live,
        long first,
        long end,
        int batchRows,
        long rows,
        List<(long From, long To)> runs)
    {
        (long start, int length) = file.ChunkRange(chunk);
        if (!sparse || pages is null || !Tiles(pages, start, length))
        {
            runs.Add((start, start + length));
            return null;
        }

        long runStart = start;
        long runEnd = pages[0].Offset;
        for (int p = 0; p < pages.Length; p++)
        {
            long to = p + 1 < pages.Length ? pages[p + 1].FirstRow : rows;
            if (!Needed(pages[p].FirstRow, to, first, end, live, batchRows))
            {
                continue;
            }

            if (pages[p].Offset != runEnd)
            {
                Add(runs, runStart, runEnd);
                runStart = pages[p].Offset;
            }

            runEnd = pages[p].Offset + pages[p].Size;
        }

        Add(runs, runStart, runEnd);
        PageLocation[] map = new PageLocation[pages.Length];
        for (int p = 0; p < map.Length; p++)
        {
            map[p] = pages[p] with { Offset = pages[p].Offset - start };
        }

        return map;

        static void Add(List<(long From, long To)> runs, long from, long to)
        {
            if (to > from)
            {
                runs.Add((from, to));
            }
        }
    }

    /// <summary>
    /// Whether the read of the chunks of <paramref name="leaves"/> in row group <paramref name="group"/>
    /// may be cut into windows, for which their offset indexes are read: from a source that does not
    /// read in place, more than <see cref="WindowedBytes"/> of them.
    /// </summary>
    internal static bool MayWindow(ParquetFile file, int group, int[] leaves)
    {
        if (file.Reader.ReadsInPlace)
        {
            return false;
        }

        long bytes = 0;
        foreach (int leaf in leaves)
        {
            bytes += file.ChunkRange(file.Footer.Chunk(group, leaf)).Length;
        }

        return bytes > WindowedBytes;
    }

    /// <summary>
    /// Cuts the read of row group <paramref name="group"/> into <paramref name="windows"/>: each the pages
    /// of a run of batches its rows from <paramref name="first"/> to <paramref name="end"/> need, which
    /// <paramref name="live"/> leaves, twice as many batches as the window before it from one until a
    /// window holds half of <see cref="MaxWindowBytes"/>, as many after, the first with what precedes
    /// each chunk's first page; and places each reader's pages in
    /// <paramref name="maps"/>, from its chunk's start. A page goes with the window of its first row:
    /// a batch's pages are all read once the windows up to its own are. False, and nothing cut, where the
    /// group is read in one request: from a source that reads in place, under
    /// <see cref="WindowedBytes"/> of pages to read, or where a chunk has no offset index that tiles it, an
    /// encrypted one among them.
    /// </summary>
    internal static bool Windows(
        ParquetFile file,
        int group,
        int[] leaves,
        PageLocation[]?[] locations,
        BlockMask? live,
        long first,
        long end,
        int batchRows,
        long rows,
        List<ReadWindow> windows,
        PageLocation[]?[] maps)
    {
        windows.Clear();
        if (file.Reader.ReadsInPlace)
        {
            return false;
        }

        ParquetFooter footer = file.Footer;
        long bytes = 0;
        foreach (int leaf in leaves)
        {
            ColumnChunkMetadata chunk = footer.Chunk(group, leaf);
            (long start, int length) = file.ChunkRange(chunk);
            if (chunk.IsEncrypted || locations[leaf] is not { } pages || !Tiles(pages, start, length))
            {
                return false;
            }

            bytes += pages[0].Offset - start;
            for (int p = 0; p < pages.Length; p++)
            {
                long to = p + 1 < pages.Length ? pages[p + 1].FirstRow : rows;
                bytes += Needed(pages[p].FirstRow, to, first, end, live, batchRows) ? pages[p].Size : 0;
            }
        }

        if (bytes <= WindowedBytes)
        {
            return false;
        }

        int[] next = new int[leaves.Length];
        int firstBatch = (int)(first / batchRows);
        int endBatch = (int)((end + batchRows - 1) / batchRows);
        for (int from = firstBatch, size = 1; from < endBatch;)
        {
            long limit = Math.Min((long)from + size, endBatch) * batchRows;
            ReadWindow window = new() { FirstBatch = from };
            for (int i = 0; i < leaves.Length; i++)
            {
                PageLocation[] pages = locations[leaves[i]]!;
                long start = file.ChunkRange(footer.Chunk(group, leaves[i])).Start;
                long runStart = start;
                long runEnd = from == firstBatch ? pages[0].Offset : start;
                int p = next[i];
                for (; p < pages.Length && pages[p].FirstRow < limit; p++)
                {
                    long to = p + 1 < pages.Length ? pages[p + 1].FirstRow : rows;
                    if (!Needed(pages[p].FirstRow, to, first, end, live, batchRows))
                    {
                        continue;
                    }

                    if (pages[p].Offset != runEnd)
                    {
                        window.Add(i, runStart, runEnd);
                        runStart = pages[p].Offset;
                    }

                    runEnd = pages[p].Offset + pages[p].Size;
                }

                next[i] = p;
                window.Add(i, runStart, runEnd);
            }

            if (window.Runs.Count > 0)
            {
                windows.Add(window);
            }

            from += size;
            if (window.Bytes * 2 <= MaxWindowBytes)
            {
                size = Math.Min(size * 2, endBatch);
            }
        }

        for (int i = 0; i < leaves.Length; i++)
        {
            PageLocation[] pages = locations[leaves[i]]!;
            long start = file.ChunkRange(footer.Chunk(group, leaves[i])).Start;
            PageLocation[] map = new PageLocation[pages.Length];
            for (int p = 0; p < map.Length; p++)
            {
                map[p] = pages[p] with { Offset = pages[p].Offset - start };
            }

            maps[i] = map;
        }

        return true;
    }

    /// <summary>
    /// Whether the batches the scan reads, from row <paramref name="first"/> of the group to row
    /// <paramref name="end"/>, need a page of the rows from <paramref name="from"/> to
    /// <paramref name="to"/>: one of them that the page index left live covers a row of it.
    /// </summary>
    private static bool Needed(long from, long to, long first, long end, BlockMask? live, int batchRows)
    {
        long start = Math.Max(from, first);
        long stop = Math.Min(to, end);
        if (start >= stop)
        {
            return false;
        }

        if (live is null)
        {
            return true;
        }

        for (long batch = start / batchRows; batch * batchRows < stop; batch++)
        {
            if (live.IsLive((int)batch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="pages"/> tile the chunk of <paramref name="length"/> bytes at
    /// <paramref name="start"/> from its first data page to its end, each where the one before it
    /// ends: an offset index that places the chunk's pages as they lie, which a read of some of them
    /// may trust.
    /// </summary>
    private static bool Tiles(PageLocation[] pages, long start, int length)
    {
        if (pages.Length == 0 || pages[0].Offset < start)
        {
            return false;
        }

        for (int p = 0; p < pages.Length; p++)
        {
            long next = p + 1 < pages.Length ? pages[p + 1].Offset : start + length;
            if (pages[p].Size <= 0 || pages[p].Offset + pages[p].Size != next)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A window of a row group's read: the batch it serves first, and the ranges of the file it asks for.</summary>
internal sealed class ReadWindow
{
    /// <summary>The group's first batch the window's pages serve; the windows before it read those of the batches before.</summary>
    internal int FirstBatch { get; init; }

    /// <summary>The ranges, each its reader's and its offsets in the file: the readers in order, and each reader's ranges rising.</summary>
    internal List<(int Reader, long From, long To)> Runs { get; } = [];

    /// <summary>The slot of each range in the request that reads the window, as the scan made it.</summary>
    internal List<int> Slots { get; } = [];

    /// <summary>The bytes the window reads.</summary>
    internal long Bytes { get; private set; }

    internal void Add(int reader, long from, long to)
    {
        if (to > from)
        {
            Runs.Add((reader, from, to));
            Bytes += to - from;
        }
    }
}
