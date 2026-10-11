// The page codecs on a file's own pages (docs/guide/benchmarks-parquet-x64.md).
//
// Every data and dictionary page of every column chunk, decompressed in turn by this package's
// decoder, and by the base class library's GZipStream where the file is GZIP's, the decoder the
// package's own replaced: the outputs compared, then each side timed over the whole file, the
// sides taking turns, and each column alone. The pages are read from the file's bytes in memory, so
// that only the codec is timed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

using Vorticity.Parquet;
using Vorticity.Parquet.Codecs;
using Vorticity.Parquet.Metadata;
using Vorticity.Zstd;

namespace Vorticity.Benchmarks;

/// <summary>A Parquet file's pages decompressed by the package's codecs, and by the base class library's GZIP.</summary>
internal static class PageCodecCost
{
    private readonly record struct CodedPage(int Column, CompressionCodec Codec, int Start, int Length, int Size);

    internal static async Task<int> RunAsync(string[] args)
    {
        int fileAt = Array.IndexOf(args, "--page-codecs") + 1;
        if (fileAt <= 0 || fileAt >= args.Length || !System.IO.File.Exists(args[fileAt]))
        {
            Console.Error.WriteLine("usage: --page-codecs <file.parquet> [--runs N]");
            return 2;
        }

        string path = args[fileAt];
        int runAt = Array.IndexOf(args, "--runs");
        int runs = runAt >= 0 && runAt + 1 < args.Length && int.TryParse(args[runAt + 1], CultureInfo.InvariantCulture, out int given) && given > 0 ? given : 9;
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
        List<CodedPage> pages;
        string[] names;
        await using (ParquetFile file = await ParquetFile.OpenAsync(path).ConfigureAwait(false))
        {
            names = file.Metadata.RowGroups.Count > 0 ? [.. file.Metadata.RowGroups[0].Chunks.Select(c => c.Column)] : [];
            pages = Pages(file, bytes);
        }

        if (pages.Count == 0)
        {
            Console.Out.WriteLine("The file holds no compressed page.");
            return 0;
        }

        int largest = pages.Max(p => p.Size);
        byte[] ours = new byte[largest];
        byte[] theirs = new byte[largest];
        ZstdDecompressor zstd = new();
        bool gzip = pages.Exists(p => p.Codec == CompressionCodec.Gzip);
        foreach (CodedPage page in pages)
        {
            PageCodecs.Decompress(page.Codec, bytes.AsSpan(page.Start, page.Length), ours.AsSpan(0, page.Size), zstd);
            if (page.Codec == CompressionCodec.Gzip)
            {
                Platform(bytes.AsSpan(page.Start, page.Length), theirs.AsSpan(0, page.Size));
                if (!ours.AsSpan(0, page.Size).SequenceEqual(theirs.AsSpan(0, page.Size)))
                {
                    Console.Error.WriteLine($"A page of '{names[page.Column]}' decodes to other bytes than the base class library's.");
                    return 1;
                }
            }
        }

        string codecs = string.Join(", ", pages.Select(p => p.Codec).Distinct());
        long size = pages.Sum(p => (long)p.Size);
        long stored = pages.Sum(p => (long)p.Length);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"PAGE CODECS, {Path.GetFileName(path)}: {pages.Count} pages of {codecs}, {stored:N0} bytes stored, {size:N0} decompressed; median of {runs} runs"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{"column",-24} {"MB",8} {"ratio",6} {"ours GB/s",10}{(gzip ? $" {"library GB/s",13}" : "")}"));

        // The whole file, the sides taking turns; then each of its ten largest columns alone.
        Line("(all)", pages, size, stored);
        foreach (IGrouping<int, CodedPage> column in pages.GroupBy(p => p.Column).OrderByDescending(g => g.Sum(p => (long)p.Size)).Take(10))
        {
            List<CodedPage> own = [.. column];
            Line(names[column.Key], own, own.Sum(p => (long)p.Size), own.Sum(p => (long)p.Length));
        }

        return 0;

        void Line(string name, List<CodedPage> set, long decompressed, long compressed)
        {
            for (int warm = 0; warm < 3; warm++)
            {
                Ours(set);
                if (gzip)
                {
                    Library(set);
                }
            }

            List<double> mine = [];
            List<double> library = [];
            for (int run = 0; run < runs; run++)
            {
                mine.Add(Time(() => Ours(set)));
                if (gzip)
                {
                    library.Add(Time(() => Library(set)));
                }
            }

            string libraryText = gzip ? string.Create(CultureInfo.InvariantCulture, $" {decompressed / Median(library) / 1e6,13:F2}") : "";
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-24} {decompressed / 1e6,8:F1} {decompressed / (double)compressed,6:F2} {decompressed / Median(mine) / 1e6,10:F2}{libraryText}"));
        }

        void Ours(List<CodedPage> set)
        {
            foreach (CodedPage page in set)
            {
                PageCodecs.Decompress(page.Codec, bytes.AsSpan(page.Start, page.Length), ours.AsSpan(0, page.Size), zstd);
            }
        }

        void Library(List<CodedPage> set)
        {
            foreach (CodedPage page in set)
            {
                if (page.Codec == CompressionCodec.Gzip)
                {
                    Platform(bytes.AsSpan(page.Start, page.Length), theirs.AsSpan(0, page.Size));
                }
            }
        }
    }

    /// <summary>Every data and dictionary page under a codec, each chunk walked by its pages' headers; a v2 page's values alone, its levels being stored as they are.</summary>
    private static List<CodedPage> Pages(ParquetFile file, byte[] bytes)
    {
        ParquetFooter footer = file.Footer;
        List<CodedPage> pages = [];
        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            for (int column = 0; column < footer.RowGroups[group].ColumnCount; column++)
            {
                ColumnChunkMetadata chunk = footer.Chunk(group, column);
                if (chunk.Codec == CompressionCodec.Uncompressed)
                {
                    continue;
                }

                (long start, int length) = file.ChunkRange(chunk);
                int at = (int)start;
                int end = at + length;
                while (at < end)
                {
                    PageHeader header = PageHeader.Read(bytes.AsSpan(at, end - at));
                    int body = at + header.HeaderLength;
                    if (header.Type is PageType.DataPage or PageType.DictionaryPage)
                    {
                        pages.Add(new CodedPage(column, chunk.Codec, body, header.CompressedPageSize, header.UncompressedPageSize));
                    }
                    else if (header.Type == PageType.DataPageV2 && header.IsCompressed)
                    {
                        int levels = header.DefinitionLevelsLength + header.RepetitionLevelsLength;
                        pages.Add(new CodedPage(column, chunk.Codec, body + levels, header.CompressedPageSize - levels, header.UncompressedPageSize - levels));
                    }

                    at = body + header.CompressedPageSize;
                }
            }
        }

        return pages;
    }

    /// <summary>A GZIP page through the base class library's stream, as the package read it before its own decoder.</summary>
    private static unsafe void Platform(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* input = source)
        {
            using UnmanagedMemoryStream stream = new(input, source.Length);
            using GZipStream gzip = new(stream, CompressionMode.Decompress);
            int written = 0;
            while (written < destination.Length)
            {
                int read = gzip.Read(destination[written..]);
                if (read == 0)
                {
                    break;
                }

                written += read;
            }
        }
    }

    private static double Time(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
