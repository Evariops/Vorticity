// Determinism and one end-to-end shape exercising every member kind at once.
//
// Byte-for-byte determinism is not cosmetic: the conformance tests compare our output against a
// stored corpus, and the vtable cache is the one place where a non-deterministic choice could
// creep in (a randomized hash seed would still deduplicate correctly but could pick a different
// winner among equal candidates - it cannot here, because Find returns the FIRST match in a chain
// built in insertion order and the hash is seedless).
using System;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferBuilderDeterminismTests
{
    [Fact]
    public void The_same_build_sequence_produces_the_same_bytes_whatever_the_initial_capacity()
    {
        // Different initial capacities take different growth paths through the scratch array. If
        // any offset were computed from an absolute position rather than a back-offset, the two
        // buffers would differ.
        byte[] tiny = Build(0);
        byte[] exact = Build(64);
        byte[] roomy = Build(1 << 16);

        Assert.Equal(tiny, exact);
        Assert.Equal(tiny, roomy);
    }

    [Fact]
    public void A_reused_builder_produces_the_same_bytes_as_a_fresh_one()
    {
        using var reused = new FlatBufferBuilder();
        _ = BuildInto(reused);
        reused.Clear();
        byte[] second = BuildInto(reused);

        Assert.Equal(Build(1024), second);
    }

    [Fact]
    public void Finish_and_FinishToArray_agree()
    {
        using var spanBuilder = new FlatBufferBuilder();
        using var arrayBuilder = new FlatBufferBuilder();

        int spanRoot = Compose(spanBuilder);
        ReadOnlySpan<byte> span = spanBuilder.Finish(spanRoot);

        int arrayRoot = Compose(arrayBuilder);
        byte[] array = arrayBuilder.FinishToArray(arrayRoot);

        Assert.True(span.SequenceEqual(array));
        Assert.Equal(spanBuilder.Offset, array.Length);
    }

    [Fact]
    public void A_buffer_containing_every_member_kind_round_trips()
    {
        using var builder = new FlatBufferBuilder(64);
        int root = Compose(builder);
        byte[] bytes = builder.FinishToArray(root);

        using AlignedTestBuffer aligned = AlignedTestBuffer.Copy(bytes);
        FlatBufferTable table = FlatBufferTable.Root(aligned.Span);

        Assert.Equal((ushort)0xBEEF, table.GetUInt16(0));
        Assert.Equal(long.MinValue, table.GetInt64(1));
        Assert.True(table.GetStringUtf8(2).SequenceEqual("composite"u8));
        Assert.True(table.GetByteVector(3).SequenceEqual(new byte[] { 0xDE, 0xAD }));
        Assert.True(table.GetStructVector<uint>(4).SequenceEqual(new uint[] { 1, 2, 3 }));

        ReadOnlySpan<SegmentSpecLike> specs = table.GetStructVector<SegmentSpecLike>(5);
        Assert.Equal(2, specs.Length);
        Assert.Equal(4096UL, specs[1].Offset);

        Assert.True(table.TryGetStruct(6, out BufferLike inline));
        Assert.Equal(512u, inline.Length);
        Assert.Equal((byte)6, inline.AlignmentExponent);

        Assert.Equal(7, table.GetTable(7).GetInt32(0));

        FlatBufferVector children = table.GetVector(8);
        Assert.Equal(2, children.Count);
        Assert.Equal(10, children.GetTable(0).GetInt32(0));
        Assert.Equal(20, children.GetTable(1).GetInt32(0));

        FlatBufferVector names = table.GetVector(9);
        Assert.Equal(2, names.Count);
        Assert.True(names.GetStringUtf8(0).SequenceEqual("alpha"u8));
        Assert.True(names.GetStringUtf8(1).SequenceEqual("beta"u8));

        Assert.True(table.TryGetUInt64(10, out ulong tri));
        Assert.Equal(0UL, tri);
        Assert.False(table.TryGetBool(11, out _));
    }

    private static byte[] Build(int initialCapacity)
    {
        using var builder = new FlatBufferBuilder(initialCapacity);
        return BuildInto(builder);
    }

    private static byte[] BuildInto(FlatBufferBuilder builder) =>
        builder.FinishToArray(Compose(builder));

    /// <summary>
    /// One table using every member kind the builder exposes: scalars, a string, a byte vector, a
    /// scalar vector, a struct vector, an inline struct, a sub-table, a vector of tables, a vector
    /// of strings, and a tri-state <c>= null</c> field.
    /// </summary>
    private static int Compose(FlatBufferBuilder builder)
    {
        int text = builder.CreateStringUtf8("composite"u8);
        int blob = builder.CreateByteVector(stackalloc byte[] { 0xDE, 0xAD });
        int scalars = builder.CreateScalarVector<uint>(new uint[] { 1, 2, 3 });
        int specs = builder.CreateStructVector<SegmentSpecLike>(
        [
            new() { Offset = 0, Length = 64, AlignmentExponent = 3, Compression = 0, Encryption = 0 },
            new() { Offset = 4096, Length = 128, AlignmentExponent = 6, Compression = 1, Encryption = 2 },
        ]);

        builder.StartTable();
        builder.AddInt32(0, 7);
        int child = builder.EndTable();

        builder.StartTable();
        builder.AddInt32(0, 10);
        int listed0 = builder.EndTable();
        builder.StartTable();
        builder.AddInt32(0, 20);
        int listed1 = builder.EndTable();
        int childVector = builder.CreateOffsetVector(new[] { listed0, listed1 });

        int nameVector = builder.CreateOffsetVector(
            new[] { builder.CreateStringUtf8("alpha"u8), builder.CreateStringUtf8("beta"u8) });

        BufferLike inline = new() { Padding = 0, AlignmentExponent = 6, Compression = 0, Length = 512 };

        builder.StartTable();
        builder.AddUInt16(0, 0xBEEF);
        builder.AddInt64(1, long.MinValue);
        builder.AddOffset(2, text);
        builder.AddOffset(3, blob);
        builder.AddOffset(4, scalars);
        builder.AddOffset(5, specs);
        builder.AddStruct(6, in inline);
        builder.AddOffset(7, child);
        builder.AddOffset(8, childVector);
        builder.AddOffset(9, nameVector);
        builder.AddUInt64Always(10, 0);
        // Field 11 is the tri-state left unwritten: "not computed".
        return builder.EndTable();
    }
}
