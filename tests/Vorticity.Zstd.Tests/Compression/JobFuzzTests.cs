using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// Random frames in jobs against libzstd built with threads: sizes from under 512 KiB to a few
/// megabytes, random levels, job sizes and overlaps, checksums, a dictionary now and then, the
/// long-distance matcher asked for in a third of the optimal parsers' frames, run at random degrees of
/// parallelism by compressors reused from frame to frame.
/// </summary>
/// <remarks><c>VORTICITY_ZSTD_FUZZ_ITERATIONS</c> sets the number of frames per seed (default 300, here a tenth of it).</remarks>
public sealed class JobFuzzTests
{
    private static int Iterations =>
        Math.Max(1, (int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_ZSTD_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out int n) ? n : 300) / 10);

    public static TheoryData<int> Seeds() => [1, 2, 3, 4];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_frames_in_jobs_compress_like_libzstd_with_workers(int seed)
    {
        LibzstdMt? libzstd = LibzstdMt.Instance;
        Assert.SkipWhen(libzstd is null, "libzstd with threads is not built: tools/native-ref/build.sh mt");
        var random = new Random(seed);
        string[] kinds = ["text", "json", "mixed", "walk64", "repeats", "incompressible"];
        var compressors = new Dictionary<(int, string, bool, int, int), ZstdCompressor>();
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            string kind = kinds[random.Next(kinds.Length)];
            int level = random.Next(4) switch
            {
                0 => -random.Next(1, 6),
                1 => random.Next(1, 8),
                2 => random.Next(8, 16),
                _ => random.Next(16, 23),
            };
            int size = random.Next(3) == 0 ? random.Next(400_000, 1_100_000) : random.Next(1_100_000, 4_000_000);
            int jobSize = random.Next(3) == 0 ? 0 : random.Next(512 << 10, 1_500_000);
            int overlapLog = random.Next(3) == 0 ? random.Next(1, 10) : 0;
            int dictionaryKind = random.Next(4) == 0 ? random.Next(1, 3) : 0;
            bool longDistance = level >= 16 && random.Next(3) == 0;
            bool checksum = random.Next(2) == 0;
            int degree = random.Next(1, 9);
            byte[]? dictionary = dictionaryKind switch
            {
                1 => Dictionaries.Get(kind, "raw").Bytes,
                2 => Dictionaries.Get(kind == "incompressible" ? "text" : kind, "trained").Bytes,
                _ => null,
            };
            byte[] data = DataKinds.Generate(kind, size, seed * 1000 + iteration);

            // A compressor keeps its dictionary: one per dictionary, which the kind of content picks.
            var key = (level, dictionary is null ? "none" : kind + "/" + dictionaryKind, longDistance, jobSize, overlapLog);
            if (!compressors.TryGetValue(key, out ZstdCompressor? compressor))
            {
                compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, dictionary);
                compressor.LongDistanceMatching = longDistance;
                compressor.JobSizeOverride = jobSize;
                compressor.OverlapLogOverride = overlapLog;
                compressors.Add(key, compressor);
            }

            compressor.AppendChecksum = checksum;
            string name = $"seed {seed}, iteration {iteration}: {kind} {size} bytes at level {level}, jobs of {jobSize}, overlapLog {overlapLog}, "
                + $"dictionary {dictionaryKind}{(longDistance ? ", ldm" : string.Empty)}{(checksum ? ", checksum" : string.Empty)}, degree {degree}";
            byte[] expected = libzstd!.Compress(data, level, workers: 2, checksum, longDistance, dictionary, jobSize, overlapLog);
            byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(size)];
            int written = await compressor.CompressAsync(data, output, degree, TestContext.Current.CancellationToken);
            Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);
        }
    }
}
