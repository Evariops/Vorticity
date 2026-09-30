// The pco writer, checked as a reader would check it: every chunk's metadata parsed and every page
// decoded by the reader the reference's vectors hold to the bit, over each of the nine types pco
// stores and the shapes that make its compressor choose differently -- a constant, a ramp and a
// curve for the deltas, multiples of a divisor for IntMult, a skew for the bins, noise, the edges
// of a page and of a chunk, and nulls, which the stream leaves out.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed.Pco;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class PcoPlanTests
{
    // The types travel as their numbers: PType is internal, and a theory's data is public.
    public static TheoryData<int, string, int> Cases()
    {
        TheoryData<int, string, int> data = [];
        PType[] types = [PType.U16, PType.U32, PType.U64, PType.I16, PType.I32, PType.I64, PType.F16, PType.F32, PType.F64];
        string[] shapes = ["constant", "ramp", "curve", "multiples", "skew", "noise", "walk"];
        foreach (PType type in types)
        {
            foreach (string shape in shapes)
            {
                data.Add((int)type, shape, 20_000);
            }

            foreach (int rows in new[] { 1, 2, 3, 10, 255, 256, 257, 8192, 8193 })
            {
                data.Add((int)type, "walk", rows);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryValueDecodesBackBitForBit(int typeNumber, string shape, int rows)
    {
        PType type = (PType)typeNumber;
        ulong[] values = Values(type, shape, rows);
        RoundTrip(type, values, valid: null);
    }

    [Theory]
    [InlineData((int)PType.I64)]
    [InlineData((int)PType.F32)]
    [InlineData((int)PType.U16)]
    public void NullsAreLeftOutOfTheStream(int typeNumber)
    {
        PType type = (PType)typeNumber;
        ulong[] values = Values(type, "walk", 10_000);
        bool[] valid = [.. Enumerable.Range(0, values.Length).Select(i => i % 7 != 3)];
        RoundTrip(type, values, valid);
    }

    [Fact]
    public void AColumnPastAChunkIsCutIntoChunks()
    {
        ulong[] values = Values(PType.I32, "walk", PcoPlan.ValuesPerChunk + 1000);
        PcoPlan plan = RoundTrip(PType.I32, values, valid: null);
        Assert.Equal(2, plan.ChunkCount);
    }

    [Fact]
    public void MultiplesOfADivisorAreWrittenAsIntMult()
    {
        ulong[] values = Values(PType.I64, "multiples", 20_000);
        (CanonicalArena arena, int node) = Node(PType.I64, values, null);
        PcoPlan plan = PcoPlan.TryBuild(arena, node, long.MaxValue)!;
        try
        {
            byte[] data = plan.TakeData();
            (int start, int length) = plan.BufferAt(0);
            PcoChunkMeta meta = PcoChunkMeta.Read(PcoPlan.Header, data.AsSpan(start, length), PcoNumber.Of(PType.I64)!.Value);
            Assert.Equal(PcoModeKind.IntMult, meta.Mode);
            Assert.Equal(100UL, meta.ModeBase);
            System.Buffers.ArrayPool<byte>.Shared.Return(data);
        }
        finally
        {
            plan.Release();
        }
    }

    [Fact]
    public void ARampIsDeltaEncodedAndCostsAlmostNothing()
    {
        ulong[] values = Values(PType.I64, "ramp", 20_000);
        (CanonicalArena arena, int node) = Node(PType.I64, values, null);
        PcoPlan plan = PcoPlan.TryBuild(arena, node, long.MaxValue)!;
        try
        {
            Assert.True(plan.EncodedSize < 400, $"{plan.EncodedSize} bytes for a ramp");
        }
        finally
        {
            plan.Release();
        }
    }

    [Fact]
    public void ACeilingItCannotMeetIsRefused()
    {
        ulong[] values = Values(PType.U64, "noise", 20_000);
        (CanonicalArena arena, int node) = Node(PType.U64, values, null);
        Assert.Null(PcoPlan.TryBuild(arena, node, 10_000));
    }

    private static PcoPlan RoundTrip(PType type, ulong[] values, bool[]? valid)
    {
        (CanonicalArena arena, int node) = Node(type, values, valid);
        PcoPlan? built = PcoPlan.TryBuild(arena, node, long.MaxValue);
        Assert.NotNull(built);
        PcoPlan plan = built;
        PcoNumber number = PcoNumber.Of(type)!.Value;
        int width = number.LatentBits / 8;
        ulong[] expected = [.. values.Where((_, i) => valid is null || valid[i])];

        byte[] data = plan.TakeData();
        try
        {
            List<ulong> decoded = [];
            PcoLatentState[] states = [new PcoLatentState(), new PcoLatentState(), new PcoLatentState()];
            PcoBatchScratch scratch = new PcoBatchScratch(
                new ulong[PcoPageDecoder.BatchSize], new int[PcoPageDecoder.BatchSize],
                new long[PcoPageDecoder.BatchSize], states, new ulong[PcoPageDecoder.BatchSize]);
            ulong[] secondary = new ulong[PcoPageDecoder.BatchSize];
            int page = 0;
            for (int chunk = 0; chunk < plan.ChunkCount; chunk++)
            {
                (int metaStart, int metaLength) = plan.BufferAt(chunk);
                PcoChunkMeta meta = PcoChunkMeta.Read(PcoPlan.Header, data.AsSpan(metaStart, metaLength), number);
                for (int p = 0; p < plan.ChunkPages[chunk]; p++, page++)
                {
                    (int start, int length) = plan.BufferAt(plan.ChunkCount + page);
                    int count = plan.PageValues[page];
                    byte[] numbers = new byte[count * width];
                    PcoPageDecoder.DecodeJoined(meta, data.AsSpan(start, length), count, secondary, in scratch, numbers);
                    for (int i = 0; i < count; i++)
                    {
                        decoded.Add(width switch
                        {
                            2 => BinaryPrimitives.ReadUInt16LittleEndian(numbers.AsSpan(i * 2)),
                            4 => BinaryPrimitives.ReadUInt32LittleEndian(numbers.AsSpan(i * 4)),
                            _ => BinaryPrimitives.ReadUInt64LittleEndian(numbers.AsSpan(i * 8)),
                        });
                    }
                }

                meta.Release();
            }

            Assert.Equal(expected.Length, decoded.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != decoded[i])
                {
                    Assert.Fail($"{type}: value {i} decoded to {decoded[i]:x}, written {expected[i]:x}");
                }
            }

            long total = 0;
            for (int b = 0; b < plan.ChunkCount + plan.PageCount; b++)
            {
                total += plan.BufferAt(b).Length;
            }

            Assert.Equal(plan.EncodedSize, total);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(data);
            plan.Release();
        }

        return plan;
    }

    /// <summary>Values of <paramref name="shape"/> as the bits of <paramref name="type"/>, zero-extended.</summary>
    private static ulong[] Values(PType type, string shape, int rows)
    {
        Random random = new Random(rows + shape.Length);
        double walk = 0;
        ulong[] bits = new ulong[rows];
        for (int i = 0; i < rows; i++)
        {
            walk += random.NextDouble() - 0.5;
            double value = shape switch
            {
                "constant" => 42,
                "ramp" => 1000 + (3 * i),
                "curve" => (i * (double)i) / 50,
                "multiples" => random.Next(200) * 100,
                "skew" => Math.Floor(1 / (random.NextDouble() + 1e-4)),
                "noise" => random.NextDouble() * 60_000,
                _ => 20_000 + (walk * 40),
            };

            bits[i] = type switch
            {
                PType.U16 => (ushort)Math.Clamp(value, 0, ushort.MaxValue),
                PType.U32 => (uint)Math.Clamp(value, 0, uint.MaxValue),
                PType.U64 => shape == "noise" ? (ulong)random.NextInt64() * 3 : (ulong)Math.Max(value, 0),
                PType.I16 => (ushort)(short)Math.Clamp(value - 10_000, short.MinValue, short.MaxValue),
                PType.I32 => (uint)(int)(value - 10_000),
                PType.I64 => (ulong)(long)(value - 10_000),
                PType.F16 => BitConverter.HalfToUInt16Bits((Half)(value / 7)),
                PType.F32 => BitConverter.SingleToUInt32Bits((float)(value / 7)),
                _ => BitConverter.DoubleToUInt64Bits(shape == "walk" ? Math.Round(value, 2) : value / 7),
            };
        }

        if (type is PType.F32 or PType.F64 && rows > 10)
        {
            // The floats' edges: both zeros, an infinity and a NaN with a payload.
            bits[1] = type == PType.F32 ? 0x8000_0000UL : 0x8000_0000_0000_0000UL;
            bits[2] = type == PType.F32 ? 0x7F80_0000UL : 0x7FF0_0000_0000_0000UL;
            bits[3] = type == PType.F32 ? 0x7FC0_1234UL : 0x7FF8_0000_0000_1234UL;
        }

        return bits;
    }

    private static (CanonicalArena Arena, int Node) Node(PType type, ulong[] bits, bool[]? valid)
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        int width = type.ByteWidth();
        VortexBuffer buffer = arena.Allocate(Math.Max(bits.Length * width, 1), width, out Span<byte> bytes);
        for (int i = 0; i < bits.Length; i++)
        {
            switch (width)
            {
                case 2:
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes[(i * 2)..], (ushort)bits[i]);
                    break;
                case 4:
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[(i * 4)..], (uint)bits[i]);
                    break;
                default:
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes[(i * 8)..], bits[i]);
                    break;
            }
        }

        Validity validity = Validity.NonNullable;
        if (valid is not null)
        {
            VortexBuffer bitmap = arena.Allocate((bits.Length + 7) / 8, 1, out Span<byte> set);
            for (int i = 0; i < valid.Length; i++)
            {
                if (valid[i])
                {
                    set[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), bits.Length, Validity.NonNullable, bitmap, 0));
        }

        DType dtype = types.Primitive(type, valid is null ? Nullability.NonNullable : Nullability.Nullable);
        return (arena, arena.AddPrimitive(dtype, bits.Length, validity, type, buffer));
    }
}
