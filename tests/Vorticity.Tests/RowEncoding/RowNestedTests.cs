// Structs and fixed-size lists: option inheritance and the canonical null body.
//
// Two rules here are easy to state and easy to get wrong in opposite directions.
//
// OPTION INHERITANCE IS IDENTITY. A nested field uses its root column's RowSortField unchanged,
// and inversion for `descending` is applied by the LEAF to its own value bytes only. The classic
// trap is a parent that re-inverts the body its children just inverted, which silently restores
// ascending order; it is avoided by the parent doing nothing at all, so the test for it compares
// a nested leaf's bytes against the same leaf encoded as a top-level column.
//
// NULL BODIES ARE CANONICALIZED. A null struct or null fixed-size list still emits a body, so two
// null parents compare byte-equal no matter what their child arrays physically contain. A
// round-trip test cannot see this rule; only an array with live values under a null can.
using System;
using Vorticity.Types;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowNestedTests
{
    /// <summary>A struct is its sentinel followed by its fields, recursively, in schema order.</summary>
    [Fact]
    public void StructIsSentinelThenFields()
    {
        using RowFixture fixture = new RowFixture();
        int a = fixture.Primitive<int>(PType.I32, [7]);
        int b = fixture.Primitive<byte>(PType.U8, [3]);
        int row = fixture.Struct(["a", "b"], [a, b], rows: 1);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 01 80 00 00 07 01 03"), rows[0]);
    }

    /// <summary>
    /// A null struct with a fixed-width child: the child contributes its own null encoding, zero
    /// fill included, so every null row of the column is the same width and the same bytes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullStructWithFixedChildIsSentinelAndZeroFill(bool descending)
    {
        using RowFixture fixture = new RowFixture();
        // The child holds live values under the null parent; they must not reach the output.
        int a = fixture.Primitive<int>(PType.I32, [0x1234_5678, 9]);
        int row = fixture.Struct(["a"], [a], rows: 2, valid: [false, true]);
        byte[][] rows = RowTestHelp.Rows(
            fixture, row, new RowSortField(descending, nullsFirst: true));

        // Neither the sentinel nor the zero fill is inverted by `descending`.
        Assert.Equal(RowTestHelp.Hex("00 00 00 00 00 00"), rows[0]);
        Assert.Equal(0x01, rows[1][0]);
    }

    /// <summary>
    /// A null struct with a VARIABLE child collapses that child to a single sentinel byte, which
    /// is the only way two null parents over children of different lengths can be byte-equal.
    /// </summary>
    [Fact]
    public void NullStructRowsWithDifferentChildLengthsAreByteEqual()
    {
        using RowFixture fixture = new RowFixture();
        int names = fixture.VarBin(
            [RowTestHelp.Utf8("short"), RowTestHelp.Utf8("x"), RowTestHelp.Utf8("much longer text data")]);
        int row = fixture.Struct(["name"], [names], rows: 3, valid: [false, true, false]);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal(rows[0], rows[2]);
        Assert.Equal(RowTestHelp.Hex("00 00"), rows[0]);
        Assert.NotEqual(rows[0][0], rows[1][0]);
    }

    /// <summary>
    /// Option inheritance is identity and inversion happens ONCE: a leaf nested in a descending
    /// struct produces the same value bytes as the same leaf encoded as a descending top-level
    /// column. A parent that re-inverted the body would silently restore ascending order.
    /// </summary>
    [Fact]
    public void NestedLeafBytesMatchTheSameLeafAtTopLevel()
    {
        RowSortField field = new RowSortField(descending: true, nullsFirst: true);

        using RowFixture nested = new RowFixture();
        int inner = nested.Primitive<int>(PType.I32, [1, -1, 0]);
        int leaf = nested.Struct(["v"], [inner], rows: 3);
        byte[][] nestedRows = RowTestHelp.Rows(nested, leaf, field);

        using RowFixture flat = new RowFixture();
        int bare = flat.Primitive<int>(PType.I32, [1, -1, 0]);
        byte[][] flatRows = RowTestHelp.Rows(flat, bare, field);

        for (int i = 0; i < 3; i++)
        {
            // Drop the struct's own sentinel; the remainder must be the leaf, byte for byte.
            Assert.Equal(flatRows[i], nestedRows[i].AsSpan(1).ToArray());
        }
    }

    /// <summary>Nesting recurses: a struct inside a struct inherits the same options again.</summary>
    [Fact]
    public void NestedStructsRecurse()
    {
        using RowFixture fixture = new RowFixture();
        int leaf = fixture.Primitive<int>(PType.I32, [7]);
        int middle = fixture.Struct(["y"], [leaf], rows: 1);
        int outer = fixture.Struct(["x"], [middle], rows: 1);
        byte[][] rows = RowTestHelp.Rows(fixture, outer, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 01 01 80 00 00 07"), rows[0]);
    }

    /// <summary>Structs sort as tuples of their fields.</summary>
    [Fact]
    public void StructsSortAsTuples()
    {
        int[] first = [2, 1, 2, 1];
        int[] second = [10, 30, 5, 20];
        using RowFixture fixture = new RowFixture();
        int a = fixture.Primitive<int>(PType.I32, first);
        int b = fixture.Primitive<int>(PType.I32, second);
        int row = fixture.Struct(["a", "b"], [a, b], rows: 4);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        // (1,20) < (1,30) < (2,5) < (2,10)
        Assert.Equal([3, 1, 2, 0], RowTestHelp.ByteOrder(rows));
    }

    /// <summary>A struct mixing fixed and variable fields keeps its column boundaries aligned.</summary>
    [Fact]
    public void StructMixingFixedAndVariableFieldsSortsAsATuple()
    {
        int[] ids = [1, 1, 2];
        string[] names = ["b", "a", "a"];
        using RowFixture fixture = new RowFixture();
        byte[]?[] bytes = new byte[]?[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            bytes[i] = RowTestHelp.Utf8(names[i]);
        }

        int id = fixture.Primitive<int>(PType.I32, ids);
        int name = fixture.VarBin(bytes);
        int row = fixture.Struct(["id", "name"], [id, name], rows: 3);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal([1, 0, 2], RowTestHelp.ByteOrder(rows));
    }

    /// <summary>A fixed-size list is its sentinel followed by its elements, back to back.</summary>
    [Fact]
    public void FixedSizeListIsSentinelThenElements()
    {
        using RowFixture fixture = new RowFixture();
        int elements = fixture.Primitive<int>(PType.I32, [1, 2, 3, 4]);
        int row = fixture.FixedSizeList(elements, size: 2, rows: 2);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("01 01 80 00 00 01 01 80 00 00 02"), rows[0]);
        Assert.Equal(RowTestHelp.Hex("01 01 80 00 00 03 01 80 00 00 04"), rows[1]);
    }

    /// <summary>
    /// A null fixed-size list with fixed-width elements emits one null element encoding per slot,
    /// so its width is the same as a non-null row's and two null rows are byte-equal.
    /// </summary>
    [Fact]
    public void NullFixedSizeListEmitsOneNullPerElement()
    {
        using RowFixture fixture = new RowFixture();
        int elements = fixture.Primitive<int>(PType.I32, [9, 9, 1, 2]);
        int row = fixture.FixedSizeList(elements, size: 2, rows: 2, valid: [false, true]);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("00 00 00 00 00 00 00 00 00 00 00"), rows[0]);
        Assert.Equal(rows[0].Length, rows[1].Length);
    }

    /// <summary>
    /// A null fixed-size list with VARIABLE elements collapses each element to one sentinel byte,
    /// so its body is exactly as long as the list is - never as long as the data happens to be.
    /// </summary>
    [Fact]
    public void NullFixedSizeListWithVariableElementsIsOneByteEach()
    {
        using RowFixture fixture = new RowFixture();
        int elements = fixture.VarBin(
        [
            RowTestHelp.Utf8("a long value that would dominate the row"),
            RowTestHelp.Utf8("b"),
            RowTestHelp.Utf8("c"),
            RowTestHelp.Utf8("d"),
        ]);
        int row = fixture.FixedSizeList(elements, size: 2, rows: 2, valid: [false, true]);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal(RowTestHelp.Hex("00 00 00"), rows[0]);
        Assert.Equal(1 + 34 + 34, rows[1].Length);
    }

    /// <summary>Fixed-size lists sort element by element, like the tuples they are.</summary>
    [Fact]
    public void FixedSizeListsSortElementwise()
    {
        using RowFixture fixture = new RowFixture();
        int elements = fixture.Primitive<int>(PType.I32, [2, 1, 1, 9, 1, 3]);
        int row = fixture.FixedSizeList(elements, size: 2, rows: 3);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        // (1,3) < (1,9) < (2,1)
        Assert.Equal([2, 1, 0], RowTestHelp.ByteOrder(rows));
    }

    /// <summary>
    /// A zero-length fixed-size list is legal upstream and must not divide by its size: every row
    /// is the sentinel alone.
    /// </summary>
    [Fact]
    public void AZeroLengthFixedSizeListIsASentinelAlone()
    {
        using RowFixture fixture = new RowFixture();
        int elements = fixture.Primitive<int>(PType.I32, []);
        int row = fixture.FixedSizeList(elements, size: 0, rows: 3, valid: [true, false, true]);
        byte[][] rows = RowTestHelp.Rows(fixture, row, RowSortField.Ascending);

        Assert.Equal([0x01], rows[0]);
        Assert.Equal([0x00], rows[1]);
        Assert.Equal([0x01], rows[2]);
    }
}
