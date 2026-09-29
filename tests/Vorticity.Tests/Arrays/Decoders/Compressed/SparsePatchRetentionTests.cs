using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// The patch set of a node read a batch at a time is decoded once for the node when it holds enough
/// patches for that to pay, and at every batch otherwise; every batch still reads its own rows.
/// </summary>
/// <remarks>
/// In the collection that runs alone, because the count it reads is the process's.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class SparsePatchRetentionTests
{
    /// <summary>The node's rows, a patch every stride rows at the middle of each stretch, and the shared decodes four batches make.</summary>
    public static TheoryData<int, int, long> PatchSets => new()
    {
        // As many patches as a selective read retains from: one decode of the indices and one of
        // the values, for the four batches.
        { 4 * Patches.RetainedFrom, 4, 2 },

        // Fewer: decoded again at each batch, and never shared.
        { 4_096, 64, 0 },
    };

    [Theory]
    [MemberData(nameof(PatchSets))]
    public void EveryBatchOfASelectiveReadSharesOneDecodeOfALargePatchSet(int rows, int stride, long shared)
    {
        // The patch set as the reference writer writes a regular one: indices and values as
        // sequences, which decode to a value per patch rather than viewing a buffer.
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create((ulong)(rows / stride), 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.sequence")
                .WithMetadata(TestMetadata.Sequence(store.UInt64((ulong)(stride / 2)), store.UInt64((ulong)stride))))
            .WithChild(new TestNode("vortex.sequence")
                .WithMetadata(TestMetadata.Sequence(store.Int64(1), store.Int64(1))));

        using DecodeHarness harness = DecodeHarness.Load(root, TestMetadata.Scalar(store.Int64(0)));
        DType i64 = harness.Types.Primitive(PType.I64, Nullability.NonNullable);
        uint? outer = harness.Scan.Decode.BeginNodeCheckScope(3);
        long before = ArrayDecodeContext.SharedChildrenDecoded;
        try
        {
            for (int batch = 0; batch < 4; batch++)
            {
                int[] wanted = Wanted(rows, batch);
                CanonicalNode node = harness.Node(harness.DecodeRootSelected(i64, rows, wanted));
                ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(node.Values.Span)[..wanted.Length];
                for (int i = 0; i < wanted.Length; i++)
                {
                    long expected = wanted[i] % stride == stride / 2 ? (wanted[i] / stride) + 1 : 0;
                    Assert.Equal(expected, values[i]);
                }
            }
        }
        finally
        {
            harness.Scan.Decode.EndNodeCheckScope(outer);
        }

        Assert.Equal(shared, ArrayDecodeContext.SharedChildrenDecoded - before);
    }

    [Theory]
    [MemberData(nameof(PatchSets))]
    public void EveryBatchOfASelectiveBitPackedReadSharesOneDecodeOfALargePatchSet(int rows, int stride, long shared)
    {
        // Twelve bits a value, and a patch every stride rows too wide for them, its index and value
        // written as sequences.
        ulong[] packedValues = new ulong[rows];
        for (int i = 0; i < rows; i++)
        {
            packedValues[i] = (ulong)(i % 4_000);
        }

        ScalarStore store = new();
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(12, 0, PatchesMetadata.Create((ulong)(rows / stride), 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.sequence")
                .WithMetadata(TestMetadata.Sequence(store.UInt64((ulong)(stride / 2)), store.UInt64((ulong)stride))))
            .WithChild(new TestNode("vortex.sequence")
                .WithMetadata(TestMetadata.Sequence(store.UInt64(1_000_000), store.UInt64(1))));

        using DecodeHarness harness = DecodeHarness.Load(root, TestPacking.Pack(packedValues, 12, 32));
        DType u32 = harness.Types.Primitive(PType.U32, Nullability.NonNullable);
        uint? outer = harness.Scan.Decode.BeginNodeCheckScope(5);
        long before = ArrayDecodeContext.SharedChildrenDecoded;
        try
        {
            for (int batch = 0; batch < 4; batch++)
            {
                int[] wanted = Wanted(rows, batch);
                CanonicalNode node = harness.Node(harness.DecodeRootSelected(u32, rows, wanted));
                ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(node.Values.Span)[..wanted.Length];
                for (int i = 0; i < wanted.Length; i++)
                {
                    int row = wanted[i];
                    uint expected = row % stride == stride / 2 ? (uint)(1_000_000 + (row / stride)) : (uint)(row % 4_000);
                    Assert.Equal(expected, values[i]);
                }
            }
        }
        finally
        {
            harness.Scan.Decode.EndNodeCheckScope(outer);
        }

        Assert.Equal(shared, ArrayDecodeContext.SharedChildrenDecoded - before);
    }

    /// <summary>Every second row of the batch's quarter of the node, which takes patched rows and the rows between them.</summary>
    private static int[] Wanted(int rows, int batch)
    {
        int[] wanted = new int[rows / 4 / 2];
        for (int i = 0; i < wanted.Length; i++)
        {
            wanted[i] = (batch * (rows / 4)) + (i * 2);
        }

        return wanted;
    }
}
