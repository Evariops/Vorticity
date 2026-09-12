// Hand-built canonical arenas. The corpus proves we agree with a real writer; these prove we
// behave correctly on shapes a real writer never produces - a forged varbinview whose view escapes
// its data buffer, a bitmap with bit offset 7, a struct whose dtype claims more fields than the
// decoded node holds. Every one of them is a file a hostile writer can emit.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Tests.Columns;

/// <summary>A disposable arena plus dtype arena, with builders for every canonical form.</summary>
internal sealed class ColumnFixture : IDisposable
{
    private readonly List<RecordBatch> _batches = [];

    internal ColumnFixture()
    {
        Types = new DTypeArena();
        Arena = new CanonicalArena();
    }

    internal DTypeArena Types { get; }

    internal CanonicalArena Arena { get; }

    /// <summary>Copies <paramref name="data"/> into arena-owned memory and returns a view of it.</summary>
    internal VortexBuffer Bytes(ReadOnlySpan<byte> data, int alignment = 16)
    {
        if (data.IsEmpty)
        {
            return VortexBuffer.Empty;
        }

        VortexBuffer buffer = Arena.Allocate(data.Length, alignment, out Span<byte> destination);
        data.CopyTo(destination);
        return buffer;
    }

    /// <summary>Little-endian bytes of <paramref name="values"/>.</summary>
    internal VortexBuffer Int32s(ReadOnlySpan<int> values)
    {
        Span<byte> raw = values.Length == 0 ? default : new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(raw[(i * 4)..], values[i]);
        }

        return Bytes(raw);
    }

    internal VortexBuffer Int64s(ReadOnlySpan<long> values)
    {
        Span<byte> raw = values.Length == 0 ? default : new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(raw[(i * 8)..], values[i]);
        }

        return Bytes(raw);
    }

    /// <summary>An LSB-first bitmap of <paramref name="bits"/> starting at <paramref name="bitOffset"/>.</summary>
    internal VortexBuffer Bitmap(ReadOnlySpan<bool> bits, int bitOffset)
    {
        int byteCount = (bitOffset + bits.Length + 7) / 8;
        Span<byte> raw = byteCount == 0 ? default : new byte[byteCount];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                int bit = bitOffset + i;
                raw[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        return Bytes(raw, 8);
    }

    /// <summary>A non-nullable Bool node over <paramref name="bits"/>, for use as a validity child.</summary>
    internal int BoolNode(ReadOnlySpan<bool> bits, int bitOffset = 0)
    {
        return Arena.AddBool(
            Types.Bool(Nullability.NonNullable), bits.Length, Validity.NonNullable,
            Bitmap(bits, bitOffset), bitOffset);
    }

    /// <summary>A bitmap validity over <paramref name="valid"/>, deliberately NOT collapsed.</summary>
    internal Validity BitmapValidity(ReadOnlySpan<bool> valid, int bitOffset = 0) =>
        Validity.Bitmap(BoolNode(valid, bitOffset));

    /// <summary>An i32 primitive node.</summary>
    internal int Int32Node(ReadOnlySpan<int> values, Validity validity, Nullability nullability = Nullability.NonNullable) =>
        Arena.AddPrimitive(
            Types.Primitive(PType.I32, nullability), values.Length, validity, PType.I32, Int32s(values));

    /// <summary>A Utf8 varbinview node whose values are all inlined or spilled as needed.</summary>
    internal int Utf8Node(ReadOnlySpan<byte[]?> values, Nullability nullability, DTypeKind kind = DTypeKind.Utf8)
    {
        int length = values.Length;
        byte[] views = new byte[length * 16];
        List<byte> heap = [];
        bool anyNull = false;

        for (int i = 0; i < length; i++)
        {
            byte[]? value = values[i];
            if (value is null)
            {
                anyNull = true;
                continue;
            }

            Span<byte> view = views.AsSpan(i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(view[4..8]);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)heap.Count);
                heap.AddRange(value);
            }
        }

        Validity validity;
        if (nullability == Nullability.NonNullable)
        {
            validity = Validity.NonNullable;
        }
        else if (!anyNull)
        {
            validity = Validity.AllValid;
        }
        else
        {
            bool[] valid = new bool[length];
            for (int i = 0; i < length; i++)
            {
                valid[i] = values[i] is not null;
            }

            validity = BitmapValidity(valid);
        }

        DType dtype = kind == DTypeKind.Utf8 ? Types.Utf8(nullability) : Types.Binary(nullability);
        VortexBuffer data = Bytes(heap.ToArray());
        Span<VortexBuffer> buffers = heap.Count == 0 ? [VortexBuffer.Empty] : [data];
        return Arena.AddVarBinView(dtype, length, validity, Bytes(views), buffers);
    }

    /// <summary>A decimal node from unscaled values already widened to <see cref="Int256"/>.</summary>
    internal int DecimalNode(ReadOnlySpan<Int256> unscaled, byte precision, sbyte scale, Nullability nullability)
    {
        DecimalStorageType storage = DecimalStorage.ForPrecision(precision);
        int width = DecimalStorage.ByteWidth(storage);
        byte[] raw = new byte[unscaled.Length * width];
        Span<byte> scratch = stackalloc byte[32];
        for (int i = 0; i < unscaled.Length; i++)
        {
            unscaled[i].WriteLittleEndianBytes(scratch);
            scratch[..width].CopyTo(raw.AsSpan(i * width, width));
        }

        return Arena.AddDecimal(
            Types.Decimal(precision, scale, nullability),
            unscaled.Length,
            nullability == Nullability.NonNullable ? Validity.NonNullable : Validity.AllValid,
            storage,
            precision,
            scale,
            Bytes(raw, 16));
    }

    /// <summary>Wraps <paramref name="rootIndex"/> in a batch this fixture disposes.</summary>
    internal RecordBatch Batch(int rootIndex, long startRow = 0)
    {
        RecordBatch batch = new RecordBatch(Arena, rootIndex, startRow);
        _batches.Add(batch);
        return batch;
    }

    public void Dispose()
    {
        foreach (RecordBatch batch in _batches)
        {
            batch.Dispose();
        }

        Arena.Reset();
    }
}
