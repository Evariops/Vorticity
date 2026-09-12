// Round trip and rejection tests for the three postscript tables of spec/flatbuffers/footer.fbs.
using System;
using System.Text;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class PostscriptSchemaTests
{
    private static int Budget => VortexLimits.MaxFlatBufferTables;

    [Fact]
    public void Round_trips_every_field()
    {
        byte[] bytes = BuildFull();

        int budget = Budget;
        PostscriptView ps = PostscriptView.Root(bytes, ref budget);

        Assert.True(ps.HasDType);
        Assert.False(ps.DType.IsNull);
        Assert.Equal(4096UL, ps.DType.Offset);
        Assert.Equal(128u, ps.DType.Length);
        Assert.Equal((byte)3, ps.DType.AlignmentExponent);
        Assert.Equal(CompressionScheme.None, ps.DType.Compression);

        Assert.Equal(8192UL, ps.Layout.Offset);
        Assert.Equal(256u, ps.Layout.Length);
        Assert.Equal((byte)4, ps.Layout.AlignmentExponent);

        Assert.True(ps.HasStatistics);
        Assert.Equal(16384UL, ps.Statistics.Offset);
        Assert.Equal(CompressionScheme.ZStd, ps.Statistics.Compression);

        Assert.Equal(32768UL, ps.Footer.Offset);
        Assert.Equal(512u, ps.Footer.Length);
        Assert.Equal((byte)6, ps.Footer.AlignmentExponent);

        Assert.Equal(2, ps.MetadataCount);
        Assert.Equal("alpha", Encoding.UTF8.GetString(ps.GetMetadata(0).KeyUtf8));
        Assert.Equal(64UL, ps.GetMetadata(0).Segment.Offset);
        Assert.Equal("beta", Encoding.UTF8.GetString(ps.GetMetadata(1).KeyUtf8));
        Assert.Equal(128UL, ps.GetMetadata(1).Segment.Offset);
    }

    [Fact]
    public void Optional_segments_are_null_when_absent()
    {
        byte[] bytes = BuildMinimal();

        int budget = Budget;
        PostscriptView ps = PostscriptView.Root(bytes, ref budget);

        Assert.False(ps.HasDType);
        Assert.True(ps.DType.IsNull);
        Assert.False(ps.HasStatistics);
        Assert.True(ps.Statistics.IsNull);
        Assert.Equal(0, ps.MetadataCount);
        Assert.False(ps.Layout.IsNull);
        Assert.False(ps.Footer.IsNull);
    }

    [Fact]
    public void ToSegmentSpec_materializes_the_locator()
    {
        byte[] bytes = BuildMinimal();

        int budget = Budget;
        PostscriptView ps = PostscriptView.Root(bytes, ref budget);
        SegmentSpec spec = ps.Layout.ToSegmentSpec();

        Assert.Equal(8192UL, spec.Offset);
        Assert.Equal(256u, spec.Length);
        Assert.Equal((byte)4, spec.AlignmentExponent);
        // Footer indices have no counterpart in a postscript segment; the inline scheme is read
        // from PostscriptSegmentView.Compression instead.
        Assert.Equal((byte)0, spec.Compression);
        Assert.Equal((ushort)0, spec.Encryption);
        Assert.Equal(8448UL, spec.End);
    }

    [Fact]
    public void ToSegmentSpec_rejects_an_alignment_exponent_above_the_cap()
    {
        // VortexLimits.MaxAlignmentExponent is 6; the wire field is a u8, so 255 is expressible.
        byte[] bytes = BuildWithAlignmentExponent(7);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).Layout.ToSegmentSpec();
        });
    }

    [Fact]
    public void ToSegmentSpec_accepts_the_cap_itself()
    {
        byte[] bytes = BuildWithAlignmentExponent(VortexLimits.MaxAlignmentExponent);

        int budget = Budget;
        Assert.Equal(
            (byte)VortexLimits.MaxAlignmentExponent,
            PostscriptView.Root(bytes, ref budget).Layout.ToSegmentSpec().AlignmentExponent);
    }

    [Fact]
    public void ToSegmentSpec_rejects_a_null_view()
    {
        byte[] bytes = BuildMinimal();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).DType.ToSegmentSpec();
        });
    }

    [Fact]
    public void Missing_layout_is_rejected()
    {
        byte[] bytes = BuildWithoutField(SkipLayout: true, SkipFooter: false);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).Layout.Offset;
        });
    }

    [Fact]
    public void Missing_footer_is_rejected()
    {
        byte[] bytes = BuildWithoutField(SkipLayout: false, SkipFooter: true);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).Footer.Offset;
        });
    }

    [Fact]
    public void The_metadata_ceiling_is_exactly_sixteen()
    {
        byte[] ok = BuildWithMetadataCount(VortexLimits.MaxMetadataSegments);

        int budget = Budget;
        Assert.Equal(VortexLimits.MaxMetadataSegments, PostscriptView.Root(ok, ref budget).MetadataCount);

        byte[] tooMany = BuildWithMetadataCount(VortexLimits.MaxMetadataSegments + 1);
        Assert.Throws<VortexFormatException>(() =>
        {
            int b = Budget;
            _ = PostscriptView.Root(tooMany, ref b);
        });
    }

    [Fact]
    public void The_metadata_key_ceiling_is_exactly_sixty_four_bytes()
    {
        byte[] ok = BuildWithKey(new string('k', VortexLimits.MaxMetadataKeyLength));

        int budget = Budget;
        Assert.Equal(
            VortexLimits.MaxMetadataKeyLength,
            PostscriptView.Root(ok, ref budget).GetMetadata(0).KeyUtf8.Length);

        byte[] tooLong = BuildWithKey(new string('k', VortexLimits.MaxMetadataKeyLength + 1));
        Assert.Throws<VortexFormatException>(() =>
        {
            int b = Budget;
            _ = PostscriptView.Root(tooLong, ref b);
        });
    }

    [Fact]
    public void An_empty_metadata_key_is_rejected()
    {
        byte[] bytes = BuildWithKey("");

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget);
        });
    }

    [Fact]
    public void Duplicate_metadata_keys_are_rejected()
    {
        // spec/flatbuffers/footer.fbs: "Keys must be unique ... readers reject postscripts that
        // violate these limits", matching vortex-file-0.86.1/src/footer/postscript.rs.
        byte[] bytes = BuildWithKeys("same", "other", "same");

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget);
        });
    }

    [Fact]
    public void A_metadata_entry_without_a_key_is_rejected()
    {
        using FlatBufferBuilder b = new();
        int segment = PostscriptWriter.WriteSegment(b, new SegmentSpec(64, 8, 0, 0, 0), CompressionScheme.None);
        b.StartTable();
        b.AddOffset(1, segment);            // segment present, key absent
        int entry = b.EndTable();
        byte[] bytes = BuildPostscriptWithMetadata(b, entry);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget);
        });
    }

    [Fact]
    public void A_metadata_entry_without_a_segment_is_rejected()
    {
        using FlatBufferBuilder b = new();
        int key = b.CreateStringUtf8("k"u8);
        b.StartTable();
        b.AddOffset(0, key);                // key present, segment absent
        int entry = b.EndTable();
        byte[] bytes = BuildPostscriptWithMetadata(b, entry);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            PostscriptView ps = PostscriptView.Root(bytes, ref budget);
            _ = ps.GetMetadata(0).Segment.Offset;
        });
    }

    [Fact]
    public void An_out_of_range_metadata_index_is_rejected()
    {
        byte[] bytes = BuildFull();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).GetMetadata(2).KeyUtf8.Length;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).GetMetadata(-1).KeyUtf8.Length;
        });
    }

    [Fact]
    public void No_truncation_of_a_postscript_yields_a_wrong_value()
    {
        // Either VortexFormatException, or exactly the values the whole buffer gives. FlatBuffers
        // pads its tail, so "every prefix throws" would be an over-strong claim.
        byte[] full = BuildFull();
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
        PostscriptView ps = PostscriptView.Root(bytes, ref budget);

        digest.Add(ps.HasDType);
        if (ps.HasDType)
        {
            AddSegment(ref digest, ps.DType);
        }

        AddSegment(ref digest, ps.Layout);
        digest.Add(ps.HasStatistics);
        if (ps.HasStatistics)
        {
            AddSegment(ref digest, ps.Statistics);
        }

        AddSegment(ref digest, ps.Footer);
        digest.Add((ulong)ps.MetadataCount);
        for (int i = 0; i < ps.MetadataCount; i++)
        {
            PostscriptMetadataView entry = ps.GetMetadata(i);
            digest.Add(entry.KeyUtf8);
            AddSegment(ref digest, entry.Segment);
        }

        return digest.Value;
    }

    private static void AddSegment(ref ValueDigest digest, PostscriptSegmentView segment)
    {
        SegmentSpec spec = segment.ToSegmentSpec();
        digest.Add(spec.Offset);
        digest.Add(spec.Length);
        digest.Add(spec.AlignmentExponent);
        digest.Add((ulong)segment.Compression);
    }

    private static byte[] BuildFull()
    {
        using FlatBufferBuilder b = new();
        int dtype = PostscriptWriter.WriteSegment(b, new SegmentSpec(4096, 128, 3, 0, 0), CompressionScheme.None);
        int layout = PostscriptWriter.WriteSegment(b, new SegmentSpec(8192, 256, 4, 0, 0), CompressionScheme.None);
        int statistics = PostscriptWriter.WriteSegment(b, new SegmentSpec(16384, 64, 3, 0, 0), CompressionScheme.ZStd);
        int footer = PostscriptWriter.WriteSegment(b, new SegmentSpec(32768, 512, 6, 0, 0), CompressionScheme.None);

        int alphaSegment = PostscriptWriter.WriteSegment(b, new SegmentSpec(64, 8, 0, 0, 0), CompressionScheme.None);
        int alpha = PostscriptWriter.WriteMetadata(b, "alpha"u8, alphaSegment);
        int betaSegment = PostscriptWriter.WriteSegment(b, new SegmentSpec(128, 16, 0, 0, 0), CompressionScheme.None);
        int beta = PostscriptWriter.WriteMetadata(b, "beta"u8, betaSegment);

        int root = PostscriptWriter.Write(b, dtype, layout, statistics, footer, [alpha, beta]);
        return b.FinishToArray(root);
    }

    private static byte[] BuildMinimal()
    {
        using FlatBufferBuilder b = new();
        int layout = PostscriptWriter.WriteSegment(b, new SegmentSpec(8192, 256, 4, 0, 0), CompressionScheme.None);
        int footer = PostscriptWriter.WriteSegment(b, new SegmentSpec(32768, 512, 3, 0, 0), CompressionScheme.None);
        int root = PostscriptWriter.Write(b, 0, layout, 0, footer, default);
        return b.FinishToArray(root);
    }

    private static byte[] BuildWithAlignmentExponent(int exponent)
    {
        using FlatBufferBuilder b = new();
        int layout = PostscriptWriter.WriteSegment(
            b, new SegmentSpec(8192, 256, (byte)exponent, 0, 0), CompressionScheme.None);
        int footer = PostscriptWriter.WriteSegment(b, new SegmentSpec(32768, 512, 3, 0, 0), CompressionScheme.None);
        int root = PostscriptWriter.Write(b, 0, layout, 0, footer, default);
        return b.FinishToArray(root);
    }

    private static byte[] BuildWithoutField(bool SkipLayout, bool SkipFooter)
    {
        // The writer refuses to omit a required segment, so the table is assembled by hand.
        using FlatBufferBuilder b = new();
        int layout = PostscriptWriter.WriteSegment(b, new SegmentSpec(8192, 256, 4, 0, 0), CompressionScheme.None);
        int footer = PostscriptWriter.WriteSegment(b, new SegmentSpec(32768, 512, 3, 0, 0), CompressionScheme.None);

        b.StartTable();
        if (!SkipLayout)
        {
            b.AddOffset(1, layout);
        }

        if (!SkipFooter)
        {
            b.AddOffset(3, footer);
        }

        int root = b.EndTable();
        return b.FinishToArray(root);
    }

    private static byte[] BuildWithMetadataCount(int count)
    {
        using FlatBufferBuilder b = new();
        int[] entries = new int[count];
        for (int i = 0; i < count; i++)
        {
            int segment = PostscriptWriter.WriteSegment(
                b, new SegmentSpec((ulong)(64 * (i + 1)), 8, 0, 0, 0), CompressionScheme.None);
            entries[i] = PostscriptWriter.WriteMetadata(
                b, Encoding.UTF8.GetBytes("key." + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), segment);
        }

        return BuildPostscriptWithMetadataVector(b, entries);
    }

    private static byte[] BuildWithKey(string key)
    {
        using FlatBufferBuilder b = new();
        int segment = PostscriptWriter.WriteSegment(b, new SegmentSpec(64, 8, 0, 0, 0), CompressionScheme.None);
        int keyOffset = b.CreateStringUtf8(Encoding.UTF8.GetBytes(key));
        b.StartTable();
        b.AddOffset(0, keyOffset);
        b.AddOffset(1, segment);
        int entry = b.EndTable();
        return BuildPostscriptWithMetadata(b, entry);
    }

    private static byte[] BuildWithKeys(params string[] keys)
    {
        using FlatBufferBuilder b = new();
        int[] entries = new int[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            int segment = PostscriptWriter.WriteSegment(
                b, new SegmentSpec((ulong)(64 * (i + 1)), 8, 0, 0, 0), CompressionScheme.None);
            entries[i] = PostscriptWriter.WriteMetadata(b, Encoding.UTF8.GetBytes(keys[i]), segment);
        }

        return BuildPostscriptWithMetadataVector(b, entries);
    }

    private static byte[] BuildPostscriptWithMetadata(FlatBufferBuilder b, int entry) =>
        BuildPostscriptWithMetadataVector(b, [entry]);

    private static byte[] BuildPostscriptWithMetadataVector(FlatBufferBuilder b, int[] entries)
    {
        int layout = PostscriptWriter.WriteSegment(b, new SegmentSpec(8192, 256, 4, 0, 0), CompressionScheme.None);
        int footer = PostscriptWriter.WriteSegment(b, new SegmentSpec(32768, 512, 3, 0, 0), CompressionScheme.None);
        int metadata = b.CreateOffsetVector(entries);

        b.StartTable();
        b.AddOffset(1, layout);
        b.AddOffset(3, footer);
        b.AddOffset(4, metadata);
        int root = b.EndTable();
        return b.FinishToArray(root);
    }
}
