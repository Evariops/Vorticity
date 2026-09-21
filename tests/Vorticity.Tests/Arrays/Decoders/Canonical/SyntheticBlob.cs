// Builds array blobs byte-for-byte the way the writer does, so the adversarial cases can be
// expressed as "this file, with this one field wrong" rather than as a mocked ArrayNode.
//
// Layout, as upstream's vortex-array 0.86.1 writes it in src/serde.rs:
//     [pad][buffer 0][pad][buffer 1]...[Array flatbuffer][u32 LE flatbuffer length]
// The FlatBuffer is located from the END on read, because the padding in front of it is recorded
// nowhere.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

/// <summary>The encoding-spec table the synthetic tests use; index is the wire <c>u16</c>.</summary>
internal static class TestEncodings
{
    internal static readonly string[] Ids =
    [
        "vortex.null",
        "vortex.bool",
        "vortex.primitive",
        "vortex.decimal",
        "vortex.varbin",
        "vortex.varbinview",
        "vortex.struct",
        "vortex.list",
        "vortex.listview",
        "vortex.fixed_size_list",
        "vortex.ext",
        "vortex.chunked",
        "vortex.constant",
        "vortex.masked",
        "acme.nonesuch",
    ];

    internal static ushort Index(string id)
    {
        for (int i = 0; i < Ids.Length; i++)
        {
            if (Ids[i] == id)
            {
                return (ushort)i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "Not in the test encoding table.");
    }
}

/// <summary>One node of a synthetic array tree.</summary>
internal sealed class BlobNode
{
    internal BlobNode(string encodingId)
    {
        Encoding = TestEncodings.Index(encodingId);
    }

    internal ushort Encoding { get; set; }

    internal byte[] Metadata { get; set; } = Array.Empty<byte>();

    internal List<BlobNode> Children { get; } = new List<BlobNode>();

    internal List<int> Buffers { get; } = new List<int>();

    internal BlobNode WithMetadata(byte[] metadata)
    {
        Metadata = metadata;
        return this;
    }

    internal BlobNode WithBuffers(params int[] indices)
    {
        Buffers.AddRange(indices);
        return this;
    }

    internal BlobNode WithChildren(params BlobNode[] children)
    {
        Children.AddRange(children);
        return this;
    }
}

/// <summary>A pinned, 64-byte-aligned segment; disposing it releases nothing but the pin's root.</summary>
internal sealed class PinnedSegment
{
    private readonly byte[] _storage;
    private readonly int _offset;
    private readonly int _length;

    internal PinnedSegment(ReadOnlySpan<byte> bytes)
    {
        // The pinned object heap does not promise 64-byte alignment, and several decoders require
        // a buffer's real base address to be aligned. Over-allocate and start on a 64 boundary.
        _storage = System.GC.AllocateArray<byte>(bytes.Length + 64, pinned: true);
        unsafe
        {
            fixed (byte* p = _storage)
            {
                _offset = (int)((64 - ((nuint)p & 63)) & 63);
            }
        }

        bytes.CopyTo(_storage.AsSpan(_offset));
        _length = bytes.Length;
    }

    internal VortexBuffer Buffer =>
        VortexBuffer.FromPinned(_storage.AsSpan(_offset, _length), VortexLimits.MaxAlignmentExponent);

    /// <summary>The raw bytes, so a test can corrupt one of them and reload.</summary>
    internal byte[] ToArray() => _storage.AsSpan(_offset, _length).ToArray();
}

/// <summary>Assembles buffers plus a node tree into a segment.</summary>
internal sealed class BlobBuilder
{
    private readonly List<byte[]> _payloads = new List<byte[]>();
    private readonly List<byte> _exponents = new List<byte>();

    /// <summary>Appends a data buffer and returns its global index.</summary>
    internal int AddBuffer(ReadOnlySpan<byte> bytes, byte alignmentExponent = 3)
    {
        _payloads.Add(bytes.ToArray());
        _exponents.Add(alignmentExponent);
        return _payloads.Count - 1;
    }

