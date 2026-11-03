using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

/// <summary>
/// A row-encoded key with its texts unpadded (<see cref="UnpaddedKeys"/>) comes back whole, orders as the row encoding
/// does, keeps a prefix of values a prefix of bytes, and reads the same values.
/// </summary>
public sealed class UnpaddedKeysTests
{
    private const int Tuples = 400;

    private static readonly DTypeArena Types = new DTypeArena();

    // A key of an integer, a text, a binary and a boolean, as a dataset's sort key may hold them, nullable but the binary.
    private static readonly DType[] Shape =
    [
        Types.Primitive(PType.I64, Nullability.Nullable),
        Types.Utf8(Nullability.Nullable),
        Types.Binary(Nullability.NonNullable),
        Types.Bool(Nullability.Nullable),
    ];

    private static readonly int[] Widths = [9, RowEncoder.VariableWidth, RowEncoder.VariableWidth, 2];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AKeyComesBackWholeAndOrdersAsItsRows(bool nullsFirst)
    {
        RowSortField field = new RowSortField(descending: false, nullsFirst);
        Random random = new Random(nullsFirst ? 20261103 : 20261104);
        List<(FilterLiteral[] Values, byte[] Rows, byte[] Unpadded)> keys = [];
        for (int t = 0; t < Tuples; t++)
        {
            FilterLiteral[] values = Tuple(random);
            byte[] rows = Encode(values, field);
            byte[] unpadded = UnpaddedKeys.Narrow(rows, Widths);
            Assert.Equal(unpadded.Length, UnpaddedKeys.NarrowedLength(rows, Widths));
            Assert.Equal(rows, UnpaddedKeys.Widen(unpadded, Widths));
            Assert.Equal(rows.Length, UnpaddedKeys.WidenedLength(unpadded, Widths));
            keys.Add((values, rows, unpadded));

            // A prefix of the values codes a prefix of the bytes, the same as the key cut there.
            for (int count = 1; count < values.Length; count++)
            {
                byte[] prefix = UnpaddedKeys.Narrow(Encode(values[..count], field), Widths);
                Assert.True(unpadded.AsSpan().StartsWith(prefix), $"tuple {t}, {count} value(s)");
            }

            // Each value read where its length says it ends, as the row encoding reads it.
            int at = 0;
            int rowAt = 0;
            for (int i = 0; i < values.Length; i++)
            {
                int length = UnpaddedKeys.ValueLength(unpadded.AsSpan(at), Widths[i]);
                int rowLength = RowEncoder.ValueLength(rows.AsSpan(rowAt), Widths[i]);
                Assert.Equal(RowEncoder.ReadFirstValue(rows.AsSpan(rowAt), Shape[i]), UnpaddedKeys.ReadFirstValue(unpadded.AsSpan(at), Shape[i]));
                at += length;
                rowAt += rowLength;
            }

            Assert.Equal(unpadded.Length, at);
        }

        for (int i = 0; i < keys.Count; i++)
        {
            for (int j = 0; j < keys.Count; j += 7)
            {
                Assert.Equal(
                    Math.Sign(keys[i].Rows.AsSpan().SequenceCompareTo(keys[j].Rows)),
                    Math.Sign(keys[i].Unpadded.AsSpan().SequenceCompareTo(keys[j].Unpadded)));
            }
        }
    }

