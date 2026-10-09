using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Serialization;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// DELTA_BINARY_PACKED, read and written: a header of the block size, the miniblocks per block, the
/// value count and the first value, then blocks of a zigzag minimum delta, a width per miniblock,
/// and the miniblocks' deltas less that minimum, bit-packed at their width.
/// </summary>
/// <remarks>
/// <para>
/// Arithmetic is two's complement and wraps, as the standard asks, so that any sequence of the type
/// round-trips. A miniblock is unpacked whole by <see cref="BitPacking"/>'s kernels, then summed;
/// the bytes of the last miniblock are the full miniblock's, padding included, and a block's
/// miniblocks past the last value have no bytes at all.
/// </para>
/// <para>
/// The writer cuts blocks of 128 values in four miniblocks of 32, and prices an encoding exactly:
/// <see cref="Size32"/> and <see cref="Size64"/> walk the same blocks <see cref="Encode32"/> and
/// <see cref="Encode64"/> write.
/// </para>
/// </remarks>
internal static class DeltaBinaryPacked
{
    /// <summary>The values of a block the writer cuts.</summary>
    internal const int BlockValues = 128;

    /// <summary>The miniblocks of a block the writer cuts.</summary>
    internal const int MiniblocksPerBlock = 4;

    private const int MiniblockValues = BlockValues / MiniblocksPerBlock;

    /// <summary>The largest miniblock a reader decodes: a forged one is refused before it sizes a buffer.</summary>
    private const int MaxMiniblockValues = 1 << 16;

    /// <summary>An encoding's header.</summary>
    /// <param name="BlockValues">Values per block.</param>
    /// <param name="Miniblocks">Miniblocks per block.</param>
    /// <param name="Count">The values encoded.</param>
    /// <param name="First">The first value.</param>
    /// <param name="Length">The header's bytes.</param>
    internal readonly record struct Header(int BlockValues, int Miniblocks, int Count, long First, int Length);

    /// <summary>Reads an encoding's header, checking the shape the standard requires of its blocks.</summary>
    internal static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        int position = 0;
        uint block = Varint.Read32<Errors>(data, ref position);
        uint miniblocks = Varint.Read32<Errors>(data, ref position);
        uint count = Varint.Read32<Errors>(data, ref position);
        long first = Varint.ZigZagDecode64(Varint.Read64<Errors>(data, ref position));
        if (block == 0 || block % 128 != 0 || miniblocks == 0 || block % miniblocks != 0
            || (block / miniblocks) % 32 != 0 || block / miniblocks > MaxMiniblockValues)
        {
            ParquetThrow.Format($"A DELTA_BINARY_PACKED header declares blocks of {block} values in {miniblocks} miniblocks, which the standard does not allow.");
        }

        if (count > int.MaxValue)
        {
            ParquetThrow.Format($"A DELTA_BINARY_PACKED header declares {count} values.");
        }

