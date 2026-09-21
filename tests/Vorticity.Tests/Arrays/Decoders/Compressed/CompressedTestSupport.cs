// Test scaffolding for the compressed decoders.
//
// Every compressed encoding has children, and every child goes through
// ArrayDecodeContext.DecodeChild -> ArrayDecoderTable.Get, so the table must hold a decoder for
// vortex.primitive, vortex.bool, vortex.varbinview and vortex.constant before any of these tests
// can run. Those belong to the `canonical-decoders` component, so this file
// registers deliberately minimal stand-ins - but ONLY into slots the real table has left empty.
// After integration ArrayDecoderTable's own static constructor fills every slot, IsImplemented
// returns true for all of them, and these tests exercise the real decoders unchanged.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>Registers the compressed decoders; the canonical ones are the table's own.</summary>
internal static class TestDecoders
{
    private static readonly object Gate = new();
    private static bool _registered;

    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;

            Register(ForDecoder.Instance);
            Register(BitPackedDecoder.Instance);
            Register(FastLanesRleDecoder.Instance);
            Register(ZigZagDecoder.Instance);
            Register(RunEndDecoder.Instance);
            Register(DictDecoder.Instance);
            Register(SparseDecoder.Instance);
            Register(SequenceDecoder.Instance);
            Register(ByteBoolDecoder.Instance);
            Register(DecimalBytePartsDecoder.Instance);
            Register(DateTimePartsDecoder.Instance);
            Register(ZstdDecoder.Instance);
            Register(AlpDecoder.Instance);
            Register(AlpRdDecoder.Instance);
            Register(FsstDecoder.Instance);
            Register(OnPairDecoder.Instance);
        }
    }

    private static void Register(ArrayDecoder decoder)
    {
        if (!ArrayDecoderTable.IsImplemented(decoder.EncodingId))
        {
            ArrayDecoderTable.Register(decoder);
        }
    }
}

/// <summary>One serialized array node, before it becomes a FlatBuffer.</summary>
internal sealed class TestNode
{
    internal TestNode(string encoding) => Encoding = encoding;

    internal string Encoding { get; }

    internal byte[] Metadata { get; set; } = [];

    internal List<TestNode> Children { get; } = [];

    internal List<int> Buffers { get; } = [];

    internal TestNode WithMetadata(byte[] metadata)
    {
        Metadata = metadata;
        return this;
    }

    internal TestNode WithChild(TestNode child)
    {
        Children.Add(child);
        return this;
    }

    internal TestNode WithBuffer(int globalIndex)
    {
        Buffers.Add(globalIndex);
        return this;
    }
}

/// <summary>Serializes a <see cref="TestNode"/> tree into a real array-blob segment.</summary>
internal static class TestBlob
{
    /// <summary>Alignment every data buffer is padded to; see <see cref="Build"/>.</summary>
    private const int BufferAlignment = 16;

    private const byte BufferAlignmentExponent = 4;

    /// <summary>
    /// Lays out <c>[pad][buffer 0][pad][buffer 1]...[Array flatbuffer][u32 length]</c> exactly as
    /// the writer does: every data buffer is preceded by the
    /// padding that carries it to its own alignment, and that padding is recorded in the buffer's
    /// <c>Buffer.padding</c> field so the reader just accumulates it. The exponent is 4 because 16
    /// is the strictest alignment any canonical decoder demands
    /// (<c>CanonicalSupport.MaxRequiredAlignment</c>) - a <c>varbinview</c> views buffer or an
    /// <c>i256</c> decimal. Packing the buffers back to back would hand
    /// <c>vortex.primitive</c> a misaligned values buffer the moment a preceding buffer's length is
    /// not a multiple of the element width, and the real decoder rejects that rather than copying.
    /// </summary>
    internal static byte[] Build(TestNode root, IReadOnlyList<byte[]> buffers, out string[] specs)
    {
        List<string> ids = [];
        Collect(root, ids);
        specs = [.. ids];

        BufferSpec[] bufferSpecs = new BufferSpec[buffers.Count];
        int[] starts = new int[buffers.Count];
        int region = 0;
        for (int i = 0; i < buffers.Count; i++)
        {
            int padding = (BufferAlignment - (region % BufferAlignment)) % BufferAlignment;
            region += padding;
            starts[i] = region;
            region += buffers[i].Length;
            bufferSpecs[i] = new BufferSpec(
                (ushort)padding, BufferAlignmentExponent, 0, (uint)buffers[i].Length);
        }

        byte[] flatBuffer;
        using (FlatBufferBuilder builder = new())
        {
            int rootOffset = WriteNode(builder, root, ids);
            int array = ArrayWriter.Write(builder, rootOffset, bufferSpecs);
            flatBuffer = builder.FinishToArray(array);
        }

        byte[] segment = new byte[region + flatBuffer.Length + 4];
        for (int i = 0; i < buffers.Count; i++)
        {
            buffers[i].CopyTo(segment, starts[i]);
        }

        flatBuffer.CopyTo(segment, region);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            segment.AsSpan(region + flatBuffer.Length), (uint)flatBuffer.Length);
        return segment;
    }

    private static int WriteNode(FlatBufferBuilder builder, TestNode node, List<string> ids)
    {
        int[] children = new int[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
        {
            children[i] = WriteNode(builder, node.Children[i], ids);
        }

        ushort[] buffers = new ushort[node.Buffers.Count];
        for (int i = 0; i < node.Buffers.Count; i++)
        {
            buffers[i] = (ushort)node.Buffers[i];
        }

        return ArrayWriter.WriteNode(
            builder, (ushort)ids.IndexOf(node.Encoding), node.Metadata, children, buffers, 0);
    }

    private static void Collect(TestNode node, List<string> ids)
    {
        if (!ids.Contains(node.Encoding))
        {
            ids.Add(node.Encoding);
        }

        foreach (TestNode child in node.Children)
        {
            Collect(child, ids);
        }
    }
}

