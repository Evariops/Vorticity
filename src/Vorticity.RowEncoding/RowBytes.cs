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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int WriteVarBody(ReadOnlySpan<byte> value, Span<byte> destination, bool descending) =>
        descending
            ? WriteVarBody<Inverted>(value, destination)
            : WriteVarBody<Plain>(value, destination);

    /// <remarks>
    /// One body a direction, each laid out by its own calls: in a single body, a profile taken
    /// while one direction ran leaves the other's copy loop out of line.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WriteVarBody<TDirection>(ReadOnlySpan<byte> value, Span<byte> destination)
        where TDirection : struct, IDirection
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

        int read = 0;
        int written = 0;
        for (int block = 0; block < continuationBlocks; block++)
        {
            Span<byte> data = destination.Slice(written, RowWidths.VarBlockData);
            TDirection.Copy(value.Slice(read, RowWidths.VarBlockData), data);
            destination[written + RowWidths.VarBlockData] = TDirection.Continuation;
            read += RowWidths.VarBlockData;
            written += RowWidths.VarBlockTotal;
        }

        TDirection.Copy(value.Slice(read, finalLength), destination.Slice(written, finalLength));
        destination.Slice(written + finalLength, RowWidths.VarBlockData - finalLength).Fill(TDirection.Pad);
        destination[written + RowWidths.VarBlockData] = TDirection.Marker(finalLength);
        return written + RowWidths.VarBlockTotal;
    }

    /// <summary>How a sort direction writes a value's bytes and its blocks' framing.</summary>
    private interface IDirection
    {
        static abstract byte Continuation { get; }

        static abstract byte Pad { get; }

        static abstract byte Marker(int finalLength);

        static abstract void Copy(ReadOnlySpan<byte> source, Span<byte> destination);
    }

    /// <summary>Ascending: the bytes as they are.</summary>
    private readonly struct Plain : IDirection
    {
        public static byte Continuation => 0xFF;

        public static byte Pad => 0x00;

        public static byte Marker(int finalLength) => (byte)finalLength;

        public static void Copy(ReadOnlySpan<byte> source, Span<byte> destination) => source.CopyTo(destination);
    }

    /// <summary>Descending: every byte complemented, the framing's too.</summary>
    private readonly struct Inverted : IDirection
    {
        public static byte Continuation => 0x00;

        public static byte Pad => 0xFF;

        public static byte Marker(int finalLength) => (byte)(finalLength ^ 0xFF);

        public static void Copy(ReadOnlySpan<byte> source, Span<byte> destination) => Invert(source, destination);
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
