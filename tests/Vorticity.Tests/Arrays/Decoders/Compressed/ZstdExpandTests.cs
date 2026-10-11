using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// A nullable column's dense values spread over its valid rows, held to the rows filled one by
/// one: every width, a view's sixteen bytes and an odd fixed width among them, validity from sparse
/// to nearly full so that each word is empty, whole or mixed, bitmaps starting mid-byte, row counts
/// past the last whole word, and exactly as many values as valid rows, so a word near the end
/// cannot read its values a vector at a time. Into rows zeroed before, and into rows of other
/// bytes, which a spread over them writes zero where a row is null.
/// </summary>
public sealed class ZstdExpandTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(12, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(12, true)]
    [InlineData(16, true)]
    public void EachValidRowTakesTheNextValueAndEachNullRowZero(int width, bool over)
    {
        Random random = new Random(width * 7919);
        foreach (int rows in new[] { 1, 63, 64, 65, 200, 1_000 })
        {
            foreach (int percent in new[] { 3, 30, 50, 90, 99 })
            {
                foreach (int bitOffset in new[] { 0, 5 })
                {
                    bool[] valid = new bool[rows];
                    int count = 0;
                    for (int i = 0; i < rows; i++)
                    {
                        valid[i] = random.Next(100) < percent;
                        count += valid[i] ? 1 : 0;
                    }

                    byte[] values = new byte[count * width];
                    random.NextBytes(values);
                    byte[] destination = new byte[rows * width];
                    CanonicalArena arena = new CanonicalArena();
                    ValidityMask mask = ValidityMask.From(arena, Bitmap(arena, valid, bitOffset));
                    if (over)
                    {
                        // Rows of other bytes, as a block of the pool holds: each null one must be written.
                        destination.AsSpan().Fill(0xCD);
                        ValidRows.SpreadOver(values, destination, in mask, rows, width, ZstdDecoder.Id);
                    }
                    else
                    {
                        ValidRows.Spread(values, destination, in mask, rows, width, ZstdDecoder.Id);
                    }

                    int next = 0;
                    for (int row = 0; row < rows; row++)
                    {
                        ReadOnlySpan<byte> expected = valid[row] ? values.AsSpan(next++ * width, width) : new byte[width];
                        Assert.True(
                            expected.SequenceEqual(destination.AsSpan(row * width, width)),
                            $"rows {rows}, {percent}% valid, offset {bitOffset}: row {row}");
                    }
                }
            }
        }
    }

    private static Validity Bitmap(CanonicalArena arena, bool[] valid, int bitOffset)
    {
        VortexBuffer buffer = arena.Allocate((valid.Length + bitOffset + 7) / 8, 1, out Span<byte> bits);

        // Bits outside the rows are set, so a kernel that reads one it should not shows.
        bits.Fill(0xFF);
        for (int i = 0; i < valid.Length; i++)
        {
            int at = bitOffset + i;
            if (!valid[i])
            {
                bits[at >> 3] &= (byte)~(1 << (at & 7));
            }
        }

        DTypeArena types = new DTypeArena();
        return Validity.Bitmap(arena.AddBool(
            types.Bool(Nullability.NonNullable), valid.Length, Validity.NonNullable, buffer, bitOffset));
    }
}
