using System;
using System.Collections.Generic;
using System.Text;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>
/// A filter by mask against the same filter by indices, row for row: a struct, itself nullable,
/// of every form the mask packs -- values of one to eight bytes, 128-bit decimals, views, bools
/// read from a bit offset, a dictionary's codes, a list's offsets and sizes -- and of forms it
/// leaves to the indices, 256-bit decimals; each with nulls, under masks keeping no row, one row
/// in eight, half, seven in eight, every row, and runs of rows, over lengths either side of a word.
/// </summary>
public sealed class MaskFilterTests
{
    [Theory]
    [InlineData(1_000)]
    [InlineData(1_280)]
    public void AMaskKeepsTheRowsItsIndicesKeep(int rows)
    {
        Random random = new Random(rows);
        (CanonicalArena arena, int root) = Build(random, rows);
        ValidityMask rootValid = ValidityMask.From(arena, arena.GetNode(root).Validity);
        foreach (string density in new[] { "none", "eighth", "half", "most", "all", "runs", "valid" })
        {
            ulong[] mask = new ulong[(rows + 63) / 64];
            List<int> kept = [];
            for (int row = 0; row < rows; row++)
            {
                bool keep = density switch
                {
                    "none" => false,
                    "eighth" => random.Next(8) == 0,
                    "half" => random.Next(2) == 0,
                    "most" => random.Next(8) != 0,
                    "all" => true,
                    "valid" => rootValid.IsValid(row),
                    _ => (row / 97) % 3 != 1,
                };
                if (keep)
                {
                    mask[row >> 6] |= 1UL << (row & 63);
                    kept.Add(row);
                }
            }

            int byIndices = CanonicalFilter.Apply(arena, root, kept.ToArray());
            int byMask = CanonicalFilter.Apply(arena, root, kept.ToArray(), mask);
            Assert.Equal(kept.Count, arena.GetNode(byMask).Length);
            Kinds(arena, byIndices, byMask, density);
            string[] expected = Render(arena, byIndices);
            string[] actual = Render(arena, byMask);
            for (int row = 0; row < expected.Length; row++)
            {
                Assert.True(expected[row] == actual[row], $"{density}, {rows} rows: row {row} is {actual[row]}, not {expected[row]}");
            }
        }
    }

    private static (CanonicalArena Arena, int Root) Build(Random random, int rows)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        List<int> fields = [];
        List<DType> fieldTypes = [];
        List<string> names = [];

        void Field(string name, DType type, int node)
        {
            names.Add(name);
            fieldTypes.Add(type);
            fields.Add(node);
        }

        foreach (PType ptype in new[] { PType.U8, PType.U16, PType.I32, PType.I64, PType.F64 })
        {
            DType type = types.Primitive(ptype, Nullability.Nullable);
            int width = ptype.ByteWidth();
            VortexBuffer values = arena.Allocate(rows * width, width, out Span<byte> bytes);
            random.NextBytes(bytes);
            Field(ptype.ToString(), type, arena.AddPrimitive(type, rows, Nulls(arena, types, random, rows), ptype, values));
        }

        foreach ((DecimalStorageType storage, byte precision) in new[] { (DecimalStorageType.I128, (byte)30), (DecimalStorageType.I256, (byte)60) })
        {
            DType type = types.Decimal(precision, 2, Nullability.Nullable);
            int width = DecimalStorage.ByteWidth(storage);
            VortexBuffer values = arena.Allocate(rows * width, width, out Span<byte> bytes);
            random.NextBytes(bytes);
            Field(storage.ToString(), type, arena.AddDecimal(type, rows, Nulls(arena, types, random, rows), storage, precision, 2, values));
        }

        {
            // A bitmap read from bit 3 of its buffer.
            DType type = types.Bool(Nullability.Nullable);
            VortexBuffer bits = arena.Allocate(((rows + 3) / 8) + 1, 1, out Span<byte> bytes);
            random.NextBytes(bytes);
            Field("bool", type, arena.AddBool(type, rows, Nulls(arena, types, random, rows), bits, 3));
        }

        {
            DType type = types.Utf8(Nullability.Nullable);
            VortexBuffer views = arena.Allocate(rows * 16, 16, out Span<byte> bytes);
            for (int row = 0; row < rows; row++)
            {
                int length = random.Next(13);
                bytes[row * 16] = (byte)length;
                for (int b = 0; b < length; b++)
                {
                    bytes[(row * 16) + 4 + b] = (byte)('a' + random.Next(26));
                }
            }

            Field("utf8", type, arena.AddVarBinView(type, rows, Nulls(arena, types, random, rows), views, default));
        }

        {
            DType valueType = types.Primitive(PType.I32, Nullability.NonNullable);
            VortexBuffer dictionaryValues = arena.Allocate(10 * 4, 4, out Span<byte> valueBytes);
            random.NextBytes(valueBytes);
            int valuesNode = arena.AddPrimitive(valueType, 10, Validity.NonNullable, PType.I32, dictionaryValues);
            DType type = types.Primitive(PType.I32, Nullability.Nullable);
            VortexBuffer codes = arena.Allocate(rows * 4, 4, out Span<byte> codeBytes);
            for (int row = 0; row < rows; row++)
            {
                codeBytes[row * 4] = (byte)random.Next(10);
            }

            Field("dictionary", type, arena.AddDictionary(type, rows, Nulls(arena, types, random, rows), codes, valuesNode));
        }

