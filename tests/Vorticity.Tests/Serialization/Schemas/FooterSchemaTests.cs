// Round trip and rejection tests for `table Footer` and `table FileStatistics`.
using System;
using System.Text;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class FooterSchemaTests
{
    private static int Budget => VortexLimits.MaxFlatBufferTables;

    private static readonly SegmentSpec[] Segments =
    [
        new(8, 124, 3, 0, 0),
        new(136, 8_200, 4, 0, 0),
        new(ulong.MaxValue - 15, uint.MaxValue, 6, 7, 9),
    ];

    [Fact]
    public void Round_trips_every_field()
    {
        byte[] bytes = Build(
            ["fastlanes.bitpacked", "vortex.primitive", "vortex.struct"],
            ["vortex.flat", "vortex.chunked"],
            Segments,
            [CompressionScheme.LZ4, CompressionScheme.ZStd],
            encryptionSpecCount: 3);

        int budget = Budget;
        FooterView footer = FooterView.Root(bytes, ref budget);

        Assert.Equal(3, footer.ArraySpecCount);
        Assert.Equal("fastlanes.bitpacked", Encoding.UTF8.GetString(footer.GetArraySpecIdUtf8(0)));
        Assert.Equal("vortex.primitive", Encoding.UTF8.GetString(footer.GetArraySpecIdUtf8(1)));
        Assert.Equal("vortex.struct", Encoding.UTF8.GetString(footer.GetArraySpecIdUtf8(2)));

        Assert.Equal(2, footer.LayoutSpecCount);
        Assert.Equal("vortex.flat", Encoding.UTF8.GetString(footer.GetLayoutSpecIdUtf8(0)));
        Assert.Equal("vortex.chunked", Encoding.UTF8.GetString(footer.GetLayoutSpecIdUtf8(1)));

        ReadOnlySpan<SegmentSpec> specs = footer.SegmentSpecs;
        Assert.Equal(3, specs.Length);
        for (int i = 0; i < specs.Length; i++)
        {
            Assert.Equal(Segments[i].Offset, specs[i].Offset);
            Assert.Equal(Segments[i].Length, specs[i].Length);
            Assert.Equal(Segments[i].AlignmentExponent, specs[i].AlignmentExponent);
            Assert.Equal(Segments[i].Compression, specs[i].Compression);
            Assert.Equal(Segments[i].Encryption, specs[i].Encryption);
        }

        Assert.Equal(2, footer.CompressionSpecCount);
        Assert.Equal(CompressionScheme.LZ4, footer.GetCompressionScheme(0));
        Assert.Equal(CompressionScheme.ZStd, footer.GetCompressionScheme(1));
        Assert.Equal(3, footer.EncryptionSpecCount);
    }

    [Fact]
    public void Absent_vectors_other_than_segment_specs_read_as_empty()
    {
        byte[] bytes = Build([], [], [], [], encryptionSpecCount: 0);

        int budget = Budget;
        FooterView footer = FooterView.Root(bytes, ref budget);

        Assert.Equal(0, footer.ArraySpecCount);
        Assert.Equal(0, footer.LayoutSpecCount);
        Assert.Equal(0, footer.CompressionSpecCount);
        Assert.Equal(0, footer.EncryptionSpecCount);
        Assert.True(footer.SegmentSpecs.IsEmpty);
    }

    [Fact]
    public void An_empty_id_string_is_legal()
    {
        // `required` means present, not non-empty.
        byte[] bytes = Build([""], [""], Segments, [], encryptionSpecCount: 0);

        int budget = Budget;
        FooterView footer = FooterView.Root(bytes, ref budget);
        Assert.Equal(1, footer.ArraySpecCount);
        Assert.True(footer.GetArraySpecIdUtf8(0).IsEmpty);
        Assert.True(footer.GetLayoutSpecIdUtf8(0).IsEmpty);
    }

    [Fact]
    public void Absent_segment_specs_is_an_error_not_an_empty_vector()
    {
        // Upstream: "FileLayout missing segment specs" - vortex-file-0.86.1/src/footer/mod.rs.
        using FlatBufferBuilder b = new();
        b.StartTable();
        b.AddOffset(3, 0);
        int root = b.EndTable();
        byte[] bytes = b.FinishToArray(root);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).SegmentSpecs.Length;
        });
    }

    [Fact]
    public void A_spec_without_its_required_id_is_rejected()
    {
        using FlatBufferBuilder b = new();
        b.StartTable();
        int emptySpec = b.EndTable();
        int specs = b.CreateOffsetVector([emptySpec]);
        int segments = b.CreateStructVector<SegmentSpec>(Segments);

        b.StartTable();
        b.AddOffset(0, specs);
        b.AddOffset(2, segments);
        int root = b.EndTable();
        byte[] bytes = b.FinishToArray(root);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).GetArraySpecIdUtf8(0).Length;
        });
    }

    [Fact]
    public void An_out_of_range_spec_index_is_rejected()
    {
        byte[] bytes = Build(["a"], ["b"], Segments, [], encryptionSpecCount: 0);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).GetArraySpecIdUtf8(1).Length;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).GetLayoutSpecIdUtf8(-1).Length;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).GetCompressionScheme(0);
        });
    }

    [Fact]
    public void The_compression_spec_ceiling_is_exactly_eight()
    {
        CompressionScheme[] eight = new CompressionScheme[VortexLimits.MaxCompressionSpecs];
        byte[] ok = Build([], [], Segments, eight, encryptionSpecCount: 0);

        int budget = Budget;
        Assert.Equal(VortexLimits.MaxCompressionSpecs, FooterView.Root(ok, ref budget).CompressionSpecCount);

        byte[] tooMany = BuildWithCompressionSpecCount(VortexLimits.MaxCompressionSpecs + 1);
        Assert.Throws<VortexFormatException>(() =>
        {
            int b = Budget;
            _ = FooterView.Root(tooMany, ref b).CompressionSpecCount;
        });
    }

    [Fact]
    public void An_out_of_domain_compression_scheme_is_returned_as_read()
    {
        // Classification of an unknown component belongs to the registry, not to the schema
        // accessor: rejecting here would make a file unopenable for a scheme it never uses
        // (Phase 1 contract §2.3).
        using FlatBufferBuilder b = new();
        b.StartTable();
        b.AddUInt8(0, 200);
        int spec = b.EndTable();
        int specs = b.CreateOffsetVector([spec]);
        int segments = b.CreateStructVector<SegmentSpec>(Segments);

        b.StartTable();
        b.AddOffset(2, segments);
        b.AddOffset(3, specs);
        int root = b.EndTable();
        byte[] bytes = b.FinishToArray(root);

        int budget = Budget;
        Assert.Equal((CompressionScheme)200, FooterView.Root(bytes, ref budget).GetCompressionScheme(0));
    }

    [Fact]
    public void A_misaligned_segment_spec_vector_is_rejected()
    {
        // FlatBuffers always places a struct vector on its natural boundary, so a vector whose
        // elements start 4 bytes off can only come from a hostile or corrupt writer. Reinterpreting
        // in place is the whole point of this accessor, so it must throw and never copy.
        byte[] bytes = RawFooterWithSegmentVector(elementsAtMultipleOfEight: false, declaredCount: 1);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).SegmentSpecs.Length;
        });
    }

    [Fact]
    public void An_aligned_hand_built_segment_spec_vector_is_accepted()
    {
        // The negative test above is only meaningful if the same shape, correctly aligned, passes.
        byte[] bytes = RawFooterWithSegmentVector(elementsAtMultipleOfEight: true, declaredCount: 1);

        int budget = Budget;
        ReadOnlySpan<SegmentSpec> specs = FooterView.Root(bytes, ref budget).SegmentSpecs;
        Assert.Equal(1, specs.Length);
        Assert.Equal(0x4142434445464748UL, specs[0].Offset);
    }

    [Fact]
    public void A_segment_spec_count_that_escapes_the_buffer_is_rejected()
    {
        // uint.MaxValue elements of 16 bytes is 64 GiB; the count times the element size must be
        // computed in 64 bits or it wraps into a range that looks valid.
        foreach (uint count in new uint[] { 2u, 1000u, uint.MaxValue })
        {
            byte[] bytes = RawFooterWithSegmentVector(elementsAtMultipleOfEight: true, declaredCount: count);
            Assert.Throws<VortexFormatException>(() =>
            {
                int budget = Budget;
                _ = FooterView.Root(bytes, ref budget).SegmentSpecs.Length;
            });
        }
    }

    [Fact]
    public void File_statistics_round_trip()
    {
        using FlatBufferBuilder b = new();
        ArrayStatsValues first = new() { NullCount = 0, IsSorted = true };
        ArrayStatsValues second = new() { NullCount = 7, IsConstant = false };
        int a = ArrayWriter.WriteStats(b, in first);
        int c = ArrayWriter.WriteStats(b, in second);
        int root = FileStatisticsWriter.Write(b, [a, c]);
        byte[] bytes = b.FinishToArray(root);

        int budget = Budget;
        FileStatisticsView stats = FileStatisticsView.Root(bytes, ref budget);
        Assert.Equal(2, stats.FieldStatsCount);

        Assert.True(stats.GetFieldStats(0).TryGetNullCount(out ulong nulls));
        Assert.Equal(0UL, nulls);
        Assert.True(stats.GetFieldStats(0).TryGetIsSorted(out bool sorted));
        Assert.True(sorted);
        Assert.False(stats.GetFieldStats(0).TryGetIsConstant(out _));

        Assert.True(stats.GetFieldStats(1).TryGetNullCount(out nulls));
        Assert.Equal(7UL, nulls);
        Assert.True(stats.GetFieldStats(1).TryGetIsConstant(out bool constant));
        Assert.False(constant);
    }

    [Fact]
    public void Empty_file_statistics_reads_as_zero_entries()
    {
        using FlatBufferBuilder b = new();
        int root = FileStatisticsWriter.Write(b, default);
        byte[] bytes = b.FinishToArray(root);

        int budget = Budget;
        Assert.Equal(0, FileStatisticsView.Root(bytes, ref budget).FieldStatsCount);
    }

    [Fact]
    public void No_truncation_of_a_footer_yields_a_wrong_value()
    {
        byte[] full = Build(
            ["fastlanes.bitpacked", "vortex.primitive"],
            ["vortex.flat"],
            Segments,
            [CompressionScheme.LZ4],
            encryptionSpecCount: 1);
        long expected = ReadEverything(full);
        int accepted = 0;

        for (int length = 0; length < full.Length; length++)
        {
            byte[] prefix = full.AsSpan(0, length).ToArray();
            long actual = 0;
            Exception? failure = Record.Exception(() => actual = ReadEverything(prefix));
            if (failure is null)
            {
                accepted++;
                Assert.Equal(expected, actual);
            }
            else
            {
                Assert.IsType<VortexFormatException>(failure);
            }
        }

        Assert.InRange(accepted, 0, 8);
    }

    private static long ReadEverything(byte[] bytes)
    {
        ValueDigest digest = new();
        int budget = Budget;
        FooterView footer = FooterView.Root(bytes, ref budget);

        digest.Add((ulong)footer.ArraySpecCount);
        for (int i = 0; i < footer.ArraySpecCount; i++)
        {
            digest.Add(footer.GetArraySpecIdUtf8(i));
        }

        digest.Add((ulong)footer.LayoutSpecCount);
        for (int i = 0; i < footer.LayoutSpecCount; i++)
        {
            digest.Add(footer.GetLayoutSpecIdUtf8(i));
        }

        ReadOnlySpan<SegmentSpec> specs = footer.SegmentSpecs;
        digest.Add((ulong)specs.Length);
        for (int i = 0; i < specs.Length; i++)
        {
            digest.Add(specs[i].Offset);
            digest.Add(specs[i].Length);
            digest.Add(specs[i].AlignmentExponent);
            digest.Add(specs[i].Compression);
            digest.Add(specs[i].Encryption);
        }

        digest.Add((ulong)footer.CompressionSpecCount);
        for (int i = 0; i < footer.CompressionSpecCount; i++)
        {
            digest.Add((ulong)footer.GetCompressionScheme(i));
        }

        digest.Add((ulong)footer.EncryptionSpecCount);
        return digest.Value;
    }

    private static byte[] Build(
        string[] arrayIds,
        string[] layoutIds,
        SegmentSpec[] segments,
        CompressionScheme[] compression,
        int encryptionSpecCount)
    {
        using FlatBufferBuilder b = new();
        int[] arrayOffsets = new int[arrayIds.Length];
        for (int i = 0; i < arrayIds.Length; i++)
        {
            arrayOffsets[i] = b.CreateStringUtf8(Encoding.UTF8.GetBytes(arrayIds[i]));
        }

        int[] layoutOffsets = new int[layoutIds.Length];
        for (int i = 0; i < layoutIds.Length; i++)
        {
            layoutOffsets[i] = b.CreateStringUtf8(Encoding.UTF8.GetBytes(layoutIds[i]));
        }

        int root = FooterWriter.Write(
            b, arrayOffsets, layoutOffsets, segments, compression, encryptionSpecCount);
        return b.FinishToArray(root);
    }

    private static byte[] BuildWithCompressionSpecCount(int count)
    {
        using FlatBufferBuilder b = new();
        int[] specs = new int[count];
        for (int i = 0; i < count; i++)
        {
            b.StartTable();
            b.AddUInt8(0, (byte)CompressionScheme.LZ4);
            specs[i] = b.EndTable();
        }

        int specVector = b.CreateOffsetVector(specs);
        int segments = b.CreateStructVector<SegmentSpec>(Segments);

        b.StartTable();
        b.AddOffset(2, segments);
        b.AddOffset(3, specVector);
        int root = b.EndTable();
        return b.FinishToArray(root);
    }

    /// <summary>
    /// Hand-assembles a Footer whose only field is <c>segment_specs</c>, so the vector's position
    /// inside the buffer is under the test's control rather than the builder's.
    /// </summary>
    private static byte[] RawFooterWithSegmentVector(bool elementsAtMultipleOfEight, uint declaredCount)
    {
        RawBytes raw = new();
        raw.U32(16);                                  //  0.. 4  root uoffset -> table at 16
        raw.U16(10);                                  //  4.. 6  vtable_size (4 header + 3 slots)
        raw.U16(8);                                   //  6.. 8  table_size
        raw.U16(0);                                   //  8..10  slot 0: array_specs absent
        raw.U16(0);                                   // 10..12  slot 1: layout_specs absent
        raw.U16(4);                                   // 12..14  slot 2: segment_specs at table + 4
        raw.U16(0);                                   // 14..16  padding
        raw.I32(12);                                  // 16..20  soffset: 16 - 12 = vtable at 4
        if (elementsAtMultipleOfEight)
        {
            raw.U32(8);                               // 20..24  uoffset -> count at 28
            raw.U32(0);                               // 24..28  filler
            raw.U32(declaredCount);                   // 28..32  count; elements start at 32
        }
        else
        {
            raw.U32(4);                               // 20..24  uoffset -> count at 24
            raw.U32(declaredCount);                   // 24..28  count; elements start at 28
        }

        raw.U64(0x4142434445464748UL);                // one SegmentSpec
        raw.U32(0x11223344u);
        raw.U8(3);
        raw.U8(0);
        raw.U16(0);
        return raw.ToArray();
    }
}