        return new Header((int)block, (int)miniblocks, (int)count, first, position);
    }

    /// <summary>Decodes INT32 values into all of <paramref name="destination"/>; the bytes the encoding took.</summary>
    /// <exception cref="ParquetFormatException">The encoding is malformed, or holds another number of values.</exception>
    internal static int Decode32(ReadOnlySpan<byte> data, Span<int> destination)
    {
        Header header = ReadHeader(data);
        RequireCount(header, destination.Length);
        if (header.Count == 0)
        {
            return header.Length;
        }

        int perMiniblock = header.BlockValues / header.Miniblocks;
        if (perMiniblock <= 256)
        {
            return Blocks32(data, destination, header, stackalloc uint[256]);
        }

        // A miniblock longer than writers cut: its deltas in a rented array, which a decode that
        // throws leaves to the collector.
        uint[] rented = ArrayPool<uint>.Shared.Rent(perMiniblock);
        int length = Blocks32(data, destination, header, rented);
        ArrayPool<uint>.Shared.Return(rented);
        return length;
    }

    /// <summary>
    /// The blocks of an INT32 encoding. No handler surrounds the loop, so that the running value
    /// stays in a register rather than being written through to the frame at every add.
    /// </summary>
    private static int Blocks32(ReadOnlySpan<byte> data, Span<int> destination, Header header, Span<uint> deltas)
    {
        int perMiniblock = header.BlockValues / header.Miniblocks;
        {
            int position = header.Length;
            uint last = (uint)header.First;
            destination[0] = (int)last;
            int done = 1;
            while (done < header.Count)
            {
                uint minimum = (uint)Varint.ZigZagDecode64(Varint.Read64<Errors>(data, ref position));
                ReadOnlySpan<byte> widths = Take(data, ref position, header.Miniblocks);
                for (int m = 0; m < header.Miniblocks && done < header.Count; m++)
                {
                    int width = widths[m];
                    if (width > 32)
                    {
                        ParquetThrow.Format($"A DELTA_BINARY_PACKED miniblock of INT32 values is {width} bits wide.");
                    }

                    int bytes = perMiniblock / 8 * width;
                    if (bytes > data.Length - position)
                    {
                        ParquetThrow.Truncated("DELTA_BINARY_PACKED block");
                    }

                    int take = Math.Min(perMiniblock, header.Count - done);
                    ref int into = ref MemoryMarshal.GetReference(destination.Slice(done, take));
                    if (width == 0)
                    {
                        // Every delta is the minimum: a progression.
                        for (int i = 0; i < take; i++)
                        {
                            last += minimum;
                            Unsafe.Add(ref into, i) = (int)last;
                        }
                    }
                    else
                    {
                        // The kernel is given the rest of the page, so that its wide loads stay on
                        // their fast path past the miniblock's last byte.
                        Span<uint> block = deltas[..take];
                        BitPacking.Unpack32(data[position..], width, block);
                        ref uint delta = ref MemoryMarshal.GetReference(block);
                        for (int i = 0; i < take; i++)
                        {
                            last += minimum + Unsafe.Add(ref delta, i);
                            Unsafe.Add(ref into, i) = (int)last;
                        }
                    }

                    position += bytes;
                    done += take;
                }
            }

            return position;
        }
    }

    /// <summary>Decodes INT64 values into all of <paramref name="destination"/>; the bytes the encoding took.</summary>
    /// <exception cref="ParquetFormatException">The encoding is malformed, or holds another number of values.</exception>
    internal static int Decode64(ReadOnlySpan<byte> data, Span<long> destination)
    {
        Header header = ReadHeader(data);
        RequireCount(header, destination.Length);
        if (header.Count == 0)
        {
            return header.Length;
        }

        int perMiniblock = header.BlockValues / header.Miniblocks;
        if (perMiniblock <= 256)
        {
            return Blocks64(data, destination, header, stackalloc ulong[256]);
        }

        ulong[] rented = ArrayPool<ulong>.Shared.Rent(perMiniblock);
        int length = Blocks64(data, destination, header, rented);
        ArrayPool<ulong>.Shared.Return(rented);
        return length;
    }

    /// <summary>The blocks of an INT64 encoding, as <see cref="Blocks32"/> walks an INT32 one's.</summary>
    private static int Blocks64(ReadOnlySpan<byte> data, Span<long> destination, Header header, Span<ulong> deltas)
    {
        int perMiniblock = header.BlockValues / header.Miniblocks;
        {
            int position = header.Length;
            ulong last = (ulong)header.First;
            destination[0] = (long)last;
            int done = 1;
            while (done < header.Count)
            {
                ulong minimum = (ulong)Varint.ZigZagDecode64(Varint.Read64<Errors>(data, ref position));
                ReadOnlySpan<byte> widths = Take(data, ref position, header.Miniblocks);
                for (int m = 0; m < header.Miniblocks && done < header.Count; m++)
                {
                    int width = widths[m];
                    if (width > 64)
                    {
                        ParquetThrow.Format($"A DELTA_BINARY_PACKED miniblock of INT64 values is {width} bits wide.");
                    }

                    int bytes = perMiniblock / 8 * width;
                    if (bytes > data.Length - position)
                    {
                        ParquetThrow.Truncated("DELTA_BINARY_PACKED block");
                    }

                    int take = Math.Min(perMiniblock, header.Count - done);
                    Span<long> into = destination.Slice(done, take);
                    if (width == 0)
                    {
                        // Every delta is the minimum: a progression.
                        ref long first = ref MemoryMarshal.GetReference(into);
                        for (int i = 0; i < take; i++)
                        {
                            last += minimum;
                            Unsafe.Add(ref first, i) = (long)last;
                        }
                    }
                    else if (width <= 57)
                    {
                        // Unpacked and summed in one pass, over the rest of the page so that every
                        // load but the page's last few is one 64-bit word.
                        last = BitPacking.UnpackSum64(data[position..], width, minimum, last, into);
                    }
                    else
                    {
                        Span<ulong> block = deltas[..take];
                        BitPacking.Unpack64(data[position..], width, block);
                        ref ulong delta = ref MemoryMarshal.GetReference(block);
                        ref long first = ref MemoryMarshal.GetReference(into);
                        for (int i = 0; i < take; i++)
                        {
                            last += minimum + Unsafe.Add(ref delta, i);
                            Unsafe.Add(ref first, i) = (long)last;
                        }
                    }

                    position += bytes;
                    done += take;
                }
            }

            return position;
        }
    }

    /// <summary>The bytes <see cref="Encode32"/> writes for <paramref name="values"/>.</summary>
    internal static int Size32(ReadOnlySpan<int> values)
    {
        Output output = new(default, measure: true);
        Write32(values, ref output);
        return output.Position;
    }

    /// <summary>Encodes <paramref name="values"/> into <paramref name="destination"/>, which holds <see cref="Size32"/>; the bytes written.</summary>
    internal static int Encode32(ReadOnlySpan<int> values, Span<byte> destination)
    {
        Output output = new(destination, measure: false);
        Write32(values, ref output);
        return output.Position;
    }

    /// <summary>The bytes <see cref="Encode64"/> writes for <paramref name="values"/>.</summary>
    internal static int Size64(ReadOnlySpan<long> values)
    {
        Output output = new(default, measure: true);
        Write64(values, ref output);
        return output.Position;
    }

    /// <summary>Encodes <paramref name="values"/> into <paramref name="destination"/>, which holds <see cref="Size64"/>; the bytes written.</summary>
    internal static int Encode64(ReadOnlySpan<long> values, Span<byte> destination)
    {
        Output output = new(destination, measure: false);
        Write64(values, ref output);
        return output.Position;
    }

    private static void Write32(ReadOnlySpan<int> values, ref Output output)
    {
        output.Varint(BlockValues);
        output.Varint(MiniblocksPerBlock);
        output.Varint((uint)values.Length);
        output.Varint(Varint.ZigZagEncode64(values.IsEmpty ? 0 : values[0]));
        Span<uint> deltas = stackalloc uint[BlockValues];
        Span<byte> widths = stackalloc byte[MiniblocksPerBlock];
        for (int start = 1; start < values.Length; start += BlockValues)
        {
            int count = Math.Min(BlockValues, values.Length - start);

            // The deltas wrap, and so does their distance from the least of them.
            int minimum = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int delta = values[start + i] - values[start + i - 1];
                deltas[i] = (uint)delta;
                minimum = Math.Min(minimum, delta);
            }

            for (int i = 0; i < count; i++)
            {
                deltas[i] -= (uint)minimum;
            }

            output.Varint(Varint.ZigZagEncode64(minimum));
            for (int m = 0; m < MiniblocksPerBlock; m++)
            {
                int from = m * MiniblockValues;
                uint bits = 0;
                for (int i = from; i < Math.Min(count, from + MiniblockValues); i++)
                {
                    bits |= deltas[i];
                }

                widths[m] = (byte)(32 - BitOperations.LeadingZeroCount(bits));
            }

            output.Bytes(widths);
            for (int m = 0; m * MiniblockValues < count; m++)
            {
                // A short last miniblock is padded with zeroes to its full length.
                Span<uint> block = deltas.Slice(m * MiniblockValues, MiniblockValues);
                block[Math.Min(MiniblockValues, count - (m * MiniblockValues))..].Clear();
                output.Pack32(block, widths[m]);
            }
        }
    }

    private static void Write64(ReadOnlySpan<long> values, ref Output output)
    {
        output.Varint(BlockValues);
        output.Varint(MiniblocksPerBlock);
        output.Varint((uint)values.Length);
        output.Varint(Varint.ZigZagEncode64(values.IsEmpty ? 0 : values[0]));
        Span<ulong> deltas = stackalloc ulong[BlockValues];
        Span<byte> widths = stackalloc byte[MiniblocksPerBlock];
        for (int start = 1; start < values.Length; start += BlockValues)
        {
            int count = Math.Min(BlockValues, values.Length - start);
            long minimum = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                long delta = values[start + i] - values[start + i - 1];
                deltas[i] = (ulong)delta;
                minimum = Math.Min(minimum, delta);
            }

            for (int i = 0; i < count; i++)
            {
                deltas[i] -= (ulong)minimum;
            }

            output.Varint(Varint.ZigZagEncode64(minimum));
            for (int m = 0; m < MiniblocksPerBlock; m++)
            {
                int from = m * MiniblockValues;
                ulong bits = 0;
                for (int i = from; i < Math.Min(count, from + MiniblockValues); i++)
                {
                    bits |= deltas[i];
                }

                widths[m] = (byte)(64 - BitOperations.LeadingZeroCount(bits));
            }

            output.Bytes(widths);
            for (int m = 0; m * MiniblockValues < count; m++)
            {
                Span<ulong> block = deltas.Slice(m * MiniblockValues, MiniblockValues);
                block[Math.Min(MiniblockValues, count - (m * MiniblockValues))..].Clear();
                output.Pack64(block, widths[m]);
            }
        }
    }

    private static void RequireCount(Header header, int expected)
    {
        if (header.Count != expected)
        {
            ParquetThrow.Format($"A DELTA_BINARY_PACKED run declares {header.Count} values where its page holds {expected}.");
        }
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int position, int count)
    {
        if (count > data.Length - position)
        {
            ParquetThrow.Truncated("DELTA_BINARY_PACKED block");
        }

        ReadOnlySpan<byte> taken = data.Slice(position, count);
        position += count;
        return taken;
    }

    /// <summary>Where an encoding goes: written into a span, or only counted, so that a price and a write walk the same blocks.</summary>
    private ref struct Output(Span<byte> destination, bool measure)
    {
        private readonly Span<byte> _destination = destination;
        private readonly bool _measure = measure;

        internal int Position { get; private set; }

        internal void Varint(ulong value)
        {
            Position += _measure
                ? Serialization.Varint.Size(value)
                : Serialization.Varint.Write(_destination[Position..], value);
        }

        internal void Bytes(scoped ReadOnlySpan<byte> bytes)
        {
            if (!_measure)
            {
                bytes.CopyTo(_destination[Position..]);
            }

            Position += bytes.Length;
        }

        internal void Pack32(scoped ReadOnlySpan<uint> values, int width)
        {
            int bytes = values.Length / 8 * width;
            if (!_measure)
            {
                BitPacking.Pack32(values, width, _destination.Slice(Position, bytes));
            }

            Position += bytes;
        }

        internal void Pack64(scoped ReadOnlySpan<ulong> values, int width)
        {
            int bytes = values.Length / 8 * width;
            if (!_measure)
            {
                BitPacking.Pack64(values, width, _destination.Slice(Position, bytes));
            }

            Position += bytes;
        }
    }

    /// <summary>The encoding's refusals of a varint.</summary>
    private readonly struct Errors : IVarintErrors
    {
        public static ulong Truncated(int position) => ParquetThrow.Format<ulong>("A DELTA_BINARY_PACKED varint runs past its page.");

        public static ulong Malformed(int position, byte value) => ParquetThrow.Format<ulong>("A DELTA_BINARY_PACKED varint holds more bits than its type.");
    }
}