        {
            DType elementType = types.Primitive(PType.I16, Nullability.NonNullable);
            VortexBuffer elementValues = arena.Allocate(100 * 2, 2, out Span<byte> elementBytes);
            random.NextBytes(elementBytes);
            int elements = arena.AddPrimitive(elementType, 100, Validity.NonNullable, PType.I16, elementValues);
            DType type = types.List(elementType, Nullability.Nullable);
            VortexBuffer offsets = arena.Allocate(rows * 4, 4, out Span<byte> offsetBytes);
            VortexBuffer sizes = arena.Allocate(rows * 2, 2, out Span<byte> sizeBytes);
            for (int row = 0; row < rows; row++)
            {
                int offset = random.Next(90);
                offsetBytes[row * 4] = (byte)offset;
                sizeBytes[row * 2] = (byte)random.Next(100 - offset);
            }

            Field("list", type, arena.AddListView(type, rows, Nulls(arena, types, random, rows), elements, offsets, PType.I32, sizes, PType.U16));
        }

        DType schema = types.Struct(names.ToArray(), fieldTypes.ToArray(), Nullability.Nullable);
        return (arena, arena.AddStruct(schema, rows, Nulls(arena, types, random, rows), fields.ToArray()));
    }

    /// <summary>A validity with about one row in five null, its bitmap read from bit 5 of its buffer.</summary>
    private static Validity Nulls(CanonicalArena arena, DTypeArena types, Random random, int rows)
    {
        VortexBuffer bits = arena.Allocate(((rows + 5) / 8) + 1, 1, out Span<byte> bytes);
        for (int row = 0; row < rows; row++)
        {
            if (random.Next(5) != 0)
            {
                bytes[(row + 5) >> 3] |= (byte)(1 << ((row + 5) & 7));
            }
        }

        return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 5));
    }

    /// <summary>The two filters' validities collapse alike: all valid, all invalid, or a bitmap, field by field.</summary>
    private static void Kinds(CanonicalArena arena, int expected, int actual, string density)
    {
        CanonicalNode left = arena.GetNode(expected);
        CanonicalNode right = arena.GetNode(actual);
        Assert.True(left.Validity.Kind == right.Validity.Kind, $"{density}: a {left.Kind} validity is {right.Validity.Kind}, not {left.Validity.Kind}");
        if (left.Kind == CanonicalKind.Struct)
        {
            for (int f = 0; f < left.FieldCount; f++)
            {
                Kinds(arena, arena.GetNode(expected).GetFieldIndex(f), arena.GetNode(actual).GetFieldIndex(f), density);
            }
        }
    }

    /// <summary>Each row as text: its validity and what it holds, field by field.</summary>
    private static string[] Render(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        string[] rows = new string[node.Length];
        ValidityMask valid = ValidityMask.From(arena, node.Validity);
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = valid.IsValid(row) ? "" : "null:";
        }

        switch (node.Kind)
        {
            case CanonicalKind.Struct:
                for (int f = 0; f < node.FieldCount; f++)
                {
                    string[] field = Render(arena, node.GetFieldIndex(f));
                    for (int row = 0; row < rows.Length; row++)
                    {
                        rows[row] += "|" + field[row];
                    }
                }

                break;
            case CanonicalKind.Primitive:
            case CanonicalKind.Decimal:
            {
                int width = node.Values.Length / Math.Max(node.Length, 1);
                for (int row = 0; row < rows.Length; row++)
                {
                    rows[row] += Convert.ToHexString(node.Values.Span.Slice(row * width, width));
                }

                break;
            }

            case CanonicalKind.Bool:
                for (int row = 0; row < rows.Length; row++)
                {
                    int bit = node.BitOffset + row;
                    rows[row] += ((node.Bits.Span[bit >> 3] >> (bit & 7)) & 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                break;
            case CanonicalKind.VarBinView:
                for (int row = 0; row < rows.Length; row++)
                {
                    rows[row] += Convert.ToHexString(node.Views.Span.Slice(row * 16, 16));
                }

                break;
            case CanonicalKind.Dictionary:
                for (int row = 0; row < rows.Length; row++)
                {
                    rows[row] += Convert.ToHexString(node.Codes.Span.Slice(row * 4, 4));
                }

                break;
            case CanonicalKind.ListView:
                for (int row = 0; row < rows.Length; row++)
                {
                    rows[row] += Convert.ToHexString(node.Offsets.Span.Slice(row * 4, 4)) + "+" + Convert.ToHexString(node.Sizes.Span.Slice(row * 2, 2));
                }

                break;
            default:
                throw new InvalidOperationException($"no rendering for {node.Kind}");
        }

        return rows;
    }
}
