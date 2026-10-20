// The codes a slice sorts its rows by: each row's rank among its column's values, in the order the slice's sort
// compares them, a null last. Every kind of number codes so, its edges among its values.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class SliceSortTests
{
    private const int Rows = 1_024;

    /// <summary>
    /// A column of each primitive type, of random values with the type's edges among them (its least and its greatest,
    /// zero and minus one, a float's zeros of either sign, its infinities and NaNs of several payloads) and a null in
    /// ten: any two rows' codes compare as the sort compares the rows, equal codes for equal values, nulls last.
    /// </summary>
    [Theory]
    [InlineData("I8")]
    [InlineData("I16")]
    [InlineData("I32")]
    [InlineData("I64")]
    [InlineData("U8")]
    [InlineData("U16")]
    [InlineData("U32")]
    [InlineData("U64")]
    [InlineData("F16")]
    [InlineData("F32")]
    [InlineData("F64")]
    public void ANumberColumnCodesItsRowsInTheSortsOrder(string type)
    {
        PType ptype = Enum.Parse<PType>(type);
        using ScanContext context = new ScanContext([]);
        CanonicalArena arena = context.Canonical;
        DTypeArena types = new DTypeArena();
        Random random = new Random((int)ptype + 7);
        int width = ptype.ByteWidth();
        VortexBuffer buffer = arena.Allocate(Rows * width, 64, out Span<byte> values);

        // Few distinct values among many rows, so that equal values meet, and the edges of the type.
        byte[] pool = new byte[64 * width];
        random.NextBytes(pool);
        for (int row = 0; row < Rows; row++)
        {
            pool.AsSpan(random.Next(64) * width, width).CopyTo(values.Slice(row * width, width));
        }

        byte[][] edges = Edges(ptype);
        for (int e = 0; e < edges.Length; e++)
        {
            edges[e].CopyTo(values.Slice(e * 7 * width, width));
        }

        VortexBuffer bits = arena.Allocate(((Rows + 63) / 64) * sizeof(ulong), sizeof(ulong), out Span<byte> validBytes);
        Span<ulong> valid = MemoryMarshal.Cast<byte, ulong>(validBytes);
        for (int row = 0; row < Rows; row++)
        {
            valid[row >> 6] |= random.Next(10) == 0 ? 0 : 1UL << (row & 63);
        }

        int validity = arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0);
        int column = arena.AddPrimitive(types.Primitive(ptype, Nullability.Nullable), Rows, Validity.Bitmap(validity), ptype, buffer);
        (int[] codes, int distinct) = SliceSort.CodesOfColumn(arena, column, Rows);
        for (int left = 0; left < Rows; left++)
        {
            for (int right = 0; right < Rows; right++)
            {
                int expected = Math.Sign(SliceSort.Compare(arena, column, left, right));
                Assert.True(
                    Math.Sign(codes[left].CompareTo(codes[right])) == expected,
                    $"{ptype}: rows {left} and {right} compare {expected}, their codes {codes[left]} and {codes[right]}");
            }

            Assert.InRange(codes[left], 0, distinct);
        }
    }

    /// <summary>The edges of <paramref name="ptype"/>'s values, each as its bytes.</summary>
    private static byte[][] Edges(PType ptype) => ptype switch
    {
        PType.I8 => [[0x80], [0x7F], [0x00], [0xFF], [0x01]],
        PType.I16 => [.. Bytes<short>(short.MinValue, short.MaxValue, 0, -1, 1)],
        PType.I32 => [.. Bytes<int>(int.MinValue, int.MaxValue, 0, -1, 1)],
        PType.I64 => [.. Bytes<long>(long.MinValue, long.MaxValue, 0, -1, 1)],
        PType.U8 => [[0x00], [0xFF], [0x80], [0x7F]],
        PType.U16 => [.. Bytes<ushort>(0, ushort.MaxValue, 0x8000, 0x7FFF)],
        PType.U32 => [.. Bytes<uint>(0, uint.MaxValue, 0x8000_0000, 0x7FFF_FFFF)],
        PType.U64 => [.. Bytes<ulong>(0, ulong.MaxValue, 0x8000_0000_0000_0000, 0x7FFF_FFFF_FFFF_FFFF)],
        PType.F16 => [.. Bytes<Half>(
            Half.Zero, Half.NegativeZero, Half.NaN, BitConverter.UInt16BitsToHalf(0x7E01), Half.PositiveInfinity, Half.NegativeInfinity,
            Half.MinValue, Half.MaxValue, Half.Epsilon, (Half)(-1))],
        PType.F32 => [.. Bytes<float>(
            0f, -0f, float.NaN, BitConverter.UInt32BitsToSingle(0x7FC0_0001), float.PositiveInfinity, float.NegativeInfinity,
            float.MinValue, float.MaxValue, float.Epsilon, -1f)],
        _ => [.. Bytes<double>(
            0d, -0d, double.NaN, BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001), double.PositiveInfinity, double.NegativeInfinity,
            double.MinValue, double.MaxValue, double.Epsilon, -1d)],
    };

    private static byte[][] Bytes<T>(params T[] values)
        where T : unmanaged =>
        Array.ConvertAll(values, value => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray());
}