    /// <summary>Serializes <paramref name="root"/> and every buffer added so far.</summary>
    internal PinnedSegment Build(BlobNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<byte> region = new List<byte>();
        BufferSpec[] specs = new BufferSpec[_payloads.Count];
        for (int i = 0; i < _payloads.Count; i++)
        {
            int alignment = 1 << _exponents[i];
            int padding = (alignment - (region.Count % alignment)) % alignment;
            for (int p = 0; p < padding; p++)
            {
                region.Add(0);
            }

            specs[i] = new BufferSpec((ushort)padding, _exponents[i], 0, (uint)_payloads[i].Length);
            region.AddRange(_payloads[i]);
        }

        byte[] flatBuffer = BuildFlatBuffer(root, specs);

        byte[] segment = new byte[region.Count + flatBuffer.Length + 4];
        region.CopyTo(segment);
        flatBuffer.CopyTo(segment.AsSpan(region.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(
            segment.AsSpan(region.Count + flatBuffer.Length), (uint)flatBuffer.Length);

        return new PinnedSegment(segment);
    }

    private static byte[] BuildFlatBuffer(BlobNode root, ReadOnlySpan<BufferSpec> specs)
    {
        using FlatBufferBuilder builder = new FlatBufferBuilder();
        int rootOffset = WriteNode(builder, root);
        int array = ArrayWriter.Write(builder, rootOffset, specs);
        return builder.FinishToArray(array);
    }

    private static int WriteNode(FlatBufferBuilder builder, BlobNode node)
    {
        // Children first: FlatBuffers offsets point backwards, so a parent cannot be written until
        // every table it references already exists.
        int[] childOffsets = new int[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
        {
            childOffsets[i] = WriteNode(builder, node.Children[i]);
        }

        ushort[] bufferIndices = new ushort[node.Buffers.Count];
        for (int i = 0; i < node.Buffers.Count; i++)
        {
            bufferIndices[i] = (ushort)node.Buffers[i];
        }

        return ArrayWriter.WriteNode(
            builder, node.Encoding, node.Metadata, childOffsets, bufferIndices, 0);
    }
}

/// <summary>Protobuf metadata payloads for the synthetic nodes.</summary>
internal static class TestMetadata
{
    internal static byte[] Bool(uint offset) => Write(1, offset);

    internal static byte[] Enum1(int value)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteEnumAlways(1, value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] VarBin(PType offsetsPType) => Enum1((int)offsetsPType);

    internal static byte[] Decimal(int valuesType) => Enum1(valuesType);

    internal static byte[] List(ulong elementsLength, PType offsetPType)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt64(1, elementsLength);
            writer.WriteEnumAlways(2, (int)offsetPType);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] ListView(ulong elementsLength, PType offsetPType, PType sizePType)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt64(1, elementsLength);
            writer.WriteEnumAlways(2, (int)offsetPType);
            writer.WriteEnumAlways(3, (int)sizePType);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>A bare <c>ScalarValue</c>, which is what <c>vortex.constant</c>'s buffer 0 holds.</summary>
    internal static byte[] ScalarNull()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            // google.protobuf.NullValue: the only defined member is NULL_VALUE = 0.
            writer.WriteEnumAlways(1, 0);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] ScalarBool(bool value)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteBoolAlways(2, value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] ScalarInt64(long value)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteSInt64Always(3, value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] ScalarString(ReadOnlySpan<byte> utf8)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteStringUtf8Always(7, utf8);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] ScalarBytes(ReadOnlySpan<byte> value)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteBytesAlways(8, value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>A <c>list_value</c> whose elements are already-encoded ScalarValue messages.</summary>
    internal static byte[] ScalarList(params byte[][] elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ProtoWriter writer = new ProtoWriter();
        try
        {
            using (ProtoWriter.MessageScope list = writer.BeginMessage(9))
            {
                foreach (byte[] element in elements)
                {
                    writer.WriteBytes(1, element);
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] Write(int field, uint value)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(field, value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
