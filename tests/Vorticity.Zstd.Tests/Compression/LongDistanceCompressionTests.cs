using System;
using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Threading.Tasks;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// libzstd turns its long-distance matcher on by itself only at level 22 for sources over 64 MiB; asked
/// for explicitly, it runs at any size. The matcher is compared that way, on sources of a few megabytes,
/// at the optimal parsers' levels.
/// </summary>
public sealed class LongDistanceCompressionTests
{
    private static readonly int[] Levels = [16, 17, 18, 19, 22];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string kind in DataKinds.All)
        {
            foreach (int level in Levels)
            {
                data.Add(CorpusCase.Name(kind, 1_500_000, level));
            }
        }

        foreach (string kind in FarRepeats.Kinds)
        {
            foreach (int level in Levels)
            {
                data.Add(CorpusCase.Name(kind, 3_000_000, level));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Compresses_like_libzstd(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = FarRepeats.Generate(@case.Kind, @case.Size);
        byte[] expected = Libzstd(data, @case.Level, longDistance: true);

        var compressor = new ZstdCompressor(@case.Level) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, name, libzstds: true);

        // A second frame on the same compressor, whose tables carry over but whose matcher starts afresh:
        // on the far repeats, whose frames the matcher's own matches fill, so that a matcher that
        // carried over would show; the other kinds give it little to find.
        if (@case.Kind.StartsWith("far-", StringComparison.Ordinal))
        {
            Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int again));
            Corpus.AssertSameBytes(expected, output.AsSpan(0, again), name);
        }
    }

    /// <summary>
    /// Sizes across the three ways a frame takes a dictionary (attached, its tables copied, loaded):
    /// libzstd's matcher starts afresh at the source in all of them, and loads a raw-content dictionary
    /// when the frame does.
    /// </summary>
    private static readonly int[] DictionarySizes = [6_000, 100_000, 1_500_000];

    public static TheoryData<string> DictionaryCases()
    {
        var data = new TheoryData<string>();
        foreach (string kind in FarRepeats.Kinds)
        {
            foreach (string type in new[] { "trained", "raw" })
            {
                foreach (int level in Levels)
                {
                    foreach (int size in DictionarySizes)
                    {
                        data.Add(CorpusCase.Name(kind, size, level, "dict=" + type));
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DictionaryCases))]
    public void Compresses_with_a_dictionary_like_libzstd(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        (byte[] dictionary, _) = Dictionaries.Get(FarRepeats.Content(@case.Kind), @case.Dictionary!);
        byte[] data = FarRepeats.Generate(@case.Kind, @case.Size, dictionary);
        using ZstandardDictionary native = ZstandardDictionary.Create(dictionary, @case.Level);
        byte[] expected = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = native, EnableLongDistanceMatching = true });

        var compressor = new ZstdCompressor(@case.Level, dictionary) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);

        byte[] decoded = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, new ZstdDecompressor(dictionary).Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);

        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int again));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, again), name);
    }

    /// <summary>
    /// Long-distance matches across a raw dictionary's end and the source's start, which follow each
    /// other in the matcher's indices: later in the source, the dictionary's last kilobyte then the
    /// source's first bytes (a match in the dictionary that runs on into the source), and the
    /// dictionary's last 24 bytes then the source's first bytes (a match at the source's start that
    /// runs back into the dictionary, too short for one of its own there). The matcher finds both,
    /// but so does the parser's own search, which counts across the segments too: the frames guard
    /// the matcher's reads at the boundary more than they depend on its lengths there.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(22)]
    public void Matches_across_a_raw_dictionary_end_like_libzstd(int level)
    {
        (byte[] dictionary, _) = Dictionaries.Get("text", "raw");
        byte[] data = DataKinds.Generate("json", 1_500_000);
        Span<byte> head = data.AsSpan(0, 8192);
        dictionary.AsSpan(dictionary.Length - 1024).CopyTo(data.AsSpan(500_000));
        head.CopyTo(data.AsSpan(500_000 + 1024));
        dictionary.AsSpan(dictionary.Length - 24).CopyTo(data.AsSpan(1_000_000));
        head.CopyTo(data.AsSpan(1_000_000 + 24));

        using ZstandardDictionary native = ZstandardDictionary.Create(dictionary, level);
        byte[] expected = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = native, EnableLongDistanceMatching = true });
        var compressor = new ZstdCompressor(level, dictionary) { LongDistanceMatching = true };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "L" + level);
    }

    /// <summary>
    /// The dictionary cases must exercise the matcher too: where frames load a raw-content dictionary,
    /// libzstd's change with it, for most of them.
    /// </summary>
    [Fact]
    public void The_matcher_changes_the_frames_with_a_raw_dictionary()
    {
        int changed = 0;
        foreach (string kind in FarRepeats.Kinds)
        {
            (byte[] dictionary, _) = Dictionaries.Get(FarRepeats.Content(kind), "raw");
            byte[] data = FarRepeats.Generate(kind, DictionarySizes[^1], dictionary);
            foreach (int level in Levels)
            {
                using ZstandardDictionary native = ZstandardDictionary.Create(dictionary, level);
                byte[] with = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = native, EnableLongDistanceMatching = true });
                byte[] without = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = native });
                changed += with.AsSpan().SequenceEqual(without) ? 0 : 1;
            }
        }

        Assert.True(changed >= FarRepeats.Kinds.Length * Levels.Length / 2, $"only {changed} frames changed");
    }

    /// <summary>The cases must exercise the matcher: libzstd's frames change with it, for most of them.</summary>
    [Fact]
    public void The_matcher_changes_the_frames()
    {
        int changed = 0;
        foreach (string kind in FarRepeats.Kinds)
        {
            byte[] data = FarRepeats.Generate(kind, 3_000_000);
            foreach (int level in Levels)
            {
                changed += Libzstd(data, level, longDistance: true).AsSpan().SequenceEqual(Libzstd(data, level, longDistance: false)) ? 0 : 1;
            }
        }

        Assert.True(changed >= FarRepeats.Kinds.Length * Levels.Length / 2, $"only {changed} frames changed");
    }

    /// <summary>
    /// Level 22 over 64 MiB: libzstd's window reaches 128 MiB and its matcher turns itself on; the
    /// frames are the same with no switch on either side.
    /// </summary>
    [Fact]
    public void Turns_itself_on_like_libzstd()
    {
        const int Size = (64 << 20) + 300_000;
        Assert.True(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, Size)));
        Assert.False(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, 64 << 20)));
        Assert.False(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(21, Size)));

        byte[] data = FarRepeats.Generate("far-walk64", Size);
        byte[] expected = Libzstd(data, 22, longDistance: false);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, new ZstdCompressor(22).Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "far-walk64/" + Size + "/L22");
        CompressionCorpus.AssertDecodes(output.AsSpan(0, written), data, "far-walk64/" + Size + "/L22", libzstds: true);
    }

    /// <summary>
    /// The same with a raw-content dictionary, which the frame loads (64 MiB is over six times its
    /// size), and into the matcher too: the dictionary's stretches in the source are found there.
    /// </summary>
    [Fact]
    public void Turns_itself_on_with_a_dictionary_like_libzstd()
    {
        const int Size = (64 << 20) + 300_000;
        (byte[] dictionary, _) = Dictionaries.Get("walk64", "raw");
        Assert.True(LongDistanceMatcher.EnabledFor(CompressionParameters.ForFrame(22, Size, dictionary.Length, attached: false)));

        byte[] data = FarRepeats.Generate("far-walk64", Size, dictionary);
        using ZstandardDictionary native = ZstandardDictionary.Create(dictionary, 22);
        byte[] expected = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = native });
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, new ZstdCompressor(22, dictionary).Compress(data, output, out _, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "far-walk64/" + Size + "/L22/dict=raw");

        byte[] decoded = new byte[data.Length];
        Assert.Equal(OperationStatus.Done, new ZstdDecompressor(dictionary).Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), "far-walk64/" + Size + "/L22/dict=raw");
    }

    public static TheoryData<string> JobCases()
    {
        var data = new TheoryData<string>();
        foreach (string kind in FarRepeats.Kinds)
        {
            foreach (int level in Levels)
            {
                foreach (int jobSize in new[] { 0, 512 << 10 })
                {
                    foreach (string dictionary in new[] { "none", "raw", "trained" })
                    {
                        data.Add(string.Create(CultureInfo.InvariantCulture, $"{kind}/{level}/{jobSize}/{dictionary}"));
                    }
                }
            }
        }

        return data;
    }

    /// <summary>
    /// libzstd with workers runs one matcher for the frame, the jobs' sections in order by chunks of
    /// 1 MiB, and gives each job its sequences, which its blocks consume in turn: against libzstd built
    /// with threads, one job of the default size, or many small ones; a dictionary, which the first
    /// job takes, never reaches the matcher.
    /// </summary>
    [Theory]
    [MemberData(nameof(JobCases))]
    public async Task Compresses_in_jobs_like_libzstd_with_workers(string name)
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        string[] parts = name.Split('/');
        int level = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int jobSize = int.Parse(parts[2], CultureInfo.InvariantCulture);
        byte[]? dictionary = parts[3] == "none" ? null : Dictionaries.Get(FarRepeats.Content(parts[0]), parts[3]).Bytes;
        byte[] data = dictionary is null ? FarRepeats.Generate(parts[0], 3_000_000) : FarRepeats.Generate(parts[0], 3_000_000, dictionary);
        byte[] expected = libzstd!.Compress(data, level, workers: 2, longDistance: true, dictionary: dictionary, jobSize: jobSize);

        ZstdCompressor compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, dictionary);
        compressor.LongDistanceMatching = true;
        compressor.JobSizeOverride = jobSize;
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        Assert.Equal(OperationStatus.Done, compressor.CompressInJobs(data, output, out int written));
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        byte[] decoded = new byte[data.Length];
        var decoder = dictionary is null ? new ZstdDecompressor() : new ZstdDecompressor(dictionary);
        Assert.Equal(OperationStatus.Done, decoder.Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize));
        Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);

        Array.Clear(output);
        int parallel = await compressor.CompressAsync(data, output, 3, TestContext.Current.CancellationToken);
        Corpus.AssertSameBytes(expected, output.AsSpan(0, parallel), name + " in parallel");
    }

    /// <summary>
    /// Level 22 over 64 MiB with workers: the matcher turns itself on, and its jobs are sized by the
    /// match finder's cycle (512 MiB here), so the frame is one job, its sequences made by chunks of
    /// 1 MiB: not the frame without workers.
    /// </summary>
    [Fact]
    public async Task Turns_itself_on_in_jobs_like_libzstd_with_workers()
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        const int Size = (64 << 20) + 300_000;
        byte[] data = FarRepeats.Generate("far-walk64", Size);
        byte[] expected = libzstd!.Compress(data, 22, workers: 2);
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        int written = await new ZstdCompressor(22).CompressAsync(data, output, 4, TestContext.Current.CancellationToken);
        Corpus.AssertSameBytes(expected, output.AsSpan(0, written), "far-walk64/" + Size + "/L22 in jobs");
    }

    /// <summary>The platform's frame, with or without its long-distance matcher.</summary>
    private static byte[] Libzstd(ReadOnlySpan<byte> data, int level, bool longDistance)
    {
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        using var encoder = new ZstandardEncoder(new ZstandardCompressionOptions { Quality = level, EnableLongDistanceMatching = longDistance });
        Assert.Equal(OperationStatus.Done, encoder.Compress(data, output, out int consumed, out int written, isFinalBlock: true));
        Assert.Equal(data.Length, consumed);
        return output.AsSpan(0, written).ToArray();
    }

    /// <summary>
    /// Sources whose matches lie far back and run long: copies of a stretch of content, slightly edited,
    /// between other content. The binary trees skip positions inside long matches, which the
    /// long-distance matcher's table still holds.
    /// </summary>
    private static class FarRepeats
    {
        public static readonly string[] Kinds = ["far-text", "far-json", "far-random", "far-walk64"];

        /// <summary>The kind of content a far-repeat kind repeats: its dictionaries' kind too.</summary>
        public static string Content(string kind) => kind.Substring(4) == "random" ? "random64" : kind.Substring(4);

        /// <summary>
        /// <see cref="Generate(string, int)"/>, with stretches of <paramref name="dictionary"/>, a few
        /// kilobytes each, written over it every 200 KB or so.
        /// </summary>
        public static byte[] Generate(string kind, int size, byte[] dictionary)
        {
            byte[] data = Generate(kind, size);
            int stretches = Math.Max(1, size / 200_000);
            int length = Math.Min(Math.Min(4096, dictionary.Length / 2), size / 3);
            for (int i = 0; i < stretches; i++)
            {
                int position = (int)((long)size * ((2 * i) + 1) / (2 * stretches));
                int start = (i * 7919) % (dictionary.Length - length);
                dictionary.AsSpan(start, Math.Min(length, size - position)).CopyTo(data.AsSpan(position));
            }

            return data;
        }

        public static byte[] Generate(string kind, int size)
        {
            if (!kind.StartsWith("far-", StringComparison.Ordinal))
            {
                return DataKinds.Generate(kind, size);
            }

            string content = Content(kind);
            var random = new Random(size ^ kind.Length);
            byte[] stretch = DataKinds.Generate(content, size / 6, seed: 2);
            byte[] data = new byte[size];
            int position = 0;
            int copy = 0;
            while (position < size)
            {
                // Other content, then a copy of the stretch with an edit every few kilobytes.
                byte[] filler = DataKinds.Generate("text", random.Next(1_000, 100_000), seed: 3 + copy);
                int length = Math.Min(filler.Length, size - position);
                filler.AsSpan(0, length).CopyTo(data.AsSpan(position));
                position += length;

                length = Math.Min(stretch.Length, size - position);
                stretch.AsSpan(0, length).CopyTo(data.AsSpan(position));
                for (int edit = random.Next(500, 5_000); edit < length; edit += random.Next(500, 5_000))
                {
                    data[position + edit] = (byte)random.Next(256);
                }

                position += length;
                copy++;
            }

            return data;
        }
    }
}
