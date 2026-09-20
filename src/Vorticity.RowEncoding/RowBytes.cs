using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Vorticity.RowEncoding;

/// <summary>Byte-level primitives shared by the encoders.</summary>
internal static class RowBytes
{
    /// <summary>
    /// Writes a non-empty variable-length value as 32-byte blocks, each followed by a marker: the
    /// continuation marker on every non-final block, the final block's data length in 1..32 on the
    /// last, so that a prefix reaches a lower marker and sorts first. The value must be non-empty
    /// and a length that is a multiple of 32 ends on a marker of 32 rather than an empty block.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    internal static int WriteVarBody(ReadOnlySpan<byte> value, Span<byte> destination, bool descending)
    {
        int length = value.Length;
        int fullBlocks = length / RowWidths.VarBlockData;
        int partial = length % RowWidths.VarBlockData;

        int continuationBlocks;
        int finalLength;
        if (partial == 0)
        {
            continuationBlocks = fullBlocks - 1;
            finalLength = RowWidths.VarBlockData;
        }
        else
        {
            continuationBlocks = fullBlocks;
            finalLength = partial;
        }

        byte continuation = descending ? (byte)0x00 : (byte)0xFF;
        byte marker = descending ? (byte)(finalLength ^ 0xFF) : (byte)finalLength;
        byte pad = descending ? (byte)0xFF : (byte)0x00;

        int read = 0;
        int written = 0;
        for (int block = 0; block < continuationBlocks; block++)
        {
            Span<byte> data = destination.Slice(written, RowWidths.VarBlockData);
            CopyMaybeInverted(value.Slice(read, RowWidths.VarBlockData), data, descending);
            destination[written + RowWidths.VarBlockData] = continuation;
            read += RowWidths.VarBlockData;
            written += RowWidths.VarBlockTotal;
        }

        CopyMaybeInverted(value.Slice(read, finalLength), destination.Slice(written, finalLength), descending);
        destination.Slice(written + finalLength, RowWidths.VarBlockData - finalLength).Fill(pad);
        destination[written + RowWidths.VarBlockData] = marker;
        return written + RowWidths.VarBlockTotal;
    }

    /// <summary>Copies <paramref name="source"/>, complementing every byte when descending.</summary>
    internal static void CopyMaybeInverted(
        ReadOnlySpan<byte> source, Span<byte> destination, bool descending)
    {
        if (!descending)
        {
            source.CopyTo(destination);
            return;
        }

        Invert(source, destination);
    }

    /// <summary>
    /// Writes <c>source XOR 0xFF</c> into <paramref name="destination"/>, which must be as long as
    /// the source. Sliced rather than loaded unsafely: the inversion is too cheap for the bounds
    /// checks to show.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Invert(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> ones = Vector256<byte>.AllBitsSet;
            for (; i <= source.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256.Xor(Vector256.Create(source.Slice(i)), ones)
                    .CopyTo(destination.Slice(i));
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> ones = Vector128<byte>.AllBitsSet;
            for (; i <= source.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128.Xor(Vector128.Create(source.Slice(i)), ones)
                    .CopyTo(destination.Slice(i));
            }
        }

        for (; i < source.Length; i++)
        {
            destination[i] = (byte)~source[i];
        }
    }

    /// <summary>Reads the 16-byte Arrow view of the 0-based row <paramref name="row"/>.</summary>
    internal static ReadOnlySpan<byte> View(ReadOnlySpan<byte> views, int row)
    {
        int start = row * 16;
        if ((uint)start > (uint)(views.Length - 16))
        {
            throw new VortexFormatException(
                $"Row {row} needs view bytes [{start}, {start + 16}) of a {views.Length}-byte views buffer.");
        }

        return views.Slice(start, 16);
    }

    /// <summary>The byte length recorded in a 16-byte view.</summary>
    internal static int ViewLength(ReadOnlySpan<byte> view)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size > int.MaxValue)
        {
            throw new VortexFormatException($"A view declares a length of {size} bytes.");
        }

        return (int)size;
    }
}
