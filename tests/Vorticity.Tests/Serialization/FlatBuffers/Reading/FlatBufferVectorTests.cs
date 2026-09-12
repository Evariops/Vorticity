// Strings, byte vectors and vectors of tables/strings.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferVectorTests
{
    [Fact]
    public void Reads_a_string_without_its_terminator()
    {
        FlatBufferTable table = FlatBufferTable.Root(StringsAndVectors);

        ReadOnlySpan<byte> value = table.GetStringUtf8(0);

        Assert.Equal(5, value.Length);
        Assert.True(value.SequenceEqual("hello"u8));
    }

    [Fact]
    public void Reads_a_byte_vector()
    {
        FlatBufferTable table = FlatBufferTable.Root(StringsAndVectors);

        ReadOnlySpan<byte> value = table.GetByteVector(1);

        ReadOnlySpan<byte> expected = [1, 2, 3];
        Assert.Equal(3, value.Length);
        Assert.True(value.SequenceEqual(expected));
    }

    [Fact]
    public void An_empty_vector_reads_as_empty_not_as_absent()
    {
        FlatBufferTable table = FlatBufferTable.Root(StringsAndVectors);

        Assert.True(table.HasField(4));
        Assert.True(table.GetByteVector(4).IsEmpty);
        Assert.Equal(0, table.GetVector(4).Count);
        Assert.True(table.GetStructVector<uint>(4).IsEmpty);
    }

    [Fact]
    public void An_absent_vector_reads_as_empty()
    {
        FlatBufferTable table = FlatBufferTable.Root(StringsAndVectors);

        Assert.True(table.GetByteVector(5).IsEmpty);
        Assert.True(table.GetStringUtf8(5).IsEmpty);
        Assert.Equal(0, table.GetVector(5).Count);
        Assert.Equal(0, table.GetVector(99).Count);
    }

    [Fact]
    public void Reads_a_vector_of_tables()
    {
        FlatBufferTable table = FlatBufferTable.Root(StringsAndVectors);

        FlatBufferVector children = table.GetVector(2);

        Assert.Equal(2, children.Count);
        Assert.Equal(7u, children.GetTable(0).GetUInt32(0));
        Assert.Equal(9u, children.GetTable(1).GetUInt32(0));
    }

    [Fact]
    public void Reads_a_vector_of_strings()
    {
        FlatBufferTable table = FlatBufferTable.Root(StringsAndVectors);

        FlatBufferVector names = table.GetVector(3);

        Assert.Equal(2, names.Count);
        Assert.True(names.GetStringUtf8(0).SequenceEqual("ab"u8));
        Assert.True(names.GetStringUtf8(1).SequenceEqual("xyz"u8));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Vector_index_outside_the_count_is_rejected(int index)
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            FlatBufferVector children = FlatBufferTable.Root(StringsAndVectors).GetVector(2);
            _ = children.GetTable(index).IsNull;
        });

        Assert.Throws<VortexFormatException>(() =>
        {
            FlatBufferVector names = FlatBufferTable.Root(StringsAndVectors).GetVector(3);
            _ = names.GetStringUtf8(index).Length;
        });
    }

    [Fact]
    public void Indexing_an_absent_vector_is_rejected()
    {
        Assert.Throws<VortexFormatException>(() =>
        {
            FlatBufferVector empty = FlatBufferTable.Root(StringsAndVectors).GetVector(5);
            _ = empty.GetTable(0).IsNull;
        });
    }

    [Fact]
    public void A_string_of_length_zero_is_not_absent()
    {
        // [44] is the "hello" length prefix; zeroing it leaves an empty but present string, and
        // the NUL that used to terminate "hello" now terminates the empty string.
        byte[] bytes = With(StringsAndVectors, 44, 0x00, 0x00, 0x00, 0x00);
        bytes[48] = 0x00;

        FlatBufferTable table = FlatBufferTable.Root(bytes);

        Assert.True(table.HasField(0));
        Assert.True(table.GetStringUtf8(0).IsEmpty);
    }

    [Fact]
    public void A_string_may_end_exactly_at_the_last_byte_of_the_buffer()
    {
        // Boundary in the accepting direction: the string at [44] may run to length 77, because
        // 44 + 4 + 77 + 1 == 126 == the buffer length, and byte [125] is a NUL.
        byte[] bytes = With(StringsAndVectors, 44, 0x4D, 0x00, 0x00, 0x00);

        FlatBufferTable table = FlatBufferTable.Root(bytes);

        Assert.Equal(77, table.GetStringUtf8(0).Length);
    }
}