/// <summary>A loaded array blob plus the scan context that decodes it.</summary>
internal sealed class DecodeHarness : IDisposable
{
    private readonly List<byte[]> _pinned = [];

    private DecodeHarness(ScanContext scan) => Scan = scan;

    internal ScanContext Scan { get; }

    internal DTypeArena Types => Scan.Types;

    /// <summary>Builds, loads and returns a harness over one array blob.</summary>
    internal static DecodeHarness Load(TestNode root, params byte[][] buffers)
    {
        TestDecoders.EnsureRegistered();
        byte[] segment = TestBlob.Build(root, buffers, out string[] specs);
        DecodeHarness harness = new(new ScanContext(specs));
        harness.LoadSegment(segment);
        return harness;
    }

    /// <summary>Decodes the loaded root at the given dtype and length.</summary>
    internal int DecodeRoot(DType dtype, int length) =>
        Scan.Decode.DecodeRoot(Scan.Nodes.Root, dtype, length);

    /// <summary>The decoded node at <paramref name="index"/>.</summary>
    internal CanonicalNode Node(int index) => Scan.Canonical.GetNode(index);

    /// <summary>Whether row <paramref name="row"/> of <paramref name="node"/> holds a value.</summary>
    /// <remarks>
    /// The library's own ValidityMask is a ref struct over a decode context, which a test that
    /// already holds the arena does not need. Reading the four kinds directly also keeps a test
    /// from passing because the mask and the decoder share a bug.
    /// </remarks>
    internal bool IsValid(CanonicalNode node, int row)
    {
        Validity validity = node.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return true;
            case ValidityKind.AllInvalid:
                return false;
            default:
                CanonicalNode bits = Scan.Canonical.GetNode(validity.CanonicalNodeIndex);
                int bit = bits.BitOffset + row;
                return (bits.Bits.Span[bit >> 3] & (1 << (bit & 7))) != 0;
        }
    }

    public void Dispose() => Scan.Dispose();

    private unsafe void LoadSegment(byte[] segment)
    {
        // Pinned so VortexBuffer.FromPinned's contract holds for the whole test, and started on a
        // 64-byte boundary because the pinned object heap only promises 8 - the decoders check a
        // buffer's REAL base address, so the segment base has to be at least as aligned as the
        // strictest buffer inside it (CanonicalSupport.MaxRequiredAlignment is 16).
        byte[] pinned = GC.AllocateArray<byte>(segment.Length + 64, pinned: true);
        _pinned.Add(pinned);

        int offset;
        fixed (byte* origin = pinned)
        {
            offset = (int)((64 - ((nuint)origin & 63)) & 63);
        }

        segment.CopyTo(pinned, offset);
        ArrayBlobReader.Load(
            Scan.Nodes,
            VortexBuffer.FromPinned(
                pinned.AsSpan(offset, segment.Length), VortexLimits.MaxAlignmentExponent),
            Scan.ArrayEncodings);
    }
}
