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

    /// <summary>
    /// <c>vortex.decimal_byte_parts</c> metadata. <c>lower_part_count</c> is not writable here on
    /// purpose: the codec pins it to zero, so a test that needs a non-zero one hand-rolls the two
    /// bytes and is testing the codec, not the decoder.
    /// </summary>
    /// <param name="zerothChildPType">The msp child's physical type.</param>
    internal static byte[] DecimalByteParts(PType zerothChildPType)
    {
        DecimalBytePartsMetadata value = new(zerothChildPType);
        ProtoWriter writer = new();
        try
        {
            DecimalBytePartsMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary><c>vortex.datetimeparts</c> metadata: the three parts' physical types.</summary>
    /// <param name="days">Physical type of the days child.</param>
    /// <param name="seconds">Physical type of the seconds child.</param>
    /// <param name="subseconds">Physical type of the subseconds child.</param>
    internal static byte[] DateTimeParts(PType days, PType seconds, PType subseconds)
    {
        DateTimePartsMetadata value = new(days, seconds, subseconds);
        ProtoWriter writer = new();
        try
        {
            DateTimePartsMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary><c>vortex.zstd</c> metadata: the dictionary size and one record per frame.</summary>
    /// <param name="dictionarySize">Bytes of the dictionary buffer, or 0 when there is none.</param>
    /// <param name="frames">One entry per frame: its uncompressed size and value count.</param>
    internal static byte[] Zstd(uint dictionarySize, params ZstdFrameMetadata[] frames)
    {
        ZstdMetadata value = new(dictionarySize, frames.Length);
        ProtoWriter writer = new();
        try
        {
            ZstdMetadata.Write(ref writer, in value, frames);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary><c>vortex.alp</c> metadata with no patches.</summary>
    /// <param name="exponentE">The e exponent, an index into the inverse scaling table.</param>
    /// <param name="exponentF">The f exponent, an index into the scaling table.</param>
    internal static byte[] Alp(uint exponentE, uint exponentF) => AlpBody(new AlpMetadata(exponentE, exponentF));

    /// <summary><c>vortex.alp</c> metadata with a patch descriptor.</summary>
    /// <param name="exponentE">The e exponent.</param>
    /// <param name="exponentF">The f exponent.</param>
    /// <param name="patches">The patch descriptor.</param>
    internal static byte[] Alp(uint exponentE, uint exponentF, in PatchesMetadata patches) =>
        AlpBody(new AlpMetadata(exponentE, exponentF, in patches));

    private static byte[] AlpBody(in AlpMetadata value)
    {
        ProtoWriter writer = new();
        try
        {
            AlpMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary><c>vortex.alprd</c> metadata.</summary>
    /// <param name="rightBitWidth">Width of the right parts, in bits.</param>
    /// <param name="leftPartsPType">Physical type of the left-parts (code) child.</param>
    /// <param name="dictionary">The left-parts dictionary, in code order.</param>
    internal static byte[] AlpRd(uint rightBitWidth, PType leftPartsPType, params uint[] dictionary) =>
        AlpRdBody(
            new AlpRdMetadata(
                rightBitWidth, (uint)dictionary.Length, dictionary.Length, leftPartsPType, null),
            dictionary);

    /// <summary><c>vortex.alprd</c> metadata with a patch descriptor.</summary>
    /// <param name="rightBitWidth">Width of the right parts, in bits.</param>
    /// <param name="leftPartsPType">Physical type of the left-parts child.</param>
    /// <param name="patches">The patch descriptor.</param>
    /// <param name="dictionary">The left-parts dictionary.</param>
    internal static byte[] AlpRd(
        uint rightBitWidth, PType leftPartsPType, in PatchesMetadata patches, params uint[] dictionary) =>
        AlpRdBody(
            new AlpRdMetadata(
                rightBitWidth, (uint)dictionary.Length, dictionary.Length, leftPartsPType, patches),
            dictionary);

    private static byte[] AlpRdBody(in AlpRdMetadata value, ReadOnlySpan<uint> dictionary)
    {
        ProtoWriter writer = new();
        try
        {
            AlpRdMetadata.Write(ref writer, in value, dictionary);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary><c>vortex.fsst</c> metadata: the two child physical types.</summary>
    /// <param name="uncompressedLengthsPType">Physical type of the uncompressed-lengths child.</param>
    /// <param name="codesOffsetsPType">Physical type of the codes-offsets child.</param>
    internal static byte[] Fsst(PType uncompressedLengthsPType, PType codesOffsetsPType)
    {
        FsstMetadata value = new(uncompressedLengthsPType, codesOffsetsPType);
        ProtoWriter writer = new();
        try
        {
            FsstMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// <c>vortex.onpair</c> metadata, with the physical types the fixtures use: u32 dictionary
    /// offsets, u16 codes, u32 code offsets and u32 uncompressed lengths.
    /// </summary>
    /// <param name="dictionarySize">Token count; the offsets child holds one more than this.</param>
    /// <param name="codesLength">How many codes the code stream holds.</param>
    internal static byte[] OnPair(uint dictionarySize, ulong codesLength)
    {
        OnPairMetadata value = new(
            PType.U32, dictionarySize, codesLength, PType.U32, PType.U16, PType.U32);
        ProtoWriter writer = new();
        try
        {
            OnPairMetadata.Write(ref writer, in value);
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
    internal static byte[] Double(params double[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(double)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)), values[i]);
        }

        return bytes;
    }

    internal static byte[] Int16(params short[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(short)];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), values[i]);
        }

        return bytes;
    }

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
