// Hand-built canonical columns for the row encoder.
//
// The row encoder never reads a file, so there is no corpus for it and no reference sidecar: its
// inputs are arrays, and the only way to test it is to build the arrays. What the builders below
// do NOT do is normalize anything - a null row's backing value is written as given, so a test can
// put garbage under a null and prove the encoder ignores it.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Tests.RowEncoding;

/// <summary>An arena plus the builders the row-encoding tests need.</summary>
internal sealed class RowFixture : IDisposable
{
    internal RowFixture()
    {
        Types = new DTypeArena();
        Arena = new CanonicalArena();
    }

    internal DTypeArena Types { get; }

    internal CanonicalArena Arena { get; }

    /// <summary>Copies bytes into arena-owned memory.</summary>
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

    /// <summary>A validity from a per-row flag array, or all-valid when <paramref name="valid"/> is null.</summary>
    internal Validity Validity(bool[]? valid)
    {
        if (valid is null)
        {
            return Vorticity.Arrays.Validity.NonNullable;
        }

        int byteCount = (valid.Length + 7) / 8;
        byte[] raw = new byte[Math.Max(byteCount, 1)];
        for (int i = 0; i < valid.Length; i++)
        {
            if (valid[i])
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        int bits = Arena.AddBool(
            Types.Bool(Nullability.NonNullable), valid.Length, Vorticity.Arrays.Validity.NonNullable,
            Bytes(raw, 8), 0);
        return Vorticity.Arrays.Validity.Bitmap(bits);
    }

    private static Nullability Null(bool[]? valid) =>
        valid is null ? Nullability.NonNullable : Nullability.Nullable;

    /// <summary>A primitive column of any physical type.</summary>
    internal int Primitive<T>(PType ptype, ReadOnlySpan<T> values, bool[]? valid = null)
        where T : unmanaged
    {
        ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(values);
        return Arena.AddPrimitive(
            Types.Primitive(ptype, Null(valid)), values.Length, Validity(valid), ptype, Bytes(raw));
    }

    /// <summary>A Bool column.</summary>
    internal int Bool(ReadOnlySpan<bool> values, bool[]? valid = null, int bitOffset = 0)
    {
        int byteCount = (bitOffset + values.Length + 7) / 8;
        byte[] raw = new byte[Math.Max(byteCount, 1)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                int bit = bitOffset + i;
                raw[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        return Arena.AddBool(
            Types.Bool(Null(valid)), values.Length, Validity(valid), Bytes(raw, 8), bitOffset);
    }

    /// <summary>An all-null column of the Null dtype.</summary>
    internal int Nulls(int rows) => Arena.AddNull(Types.Null(Nullability.Nullable), rows);

    /// <summary>A Utf8 or Binary column. A null entry is a null row.</summary>
    internal int VarBin(ReadOnlySpan<byte[]?> values, DTypeKind kind = DTypeKind.Utf8, bool forceNullable = false)
    {
        int length = values.Length;
        byte[] views = new byte[Math.Max(length * 16, 1)];
        List<byte> heap = [];
        bool[] valid = new bool[length];
        bool anyNull = false;

        for (int i = 0; i < length; i++)
        {
            byte[]? value = values[i];
            valid[i] = value is not null;
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

        bool nullable = anyNull || forceNullable;
        Nullability nullability = nullable ? Nullability.Nullable : Nullability.NonNullable;
        Validity validity = anyNull
            ? Validity(valid)
            : (nullable ? Vorticity.Arrays.Validity.AllValid : Vorticity.Arrays.Validity.NonNullable);

        DType dtype = kind == DTypeKind.Utf8 ? Types.Utf8(nullability) : Types.Binary(nullability);
        Span<VortexBuffer> buffers = heap.Count == 0
            ? [VortexBuffer.Empty]
            : [Bytes(heap.ToArray())];
        return Arena.AddVarBinView(dtype, length, validity, Bytes(views), buffers);
    }

    /// <summary>A Decimal column whose physical storage width is chosen explicitly.</summary>
    internal int Decimal(
        ReadOnlySpan<Int128> unscaled, byte precision, sbyte scale,
        DecimalStorageType storage, bool[]? valid = null)
    {
        int width = DecimalStorage.ByteWidth(storage);
        byte[] raw = new byte[Math.Max(unscaled.Length * width, 1)];
        Span<byte> scratch = stackalloc byte[16];
        for (int i = 0; i < unscaled.Length; i++)
        {
            BinaryPrimitives.WriteInt128LittleEndian(scratch, unscaled[i]);
            scratch[..width].CopyTo(raw.AsSpan(i * width, width));
        }

        return Arena.AddDecimal(
            Types.Decimal(precision, scale, Null(valid)), unscaled.Length, Validity(valid),
            storage, precision, scale, Bytes(raw, 16));
    }

    /// <summary>A Struct column over already-built children.</summary>
    internal int Struct(ReadOnlySpan<string> names, ReadOnlySpan<int> children, int rows, bool[]? valid = null)
    {
        Span<DType> fieldTypes = new DType[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            fieldTypes[i] = Arena.GetNode(children[i]).DType;
        }

        return Arena.AddStruct(
            Types.Struct(names, fieldTypes, Null(valid)), rows, Validity(valid), children);
    }

    /// <summary>A FixedSizeList column over an already-built elements child.</summary>
    internal int FixedSizeList(int elements, uint size, int rows, bool[]? valid = null)
    {
        DType elementType = Arena.GetNode(elements).DType;
        return Arena.AddFixedSizeList(
            Types.FixedSizeList(elementType, size, Null(valid)), rows, Validity(valid), elements, size);
    }

    public void Dispose() => Arena.Reset();
}