    [Fact]
    public void ATextInUtf8TakesItsBytesAndTwoMore()
    {
        byte[] rows = Encode([FilterLiteral.From(7L), FilterLiteral.From("checkout-service"), FilterLiteral.From(new byte[] { 0, 1, 2 }), FilterLiteral.Null], Ascending);
        byte[] unpadded = UnpaddedKeys.Narrow(rows, Widths);

        // The integer as it is, the text's sentinel, its 16 bytes and its end, the binary's zero and one escaped.
        Assert.Equal(9 + 18 + 7 + 2, unpadded.Length);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x01, 0x01, 0x02, 0x02, 0x00 }, unpadded.AsSpan(27, 7).ToArray());
        Assert.Equal(9 + 34 + 34 + 2, rows.Length);
    }

    [Fact]
    public void ValuesPastTheWidthsGivenAreCopiedAsTheyAre()
    {
        byte[] rows = Encode([FilterLiteral.From(-3L), FilterLiteral.From("a"), FilterLiteral.From(new byte[] { 9 }), FilterLiteral.From(true)], Ascending);
        byte[] unpadded = UnpaddedKeys.Narrow(rows, Widths[..2]);
        Assert.Equal(rows.AsSpan(9 + 34).ToArray(), unpadded.AsSpan(9 + 3).ToArray());
        Assert.Equal(rows, UnpaddedKeys.Widen(unpadded, Widths[..2]));
    }

    [Fact]
    public void ADescendingTextOrAnUntoldWidthIsNotCoded()
    {
        RowSortField descending = new RowSortField(descending: true, nullsFirst: false);
        byte[] text = RowEncoder.EncodeKey([FilterLiteral.From("x")], [Types.Utf8(Nullability.NonNullable)], [descending]);
        Assert.Throws<ArgumentException>(() => UnpaddedKeys.Narrow(text, [RowEncoder.VariableWidth]));
        Assert.Throws<ArgumentException>(() => UnpaddedKeys.Narrow([0x01, 0x02], [-1]));
        Assert.False(UnpaddedKeys.Codes([9, RowEncoder.VariableWidth, -1]));
        Assert.True(UnpaddedKeys.Codes(Widths));
    }

    [Fact]
    public void AnUnpaddedValueThatRunsPastItsKeyOrEscapesAnotherByteIsRefused()
    {
        int[] text = [RowEncoder.VariableWidth];
        Assert.Throws<VortexFormatException>(() => UnpaddedKeys.Widen([0x02, 0x61, 0x62], text));
        Assert.Throws<VortexFormatException>(() => UnpaddedKeys.Widen([0x02, 0x01, 0x07, 0x00], text));
        Assert.Throws<VortexFormatException>(() => UnpaddedKeys.Widen([0x02, 0x00], text));
        Assert.Equal(-1, UnpaddedKeys.ValueLength([0x02, 0x61], RowEncoder.VariableWidth));
    }

    private static readonly RowSortField Ascending = new RowSortField(descending: false, nullsFirst: false);

    private static byte[] Encode(FilterLiteral[] values, RowSortField field)
    {
        RowSortField[] fields = new RowSortField[values.Length];
        Array.Fill(fields, field);
        return RowEncoder.EncodeKey(values, Shape.AsSpan(0, values.Length), fields);
    }

    /// <summary>A tuple of the shape's values, each of a few on purpose so that keys share their leading values.</summary>
    private static FilterLiteral[] Tuple(Random random) =>
    [
        random.Next(8) == 0 ? FilterLiteral.Null : FilterLiteral.From((long)random.Next(-2, 3)),
        random.Next(8) == 0 ? FilterLiteral.Null : FilterLiteral.From(Bytes(random)),
        FilterLiteral.From(Bytes(random)),
        random.Next(5) == 0 ? FilterLiteral.Null : FilterLiteral.From(random.Next(2) == 0),
    ];

    /// <summary>Bytes of a length around a block's, each a zero, a one, a two, 0xFF or any, often a prefix of another's.</summary>
    private static byte[] Bytes(Random random)
    {
        int[] lengths = [0, 1, 2, 31, 32, 33, 63, 64, 65, random.Next(100)];
        byte[] bytes = new byte[lengths[random.Next(lengths.Length)]];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = random.Next(5) switch
            {
                0 => 0x00,
                1 => 0x01,
                2 => 0x02,
                3 => 0xFF,
                _ => (byte)random.Next(3, 6),
            };
        }

        return bytes;
    }
}
