using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class DictionaryGroupKeysTests
{
    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly uint[] Codes = [0, 1, 2, 2, 1, 0, 0, 1, 3, 3, 2, 0, 1, 3, 2, 1];

    // The later batches of a chunk meet codes the first did not, and find the groups of the others.
    [Fact]
    public void TheBatchesOfOnePublishedDictionaryGroupByItsValues()
    {
        long[] values = [10, 20, 30, 40];
        GroupKeys keys = new FixedKeys<long>(Shape(VortexType.Int64), sorted: false);
        RetainingArena chunk = new RetainingArena();
        int dictionary = Integers(chunk, values);
        chunk.Seal();

        AssertGroupedAs(keys, chunk, dictionary, 0, 8, row => values[Codes[row]]);
        AssertGroupedAs(keys, chunk, dictionary, 8, 8, row => values[Codes[row]]);
        Assert.Equal(4, keys.Count);
    }

    // The arena of a chunk is refilled for the next chunk, which puts other values under the same
    // codes at the same node: only the publication tells the two apart.
    [Fact]
    public void ADictionaryPublishedAgainGroupsByItsNewValues()
    {
        long[] first = [10, 20, 30, 40];
        long[] second = [40, 30, 10, 20];
        GroupKeys keys = new FixedKeys<long>(Shape(VortexType.Int64), sorted: false);
        RetainingArena chunk = new RetainingArena();
        int dictionary = Integers(chunk, first);
        chunk.Seal();
        AssertGroupedAs(keys, chunk, dictionary, 0, 16, row => first[Codes[row]]);

        chunk.Reset();
        Assert.Equal(dictionary, Integers(chunk, second));
        chunk.Seal();

        AssertGroupedAs(keys, chunk, dictionary, 0, 16, row => second[Codes[row]]);
    }

    [Fact]
    public void ADictionaryNotPublishedGroupsEachBatchByItsOwnValues()
    {
        long[] first = [10, 20, 30, 40];
        long[] second = [40, 30, 10, 20];
        GroupKeys keys = new FixedKeys<long>(Shape(VortexType.Int64), sorted: false);
        CanonicalArena chunk = new CanonicalArena();
        int dictionary = Integers(chunk, first);
        AssertGroupedAs(keys, chunk, dictionary, 0, 16, row => first[Codes[row]]);

        chunk.Reset();
        Assert.Equal(dictionary, Integers(chunk, second));

        AssertGroupedAs(keys, chunk, dictionary, 0, 16, row => second[Codes[row]]);
    }

    [Fact]
    public void ATextDictionaryPublishedAgainGroupsByItsNewValues()
    {
        string[] first = ["red", "green", "blue", "a value longer than a view holds"];
        string[] second = ["blue", "a value longer than a view holds", "red", "green"];
        GroupKeys keys = new BytesKeys(Shape(VortexType.Utf8), sorted: false);
        RetainingArena chunk = new RetainingArena();
        int dictionary = Strings(chunk, first);
        chunk.Seal();
        AssertGroupedAs(keys, chunk, dictionary, 0, 16, row => first[Codes[row]]);

        chunk.Reset();
        Assert.Equal(dictionary, Strings(chunk, second));
        chunk.Seal();

        AssertGroupedAs(keys, chunk, dictionary, 0, 16, row => second[Codes[row]]);
    }

    private static void AssertGroupedAs<T>(GroupKeys keys, CanonicalArena chunk, int dictionary, int start, int rows, Func<int, T> expected)
    {
        RetainingArena batch = new RetainingArena();
        int node = CanonicalSlice.SliceAcross(chunk, batch, dictionary, start, rows);
        int[] rowGroups = new int[rows];

        bool ranged = keys.Assign(batch, [node], rows, [(1UL << rows) - 1], rowGroups, new GroupRanges());

        Assert.False(ranged);
        Func<int, T> key = keys.Reader<T>(0);
        for (int row = 0; row < rows; row++)
        {
            Assert.Equal(expected(start + row), key(rowGroups[row]));
        }
    }

    private static ColumnShape Shape(VortexType type) =>
        new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, []));

    private static int Integers(CanonicalArena arena, long[] values)
    {
        DType dtype = Types.Primitive(PType.I64, Nullability.NonNullable);
        VortexBuffer buffer = arena.Allocate(values.Length * sizeof(long), sizeof(long), out Span<byte> bytes);
        values.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(bytes));
        int node = arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.I64, buffer);
        return Dictionary(arena, dtype, node);
    }

    private static int Strings(CanonicalArena arena, string[] values)
    {
        DType dtype = Types.Utf8(Nullability.NonNullable);
        VortexBuffer heap = arena.Allocate(values.Length * 64, 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        int used = 0;
        for (int i = 0; i < values.Length; i++)
        {
            byte[] value = Encoding.UTF8.GetBytes(values[i]);
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value.CopyTo(heapBytes[used..]);
            value.AsSpan(0, 4).CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
            used += value.Length;
        }

        int node = arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, [heap]);
        return Dictionary(arena, dtype, node);
    }

    private static int Dictionary(CanonicalArena arena, DType dtype, int values)
    {
        VortexBuffer codes = arena.Allocate(Codes.Length * sizeof(uint), sizeof(uint), out Span<byte> bytes);
        Codes.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(bytes));
        return arena.AddDictionary(dtype, Codes.Length, Validity.NonNullable, codes, values);
    }
}
