using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Zstd.Bench;

namespace Vorticity.Zstd.Perf;

/// <summary>
/// Throughput on the corpora of zstd's own benchmarks (bench/zstd-corpus.sh), level by level: libzstd 1.5.7
/// freshly compiled, the reference; the platform's <see cref="ZstandardEncoder"/> and
/// <see cref="ZstandardDecoder"/>, the libzstd the runtime ships; Vorticity.Zstd. At each level the
/// candidates compress the data set, then decode libzstd's frames, each direction in rounds that time
/// every candidate once, in an order that rotates, so that whatever the machine does at that moment
/// lands on all of them alike. The first round is also the check: every candidate must write libzstd's
/// frames byte for byte, and give the content back from them.
/// </summary>
/// <remarks>
/// Usage: <c>Vorticity.Zstd.Perf --corpus silesia,github,github-dict [--levels 1,3,19] [--seconds S] [--only zstd] [--directions decompression]
/// [--results file.tsv] [--markdown file.md]</c>, or <c>Vorticity.Zstd.Perf --corpus-report file.tsv [--markdown file.md]</c>
/// to render results already measured. <c>silesia</c> is silesia.tar, the twelve files in one frame,
/// as lzbench reads it; <c>github</c> the 500 records, a frame each, on one context;
/// <c>github-dict</c> the same with github.dict; <c>github.tar</c> and <c>github.tar-dict</c> the
/// records' tar in one frame, as zstd's regression tests also take them. A direction is timed for some <c>--seconds</c> (5 by
/// default), never fewer than three rounds. <c>--only</c> keeps the named candidates besides libzstd,
/// the reference. <c>--results</c> appends each level's measures to a file as soon as they are taken,
/// and the tables are rendered from the whole file, a later measure replacing an earlier one: a run
/// can take up only the levels to measure again.
/// </remarks>
internal static class Corpus
{
    private const int MinRounds = 3;
    private const int MaxRounds = 400;

    /// <summary>A level of each of libzstd's strategies on sources over 256 KiB but btultra: fast, dfast, greedy, lazy, lazy2, btlazy2, btopt, btultra2.</summary>
    private static readonly int[] DefaultLevels = [1, 3, 5, 7, 9, 13, 16, 19];

    private sealed record DataSet(string Name, string Label, string Title, byte[] Content, int[] Offsets, byte[]? Dictionary);

    private sealed record Candidate(string Name, string Label, SpanCodec Compress, SpanCodec Decompress);

    /// <summary>A candidate's median throughput, in MB/s of content (10^6 bytes), on a data set at a level, in one direction.</summary>
    private sealed record Row(string Set, string SetLabel, string Title, int Level, double Ratio, string Direction, string Candidate, string Label, double Speed, bool Differs)
    {
        public string Key => $"{Set}/{Level}/{Direction}/{Candidate}";

        public string ToTsv() => string.Join('\t', Set, SetLabel, Title, Level.ToString(CultureInfo.InvariantCulture),
            Ratio.ToString("R", CultureInfo.InvariantCulture), Direction, Candidate, Label,
            Speed.ToString("R", CultureInfo.InvariantCulture), Differs ? "differs" : "same");

        public static Row FromTsv(string line)
        {
            string[] f = line.Split('\t');
            return new Row(f[0], f[1], f[2], int.Parse(f[3], CultureInfo.InvariantCulture), double.Parse(f[4], CultureInfo.InvariantCulture),
                f[5], f[6], f[7], double.Parse(f[8], CultureInfo.InvariantCulture), f[9] == "differs");
        }
    }

