// The contract BETWEEN the two passes, which no order test and no golden vector can see.
//
// Pass 1 says how many bytes each row needs; pass 2 writes them. If pass 1 over-counts, the keys
// still sort correctly and still match their golden bytes - they simply carry trailing bytes
// nobody wrote, which read as whatever the pooled buffer happened to hold. That is a real bug
// (two encodings of the same row can then differ) and it is invisible to every other test in this
// folder, so it gets its own: encode the same input into two destinations pre-filled with
// DIFFERENT bytes and require the results to be identical. A byte the encoder never wrote is the
// one thing that would differ.
using System;
using Vorticity.Columns;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.RowEncoding;
using Xunit;

namespace Vorticity.Tests.RowEncoding;

public sealed class RowTwoPassTests
{
    /// <summary>Builds a column set exercising every encoder path, including both null bodies.</summary>
    private static int[] EveryPath(RowFixture fixture, out RowSortField[] fields)
    {
        int ints = fixture.Primitive<int>(PType.I32, [1, -2, 3, 4], [true, true, false, true]);
        int flags = fixture.Bool([true, false, true, false], [true, false, true, true]);
        int nulls = fixture.Nulls(4);
        int money = fixture.Decimal([1, -2, 3, 4], precision: 7, scale: 2, DecimalStorageType.I64);
        int text = fixture.VarBin(
        [
            RowTestHelp.Utf8("a value long enough to cross the thirty-two byte block boundary"),
            null,
            [],
            RowTestHelp.Utf8("x"),
        ]);

        int structChildText = fixture.VarBin(
            [RowTestHelp.Utf8("one"), RowTestHelp.Utf8("two"), RowTestHelp.Utf8("three"), null]);
        int structChildInt = fixture.Primitive<int>(PType.I32, [9, 8, 7, 6]);
        int composite = fixture.Struct(
            ["t", "i"], [structChildText, structChildInt], rows: 4, valid: [true, false, true, false]);

        int fslElements = fixture.Primitive<int>(PType.I32, [1, 2, 3, 4, 5, 6, 7, 8]);
        int list = fixture.FixedSizeList(fslElements, size: 2, rows: 4, valid: [true, false, true, true]);

        int varElements = fixture.VarBin(
            [RowTestHelp.Utf8("p"), null, RowTestHelp.Utf8("qq"), RowTestHelp.Utf8("rrr"),
             [], RowTestHelp.Utf8("s"), RowTestHelp.Utf8("t"), RowTestHelp.Utf8("u")]);
        int varList = fixture.FixedSizeList(varElements, size: 2, rows: 4, valid: [false, true, true, false]);

        fields =
        [
            RowSortField.Ascending,
            new RowSortField(descending: true, nullsFirst: false),
            RowSortField.Ascending,
            new RowSortField(descending: true, nullsFirst: true),
            new RowSortField(descending: false, nullsFirst: false),
            RowSortField.Ascending,
            new RowSortField(descending: true, nullsFirst: true),
            RowSortField.Ascending,
        ];

        return [ints, flags, nulls, money, text, composite, list, varList];
    }

    /// <summary>
    /// Every byte the size pass reserved is written by the write pass - proved by writing into two
    /// differently pre-filled buffers and requiring the same answer.
    /// </summary>
    [Fact]
    public void EveryReservedByteIsWritten()
    {
        using RowFixture fixture = new RowFixture();
        int[] columns = EveryPath(fixture, out RowSortField[] fields);
        const int rows = 4;

        int[] sizes = new int[rows];
        int total = RowEncoder.ComputeSizes(fixture.Arena, columns, fields, sizes);
        int[] offsets = new int[rows];
        Assert.Equal(total, RowEncoder.ComputeOffsets(sizes, offsets));

        byte[] overZeros = new byte[total];
        byte[] overOnes = new byte[total];
        Array.Fill(overOnes, (byte)0xFF);

        int[] cursorsA = new int[rows];
        int[] cursorsB = new int[rows];
        RowEncoder.Encode(fixture.Arena, columns, fields, offsets, cursorsA, overZeros);
        RowEncoder.Encode(fixture.Arena, columns, fields, offsets, cursorsB, overOnes);

        Assert.Equal(overZeros, overOnes);

        // And the write pass advanced each cursor by exactly what the size pass reserved, which is
        // what lets the cursor array double as the output's per-row sizes.
        Assert.Equal(sizes, cursorsA);
        Assert.Equal(sizes, cursorsB);
    }

    /// <summary>The pooled entry point agrees with the span entry point, byte for byte.</summary>
    [Fact]
    public void ThePooledAndSpanEntryPointsAgree()
    {
        using RowFixture fixture = new RowFixture();
        int[] columns = EveryPath(fixture, out RowSortField[] fields);
        const int rows = 4;

        int[] sizes = new int[rows];
        int total = RowEncoder.ComputeSizes(fixture.Arena, columns, fields, sizes);
        int[] offsets = new int[rows];
        _ = RowEncoder.ComputeOffsets(sizes, offsets);
        byte[] manual = new byte[total];
        RowEncoder.Encode(fixture.Arena, columns, fields, offsets, new int[rows], manual);

        using RowKeys keys = RowEncoder.Encode(fixture.Arena, columns, fields);
        Assert.Equal(rows, keys.RowCount);
        Assert.Equal(total, keys.TotalBytes);
        Assert.Equal(manual, keys.Elements.ToArray());
        Assert.Equal(sizes, keys.Sizes.ToArray());
        Assert.Equal(offsets, keys.Offsets.ToArray());
    }

