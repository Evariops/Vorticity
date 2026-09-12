// The variable-length body, and the one place SIMD earns its keep.
//
// docs/06-row-encoding.md §7 calls out three vectorizable operations. Two of them are not worth a
// hand-written kernel in .NET - big-endian conversion is already a single BSWAP through
// IBinaryInteger.WriteBigEndian, and a strided sentinel+value write is a scatter no vector width
// helps with. The third one is here: the 32-byte block. Ascending is a plain 32-byte copy;
// descending is a 32-byte copy XORed with 0xFF, which is exactly one Vector256 load, xor and store.
//
// Transcribed from `encode_non_empty_varlen_body` in vortex-row/src/codec.rs at 0.86.1.
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Vorticity.RowEncoding;

/// <summary>Byte-level primitives shared by the encoders.</summary>
internal static class RowBytes
{
    /// <summary>
    /// Writes the body of a non-empty variable-length value: whole 32-byte blocks, each followed
    /// by a marker.
    /// </summary>
    /// <remarks>
    /// The marker is what preserves prefix ordering. Every non-final block carries the
    /// continuation marker (<c>0xFF</c> ascending, <c>0x00</c> descending); the final block
    /// carries its real data length in 1..32, zero-padded (or <c>0xFF</c>-padded when descending)
    /// to the full 32 bytes. A shorter value therefore reaches a marker below <c>0xFF</c> while a
    /// longer one is still emitting continuations, so the prefix sorts first.
    ///
    /// A length that is an exact multiple of 32 takes the same path with one fewer continuation
    /// block and a final marker of 32 - not an extra empty block, which would compare wrong.
    /// </remarks>
    /// <param name="value">The value's bytes; never empty.</param>
    /// <param name="destination">Where to write, at least <c>NonEmptyVarSize(len) - 1</c> long.</param>
    /// <param name="descending">Whether to invert the data bytes and the markers.</param>
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
    /// <param name="source">The bytes to copy.</param>
    /// <param name="destination">Where to write them; same length as <paramref name="source"/>.</param>
    /// <param name="descending">Whether to complement.</param>
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

    /// <summary>Writes <c>source XOR 0xFF</c> into <paramref name="destination"/>.</summary>
    /// <param name="source">The bytes to complement.</param>
    /// <param name="destination">Where to write them; same length as <paramref name="source"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Invert(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            // The whole reason this file exists: the common block is exactly 32 bytes, which is
            // one Vector256 operation and no loop at all.
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

    /// <summary>Reads the 16-byte Arrow view of row <paramref name="row"/>.</summary>
    /// <param name="views">The views buffer.</param>
    /// <param name="row">0-based row index.</param>
    /// <returns>The 16 bytes of the view.</returns>
    /// <exception cref="VortexFormatException">The buffer is too short for the row.</exception>
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
    /// <param name="view">The 16 view bytes.</param>
    /// <returns>The value's length.</returns>
    /// <exception cref="VortexFormatException">The length does not fit an <see cref="int"/>.</exception>
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
