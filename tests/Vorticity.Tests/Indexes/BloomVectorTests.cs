// The split-block Bloom filter against the reference's own, byte for byte.
//
// `tools/conformance-gen/examples/gen_bloom_vectors.rs` builds each filter through vortex 0.86.1's
// public `Accumulator` over a typed array, so the vectors carry upstream's hash, seed, block
// choice, salt order AND its rule for which bytes a value hashes as. Our side inserts the row bytes
// the generator recorded; equal filters prove both halves at once. Regenerate with:
//
//   cd tools/conformance-gen && cargo run --release --example gen_bloom_vectors > \
//     ../../tests/Vorticity.Tests/Indexes/BloomVectors.json
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class BloomVectorTests
{
    internal sealed record Vector(string Name, string DType, int Blocks, byte[]?[] Rows, byte[] Filter);

    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> cases = [];
        foreach (Vector vector in Load())
        {
            cases.Add(vector.Name, vector.Blocks);
        }

        return cases;
    }

    [Fact]
    public void TheVectorsCoverEveryHashedShape()
    {
        HashSet<string> dtypes = [];
        foreach (Vector vector in Load())
        {
            dtypes.Add(vector.DType);
        }

        Assert.Equal(
            new SortedSet<string> { "binary", "f32", "f64", "i16", "i32", "i64", "i8", "u16", "u32", "u64", "u8", "utf8" },
            new SortedSet<string>(dtypes));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OurFilterIsTheReferencesByteForByte(string name, int blocks)
    {
        Vector vector = Find(name, blocks);
        uint[] words = new uint[blocks * SplitBlockBloom.WordsPerBlock];
        foreach (byte[]? row in vector.Rows)
        {
            if (row is not null)
            {
                SplitBlockBloom.Insert(words, SplitBlockBloom.Hash(row, BloomHash.XxHash3));
            }
        }

        Assert.Equal(vector.Filter.Length, words.Length * sizeof(uint));
        Assert.Equal(Convert.ToHexString(vector.Filter), Convert.ToHexString(MemoryMarshal.AsBytes(words.AsSpan())));

        // And read back from the reference's bytes, every inserted value is found.
        ReadOnlySpan<uint> theirs = SplitBlockBloom.Words(vector.Filter);
        foreach (byte[]? row in vector.Rows)
        {
            if (row is not null)
            {
                Assert.True(SplitBlockBloom.Contains(theirs, SplitBlockBloom.Hash(row, BloomHash.XxHash3)));
            }
        }
    }

    /// <summary>
    /// The vectors a builder can size exactly: at 1 ppm the formula asks for more blocks than the
    /// vector's count, so the clamp decides -- except where a vector holds too few values to
    /// reach it, which is every 64-block vector.
    /// </summary>
    public static TheoryData<string, int> WriterCases()
    {
        TheoryData<string, int> cases = [];
        foreach (Vector vector in Load())
        {
            if (SplitBlockBloom.BlocksFor(Distinct(vector), 1, vector.Blocks) == vector.Blocks)
            {
                cases.Add(vector.Name, vector.Blocks);
            }
        }

        return cases;
    }

    private static int Distinct(Vector vector)
    {
        HashSet<string> distinct = [];
        foreach (byte[]? row in vector.Rows)
        {
            if (row is not null)
            {
                distinct.Add(Convert.ToHexString(row));
            }
        }

        return distinct.Count;
    }

    [Fact]
    public void TheWriterCasesCoverEveryDTypeAtTwoSizes()
    {
        HashSet<(string, int)> covered = [];
        foreach (Vector vector in Load())
        {
            if (SplitBlockBloom.BlocksFor(Distinct(vector), 1, vector.Blocks) == vector.Blocks)
            {
                covered.Add((vector.DType, vector.Blocks));
            }
        }

        Assert.Equal(12 * 2, covered.Count);
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task TheWritersBlockFilterIsTheReferencesToo(string name, int blocks)
    {
        // The half the first test cannot see: the builder reads a CANONICAL column -- a primitive
        // buffer at its width, a view that is inline or points into a data buffer, a validity
        // bitmap -- and the bytes it hashes must be the ones the reference hashed.
        Vector vector = Find(name, blocks);
        IndexSpec policy = IndexSpec.Bloom(falsePositivePpm: 1, resolutions: 1, maxBlocks: blocks, minDistinct: 0);

        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        int node = Column(arena, types, vector);
        using BloomBuilder builder = new BloomBuilder(policy);
        builder.Accumulate(arena, node, 0, vector.Rows.Length);
        builder.CloseBlock();
        await builder.EndOfDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(blocks, Assert.Single(builder.BlockFilterBlocks));
        PendingPayload payload = Assert.IsType<PendingPayload>(Assert.Single(builder.Runs).Blocks);

        // The payload as the index writer lays it out: a u32 array, the filter's words in order.
        CanonicalArena laid = new CanonicalArena();
        CanonicalNode array = laid.GetNode(payload.Build(laid, types));
        Assert.Equal(PType.U32, array.PType);
        Assert.Equal(Convert.ToHexString(vector.Filter), Convert.ToHexString(array.Values.Span));
    }

    private static int Column(CanonicalArena arena, DTypeArena types, Vector vector)
    {
        int length = vector.Rows.Length;
        bool nullable = Array.Exists(vector.Rows, row => row is null);
        Nullability nullability = nullable ? Nullability.Nullable : Nullability.NonNullable;
        Validity validity = Validity.NonNullable;
        if (nullable)
        {
            VortexBuffer bits = arena.Allocate(Math.Max((length + 7) / 8, 1), 8, out Span<byte> raw);
            raw.Clear();
            for (int i = 0; i < length; i++)
            {
                if (vector.Rows[i] is not null)
                {
                    raw[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), length, Validity.NonNullable, bits, 0));
        }

        if (vector.DType is "utf8" or "binary")
        {
            int heap = 0;
            foreach (byte[]? row in vector.Rows)
            {
                heap += row is { Length: > 12 } ? row.Length : 0;
            }

            VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
            VortexBuffer views = arena.Allocate(length * 16, 16, out Span<byte> viewBytes);
            viewBytes.Clear();
            int offset = 0;
            for (int i = 0; i < length; i++)
            {
                byte[] row = vector.Rows[i] ?? [];
                Span<byte> view = viewBytes.Slice(i * 16, 16);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, row.Length);
                if (row.Length <= 12)
                {
                    row.CopyTo(view[4..]);
                    continue;
                }

                row.AsSpan(0, 4).CopyTo(view[4..]);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
                row.CopyTo(dataBytes[offset..]);
                offset += row.Length;
            }

            DType dtype = vector.DType == "utf8" ? types.Utf8(nullability) : types.Binary(nullability);
            return arena.AddVarBinView(dtype, length, validity, views, [data]);
        }

        PType ptype = vector.DType switch
        {
            "i8" => PType.I8,
            "u8" => PType.U8,
            "i16" => PType.I16,
            "u16" => PType.U16,
            "i32" => PType.I32,
            "u32" => PType.U32,
            "i64" => PType.I64,
            "u64" => PType.U64,
            "f32" => PType.F32,
            _ => PType.F64,
        };
        int width = ptype.ByteWidth();
        VortexBuffer values = arena.Allocate(length * width, width, out Span<byte> valueBytes);
        valueBytes.Clear();
        for (int i = 0; i < length; i++)
        {
            vector.Rows[i]?.CopyTo(valueBytes[(i * width)..]);
        }

        return arena.AddPrimitive(types.Primitive(ptype, nullability), length, validity, ptype, values);
    }

    [Fact]
    public void SizingFollowsParquetsFormulaAndItsClamp()
    {
        // m = -8 n / ln(1 - p^(1/8)) bits: 1 000 values at 1 % need ~1 211 bytes, 38 blocks, 64.
        Assert.Equal(64, SplitBlockBloom.BlocksFor(1_000, 10_000, 4_096));
        Assert.Equal(1, SplitBlockBloom.BlocksFor(1, 10_000, 4_096));
        Assert.Equal(1, SplitBlockBloom.BlocksFor(0, 10_000, 4_096));
        Assert.Equal(4_096, SplitBlockBloom.BlocksFor(10_000_000, 10_000, 4_096));
        Assert.Equal(100, SplitBlockBloom.BlocksFor(10_000_000, 10_000, 100));

        // A lower rate never needs fewer blocks.
        int previous = 0;
        foreach (int ppm in new[] { 500_000, 100_000, 10_000, 1_000, 100, 1 })
        {
            int blocks = SplitBlockBloom.BlocksFor(8_192, ppm, 1 << 20);
            Assert.True(blocks >= previous, $"{ppm} ppm gave {blocks} blocks after {previous}");
            Assert.True(int.IsPow2(blocks));
            previous = blocks;
        }
    }

    [Fact]
    public void TheParquetVariantIsXxHash64()
    {
        // The known XXH64 of the empty input with seed 0, and XXH3's, which differ.
        Assert.Equal(0xEF46DB3751D8E999UL, SplitBlockBloom.Hash([], BloomHash.XxHash64));
        Assert.Equal(0x2D06800538D394C2UL, SplitBlockBloom.Hash([], BloomHash.XxHash3));
    }

    [Fact]
    public void AMeasuredFalsePositiveRateStaysNearItsTarget()
    {
        const int Inserted = 8_192;
        int blocks = SplitBlockBloom.BlocksFor(Inserted, 10_000, 1 << 20);
        uint[] words = new uint[blocks * SplitBlockBloom.WordsPerBlock];
        Span<byte> key = stackalloc byte[sizeof(long)];
        for (long i = 0; i < Inserted; i++)
        {
            BitConverterWrite(key, i);
            SplitBlockBloom.Insert(words, SplitBlockBloom.Hash(key, BloomHash.XxHash3));
        }

        int positives = 0;
        const int Probes = 200_000;
        for (long i = Inserted; i < Inserted + Probes; i++)
        {
            BitConverterWrite(key, i);
            if (SplitBlockBloom.Contains(words, SplitBlockBloom.Hash(key, BloomHash.XxHash3)))
            {
                positives++;
            }
        }

        // Asked for 1 %; the power-of-two rounding only lowers it. Twice the target is the alarm.
        double rate = (double)positives / Probes;
        Assert.InRange(rate, 0.0, 0.02);
    }

    /// <summary>
    /// The probe against the split-block rule spelled out -- the block from the hash's upper
    /// half, one salted bit in each of its eight words -- on a filter dense enough that both
    /// answers are common, and every bit a lane can name among the probes.
    /// </summary>
    [Fact]
    public void TheProbeAnswersTheRuleWordByWord()
    {
        uint[] salts = [0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31];
        uint[] words = new uint[4 * SplitBlockBloom.WordsPerBlock];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < 200; i++)
        {
            state = (state * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL;
            SplitBlockBloom.Insert(words, state);
        }

        int present = 0;
        for (int i = 0; i < 20_000; i++)
        {
            state = (state * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL;
            int block = (int)(((state >> 32) * 4UL) >> 32);
            bool expected = true;
            for (int w = 0; w < 8; w++)
            {
                uint bit = 1u << (int)(((uint)state * salts[w]) >> 27);
                expected &= (words[(block * 8) + w] & bit) != 0;
            }

            bool actual = SplitBlockBloom.Contains(words, state);
            Assert.Equal(expected, actual);
            present += actual ? 1 : 0;
        }

        Assert.InRange(present, 100, 19_900);
    }

    /// <summary>
    /// The inlined short paths of XxHash3 against the library, at every length the writer hands
    /// them: 1 to 3, 4, 8 and 9 to 16 bytes, over random inputs and the all-zero and all-one ones.
    /// </summary>
    [Fact]
    public void TheFixedWidthHashesAreXxHash3()
    {
        Random random = new Random(20260917);
        byte[] buffer = new byte[16];
        for (int round = 0; round < 20_000; round++)
        {
            if (round == 0)
            {
                Array.Fill(buffer, (byte)0);
            }
            else if (round == 1)
            {
                Array.Fill(buffer, (byte)0xFF);
            }
            else
            {
                random.NextBytes(buffer);
            }

            for (int length = 1; length <= 3; length++)
            {
                Assert.Equal(XxHash3.HashToUInt64(buffer.AsSpan(0, length)), XxHash3Fixed.Hash1To3(buffer.AsSpan(0, length)));
            }

            Assert.Equal(XxHash3.HashToUInt64(buffer.AsSpan(0, 4)), XxHash3Fixed.Hash4(BitConverter.ToUInt32(buffer, 0)));
            Assert.Equal(XxHash3.HashToUInt64(buffer.AsSpan(0, 8)), XxHash3Fixed.Hash8(BitConverter.ToUInt64(buffer, 0)));
            for (int length = 9; length <= 16; length++)
            {
                Assert.Equal(XxHash3.HashToUInt64(buffer.AsSpan(0, length)), XxHash3Fixed.Hash9To16(buffer.AsSpan(0, length)));
            }
        }
    }

    private static void BitConverterWrite(Span<byte> destination, long value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(destination, value);

    private static Vector Find(string name, int blocks)
    {
        foreach (Vector vector in Load())
        {
            if (vector.Name == name && vector.Blocks == blocks)
            {
                return vector;
            }
        }

        throw new InvalidOperationException($"no vector {name}/{blocks}");
    }

    private static Vector[]? _cache;

    internal static Vector[] Load([CallerFilePath] string thisFile = "")
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        string path = Path.Combine(new FileInfo(thisFile).Directory!.FullName, "BloomVectors.json");
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        List<Vector> vectors = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            List<byte[]?> rows = [];
            foreach (JsonElement row in element.GetProperty("rows").EnumerateArray())
            {
                rows.Add(row.ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(row.GetString()!));
            }

            vectors.Add(new Vector(
                element.GetProperty("name").GetString()!,
                element.GetProperty("dtype").GetString()!,
                element.GetProperty("blocks").GetInt32(),
                [.. rows],
                Convert.FromHexString(element.GetProperty("filter").GetString()!)));
        }

        _cache = [.. vectors];
        return _cache;
    }
}