    /// <summary>Encoding is deterministic: the same input twice gives the same bytes.</summary>
    [Fact]
    public void EncodingTheSameInputTwiceGivesTheSameBytes()
    {
        using RowFixture fixture = new RowFixture();
        int[] columns = EveryPath(fixture, out RowSortField[] fields);

        using RowKeys first = RowEncoder.Encode(fixture.Arena, columns, fields);
        byte[] snapshot = first.Elements.ToArray();
        using RowKeys second = RowEncoder.Encode(fixture.Arena, columns, fields);

        Assert.Equal(snapshot, second.Elements.ToArray());
    }

    /// <summary>
    /// The write pass allocates nothing once the pools are warm. Its buffers are rented, and the
    /// scratch a variable-width child needs is rented too - a per-call allocation there would be
    /// paid on every batch of a sort.
    /// </summary>
    [Fact]
    public void AWarmWritePassAllocatesNothing()
    {
        using RowFixture fixture = new RowFixture();
        int[] columns = EveryPath(fixture, out RowSortField[] fields);
        const int rows = 4;

        int[] sizes = new int[rows];
        int total = RowEncoder.ComputeSizes(fixture.Arena, columns, fields, sizes);
        int[] offsets = new int[rows];
        _ = RowEncoder.ComputeOffsets(sizes, offsets);
        byte[] destination = new byte[total];
        int[] cursors = new int[rows];

        // Warm the array pools and the JIT.
        for (int i = 0; i < 8; i++)
        {
            Array.Clear(cursors);
            RowEncoder.Encode(fixture.Arena, columns, fields, offsets, cursors, destination);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++)
        {
            Array.Clear(cursors);
            RowEncoder.Encode(fixture.Arena, columns, fields, offsets, cursors, destination);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    /// <summary>The sizing pass does not walk fixed-width columns, so its cost is theirs to prove.</summary>
    [Fact]
    public void SizesAreTheSumOfTheColumnsContributions()
    {
        using RowFixture fixture = new RowFixture();
        int ints = fixture.Primitive<int>(PType.I32, [1, 2, 3]);
        int text = fixture.VarBin([RowTestHelp.Utf8("a"), [], null]);
        RowSortField[] fields = [RowSortField.Ascending, RowSortField.Ascending];

        int[] sizes = new int[3];
        int total = RowEncoder.ComputeSizes(fixture.Arena, [ints, text], fields, sizes);

        // i32 is 5 bytes on every row; the text is 34, 1 and 1.
        Assert.Equal([5 + 34, 5 + 1, 5 + 1], sizes);
        Assert.Equal(5 + 34 + 5 + 1 + 5 + 1, total);
    }

    /// <summary>The batch overload encodes the root struct's fields as columns, in schema order.</summary>
    [Fact]
    public void TheBatchOverloadEncodesTheRootFields()
    {
        using RowFixture fixture = new RowFixture();
        int a = fixture.Primitive<int>(PType.I32, [2, 1]);
        int b = fixture.Primitive<int>(PType.I32, [10, 20]);
        int root = fixture.Struct(["a", "b"], [a, b], rows: 2);
        RowSortField[] fields = [RowSortField.Ascending, RowSortField.Ascending];

        using RecordBatch batch = new RecordBatch(fixture.Arena, root, 0);
        using RowKeys fromBatch = RowEncoder.Encode(batch, fields);
        using RowKeys fromColumns = RowEncoder.Encode(fixture.Arena, [a, b], fields);

        Assert.Equal(fromColumns.Elements.ToArray(), fromBatch.Elements.ToArray());
    }

    [Fact]
    public void SortingIndicesPutsThemInKeyOrder()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<int>(PType.I32, [5, 1, 4, 1, 9], [true, true, false, true, true]);
        using RowKeys keys = RowEncoder.Encode(
            fixture.Arena, [column], [new RowSortField(descending: false, nullsFirst: false)]);

        int[] indices = [0, 1, 2, 3, 4];
        keys.SortIndices(indices);

        // Nulls last: 1, 1, 5, 9, then the null.
        Assert.Equal(2, indices[^1]);
        Assert.True(keys.Compare(indices[0], indices[1]) <= 0);
        Assert.True(keys.Compare(indices[1], indices[2]) <= 0);
        Assert.True(keys.Compare(indices[2], indices[3]) <= 0);
    }

    [Fact]
    public void DisposedKeysRefuseEveryAccessor()
    {
        using RowFixture fixture = new RowFixture();
        int column = fixture.Primitive<int>(PType.I32, [1]);
        RowKeys keys = RowEncoder.Encode(fixture.Arena, [column], [RowSortField.Ascending]);
        keys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => keys.Row(0).Length);
        Assert.Throws<ObjectDisposedException>(() => keys.Elements.Length);
        Assert.Throws<ObjectDisposedException>(() => keys.Offsets.Length);
        Assert.Throws<ObjectDisposedException>(() => keys.Sizes.Length);

        // Disposing twice must not return a pooled buffer twice, which would be corruption.
        keys.Dispose();
    }
}
