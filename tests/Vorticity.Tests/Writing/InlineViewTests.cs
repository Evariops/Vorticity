using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
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

    [Fact]
    public void BoundsKeyedOnTheirFirstBytesOrderAsTheirBytesDo()
    {
        // Values that tie on a key, differ only past eight bytes, or differ only by a trailing
        // zero: every place a key could order two strings the wrong way.
        byte[][] values =
        [
            [0x61], [0x61, 0x00], [0x61, 0x00, 0x00], [], [0x00], [0x00, 0x00, 0x01],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x00],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x01],
            [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0xFF],
            [0x7F], [0x80], [0x80, 0x00],
        ];

        Random random = new Random(7);
        for (int trial = 0; trial < 300; trial++)
        {
            int rows = 1 + random.Next(12);
            byte[]?[] column = new byte[]?[rows];
            for (int i = 0; i < rows; i++)
            {
                column[i] = values[random.Next(values.Length)];
            }

            using ColumnFixture fixture = new ColumnFixture();
            int node = fixture.Utf8Node(column, Nullability.NonNullable, DTypeKind.Binary);
            StringZones zones = new StringZones(limit: 64);
            zones.Accumulate(fixture.Arena, fixture.Arena.GetNode(node), 0, rows);
            zones.Close();
            ZoneString zone = zones.All(1)![0];

            byte[] min = column[0]!;
            byte[] max = column[0]!;
            foreach (byte[]? value in column)
            {
                min = value.AsSpan().SequenceCompareTo(min) < 0 ? value! : min;
                max = value.AsSpan().SequenceCompareTo(max) > 0 ? value! : max;
            }

            Assert.Equal(min, zone.Min.ToArray());
            Assert.Equal(max, Assert.NotNull(zone.Max).ToArray());
        }
    }

    /// <summary>The two words of an inline view of <paramref name="value"/>, the bytes past its size random.</summary>
    private static (ulong Low, ulong High) View(byte[] value, Random garbage)
    {
        Span<byte> view = stackalloc byte[16];
        garbage.NextBytes(view);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
        value.CopyTo(view[4..]);
        return (
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(view),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(view[8..]));
    }
}
