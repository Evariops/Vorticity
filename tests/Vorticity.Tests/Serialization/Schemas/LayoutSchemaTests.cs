// Round trip and adversarial tests for `table Layout` in spec/flatbuffers/layout.fbs.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class LayoutSchemaTests
{
    private static int Budget => VortexLimits.MaxFlatBufferTables;

    [Fact]
    public void Round_trips_a_nested_tree()
    {
        byte[] bytes = BuildTree();

        using AlignedBytes aligned = AlignedBytes.Copy(bytes);
        int budget = Budget;
        LayoutView root = LayoutView.Root(aligned.Span, ref budget);

        Assert.False(root.IsNull);
        Assert.Equal((ushort)1, root.Encoding);
        Assert.Equal(4096UL, root.RowCount);
        Assert.Equal(new byte[] { 0x01 }, root.Metadata.ToArray());
        Assert.True(root.Segments.IsEmpty);
        Assert.Equal(2, root.ChildCount);

        LayoutView first = root.GetChild(0);
        Assert.Equal((ushort)0, first.Encoding);
        Assert.Equal(1024UL, first.RowCount);
        Assert.True(first.Metadata.IsEmpty);
        Assert.Equal(new uint[] { 0, 1, 2 }, first.Segments.ToArray());
        Assert.Equal(0, first.ChildCount);

        LayoutView second = root.GetChild(1);
        Assert.Equal((ushort)3, second.Encoding);
        Assert.Equal(3072UL, second.RowCount);
        Assert.Equal(new uint[] { 7 }, second.Segments.ToArray());
    }

    [Fact]
    public void A_row_count_of_ulong_max_is_returned_faithfully()
    {
        // Attacker-controlled and returned as-is: narrowing and range checks belong to the layout
        // consumer, once, not to this accessor.
        using FlatBufferBuilder b = new();
        int root = LayoutWriter.Write(b, 0, ulong.MaxValue, default, default, default);
        byte[] bytes = b.FinishToArray(root);

        int budget = Budget;
        Assert.Equal(ulong.MaxValue, LayoutView.Root(bytes, ref budget).RowCount);
    }

    [Fact]
    public void Every_boundary_row_count_round_trips()
    {
        // The corpus row counts, plus the u32 and u64 boundaries.
        ulong[] counts =
        [
            0, 1, 1023, 1024, 1025, 8191, 8192, 8193, 65536,
            uint.MaxValue, (ulong)uint.MaxValue + 1, long.MaxValue, ulong.MaxValue,
        ];

        foreach (ulong count in counts)
        {
            using FlatBufferBuilder b = new();
            int root = LayoutWriter.Write(b, 2, count, default, default, default);
            byte[] bytes = b.FinishToArray(root);

            int budget = Budget;
            Assert.Equal(count, LayoutView.Root(bytes, ref budget).RowCount);
        }
    }

    [Fact]
    public void Absent_fields_read_as_their_schema_defaults()
    {
        using FlatBufferBuilder b = new();
        int root = LayoutWriter.Write(b, 0, 0, default, default, default);
        byte[] bytes = b.FinishToArray(root);

        int budget = Budget;
        LayoutView layout = LayoutView.Root(bytes, ref budget);
        Assert.False(layout.IsNull);
        Assert.Equal((ushort)0, layout.Encoding);
        Assert.Equal(0UL, layout.RowCount);
        Assert.True(layout.Metadata.IsEmpty);
        Assert.Equal(0, layout.ChildCount);
        Assert.True(layout.Segments.IsEmpty);
    }

    [Fact]
    public void An_out_of_range_child_index_is_rejected()
    {
        byte[] bytes = BuildTree();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = LayoutView.Root(bytes, ref budget).GetChild(2).Encoding;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = LayoutView.Root(bytes, ref budget).GetChild(-1).Encoding;
        });
    }

    [Fact]
    public void A_shared_child_dag_is_stopped_by_the_table_budget()
    {
        // The layout tree is the format's other recursive, file-supplied structure, so it carries
        // exactly the same exponential-sharing hazard as the array tree.
        byte[] bytes = BuildSharedChildDag(depth: 20, fanout: 30);
        Assert.True(bytes.Length < 8192, $"the DAG should be tiny, it is {bytes.Length} bytes");

        Assert.Throws<VortexFormatException>(() => WalkEveryLayout(bytes));
    }

    [Fact]
    public void A_deep_but_unshared_chain_is_not_rejected()
    {
        byte[] bytes = BuildSharedChildDag(depth: 20, fanout: 1);
        Assert.Equal(21, WalkEveryLayout(bytes));
    }

    [Fact]
    public void No_truncation_of_a_layout_yields_a_wrong_value()
    {
        byte[] full = BuildTree();
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
        using AlignedBytes aligned = AlignedBytes.Copy(bytes);
        ValueDigest digest = new();
        int budget = Budget;
        ReadLayout(LayoutView.Root(aligned.Span, ref budget), ref digest);
        return digest.Value;
    }

    private static void ReadLayout(LayoutView layout, ref ValueDigest digest)
    {
        digest.Add(layout.Encoding);
        digest.Add(layout.RowCount);
        digest.Add(layout.Metadata);
        ReadOnlySpan<uint> segments = layout.Segments;
        digest.Add((ulong)segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            digest.Add(segments[i]);
        }

        int children = layout.ChildCount;
        digest.Add((ulong)children);
        for (int i = 0; i < children; i++)
        {
            ReadLayout(layout.GetChild(i), ref digest);
        }
    }

    private static int WalkEveryLayout(byte[] bytes)
    {
        int budget = Budget;
        return CountLayouts(LayoutView.Root(bytes, ref budget));
    }

    private static int CountLayouts(LayoutView layout)
    {
        int total = 1;
        int children = layout.ChildCount;
        for (int i = 0; i < children; i++)
        {
            total += CountLayouts(layout.GetChild(i));
        }

        return total;
    }

    private static byte[] BuildTree()
    {
        using FlatBufferBuilder b = new();
        int flat = LayoutWriter.Write(b, 0, 1024, default, default, [0, 1, 2]);
        int structural = LayoutWriter.Write(b, 3, 3072, default, default, [7]);
        int root = LayoutWriter.Write(b, 1, 4096, [0x01], [flat, structural], default);
        return b.FinishToArray(root);
    }

    private static byte[] BuildSharedChildDag(int depth, int fanout)
    {
        using FlatBufferBuilder b = new();
        int current = LayoutWriter.Write(b, 0, 1, default, default, default);

        int[] children = new int[fanout];
        for (int level = 1; level <= depth; level++)
        {
            for (int i = 0; i < fanout; i++)
            {
                children[i] = current;
            }

            current = LayoutWriter.Write(b, (ushort)level, (ulong)level, default, children, default);
        }

        return b.FinishToArray(current);
    }
}
