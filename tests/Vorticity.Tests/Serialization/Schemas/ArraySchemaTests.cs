// Round trip, tri-state and adversarial tests for `table Array`, `table ArrayNode` and
// `table ArrayStats` in spec/flatbuffers/array.fbs.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class ArraySchemaTests
{
    private static int Budget => VortexLimits.MaxFlatBufferTables;

    private static readonly BufferSpec[] Buffers =
    [
        new(0, 3, (byte)BufferCompression.None, 8_200),
        new(7, 6, (byte)BufferCompression.LZ4, 129),
        new(ushort.MaxValue, 0, 0, uint.MaxValue),
    ];

    [Fact]
    public void Round_trips_a_three_level_tree()
    {
        byte[] bytes = BuildTree();

        using AlignedBytes aligned = AlignedBytes.Copy(bytes);
        int budget = Budget;
        ArrayView array = ArrayView.Root(aligned.Span, ref budget);

        ReadOnlySpan<BufferSpec> buffers = array.Buffers;
        Assert.Equal(3, buffers.Length);
        for (int i = 0; i < buffers.Length; i++)
        {
            Assert.Equal(Buffers[i].Padding, buffers[i].Padding);
            Assert.Equal(Buffers[i].AlignmentExponent, buffers[i].AlignmentExponent);
            Assert.Equal(Buffers[i].Compression, buffers[i].Compression);
            Assert.Equal(Buffers[i].Length, buffers[i].Length);
        }

        ArrayNodeView root = array.Root_;
        Assert.False(root.IsNull);
        Assert.Equal((ushort)7, root.Encoding);
        Assert.True(root.Metadata.IsEmpty);
        Assert.Equal(0, root.BufferIndexCount);
        Assert.Equal(1, root.ChildCount);
        Assert.False(root.HasStats);
        Assert.True(root.Stats.IsNull);

        ArrayNodeView child = root.GetChild(0);
        Assert.Equal((ushort)21, child.Encoding);
        Assert.Equal(new byte[] { 0x08, 0x01, 0x10, 0x02 }, child.Metadata.ToArray());
        Assert.Equal(new ushort[] { 0, 2 }, child.BufferIndices.ToArray());
        Assert.Equal(2, child.BufferIndexCount);
        Assert.True(child.HasStats);
        Assert.True(child.Stats.TryGetNullCount(out ulong nulls));
        Assert.Equal(0UL, nulls);

        ArrayNodeView grandchild = child.GetChild(0);
        Assert.Equal((ushort)3, grandchild.Encoding);
        Assert.Equal(0, grandchild.ChildCount);
        Assert.Equal(new ushort[] { 1 }, grandchild.BufferIndices.ToArray());
    }

    [Fact]
    public void A_node_may_carry_buffers_and_no_children()
    {
        using FlatBufferBuilder b = new();
        int node = ArrayWriter.WriteNode(b, 5, default, default, [3, 1, 4], 0);
        int root = ArrayWriter.Write(b, node, default);
        byte[] bytes = b.FinishToArray(root);

        using AlignedBytes aligned = AlignedBytes.Copy(bytes);
        int budget = Budget;
        ArrayView array = ArrayView.Root(aligned.Span, ref budget);

        Assert.True(array.Buffers.IsEmpty);
        Assert.Equal(0, array.Root_.ChildCount);
        Assert.Equal(new ushort[] { 3, 1, 4 }, array.Root_.BufferIndices.ToArray());
    }

    [Fact]
    public void A_node_may_carry_children_and_no_buffers()
    {
        using FlatBufferBuilder b = new();
        int leaf = ArrayWriter.WriteNode(b, 1, default, default, default, 0);
        int node = ArrayWriter.WriteNode(b, 2, default, [leaf, leaf, leaf], default, 0);
        int root = ArrayWriter.Write(b, node, default);
        byte[] bytes = b.FinishToArray(root);

        using AlignedBytes aligned = AlignedBytes.Copy(bytes);
        int budget = Budget;
        ArrayNodeView view = ArrayView.Root(aligned.Span, ref budget).Root_;

        Assert.Equal(3, view.ChildCount);
        Assert.Equal(0, view.BufferIndexCount);
        Assert.True(view.BufferIndices.IsEmpty);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((ushort)1, view.GetChild(i).Encoding);
        }
    }

    [Fact]
    public void A_missing_root_node_is_rejected()
    {
        using FlatBufferBuilder b = new();
        int buffers = b.CreateStructVector<BufferSpec>(Buffers);
        b.StartTable();
        b.AddOffset(1, buffers);
        int root = b.EndTable();
        byte[] bytes = b.FinishToArray(root);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = ArrayView.Root(bytes, ref budget).Root_.Encoding;
        });
    }

    [Fact]
    public void An_out_of_range_child_index_is_rejected()
    {
        byte[] bytes = BuildTree();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = ArrayView.Root(bytes, ref budget).Root_.GetChild(1).Encoding;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = ArrayView.Root(bytes, ref budget).Root_.GetChild(-1).Encoding;
        });
    }

    [Fact]
    public void Array_stats_round_trip_every_tri_state_of_the_three_flags()
    {
        // absent / present-and-false / present-and-true, for is_sorted, is_strict_sorted and
        // is_constant independently: 27 combinations, all of which must survive the round trip.
        bool?[] states = [null, false, true];
        foreach (bool? sorted in states)
        {
            foreach (bool? strict in states)
            {
                foreach (bool? constant in states)
                {
                    ArrayStatsValues values = new()
                    {
                        IsSorted = sorted,
                        IsStrictSorted = strict,
                        IsConstant = constant,
                    };

                    byte[] bytes = BuildStats(in values);
                    int budget = Budget;
                    ArrayStatsView stats = ArrayView.Root(bytes, ref budget).Root_.Stats;

                    Assert.Equal(sorted.HasValue, stats.TryGetIsSorted(out bool readSorted));
                    Assert.Equal(sorted ?? false, readSorted);
                    Assert.Equal(strict.HasValue, stats.TryGetIsStrictSorted(out bool readStrict));
                    Assert.Equal(strict ?? false, readStrict);
                    Assert.Equal(constant.HasValue, stats.TryGetIsConstant(out bool readConstant));
                    Assert.Equal(constant ?? false, readConstant);
                }
            }
        }
    }

    [Fact]
    public void Array_stats_round_trip_every_tri_state_of_the_three_counts()
    {
        // A present 0 is the case that a defaulting writer silently turns into "unknown".
        ulong?[] states = [null, 0UL, ulong.MaxValue];
        foreach (ulong? nulls in states)
        {
            foreach (ulong? size in states)
            {
                foreach (ulong? nans in states)
                {
                    ArrayStatsValues values = new()
                    {
                        NullCount = nulls,
                        UncompressedSizeInBytes = size,
                        NanCount = nans,
                    };

                    byte[] bytes = BuildStats(in values);
                    int budget = Budget;
                    ArrayStatsView stats = ArrayView.Root(bytes, ref budget).Root_.Stats;

                    Assert.Equal(nulls.HasValue, stats.TryGetNullCount(out ulong readNulls));
                    Assert.Equal(nulls ?? 0UL, readNulls);
                    Assert.Equal(size.HasValue, stats.TryGetUncompressedSizeInBytes(out ulong readSize));
                    Assert.Equal(size ?? 0UL, readSize);
                    Assert.Equal(nans.HasValue, stats.TryGetNanCount(out ulong readNans));
                    Assert.Equal(nans ?? 0UL, readNans);
                }
            }
        }
    }

    [Fact]
    public void Array_stats_round_trip_the_scalar_payloads_and_their_precision()
    {
        ArrayStatsValues values = new()
        {
            Min = [0x08, 0x2A],
            MinPrecision = StatPrecision.Exact,
            Max = [],
            MaxPrecision = StatPrecision.Inexact,
            Sum = [0x11, 0x22, 0x33],
        };

        byte[] bytes = BuildStats(in values);
        int budget = Budget;
        ArrayStatsView stats = ArrayView.Root(bytes, ref budget).Root_.Stats;

        Assert.False(stats.IsNull);
        Assert.Equal(new byte[] { 0x08, 0x2A }, stats.MinBytes.ToArray());
        Assert.Equal(StatPrecision.Exact, stats.MinPrecision);
        Assert.True(stats.MaxBytes.IsEmpty);
        Assert.Equal(StatPrecision.Inexact, stats.MaxPrecision);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, stats.SumBytes.ToArray());
    }

    [Fact]
    public void An_empty_stats_table_reports_every_statistic_unknown()
    {
        ArrayStatsValues values = default;
        byte[] bytes = BuildStats(in values);

        int budget = Budget;
        ArrayNodeView node = ArrayView.Root(bytes, ref budget).Root_;
        Assert.True(node.HasStats);

        ArrayStatsView stats = node.Stats;
        Assert.False(stats.IsNull);
        Assert.True(stats.MinBytes.IsEmpty);
        Assert.True(stats.MaxBytes.IsEmpty);
        Assert.True(stats.SumBytes.IsEmpty);
        Assert.False(stats.TryGetIsSorted(out _));
        Assert.False(stats.TryGetIsStrictSorted(out _));
        Assert.False(stats.TryGetIsConstant(out _));
        Assert.False(stats.TryGetNullCount(out _));
        Assert.False(stats.TryGetUncompressedSizeInBytes(out _));
        Assert.False(stats.TryGetNanCount(out _));
    }

    [Fact]
    public void An_absent_stats_table_is_null_and_reports_nothing()
    {
        using FlatBufferBuilder b = new();
        int node = ArrayWriter.WriteNode(b, 1, default, default, default, 0);
        int root = ArrayWriter.Write(b, node, default);
        byte[] bytes = b.FinishToArray(root);

        int budget = Budget;
        ArrayNodeView view = ArrayView.Root(bytes, ref budget).Root_;
        Assert.False(view.HasStats);
        Assert.True(view.Stats.IsNull);
        Assert.False(view.Stats.TryGetNullCount(out _));
        Assert.True(view.Stats.MinBytes.IsEmpty);
    }

    [Fact]
    public void A_shared_child_dag_is_stopped_by_the_table_budget()
    {
        // 30 parents pointing at one child, 20 levels deep: 30^20 paths through 3 KB of bytes.
        // Every offset, depth and bounds rule is satisfied, so only the total-table budget can
        // stop it (docs/03-architecture.md §6).
        byte[] bytes = BuildSharedChildDag(depth: 20, fanout: 30);
        Assert.True(bytes.Length < 8192, $"the DAG should be tiny, it is {bytes.Length} bytes");

        Assert.Throws<VortexFormatException>(() => WalkEveryNode(bytes));
    }

    [Fact]
    public void A_deep_but_unshared_chain_is_not_rejected()
    {
        // The budget must not over-reject: a chain of the same depth with one child per level
        // costs 21 tables and has to parse.
        byte[] bytes = BuildSharedChildDag(depth: 20, fanout: 1);
        Assert.Equal(21, WalkEveryNode(bytes));
    }

    [Fact]
    public void No_truncation_of_an_array_blob_yields_a_wrong_value()
    {
        // The property is not "every prefix throws": FlatBuffers pads its tail, so dropping the
        // last couple of bytes of this blob is lossless and must NOT be rejected. What must hold is
        // that a prefix either throws VortexFormatException or reproduces the values exactly.
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

        // Only trailing padding may be dropped; if a large suffix became droppable something is
        // no longer being read.
        Assert.InRange(accepted, 0, 8);
    }

    private static long ReadEverything(byte[] bytes)
    {
        using AlignedBytes aligned = AlignedBytes.Copy(bytes);
        ValueDigest digest = new();
        int budget = Budget;
        ArrayView array = ArrayView.Root(aligned.Span, ref budget);
        ReadOnlySpan<BufferSpec> buffers = array.Buffers;
        digest.Add((ulong)buffers.Length);
        for (int i = 0; i < buffers.Length; i++)
        {
            digest.Add(buffers[i].Padding);
            digest.Add(buffers[i].AlignmentExponent);
            digest.Add(buffers[i].Compression);
            digest.Add(buffers[i].Length);
        }

        ReadNode(array.Root_, ref digest);
        return digest.Value;
    }

    private static void ReadNode(ArrayNodeView node, ref ValueDigest digest)
    {
        digest.Add(node.Encoding);
        digest.Add(node.Metadata);
        ReadOnlySpan<ushort> indices = node.BufferIndices;
        digest.Add((ulong)indices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            digest.Add(indices[i]);
        }

        digest.Add(node.HasStats);
        if (node.HasStats)
        {
            ArrayStatsView stats = node.Stats;
            digest.Add(stats.MinBytes);
            digest.Add(stats.MaxBytes);
            digest.Add(stats.SumBytes);
            digest.Add((ulong)stats.MinPrecision);
            digest.Add((ulong)stats.MaxPrecision);
            digest.Add(stats.TryGetIsSorted(out bool sorted));
            digest.Add(sorted);
            digest.Add(stats.TryGetIsStrictSorted(out bool strict));
            digest.Add(strict);
            digest.Add(stats.TryGetIsConstant(out bool constant));
            digest.Add(constant);
            digest.Add(stats.TryGetNullCount(out ulong nulls));
            digest.Add(nulls);
            digest.Add(stats.TryGetUncompressedSizeInBytes(out ulong size));
            digest.Add(size);
            digest.Add(stats.TryGetNanCount(out ulong nans));
            digest.Add(nans);
        }

        int children = node.ChildCount;
        digest.Add((ulong)children);
        for (int i = 0; i < children; i++)
        {
            ReadNode(node.GetChild(i), ref digest);
        }
    }

    private static int WalkEveryNode(byte[] bytes)
    {
        int budget = Budget;
        ArrayView array = ArrayView.Root(bytes, ref budget);
        return CountNodes(array.Root_) + 0;
    }

    private static int CountNodes(ArrayNodeView node)
    {
        int total = 1;
        int children = node.ChildCount;
        for (int i = 0; i < children; i++)
        {
            total += CountNodes(node.GetChild(i));
        }

        return total;
    }

    private static byte[] BuildTree()
    {
        using FlatBufferBuilder b = new();
        int grandchild = ArrayWriter.WriteNode(b, 3, default, default, [1], 0);

        ArrayStatsValues values = new() { NullCount = 0, MinPrecision = StatPrecision.Exact, Min = [0x00] };
        int stats = ArrayWriter.WriteStats(b, in values);
        int child = ArrayWriter.WriteNode(
            b, 21, [0x08, 0x01, 0x10, 0x02], [grandchild], [0, 2], stats);

        int rootNode = ArrayWriter.WriteNode(b, 7, default, [child], default, 0);
        int root = ArrayWriter.Write(b, rootNode, Buffers);
        return b.FinishToArray(root);
    }

    private static byte[] BuildStats(in ArrayStatsValues values)
    {
        using FlatBufferBuilder b = new();
        int stats = ArrayWriter.WriteStats(b, in values);
        int node = ArrayWriter.WriteNode(b, 1, default, default, default, stats);
        int root = ArrayWriter.Write(b, node, default);
        return b.FinishToArray(root);
    }

    private static byte[] BuildSharedChildDag(int depth, int fanout)
    {
        using FlatBufferBuilder b = new();
        int current = ArrayWriter.WriteNode(b, 0, default, default, default, 0);

        int[] children = new int[fanout];
        for (int level = 1; level <= depth; level++)
        {
            // Every slot is the SAME table offset: forward-only uoffsets forbid cycles, but they
            // do not forbid sharing.
            for (int i = 0; i < fanout; i++)
            {
                children[i] = current;
            }

            current = ArrayWriter.WriteNode(b, (ushort)level, default, children, default, 0);
        }

        int root = ArrayWriter.Write(b, current, default);
        return b.FinishToArray(root);
    }
}
