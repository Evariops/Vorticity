// Vector alignment. Footer.segment_specs (16-byte SegmentSpec), Array.buffers (8-byte Buffer),
// ArrayNode.buffers ([uint16]) and Layout.segments ([uint32]) are all read by REINTERPRETING the
// element bytes in place, with no traversal and no copy (docs/02-format.md §3). That only works
// when the writer put the first element on alignof(T), so this is the writer half of the same
// contract FlatBufferStructVectorTests checks on the reader side.
using System;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferBuilderVectorAlignmentTests
{
    [Fact]
    public void The_finished_buffer_starts_eight_byte_aligned()
    {
        // The builder writes back to front, so a position inside the finished buffer is
        // `length - backOffset`. Padding the head until the length is a multiple of 8 is therefore
        // exactly what makes every object land on its own boundary once the buffer sits at an
        // 8-byte aligned address (docs/03-architecture.md §3.5).
        foreach (int width in new[] { 1, 2, 4, 8 })
        {
            using var builder = new FlatBufferBuilder();
            builder.StartTable();
            switch (width)
            {
                case 1: builder.AddUInt8(0, 1); break;
                case 2: builder.AddUInt16(0, 1); break;
                case 4: builder.AddUInt32(0, 1); break;
                default: builder.AddUInt64(0, 1); break;
            }

            int root = builder.EndTable();
            byte[] bytes = builder.FinishToArray(root);

            Assert.Equal(0, bytes.Length % 8);
            FlatBufferTable table = FlatBufferTable.Root(bytes);
            ulong read = width switch
            {
                1 => table.GetUInt8(0),
                2 => table.GetUInt16(0),
                4 => table.GetUInt32(0),
                _ => table.GetUInt64(0),
            };

            Assert.Equal(1UL, read);
        }
    }

    [Fact]
    public void An_empty_buffer_of_an_empty_table_is_still_eight_byte_aligned()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        Assert.Equal(0, bytes.Length % 8);
        Assert.False(FlatBufferTable.Root(bytes).IsNull);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(17)]
    public void Scalar_vectors_are_aligned_to_their_element_width(int count)
    {
        ulong[] longs = new ulong[count];
        uint[] ints = new uint[count];
        ushort[] shorts = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            longs[i] = 0xFFFF_0000_0000_0000UL + (ulong)i;
            ints[i] = 0xF000_0000u + (uint)i;
            shorts[i] = (ushort)(0xF000 + i);
        }

        using var builder = new FlatBufferBuilder(64);
        int longVector = builder.CreateScalarVector<ulong>(longs);
        int intVector = builder.CreateScalarVector<uint>(ints);
        int shortVector = builder.CreateScalarVector<ushort>(shorts);
        builder.StartTable();
        builder.AddOffset(0, longVector);
        builder.AddOffset(1, intVector);
        builder.AddOffset(2, shortVector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 0) % 8);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 1) % 4);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 2) % 2);
        Assert.Equal(count, BuiltFlatBuffer.CountOf(bytes, rootPos, 0));

        // ... and the reader reinterprets them in place from a 64-byte aligned copy, which is what
        // a real segment looks like.
        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable table = FlatBufferTable.Root(aligned.Span);
        Assert.True(table.GetStructVector<ulong>(0).SequenceEqual(longs));
        Assert.True(table.GetStructVector<uint>(1).SequenceEqual(ints));
        Assert.True(table.GetStructVector<ushort>(2).SequenceEqual(shorts));
        Assert.True(table.GetStructVector<byte>(3).IsEmpty);
    }

    [Fact]
    public void SegmentSpec_vectors_are_eight_byte_aligned_and_reinterpret_in_place()
    {
        // spec/flatbuffers/footer.fbs `struct SegmentSpec`: 16 bytes, 8-byte aligned.
        SegmentSpecLike[] specs = new SegmentSpecLike[3];
        for (int i = 0; i < specs.Length; i++)
        {
            specs[i] = new SegmentSpecLike
            {
                Offset = 4096UL * (ulong)(i + 1),
                Length = 256u * (uint)(i + 1),
                AlignmentExponent = (byte)(i + 4),
                Compression = (byte)i,
                Encryption = (ushort)(i * 7),
            };
        }

        using var builder = new FlatBufferBuilder();
        // A one-byte object first, so that the vector has to be padded rather than landing on 8 by
        // accident.
        int filler = builder.CreateByteVector(stackalloc byte[] { 0xAB });
        int vector = builder.CreateStructVector<SegmentSpecLike>(specs);
        builder.StartTable();
        builder.AddOffset(0, vector);
        builder.AddOffset(1, filler);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        Assert.Equal(8, FlatBufferAccess.AlignmentOf<SegmentSpecLike>());
        Assert.Equal(16, Unsafe.SizeOf<SegmentSpecLike>());
        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 0) % 8);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        ReadOnlySpan<SegmentSpecLike> read =
            FlatBufferTable.Root(aligned.Span).GetStructVector<SegmentSpecLike>(0);
        Assert.Equal(specs.Length, read.Length);
        for (int i = 0; i < specs.Length; i++)
        {
            Assert.Equal(specs[i].Offset, read[i].Offset);
            Assert.Equal(specs[i].Length, read[i].Length);
            Assert.Equal(specs[i].AlignmentExponent, read[i].AlignmentExponent);
            Assert.Equal(specs[i].Compression, read[i].Compression);
            Assert.Equal(specs[i].Encryption, read[i].Encryption);
        }
    }

    [Fact]
    public void Buffer_vectors_are_four_byte_aligned_not_eight()
    {
        // spec/flatbuffers/array.fbs `struct Buffer` is 8 bytes but only 4-byte aligned. Aligning
        // it to sizeof(T) would produce a file that disagrees with every other writer, and the
        // reader deliberately checks alignof(T) so it would not even notice.
        BufferLike[] buffers =
        [
            new() { Padding = 0, AlignmentExponent = 6, Compression = 0, Length = 1024 },
            new() { Padding = 8, AlignmentExponent = 3, Compression = 1, Length = 64 },
        ];

        using var builder = new FlatBufferBuilder();
        int filler = builder.CreateByteVector(stackalloc byte[] { 0xAB, 0xCD, 0xEF, 0x01 });
        int vector = builder.CreateStructVector<BufferLike>(buffers);
        builder.StartTable();
        builder.AddOffset(0, vector);
        builder.AddOffset(1, filler);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        Assert.Equal(4, FlatBufferAccess.AlignmentOf<BufferLike>());
        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 0) % 4);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        ReadOnlySpan<BufferLike> read =
            FlatBufferTable.Root(aligned.Span).GetStructVector<BufferLike>(0);
        Assert.Equal(2, read.Length);
        Assert.Equal(1024u, read[0].Length);
        Assert.Equal((byte)6, read[0].AlignmentExponent);
        Assert.Equal((ushort)8, read[1].Padding);
        Assert.Equal(64u, read[1].Length);
    }

    [Fact]
    public void The_count_prefix_sits_immediately_before_the_first_element()
    {
        // Padding between the u32 count and the elements would detach the two; the reader computes
        // the element position as vectorPos + 4 and nothing else.
        using var builder = new FlatBufferBuilder();
        int vector = builder.CreateScalarVector<ulong>(new ulong[] { 1, 2, 3 });
        builder.StartTable();
        builder.AddOffset(0, vector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int vectorPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 0);
        Assert.Equal(0, vectorPos % 4);
        Assert.Equal(3u, BuiltFlatBuffer.ReadUInt32(bytes, vectorPos));
        Assert.Equal(vectorPos + 4, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 0));
    }

    [Fact]
    public void Several_vectors_in_one_buffer_each_keep_their_own_alignment()
    {
        // Odd-length byte vectors between the aligned ones force real padding rather than letting
        // everything land on 8 by luck.
        using var builder = new FlatBufferBuilder(64);
        int a = builder.CreateByteVector(stackalloc byte[] { 1, 2, 3 });
        int b = builder.CreateScalarVector<ulong>(new ulong[] { 7, 8 });
        int c = builder.CreateByteVector(stackalloc byte[] { 4, 5, 6, 7, 8 });
        int d = builder.CreateScalarVector<uint>(new uint[] { 9, 10, 11 });
        int e = builder.CreateScalarVector<ushort>(new ushort[] { 12 });

        builder.StartTable();
        builder.AddOffset(0, a);
        builder.AddOffset(1, b);
        builder.AddOffset(2, c);
        builder.AddOffset(3, d);
        builder.AddOffset(4, e);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 1) % 8);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 3) % 4);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 4) % 2);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable table = FlatBufferTable.Root(aligned.Span);
        Assert.True(table.GetByteVector(0).SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.True(table.GetStructVector<ulong>(1).SequenceEqual(new ulong[] { 7, 8 }));
        Assert.True(table.GetByteVector(2).SequenceEqual(new byte[] { 4, 5, 6, 7, 8 }));
        Assert.True(table.GetStructVector<uint>(3).SequenceEqual(new uint[] { 9, 10, 11 }));
        Assert.True(table.GetStructVector<ushort>(4).SequenceEqual(new ushort[] { 12 }));
    }

    [Fact]
    public void Growing_the_scratch_array_preserves_every_offset_and_all_alignment()
    {
        // The scratch array is rented and doubles as it fills. Offsets are counted from the END,
        // so growth must copy the written region to the end of the new array; getting that wrong
        // silently shifts every reference by the size difference.
        const int Elements = 40000;

        ulong[] values = new ulong[Elements];
        for (int i = 0; i < Elements; i++)
        {
            values[i] = 0x0102_0304_0000_0000UL | (uint)i;
        }

        using var builder = new FlatBufferBuilder(0);
        int marker = builder.CreateStringUtf8("marker"u8);
        int vector = builder.CreateScalarVector<ulong>(values);
        builder.StartTable();
        builder.AddOffset(0, vector);
        builder.AddOffset(1, marker);
        builder.AddUInt64(2, ulong.MaxValue);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        Assert.Equal(0, bytes.Length % 8);
        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        Assert.Equal(0, BuiltFlatBuffer.ElementsOf(bytes, rootPos, 0) % 8);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable table = FlatBufferTable.Root(aligned.Span);
        Assert.True(table.GetStructVector<ulong>(0).SequenceEqual(values));
        Assert.True(table.GetStringUtf8(1).SequenceEqual("marker"u8));
        Assert.Equal(ulong.MaxValue, table.GetUInt64(2));
    }
}
