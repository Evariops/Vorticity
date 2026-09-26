using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// The patch set of a sparse node read a batch at a time is decoded once for the node, and every
/// batch still reads its own rows.
/// </summary>
/// <remarks>
/// In the collection that runs alone, because the count it reads is the process's.
/// </remarks>
[Collection(nameof(AllocationCollection))]
public sealed class SparsePatchRetentionTests
{
    private const int Rows = 4_096;

    /// <summary>A patch every this many rows, at the middle of each stretch.</summary>
    private const int Stride = 64;

    [Fact]
    public void EveryBatchOfASelectiveReadSharesOneDecodeOfThePatchSet()
    {
        // The patch set as the reference writer writes a regular one: indices and values as
        // sequences, which decode to a value per patch rather than viewing a buffer.
        ScalarStore store = new();
        TestNode root = new TestNode("vortex.sparse")
            .WithMetadata(TestMetadata.Sparse(PatchesMetadata.Create(Rows / Stride, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.sequence")
                .WithMetadata(TestMetadata.Sequence(store.UInt64(Stride / 2), store.UInt64(Stride))))
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
                // Every eighth row of the batch's quarter of the node, which takes each patch of it.
                int[] wanted = new int[Rows / 4 / 8];
                for (int i = 0; i < wanted.Length; i++)
                {
                    wanted[i] = (batch * (Rows / 4)) + (i * 8);
                }

                CanonicalNode node = harness.Node(harness.DecodeRootSelected(i64, Rows, wanted));
                ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(node.Values.Span)[..wanted.Length];
                for (int i = 0; i < wanted.Length; i++)
                {
                    long expected = wanted[i] % Stride == Stride / 2 ? (wanted[i] / Stride) + 1 : 0;
                    Assert.Equal(expected, values[i]);
                }
            }
        }
        finally
        {
            harness.Scan.Decode.EndNodeCheckScope(outer);
        }

        // One decode of the indices and one of the values, for the four batches.
        Assert.Equal(2, ArrayDecodeContext.SharedChildrenDecoded - before);
    }

    [Fact]
    public void EveryBatchOfASelectiveBitPackedReadSharesOneDecodeOfItsPatches()
    {
        // Twelve bits a value, and a patch every 64 rows too wide for them, its index and value
        // written as sequences.
        ulong[] packedValues = new ulong[Rows];
        for (int i = 0; i < Rows; i++)
        {
            packedValues[i] = (ulong)(i % 4_000);
        }

        ScalarStore store = new();
        TestNode root = new TestNode("fastlanes.bitpacked")
            .WithMetadata(TestMetadata.BitPacked(12, 0, PatchesMetadata.Create(Rows / Stride, 0, PType.U32)))
            .WithBuffer(0)
            .WithChild(new TestNode("vortex.sequence")
                .WithMetadata(TestMetadata.Sequence(store.UInt64(Stride / 2), store.UInt64(Stride))))
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
                int[] wanted = new int[Rows / 4 / 8];
                for (int i = 0; i < wanted.Length; i++)
                {
                    wanted[i] = (batch * (Rows / 4)) + (i * 8);
                }

                CanonicalNode node = harness.Node(harness.DecodeRootSelected(u32, Rows, wanted));
                ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(node.Values.Span)[..wanted.Length];
                for (int i = 0; i < wanted.Length; i++)
                {
                    int row = wanted[i];
                    uint expected = row % Stride == Stride / 2 ? (uint)(1_000_000 + (row / Stride)) : (uint)(row % 4_000);
                    Assert.Equal(expected, values[i]);
                }
            }
        }
        finally
        {
            harness.Scan.Decode.EndNodeCheckScope(outer);
        }

        Assert.Equal(2, ArrayDecodeContext.SharedChildrenDecoded - before);
    }
}
