using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The shortcuts the write path takes on strings short enough to live in their views: equality read
/// from the view's words, and the bounds' order read from a big-endian key of the first bytes.
/// Both are held to the plain byte comparison they stand in for, on the values that make them
/// hard -- trailing zero bytes, bytes past the size that are not zero.
/// </summary>
public sealed class InlineViewTests
{
    [Fact]
    public void InlineEqualityReadsTheSizeAndNoFurther()
    {
        Random random = new Random(20260922);
        for (int size = 0; size <= 12; size++)
        {
            for (int trial = 0; trial < 200; trial++)
            {
                byte[] value = new byte[size];
                random.NextBytes(value);
                (ulong a0, ulong a1) = View(value, garbage: random);
                (ulong b0, ulong b1) = View(value, garbage: random);
                Assert.True(BlockStatsPass.InlineEqual(a0, a1, b0, b1, size), $"size {size}: equal values");

                if (size == 0)
                {
                    continue;
                }

                byte[] other = (byte[])value.Clone();
                other[random.Next(size)] ^= (byte)(1 + random.Next(255));
                (ulong c0, ulong c1) = View(other, garbage: random);
                Assert.False(BlockStatsPass.InlineEqual(a0, a1, c0, c1, size), $"size {size}: one byte apart");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(64)]
    public void BoundsKeyedOnTheirFirstBytesOrderAsTheirBytesDo(int limit)
    {
        // Values that tie on a key, differ only past eight bytes, or differ only by a trailing
        // zero: every place a key could order two strings the wrong way. The limits cut them
        // inside the key, at its edge and past it.
        byte[][] values =
        [
            [0x61], [0x61, 0x00], [0x61, 0x00, 0x00], [], [0x00], [0x00, 0x00, 0x01],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x00],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x01],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x01, 0x02, 0x03, 0x04, 0x05, 0x07],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0xFF],
            [0x7F], [0x80], [0x80, 0x00],
        ];

        Random random = new Random(7 + limit);
        for (int trial = 0; trial < 300; trial++)
        {
            int rows = 1 + random.Next(12);
            byte[][] column = new byte[rows][];
            for (int i = 0; i < rows; i++)
            {
                column[i] = values[random.Next(values.Length)];
            }

            using ColumnFixture fixture = new ColumnFixture();
            int node = GarbledNode(fixture, column, random);
            StringZones zones = new StringZones(limit);
            zones.Accumulate(fixture.Arena, fixture.Arena.GetNode(node), 0, rows);
            zones.Close();
            ZoneString zone = zones.All(1)![0];

            byte[] min = column[0];
            byte[] max = column[0];
            foreach (byte[] value in column)
            {
                min = value.AsSpan().SequenceCompareTo(min) < 0 ? value : min;
                max = value.AsSpan().SequenceCompareTo(max) > 0 ? value : max;
            }

            Assert.Equal(StringBounds.LowerBound(min, limit, utf8: false), zone.Min.ToArray());
            Assert.Equal(StringBounds.UpperBound(max, limit, utf8: false), zone.Max?.ToArray());
        }
    }

    /// <summary>
    /// A binary node over <paramref name="column"/> whose inline views carry random bytes past the
    /// size, as a builder that does not clear them would leave them.
    /// </summary>
    private static int GarbledNode(ColumnFixture fixture, byte[][] column, Random random)
    {
        byte[] views = new byte[column.Length * 16];
        List<byte> heap = [];
        for (int i = 0; i < column.Length; i++)
        {
            byte[] value = column[i];
            Span<byte> view = views.AsSpan(i * 16, 16);
            random.NextBytes(view);
            BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteInt32LittleEndian(view[8..12], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..16], heap.Count);
            heap.AddRange(value);
        }

        VortexBuffer data = heap.Count == 0
            ? VortexBuffer.Empty
            : fixture.Bytes(heap.ToArray());
        return fixture.Arena.AddVarBinView(
            fixture.Types.Binary(Nullability.NonNullable), column.Length, Validity.NonNullable,
            fixture.Bytes(views), [data]);
    }

    /// <summary>The two words of an inline view of <paramref name="value"/>, the bytes past its size random.</summary>
    private static (ulong Low, ulong High) View(byte[] value, Random garbage)
    {
        Span<byte> view = stackalloc byte[16];
        garbage.NextBytes(view);
        BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
        value.CopyTo(view[4..]);
        return (
            BinaryPrimitives.ReadUInt64LittleEndian(view),
            BinaryPrimitives.ReadUInt64LittleEndian(view[8..]));
    }
}
