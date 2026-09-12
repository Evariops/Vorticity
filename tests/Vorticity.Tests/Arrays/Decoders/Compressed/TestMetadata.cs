// Metadata and buffer builders for the compressed-decoder tests. Everything goes through the
// Phase 0 ProtoWriter and the §6 metadata codecs' own Write methods, so a test never hand-rolls a
// protobuf body and a codec change cannot leave the tests asserting a stale encoding.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Vorticity.Types.Serialization;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

internal static class TestMetadata
{
    internal static byte[] BitPacked(uint bitWidth, uint offset)
    {
        BitPackedMetadata value = new(bitWidth, offset);
        ProtoWriter writer = new();
        try
        {
            BitPackedMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] BitPacked(uint bitWidth, uint offset, in PatchesMetadata patches)
    {
        BitPackedMetadata value = new(bitWidth, offset, in patches);
        ProtoWriter writer = new();
        try
        {
            BitPackedMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] Sparse(in PatchesMetadata patches)
    {
        SparseMetadata value = new(in patches);
        ProtoWriter writer = new();
        try
        {
            SparseMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] RunEnd(PType endsPType, ulong runCount, ulong offset)
    {
        RunEndMetadata value = new(endsPType, runCount, offset);
        ProtoWriter writer = new();
        try
        {
            RunEndMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] Dict(uint valuesLength, PType codesPType, bool? isNullableCodes)
    {
        DictMetadata value = new(valuesLength, codesPType, isNullableCodes, null);
        ProtoWriter writer = new();
        try
        {
            DictMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] Rle(
        ulong valuesLength,
        ulong indicesLength,
        PType indicesPType,
        ulong offsetsLength,
        PType offsetsPType,
        ulong offset)
    {
        RleMetadata value = new(
            valuesLength, indicesLength, indicesPType, offsetsLength, offsetsPType, offset);
        ProtoWriter writer = new();
        try
        {
            RleMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] Sequence(ScalarValue baseValue, ScalarValue multiplier)
    {
        SequenceMetadata value = new(baseValue, multiplier);
        ProtoWriter writer = new();
        try
        {
            SequenceMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static byte[] BoolOffset(uint offset)
    {
        BoolMetadata value = new(offset);
        ProtoWriter writer = new();
        try
        {
            BoolMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// <c>vortex.decimal</c> metadata. It is never optional in practice: the storage width is the
    /// stride of the values buffer, and an absent field means <c>I8</c>, which the decoder rejects
    /// for any precision wider than two digits (contract §9.1).
    /// </summary>
    internal static byte[] Decimal(DecimalStorageType valuesType)
    {
        DecimalMetadata value = new(valuesType);
        ProtoWriter writer = new();
        try
        {
            DecimalMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>A bare <c>ScalarValue</c> message body, as fastlanes.for's metadata and
    /// vortex.constant's / vortex.sparse's buffer 0 carry it.</summary>
    internal static byte[] Scalar(ScalarValue value) => ScalarProtobuf.SerializeValue(value);
}

internal static class TestBuffers
{
    internal static byte[] Bytes(params byte[] values) => values;

    internal static byte[] Int32(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    internal static byte[] UInt32(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    internal static byte[] Int64(params long[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    internal static byte[] UInt64(params ulong[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    internal static byte[] UInt16(params ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return bytes;
    }

    /// <summary>An LSB-first bitmap of <paramref name="bits"/>.</summary>
    internal static byte[] Bitmap(params bool[] bits)
    {
        byte[] bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return bytes;
    }
}
