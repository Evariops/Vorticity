using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using Vorticity.Zstd.Tests.Differential;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// Random dictionaries, raw content from 1 byte (under the 8 libzstd ignores) to 300 KB and trained
/// ones, at random levels, over random sources that copy from the dictionary as well as from
/// themselves, of sizes around the points where a frame changes the way it takes the dictionary
/// (attached up to 8 to 32 KiB, its tables copied below 128 KiB or six times its size, loaded beyond).
/// The compressors are reused from frame to frame, whatever way the last one took; the sources lie
/// against inaccessible pages. Every frame must be the platform's, given the dictionary prepared at
/// the same level, and decode to its content. A third of the optimal parsers' frames are made with
/// the long-distance matcher asked for.
/// </summary>
/// <remarks><c>VORTICITY_ZSTD_FUZZ_ITERATIONS</c> sets the number of frames per seed (default 300).</remarks>
public sealed class DictionaryFuzzTests
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_ZSTD_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out int n) ? n : 300;

    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_dictionaries_compress_like_libzstd(int seed)
    {
        var random = new Random(seed);
        var dictionaries = new List<byte[]>
        {
            Dictionaries.Get("text", "trained").Bytes,
            Dictionaries.Get("json", "trained").Bytes,
        };
        for (int i = 0; i < 4; i++)
        {
            int size = random.Next(4) switch
            {
                0 => random.Next(1, 17),
                1 => random.Next(17, 2_000),
                2 => random.Next(2_000, 40_000),
                _ => random.Next(40_000, 300_000),
            };
            dictionaries.Add(Generate(random, size, null));
        }

        var compressors = new Dictionary<(int, int, bool), (ZstdCompressor Zstd, ZstandardDictionary Native)>();
        using var source = new GuardedBuffer(1 << 20);
        try
        {
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                int index = random.Next(dictionaries.Count);
                byte[] dictionary = dictionaries[index];
                int level = random.Next(5) switch
                {
                    0 => -random.Next(1, 8),
                    1 => random.Next(1, 5),
                    2 or 3 => random.Next(5, 16),
                    _ => random.Next(16, 23),
                };
                bool longDistance = level >= 16 && random.Next(3) == 0;
                int size = random.Next(5) switch
                {
                    0 => random.Next(64),
                    1 => random.Next(40_000),
                    2 => 100_000 + random.Next(60_000),
                    3 => random.Next(Math.Min(1 << 20, (7 * dictionary.Length) + 1)),
                    _ => random.Next(1 << 20),
                };

                byte[] data = Generate(random, size, dictionary);
                Span<byte> src = random.Next(2) == 0 ? source.AtEnd(size) : source.AtStart(size);
                data.CopyTo(src);

                if (!compressors.TryGetValue((index, level, longDistance), out var pair))
                {
                    pair = (new ZstdCompressor(level, dictionary) { LongDistanceMatching = longDistance }, ZstandardDictionary.Create(dictionary, level));
                    compressors.Add((index, level, longDistance), pair);
                }

                string name = $"seed {seed}, iteration {iteration}: {size} bytes at level {level}, dictionary {index} of {dictionary.Length} bytes"
                    + (longDistance ? " with ldm" : string.Empty);
                byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(size)];
                Assert.True(pair.Zstd.Compress(src, output, out int consumed, out int written) == OperationStatus.Done, name);
                Assert.Equal(size, consumed);
                byte[] expected = NativeZstd.Compress(data, new ZstandardCompressionOptions { Dictionary = pair.Native, EnableLongDistanceMatching = longDistance });
                Corpus.AssertSameBytes(expected, output.AsSpan(0, written), name);

                byte[] decoded = new byte[size];
                Assert.True(new ZstdDecompressor(dictionary).Decompress(output.AsSpan(0, written), decoded, out _, out int decodedSize) == OperationStatus.Done, name);
                Corpus.AssertSameBytes(data, decoded.AsSpan(0, decodedSize), name);
            }
        }
        finally
        {
            foreach ((_, ZstandardDictionary native) in compressors.Values)
            {
                native.Dispose();
            }
        }
    }

    /// <summary>
    /// Content made of segments: noise, runs of one byte, words, copies of what precedes at short,
    /// medium and long distances, and copies of stretches of <paramref name="dictionary"/>, each
    /// sometimes altered by a byte.
    /// </summary>
    private static byte[] Generate(Random random, int size, byte[]? dictionary)
    {
        byte[] data = new byte[size];
        int i = 0;
        while (i < size)
        {
            int length = Math.Min(size - i, 1 + random.Next(random.Next(4) == 0 ? 2000 : 60));
            switch (random.Next(dictionary is null ? 6 : 8))
            {
                case 0:
                    random.NextBytes(data.AsSpan(i, length));
                    break;
                case 1:
                    data.AsSpan(i, length).Fill((byte)random.Next(256));
                    break;
                case 2:
                    for (int k = 0; k < length; k++)
                    {
                        data[i + k] = (byte)"etaoin shrdlu"[random.Next(13)];
                    }

                    break;
                case 6 or 7:
                {
                    // A stretch of the dictionary, its end included now and then.
                    int start = random.Next(4) == 0 ? Math.Max(0, dictionary!.Length - length) : random.Next(dictionary!.Length);
                    int count = Math.Min(length, dictionary.Length - start);
                    dictionary.AsSpan(start, count).CopyTo(data.AsSpan(i));
                    if (count > 0 && random.Next(3) == 0)
                    {
                        data[i + random.Next(count)] ^= (byte)(1 + random.Next(255));
                    }

                    length = Math.Max(count, 1);
                    break;
                }

                default:
                    if (i == 0)
                    {
                        goto case 0;
                    }

                    int distance = random.Next(3) switch
                    {
                        0 => 1 + random.Next(Math.Min(i, 16)),
                        1 => 1 + random.Next(Math.Min(i, 4096)),
                        _ => 1 + random.Next(i),
                    };
                    for (int k = 0; k < length; k++)
                    {
                        data[i + k] = data[i + k - distance];
                    }

                    if (random.Next(3) == 0)
                    {
                        data[i + random.Next(length)] ^= (byte)(1 + random.Next(255));
                    }

                    break;
            }

            i += length;
        }

        return data;
    }
}
