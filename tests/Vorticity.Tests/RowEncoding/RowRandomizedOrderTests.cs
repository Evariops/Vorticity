// docs/04-conformance.md §7, first bullet: the randomized order property.
//
// "Generate random tuples over a random schema and random per-column RowSortField settings; assert
// that sorting by memcmp of the encoded rows produces exactly the same permutation as sorting by
// tuple comparison. This is the whole contract, and it catches sign-bit, endianness, inversion and
// block-marker mistakes in one shot."
//
// The generator's value is that it produces combinations nobody would write by hand: a descending
// nulls-last struct whose Utf8 field is empty on one row and 33 bytes on the next, under a parent
// that is null on a third. The fixed tests in this folder each cover one rule; this covers their
// interactions.
//
// SEEDED, so a failure is reproducible from the test output alone. The reference oracle below is
// written independently of the encoder - it compares VALUES, not bytes - which is what makes
// agreement meaningful rather than circular.
using System;
using System.Collections.Generic;
using Vorticity.Types;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowRandomizedOrderTests
{
    private const int Iterations = 2000;

    [Fact]
    public void ByteOrderEqualsTupleOrderOverRandomSchemas()
    {
        Random random = new Random(20260912);
        int withNulls = 0;
        int withVariable = 0;
        int withNested = 0;

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            int rows = 1 + random.Next(24);
            int columnCount = 1 + random.Next(3);

            using RowFixture fixture = new RowFixture();
            int[] columns = new int[columnCount];
            RowSortField[] fields = new RowSortField[columnCount];
            object?[][] cells = new object?[columnCount][];
            Kind[] kinds = new Kind[columnCount];

            for (int c = 0; c < columnCount; c++)
            {
                kinds[c] = (Kind)random.Next((int)Kind.Count);
                fields[c] = new RowSortField(random.Next(2) == 0, random.Next(2) == 0);
                columns[c] = Build(fixture, kinds[c], rows, random, out cells[c]);

                foreach (object? cell in cells[c])
                {
                    if (cell is null)
                    {
                        withNulls++;
                        break;
                    }
                }

                if (kinds[c] is Kind.Utf8)
                {
                    withVariable++;
                }

                if (kinds[c] is Kind.Struct or Kind.FixedSizeList)
                {
                    withNested++;
                }
            }

            byte[][] encoded = RowTestHelp.Rows(fixture, columns, fields);
            int[] byOrder = RowTestHelp.ByteOrder(encoded);
            int[] byValue = RowTestHelp.ValueOrder(
                Sequence(rows),
                (a, b) =>
                {
                    for (int c = 0; c < columnCount; c++)
                    {
                        int comparison = CompareCell(cells[c][a], cells[c][b], fields[c]);
                        if (comparison != 0)
                        {
                            return comparison;
                        }
                    }

                    return 0;
                });

            Assert.True(
                AsEqual(byOrder, byValue),
                $"iteration {iteration}: byte order {string.Join(',', byOrder)} but tuple order " +
                $"{string.Join(',', byValue)} for kinds {string.Join(',', kinds)} and fields " +
                $"{string.Join(" | ", fields)}");
        }

        // The coverage assertion, without which a generator that emitted 300 non-null i32 columns
        // would report the same "passed" as one that exercised the format.
        Assert.True(withNulls > 1000, $"only {withNulls} columns had a null");
        Assert.True(withVariable > 300, $"only {withVariable} columns were variable-width");
        Assert.True(withNested > 600, $"only {withNested} columns were nested");
    }

    private enum Kind
    {
        Int32 = 0,
        Int64 = 1,
        UInt32 = 2,
        Float64 = 3,
        Bool = 4,
        Utf8 = 5,
        Struct = 6,
        FixedSizeList = 7,
        Count = 8,
    }

    /// <summary>Builds one random column, and the cells a value comparison will use.</summary>
    private static int Build(RowFixture fixture, Kind kind, int rows, Random random, out object?[] cells)
    {
        bool nullable = random.Next(3) != 0;
        bool[]? valid = nullable ? Validity(rows, random) : null;
        cells = new object?[rows];

        switch (kind)
        {
            case Kind.Int32:
            {
                int[] values = new int[rows];
                for (int i = 0; i < rows; i++)
                {
                    values[i] = Extreme(random) ? (random.Next(2) == 0 ? int.MinValue : int.MaxValue)
                        : random.Next(-40, 40);
                    cells[i] = Alive(valid, i) ? (long)values[i] : null;
                }

                return fixture.Primitive<int>(PType.I32, values, valid);
            }

            case Kind.Int64:
            {
                long[] values = new long[rows];
                for (int i = 0; i < rows; i++)
                {
                    values[i] = Extreme(random) ? (random.Next(2) == 0 ? long.MinValue : long.MaxValue)
                        : random.Next(-40, 40);
                    cells[i] = Alive(valid, i) ? values[i] : null;
                }

                return fixture.Primitive<long>(PType.I64, values, valid);
            }

            case Kind.UInt32:
            {
                uint[] values = new uint[rows];
                for (int i = 0; i < rows; i++)
                {
                    values[i] = Extreme(random) ? uint.MaxValue : (uint)random.Next(0, 80);
                    cells[i] = Alive(valid, i) ? (ulong)values[i] : null;
                }

                return fixture.Primitive<uint>(PType.U32, values, valid);
            }

            case Kind.Float64:
            {
                double[] values = new double[rows];
                for (int i = 0; i < rows; i++)
                {
                    // -0.0 and the infinities are in the pool on purpose: the total order the row
                    // format defines is not the one IEEE comparison gives.
                    values[i] = random.Next(8) switch
                    {
                        0 => -0.0,
                        1 => 0.0,
                        2 => double.NegativeInfinity,
                        3 => double.PositiveInfinity,
                        _ => (random.Next(-200, 200) / 8.0),
                    };
                    cells[i] = Alive(valid, i) ? values[i] : null;
                }

                return fixture.Primitive<double>(PType.F64, values, valid);
            }

            case Kind.Bool:
            {
                bool[] values = new bool[rows];
                for (int i = 0; i < rows; i++)
                {
                    values[i] = random.Next(2) == 0;
                    cells[i] = Alive(valid, i) ? values[i] : null;
                }

                return fixture.Bool(values, valid);
            }

            case Kind.Utf8:
            {
                byte[]?[] values = new byte[]?[rows];
                for (int i = 0; i < rows; i++)
                {
                    values[i] = Alive(valid, i) ? Text(random) : null;
                    cells[i] = values[i];
                }

                return fixture.VarBin(values);
            }

            case Kind.Struct:
            {
                // A fixed field and a variable one, which is the combination that exercises both
                // canonical null bodies at once.
                int[] numbers = new int[rows];
                byte[]?[] texts = new byte[]?[rows];
                bool[]? numberValid = random.Next(2) == 0 ? Validity(rows, random) : null;
                for (int i = 0; i < rows; i++)
                {
                    numbers[i] = random.Next(-20, 20);
                    texts[i] = random.Next(5) == 0 ? null : Text(random);
                }

                int a = fixture.Primitive<int>(PType.I32, numbers, numberValid);
                int b = fixture.VarBin(texts);
                for (int i = 0; i < rows; i++)
                {
                    cells[i] = Alive(valid, i)
                        ? new object?[] { Alive(numberValid, i) ? (long)numbers[i] : null, texts[i] }
                        : null;
                }

                return fixture.Struct(["a", "b"], [a, b], rows, valid);
            }

            default:
            {
                const int size = 2;
                int[] elements = new int[rows * size];
                bool[]? elementValid = random.Next(2) == 0 ? Validity(rows * size, random) : null;
                for (int i = 0; i < elements.Length; i++)
                {
                    elements[i] = random.Next(-20, 20);
                }

                int child = fixture.Primitive<int>(PType.I32, elements, elementValid);
                for (int i = 0; i < rows; i++)
                {
                    if (!Alive(valid, i))
                    {
                        cells[i] = null;
                        continue;
                    }

                    object?[] list = new object?[size];
                    for (int j = 0; j < size; j++)
                    {
                        int at = (i * size) + j;
                        list[j] = Alive(elementValid, at) ? (long)elements[at] : null;
                    }

                    cells[i] = list;
                }

                return fixture.FixedSizeList(child, size, rows, valid);
            }
        }
    }

    /// <summary>
    /// The oracle. Independent of the encoder by construction: it walks values, never bytes.
    /// </summary>
    /// <remarks>
    /// Nullness is decided FIRST and by <c>NullsFirst</c> alone; only the value comparison is
    /// reversed by <c>Descending</c>. Nested cells inherit the same field unchanged, which is the
    /// rule the encoder's parents implement by doing nothing.
    /// </remarks>
    private static int CompareCell(object? left, object? right, RowSortField field)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return field.NullsFirst ? -1 : 1;
        }

        if (right is null)
        {
            return field.NullsFirst ? 1 : -1;
        }

        int comparison = CompareValue(left, right, field);
        return comparison;
    }

    private static int CompareValue(object left, object right, RowSortField field)
    {
        int raw;
        switch (left)
        {
            case long l:
                raw = l.CompareTo((long)right);
                break;
            case ulong u:
                raw = u.CompareTo((ulong)right);
                break;
            case double d:
                raw = CompareDouble(d, (double)right);
                break;
            case bool b:
                raw = b.CompareTo((bool)right);
                break;
            case byte[] bytes:
                raw = bytes.AsSpan().SequenceCompareTo((byte[])right);
                break;
            default:
            {
                // A composite: compare element by element with the SAME field, and return its
                // result directly - the children have already applied the direction.
                object?[] a = (object?[])left;
                object?[] b = (object?[])right;
                for (int i = 0; i < a.Length; i++)
                {
                    int element = CompareCell(a[i], b[i], field);
                    if (element != 0)
                    {
                        return element;
                    }
                }

                return 0;
            }
        }

        return field.Descending ? -raw : raw;
    }

    /// <summary>
    /// The total order the row format defines over floats, stated as a RULE rather than as the
    /// encoder's bit trick: every negative sorts below every non-negative, and among negatives a
    /// bigger magnitude sorts lower. That puts -0.0 immediately below +0.0 and orders NaNs by
    /// payload, without re-deriving the XOR the encoder uses - which is what keeps this an oracle
    /// and not a copy.
    /// </summary>
    private static int CompareDouble(double left, double right)
    {
        ulong a = BitConverter.DoubleToUInt64Bits(left);
        ulong b = BitConverter.DoubleToUInt64Bits(right);
        bool negativeA = (a >> 63) != 0;
        bool negativeB = (b >> 63) != 0;
        if (negativeA != negativeB)
        {
            return negativeA ? -1 : 1;
        }

        int magnitude = (a & 0x7FFF_FFFF_FFFF_FFFFul).CompareTo(b & 0x7FFF_FFFF_FFFF_FFFFul);
        return negativeA ? -magnitude : magnitude;
    }

    private static bool[] Validity(int rows, Random random)
    {
        bool[] valid = new bool[rows];
        for (int i = 0; i < rows; i++)
        {
            valid[i] = random.Next(4) != 0;
        }

        return valid;
    }

    private static bool Alive(bool[]? valid, int index) => valid is null || valid[index];

    private static bool Extreme(Random random) => random.Next(8) == 0;

    private static byte[] Text(Random random)
    {
        // Lengths straddling the 32-byte block boundary, plus the empty string, because those are
        // the lengths the marker byte exists for.
        int length = random.Next(6) switch
        {
            0 => 0,
            1 => 31,
            2 => 32,
            3 => 33,
            4 => 64,
            _ => random.Next(1, 6),
        };

        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // A tiny alphabet, so prefixes and ties actually happen.
            bytes[i] = (byte)random.Next('a', 'd');
        }

        return bytes;
    }

    private static int[] Sequence(int count)
    {
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = i;
        }

        return values;
    }

    private static bool AsEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