    public static int Run(string[] sets, string? levels, string? seconds, string? only, string? results, string? markdown, string? directions = null)
    {
        Compresses = directions is null || directions.Split(',').Contains("compression");
        int[] levelList = levels?.Split(',').Select(l => int.Parse(l, CultureInfo.InvariantCulture)).ToArray() ?? DefaultLevels;
        double budget = seconds is null ? 5 : double.Parse(seconds, CultureInfo.InvariantCulture);
        NativeReference? native = NativeReference.TryLoad();
        if (native is null)
        {
            Console.WriteLine($"no {NativeReference.LibraryPath}: run tools/native-ref/build.sh");
            return 1;
        }

        Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"{(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT")}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
            $"some {budget} s a direction");
        var rows = new List<Row>();
        foreach (string name in sets)
        {
            DataSet set = Load(name);
            Console.WriteLine();
            Console.WriteLine(set.Title);
            foreach (int level in levelList)
            {
                (List<Candidate> candidates, IDisposable[] owned) = Candidates(native, set, level, only);
                List<Row> measured = Measure(set, level, candidates, budget);
                foreach (IDisposable disposable in owned)
                {
                    disposable.Dispose();
                }

                Print(measured);
                rows.AddRange(measured);
                if (results is not null)
                {
                    File.AppendAllLines(results, measured.Select(r => r.ToTsv()));
                }
            }
        }

        return Report(results is null ? rows : Read(results), markdown);
    }

    /// <summary>The tables of results already measured: <c>--corpus-report</c>.</summary>
    public static int Report(string results, string? markdown) => Report(Read(results), markdown);

    private static int Report(List<Row> rows, string? markdown)
    {
        string tables = Tables(rows);
        Console.WriteLine();
        Console.Write(tables);
        if (markdown is not null)
        {
            File.WriteAllText(markdown, tables);
        }

        return 0;
    }

    /// <summary>A results file, a later measure replacing an earlier one of the same set, level, direction and candidate.</summary>
    private static List<Row> Read(string results)
    {
        var rows = new List<Row>();
        var index = new Dictionary<string, int>();
        foreach (string line in File.ReadLines(results).Where(l => l.Length > 0))
        {
            Row row = Row.FromTsv(line);
            if (index.TryGetValue(row.Key, out int at))
            {
                rows[at] = row;
            }
            else
            {
                index[row.Key] = rows.Count;
                rows.Add(row);
            }
        }

        return rows;
    }

    private static (List<Candidate> Candidates, IDisposable[] Owned) Candidates(NativeReference native, DataSet set, int level, string? only)
    {
        byte[]? dictionary = set.Dictionary;
        ZstandardDictionary? prepared = dictionary is null ? null : ZstandardDictionary.Create(dictionary, level);
        var encoder = new ZstandardEncoder(new ZstandardCompressionOptions { Quality = level, Dictionary = prepared });
        var decoder = prepared is null ? new ZstandardDecoder() : new ZstandardDecoder(prepared);
        var compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, dictionary);
        var decompressor = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        var candidates = new List<Candidate>
        {
            new("libzstd", "libzstd 1.5.7 (native)",
                dictionary is null ? (s, d) => native.Compress(s, d, level) : native.CompressorWith(dictionary, level),
                dictionary is null ? native.Decompress : native.DecompressorWith(dictionary)),
            new("platform", $".NET {Environment.Version.Major} (System.IO.Compression)",
                (s, d) =>
                {
                    encoder.Reset();
                    return encoder.Compress(s, d, out _, out int w, isFinalBlock: true) == OperationStatus.Done ? w : -1;
                },
                (s, d) =>
                {
                    decoder.Reset();
                    return decoder.Decompress(s, d, out _, out int w) == OperationStatus.Done ? w : -1;
                }),
            new("zstd", $"Vorticity.Zstd ({(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT")})",
                (s, d) => compressor.Compress(s, d, out _, out int w) == OperationStatus.Done ? w : -1,
                (s, d) => decompressor.Decompress(s, d, out _, out int w) == OperationStatus.Done ? w : -1),
        };
        IDisposable[] owned = prepared is null ? [encoder, decoder] : [encoder, decoder, prepared];
        return (only is null ? candidates : candidates.Where(c => c.Name == "libzstd" || only.Split(',').Contains(c.Name)).ToList(), owned);
    }

    /// <summary>
    /// One level: the compression of the data set, whose first round writes libzstd's frames, which
    /// every other candidate must write too; then the decoding of those frames, whose first round
    /// checks that every candidate gives the content back.
    /// </summary>
    /// <summary>Whether compression is timed: <c>--directions decompression</c> leaves it out, the frames written once.</summary>
    private static bool Compresses { get; set; } = true;

    private static List<Row> Measure(DataSet set, int level, List<Candidate> candidates, double budget)
    {
        int records = set.Offsets.Length - 1;
        int[] bounds = new int[records + 1];
        for (int i = 0; i < records; i++)
        {
            bounds[i + 1] = bounds[i] + ZstdCompressor.GetMaxCompressedLength(set.Offsets[i + 1] - set.Offsets[i]);
        }

        // Every page of the outputs touched before anything is timed.
        byte[] reference = new byte[bounds[^1]];
        byte[] scratch = new byte[bounds[^1]];
        byte[] decoded = new byte[set.Content.Length];
        Array.Fill(reference, (byte)1);
        Array.Fill(scratch, (byte)1);
        Array.Fill(decoded, (byte)1);
        int[] sizes = new int[records];
        int[] written = new int[records];
        int count = candidates.Count;

        bool[] compressionDiffers = new bool[count];
        if (!Compresses)
        {
            // Decompression alone: libzstd's frames, written once, untimed.
            Pass(candidates[0].Compress, set.Content, set.Offsets, reference, bounds, sizes);
        }

        double[] compression = !Compresses ? new double[count] : Time(candidates, budget, (c, round) =>
        {
            if (round > 0)
            {
                return Pass(candidates[c].Compress, set.Content, set.Offsets, scratch, bounds, written);
            }

            if (c == 0)
            {
                return Pass(candidates[c].Compress, set.Content, set.Offsets, reference, bounds, sizes);
            }

            double seconds = Pass(candidates[c].Compress, set.Content, set.Offsets, scratch, bounds, written);
            for (int i = 0; i < records && !compressionDiffers[c]; i++)
            {
                compressionDiffers[c] = written[i] != sizes[i] || !scratch.AsSpan(bounds[i], sizes[i]).SequenceEqual(reference.AsSpan(bounds[i], sizes[i]));
            }

            return seconds;
        });

        // libzstd's frames, one after the other, for the decoders.
        int[] frameOffsets = new int[records + 1];
        for (int i = 0; i < records; i++)
        {
            frameOffsets[i + 1] = frameOffsets[i] + Math.Max(sizes[i], 0);
        }

        byte[] frames = new byte[frameOffsets[^1]];
        for (int i = 0; i < records; i++)
        {
            reference.AsSpan(bounds[i], frameOffsets[i + 1] - frameOffsets[i]).CopyTo(frames.AsSpan(frameOffsets[i]));
        }

        bool[] decompressionDiffers = new bool[count];
        double[] decompression = Time(candidates, budget, (c, round) =>
        {
            if (round > 0)
            {
                return Pass(candidates[c].Decompress, frames, frameOffsets, decoded, set.Offsets, written);
            }

            Array.Fill(decoded, (byte)1);
            double seconds = Pass(candidates[c].Decompress, frames, frameOffsets, decoded, set.Offsets, written);
            decompressionDiffers[c] = !decoded.AsSpan().SequenceEqual(set.Content);
            for (int i = 0; i < records && !decompressionDiffers[c]; i++)
            {
                decompressionDiffers[c] = written[i] != set.Offsets[i + 1] - set.Offsets[i];
            }

            return seconds;
        });

        double ratio = (double)set.Content.Length / frames.Length;
        var rows = new List<Row>();
        for (int c = 0; c < count && Compresses; c++)
        {
            rows.Add(new Row(set.Name, set.Label, set.Title, level, ratio, "compression", candidates[c].Name, candidates[c].Label,
                set.Content.Length / compression[c] / 1e6, compressionDiffers[c]));
        }

        for (int c = 0; c < count; c++)
        {
            rows.Add(new Row(set.Name, set.Label, set.Title, level, ratio, "decompression", candidates[c].Name, candidates[c].Label,
                set.Content.Length / decompression[c] / 1e6, decompressionDiffers[c]));
        }

        return rows;
    }

    /// <summary>The codec over every record, into its slot of the output; the seconds it took.</summary>
    private static double Pass(SpanCodec codec, byte[] input, int[] inputOffsets, byte[] output, int[] outputOffsets, int[] written)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < written.Length; i++)
        {
            written[i] = codec(
                input.AsSpan(inputOffsets[i], inputOffsets[i + 1] - inputOffsets[i]),
                output.AsSpan(outputOffsets[i], outputOffsets[i + 1] - outputOffsets[i]));
        }

        return Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    /// <summary>
    /// Rounds of <paramref name="run"/>(candidate, round), every candidate once a round in an order that
    /// rotates, the first in the candidates' order; as many as fit in the budget given what the first
    /// took. Each candidate's median, in seconds.
    /// </summary>
    private static double[] Time(List<Candidate> candidates, double budget, Func<int, int, double> run)
    {
        int count = candidates.Count;
        var first = new double[count];
        for (int c = 0; c < count; c++)
        {
            first[c] = run(c, 0);
        }

        int rounds = Math.Clamp((int)Math.Ceiling(budget / first.Sum()), MinRounds, MaxRounds);
        var samples = new double[count][];
        for (int c = 0; c < count; c++)
        {
            samples[c] = new double[rounds];
            samples[c][0] = first[c];
        }

        for (int r = 1; r < rounds; r++)
        {
            for (int k = 0; k < count; k++)
            {
                int c = (r + k) % count;
                samples[c][r] = run(c, r);
            }
        }

        var medians = new double[count];
        Console.Write($"    {rounds} rounds, quartiles/median:");
        for (int c = 0; c < count; c++)
        {
            Array.Sort(samples[c]);
            medians[c] = samples[c][rounds / 2];
            Console.Write($" {candidates[c].Name} {samples[c][rounds / 4] / medians[c]:F3}-{samples[c][3 * rounds / 4] / medians[c]:F3}");
        }

        Console.WriteLine();
        return medians;
    }

    private static void Print(List<Row> rows)
    {
        foreach (IGrouping<string, Row> direction in rows.GroupBy(r => r.Direction))
        {
            Row reference = direction.First();
            var line = new StringBuilder($"  L{reference.Level,-3} ratio {reference.Ratio:F3} {direction.Key,-13}");
            foreach (Row row in direction)
            {
                line.Append(CultureInfo.InvariantCulture, $" | {row.Candidate} {row.Speed:F1} MB/s");
                if (row != reference)
                {
                    line.Append(CultureInfo.InvariantCulture, $" ({row.Speed / reference.Speed:F3}x)");
                }

                if (row.Differs)
                {
                    line.Append(" DIFFERENT OUTPUT");
                }
            }

            Console.WriteLine(line);
        }
    }

    /// <summary>
    /// The tables, the levels in columns: the ratio, one for all (the frames are the same); then, for
    /// each direction, the throughputs, and apart from them the speedups over libzstd.
    /// </summary>
    private static string Tables(List<Row> rows)
    {
        string[] sets = rows.Select(r => r.Set).Distinct().ToArray();
        int[] levels = rows.Select(r => r.Level).Distinct().Order().ToArray();
        string Header(string first) =>
            $"| {first} | " + string.Join(" | ", levels.Select(l => l.ToString(CultureInfo.InvariantCulture))) + " |" + Environment.NewLine +
            "|:--|" + (first.Contains('|', StringComparison.Ordinal) ? ":--|" : string.Empty) + string.Concat(levels.Select(_ => "--:|")) + Environment.NewLine;
        Row? Find(string set, int level, string direction, string candidate) =>
            rows.LastOrDefault(r => r.Set == set && r.Level == level && r.Direction == direction && r.Candidate == candidate);

        var md = new StringBuilder();
        foreach (string set in sets)
        {
            Row any = rows.First(r => r.Set == set);
            md.AppendLine(CultureInfo.InvariantCulture, $"- {any.SetLabel}: {any.Title}");
        }

        md.AppendLine();
        md.AppendLine("**Compression ratio**");
        md.AppendLine();
        md.Append(Header("level"));
        foreach (string set in sets)
        {
            md.Append(CultureInfo.InvariantCulture, $"| {rows.First(r => r.Set == set).SetLabel} |");
            foreach (int level in levels)
            {
                Row? row = rows.LastOrDefault(r => r.Set == set && r.Level == level);
                md.Append(' ').Append(row is null ? "–" : row.Ratio.ToString("F3", CultureInfo.InvariantCulture)).Append(" |");
            }

            md.AppendLine();
        }

        foreach (string direction in new[] { "compression", "decompression" })
        {
            string title = char.ToUpperInvariant(direction[0]) + direction[1..];
            (string Name, string Label)[] candidates = rows.Where(r => r.Direction == direction)
                .Select(r => (r.Candidate, r.Label)).Distinct().ToArray();
            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"**{title}, MB/s**");
            md.AppendLine();
            md.Append(Header("corpus | implementation"));
            foreach (string set in sets)
            {
                for (int c = 0; c < candidates.Length; c++)
                {
                    md.Append(CultureInfo.InvariantCulture, $"| {(c == 0 ? rows.First(r => r.Set == set).SetLabel : string.Empty)} | {candidates[c].Label} |");
                    foreach (int level in levels)
                    {
                        Row? row = Find(set, level, direction, candidates[c].Name);
                        md.Append(' ').Append(row is null ? "–" : Speed(row.Speed) + (row.Differs ? " ≠" : string.Empty)).Append(" |");
                    }

                    md.AppendLine();
                }
            }

            md.AppendLine();
            md.AppendLine(CultureInfo.InvariantCulture, $"**{title}, speedup over {candidates[0].Label}**");
            md.AppendLine();
            md.Append(Header("corpus | implementation"));
            foreach (string set in sets)
            {
                for (int c = 1; c < candidates.Length; c++)
                {
                    md.Append(CultureInfo.InvariantCulture, $"| {(c == 1 ? rows.First(r => r.Set == set).SetLabel : string.Empty)} | {candidates[c].Label} |");
                    foreach (int level in levels)
                    {
                        Row? row = Find(set, level, direction, candidates[c].Name);
                        Row? reference = Find(set, level, direction, candidates[0].Name);
                        md.Append(' ').Append(row is null || reference is null ? "–"
                            : (row.Speed / reference.Speed).ToString("F2", CultureInfo.InvariantCulture) + "×").Append(" |");
                    }

                    md.AppendLine();
                }
            }
        }

        return md.ToString();
    }

    private static string Speed(double mbPerSecond) =>
        mbPerSecond.ToString(mbPerSecond >= 100 ? "F0" : mbPerSecond >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture);

    /// <summary>A data set's content, where each record starts, and its dictionary: for the micro-benchmarks.</summary>
    public static (byte[] Content, int[] Offsets, byte[]? Dictionary) LoadSet(string name)
    {
        DataSet set = Load(name);
        return (set.Content, set.Offsets, set.Dictionary);
    }

    private static DataSet Load(string name)
    {
        string directory = Path.Combine(BenchFrames.RepositoryRoot, "tests", "Vorticity.Zstd.Tests", "testdata", "corpus");
        switch (name)
        {
            case "silesia":
            {
                byte[] tar = SilesiaTar(Path.Combine(directory, "silesia.zip"));
                return new DataSet(name, "silesia.tar", $"the Silesia corpus as one tar, {tar.Length:N0} bytes, one frame", tar, [0, tar.Length], null);
            }

            case "silesia-sample":
            {
                // 1 MiB every 16 MiB of silesia.tar, as one frame: its kinds of data, text, binaries,
                // images and databases, in a frame that compresses in a fraction of the time, for the
                // micro-benchmarks and the profiler.
                byte[] tar = SilesiaTar(Path.Combine(directory, "silesia.zip"));
                const int Slice = 1 << 20;
                const int Stride = 16 << 20;
                using var sample = new MemoryStream();
                for (int at = 0; at + Slice <= tar.Length; at += Stride)
                {
                    sample.Write(tar, at, Slice);
                }

                byte[] content = sample.ToArray();
                return new DataSet(name, "silesia sample", $"1 MiB every 16 MiB of silesia.tar, {content.Length:N0} bytes, one frame", content, [0, content.Length], null);
            }

            case "github":
            case "github-dict":
            {
                (byte[] content, int[] offsets) = Records(Path.Combine(directory, "github.tar.zst"));
                byte[]? dictionary = name == "github-dict" ? Unzstd(Path.Combine(directory, "github.dict.zst")) : null;
                string title = $"{offsets.Length - 1} JSON records, {content.Length:N0} bytes, a frame each" +
                    (dictionary is null ? string.Empty : $", with github.dict ({dictionary.Length:N0} bytes)");
                return new DataSet(name, dictionary is null ? "github" : "github + dict", title, content, offsets, dictionary);
            }

            case "github.tar":
            case "github.tar-dict":
            {
                // The archive itself, one frame of several blocks, as zstd's regression tests take it.
                byte[] tar = Unzstd(Path.Combine(directory, "github.tar.zst"));
                byte[]? dictionary = name == "github.tar-dict" ? Unzstd(Path.Combine(directory, "github.dict.zst")) : null;
                string title = $"the records' tar, {tar.Length:N0} bytes, one frame" +
                    (dictionary is null ? string.Empty : $", with github.dict ({dictionary.Length:N0} bytes)");
                return new DataSet(name, dictionary is null ? "github.tar" : "github.tar + dict", title, tar, [0, tar.Length], dictionary);
            }

            default:
                throw new ArgumentException("unknown corpus " + name + " (silesia, silesia-sample, github, github-dict, github.tar, github.tar-dict)", nameof(name));
        }
    }

    /// <summary>
    /// silesia.tar, as lzbench reads it: the twelve files in name order, in a ustar archive whose
    /// headers depend on nothing but the files.
    /// </summary>
    private static byte[] SilesiaTar(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                using var data = new MemoryStream();
                using (Stream source = entry.Open())
                {
                    source.CopyTo(data);
                }

                data.Position = 0;
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "silesia/" + entry.FullName)
                {
                    DataStream = data,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                });
            }
        }

        return tar.ToArray();
    }

    /// <summary>The regular files of a .tar.zst, one after the other, and where each starts.</summary>
    private static (byte[] Content, int[] Offsets) Records(string path)
    {
        using var content = new MemoryStream();
        var offsets = new List<int> { 0 };
        using (var reader = new TarReader(new MemoryStream(Unzstd(path))))
        {
            while (reader.GetNextEntry() is TarEntry entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is Stream data)
                {
                    data.CopyTo(content);
                    offsets.Add(checked((int)content.Length));
                }
            }
        }

        return (content.ToArray(), offsets.ToArray());
    }

    private static byte[] Unzstd(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var zstd = new ZstandardStream(file, CompressionMode.Decompress);
        using var content = new MemoryStream();
        zstd.CopyTo(content);
        return content.ToArray();
    }
}
