using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Serialization;

namespace Vorticity.Parquet.Encodings;

/// <summary>
/// A cursor over the RLE/bit-packing hybrid: runs read from their headers as values are asked for,
/// so that a page's levels or codes decode in the batches its reader cuts.
/// </summary>
/// <remarks>
/// <para>
/// The cursor holds positions, not the bytes: each call is given the run data again, which the page
/// buffer keeps alive between batches. A run's header is checked when it is read: a length of zero,
/// a header past the data, or a bit-packed run whose bytes the data does not hold is
/// <see cref="ParquetFormatException"/>. The values of the last bit-packed run past those asked for
/// are padding the standard lets a writer leave, and are never read.
/// </para>
/// <para>
/// Width 1, a boolean or the definition levels of a flat optional column, has a path of its own:
/// a bit-packed run of width 1 is already a bitmap, least significant bit first, and is copied
/// whole into the destination's bits; a repeated run fills them.
/// </para>
/// </remarks>
internal struct RleHybridDecoder
{
    private readonly int _bitWidth;
    private readonly int _valueBytes;
    private int _position;
    private int _remaining;
    private bool _packed;
    private uint _value;
    private int _packedStart;
    private int _packedIndex;

    /// <summary>A cursor at the start of run data of <paramref name="bitWidth"/>, 0 to 32.</summary>
    internal RleHybridDecoder(int bitWidth)
    {
        if ((uint)bitWidth > 32)
        {
            ParquetThrow.Format($"An RLE/bit-packing hybrid declares a width of {bitWidth} bits, past 32.");
        }

        _bitWidth = bitWidth;
        _valueBytes = (bitWidth + 7) >> 3;
    }

    internal readonly int BitWidth => _bitWidth;

    /// <summary>Decodes <c>destination.Length</c> values; throws when the data holds fewer.</summary>
    internal void Read(ReadOnlySpan<byte> data, Span<uint> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            if (_remaining == 0)
            {
                NextRun(data);
            }

            int take = Math.Min(_remaining, destination.Length - done);
            if (_packed)
            {
                Unpack(data, destination.Slice(done, take));
            }
            else
            {
                destination.Slice(done, take).Fill(_value);
            }

            _remaining -= take;
            done += take;
        }
    }

    /// <summary>Decodes <c>destination.Length</c> values of at most 8 bits, a byte each: levels, and a small dictionary's codes.</summary>
    /// <remarks>
    /// Runs of a few values each, as a column of few values that change often has, cost their
    /// header and their dispatch more than their values: a repeated run is laid out a word at a
    /// time, its last word ending at the run's end, and a bit-packed run's whole groups are laid out
    /// here, a word of eight values each by one bit deposit, while each group's word lies in the data.
    /// </remarks>
    internal void Read(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (_bitWidth > 8)
        {
            ParquetThrow.Format($"Levels of {_bitWidth} bits do not fit a byte.");
        }

        int count = destination.Length;
        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte input = ref MemoryMarshal.GetReference(data);
        bool deposits = Bmi2.X64.IsSupported && BitConverter.IsLittleEndian && _bitWidth is > 0 and < 8;
        ulong lanes = 0x0101010101010101UL * ((1UL << _bitWidth) - 1);
        int done = 0;
        while (done < count)
        {
            if (_remaining == 0)
            {
                NextRun(data);
            }

            int take = Math.Min(_remaining, count - done);
            if (!_packed)
            {
                Repeat(ref Unsafe.Add(ref output, done), take, (byte)_value);
            }
            else if (deposits && (_packedIndex & 7) == 0)
            {
                // The run's groups from its next, while the word of each lies in the data.
                int at = _packedStart + ((_packedIndex >> 3) * _bitWidth);
                int room = data.Length - 8 - at;
                int groups = Math.Min(take >> 3, room < 0 ? 0 : (room / _bitWidth) + 1);
                for (int group = 0; group < groups; group++)
                {
                    ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref input, at + (group * _bitWidth)));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, done + (group * 8)), Bmi2.X64.ParallelBitDeposit(word, lanes));
                }

                int laid = groups * 8;
                _packedIndex += laid;
                if (laid < take)
                {
                    UnpackBytes(data, destination.Slice(done + laid, take - laid));
                }
            }
            else
            {
                UnpackBytes(data, destination.Slice(done, take));
            }

            _remaining -= take;
            done += take;
        }
    }

    /// <summary>Lays <paramref name="count"/> bytes of <paramref name="value"/> from <paramref name="into"/>, a word at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Repeat(ref byte into, int count, byte value)
    {
        if (count < 8)
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.Add(ref into, i) = value;
            }

            return;
        }

        ulong word = 0x0101010101010101UL * value;
        int at = 0;
        for (; at <= count - 8; at += 8)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref into, at), word);
        }

        // The last word ends at the run's end, over bytes already laid.
        if (at < count)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref into, count - 8), word);
        }
    }

    /// <summary>
    /// Decodes <paramref name="count"/> values of width 1 into <paramref name="bits"/> from bit
    /// <paramref name="start"/>, least significant first, and returns how many are set.
    /// </summary>
    internal int ReadBits(ReadOnlySpan<byte> data, Span<byte> bits, int start, int count)
    {
        if (_bitWidth != 1)
        {
            ParquetThrow.Format($"Values of {_bitWidth} bits read as bits.");
        }

        int done = 0;
        int set = 0;
        while (done < count)
        {
            if (_remaining == 0)
            {
                NextRun(data);
            }

            int take = Math.Min(_remaining, count - done);
            if (_packed)
            {
                ReadOnlySpan<byte> packed = data[_packedStart..];
                BitmapKernels.CopyRange(packed, _packedIndex, bits, start + done, take);
                set += BitmapKernels.CountSet(packed, _packedIndex, take);
                _packedIndex += take;
            }
            else
            {
                bool value = (_value & 1) != 0;
                BitmapKernels.FillRange(bits, start + done, take, value);
                set += value ? take : 0;
            }

            _remaining -= take;
            done += take;
        }

        return set;
    }

    /// <summary>Steps over <paramref name="count"/> values without decoding them.</summary>
    internal void Skip(ReadOnlySpan<byte> data, int count)
    {
        while (count > 0)
        {
            if (_remaining == 0)
            {
                NextRun(data);
            }

            int take = Math.Min(_remaining, count);
            if (_packed)
            {
                _packedIndex += take;
            }

            _remaining -= take;
            count -= take;
        }
    }

    /// <summary>
    /// The run under the cursor, read when the last one ran out: a bit-packed run of whole groups of
    /// eight, or a repeated run of one value.
    /// </summary>
    private void NextRun(ReadOnlySpan<byte> data)
    {
        if (_position >= data.Length)
        {
            ParquetThrow.Format("An RLE/bit-packing hybrid ends before the values its page declares.");
        }

        uint header = ReadVarint(data, ref _position);
        if ((header & 1) != 0)
        {
            int groups = (int)(header >> 1);
            long bytes = (long)groups * _bitWidth;
            if (groups == 0 || bytes > data.Length - _position)
            {
                ParquetThrow.Format("A bit-packed run is empty or longer than its data.");
            }

            _packed = true;
            _remaining = groups * 8;
            _packedStart = _position;
            _packedIndex = 0;
            _position += (int)bytes;
        }
        else
        {
            int length = (int)(header >> 1);
            if (length == 0 || _valueBytes > data.Length - _position)
            {
                ParquetThrow.Format("A repeated run is empty or its value lies past the data.");
            }

            uint value = 0;
            for (int i = 0; i < _valueBytes; i++)
            {
                value |= (uint)data[_position + i] << (8 * i);
            }

            _packed = false;
            _remaining = length;
            _value = value;
            _position += _valueBytes;
        }
    }

    private void Unpack(ReadOnlySpan<byte> data, Span<uint> destination)
    {
        // A run that starts mid-group continues from its value index: unpack from the group it is
        // in and drop the values before it, at most seven.
        int first = _packedIndex;
        int group = first >> 3;
        int skip = first & 7;
        ReadOnlySpan<byte> run = data.Slice(_packedStart + group * _bitWidth);
        if (skip == 0)
        {
            BitPacking.Unpack32(run, _bitWidth, destination);
        }
        else
        {
            Span<uint> head = stackalloc uint[8];
            int fromHead = Math.Min(8 - skip, destination.Length);
            BitPacking.Unpack32(run, _bitWidth, head);
            head.Slice(skip, fromHead).CopyTo(destination);
            if (destination.Length > fromHead)
            {
                BitPacking.Unpack32(run.Slice(_bitWidth), _bitWidth, destination[fromHead..]);
            }
        }

        _packedIndex += destination.Length;
    }

    private void UnpackBytes(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int first = _packedIndex;
        int group = first >> 3;
        int skip = first & 7;
        ReadOnlySpan<byte> run = data.Slice(_packedStart + group * _bitWidth);
        if (skip == 0)
        {
            BitPacking.Unpack8(run, _bitWidth, destination);
        }
        else
        {
            Span<byte> head = stackalloc byte[8];
            int fromHead = Math.Min(8 - skip, destination.Length);
            BitPacking.Unpack8(run, _bitWidth, head);
            head.Slice(skip, fromHead).CopyTo(destination);
            if (destination.Length > fromHead)
            {
                BitPacking.Unpack8(run.Slice(_bitWidth), _bitWidth, destination[fromHead..]);
            }
        }

        _packedIndex += destination.Length;
    }

    /// <summary>A run header: an unsigned varint of at most 32 bits.</summary>
    internal static uint ReadVarint(ReadOnlySpan<byte> data, ref int position) =>
        Varint.Read32<RunHeaderErrors>(data, ref position);

    /// <summary>The hybrid's refusals of a run header.</summary>
    private readonly struct RunHeaderErrors : IVarintErrors
    {
        public static ulong Truncated(int position) => ParquetThrow.Format<ulong>("An RLE/bit-packing run header is truncated.");

        public static ulong Malformed(int position, byte value) => ParquetThrow.Format<ulong>("An RLE/bit-packing run header passes 32 bits.");
    }
}

/// <summary>
/// Writes the RLE/bit-packing hybrid: values planned once into repeated runs where a run pays for
/// its header, and bit-packed runs of whole groups of eight between them.
/// </summary>
/// <remarks>
/// The plan is exact and shared: <c>Size</c> walks it to price a page before writing it, and
/// <c>Encode</c> walks the same one to write exactly that many bytes. A repeated run is taken
/// only where it costs less than packing its values, its header and the next packed run's header
/// counted; a packed run in the middle of the data holds whole groups, borrowing the head of the
/// repeated run after it to complete its last group, and only the last packed run is padded.
/// </remarks>
internal static class RleHybridEncoder
{
    /// <summary>The bytes <see cref="Encode(ReadOnlySpan{uint}, int, Span{byte})"/> writes for <paramref name="values"/>.</summary>
    internal static int Size(ReadOnlySpan<uint> values, int bitWidth)
    {
        Sizer sizer = new(bitWidth);
        Plan(values, bitWidth, ref sizer);
        return sizer.Bytes;
    }

    /// <summary>The bytes <see cref="Encode(ReadOnlySpan{byte}, int, Span{byte})"/> writes for <paramref name="values"/>.</summary>
    internal static int Size(ReadOnlySpan<byte> values, int bitWidth)
    {
        Sizer sizer = new(bitWidth);
        Plan(values, bitWidth, ref sizer);
        return sizer.Bytes;
    }

    /// <summary>
    /// The most bytes <c>Encode</c> writes for <paramref name="count"/> values of
    /// <paramref name="bitWidth"/>, what a caller reserves to encode once rather than price first.
    /// </summary>
    /// <remarks>
    /// The packed runs hold at most one group more than the values fill, since only the last one is
    /// padded; a repeated run, of at least eight values, costs at most a header and four bytes; and
    /// a packed run's header follows each of them, and the first.
    /// </remarks>
    internal static int MaxSize(int count, int bitWidth) =>
        checked((((count >> 3) + 1) * (bitWidth + Varint.MaxLength32 + Varint.MaxLength32 + sizeof(uint))) + Varint.MaxLength32);

    /// <summary>Encodes <paramref name="values"/>, each below 2^<paramref name="bitWidth"/>, returning the bytes written.</summary>
    internal static int Encode(ReadOnlySpan<uint> values, int bitWidth, Span<byte> destination)
    {
        Writer<uint> writer = new(values, bitWidth, destination);
        Plan(values, bitWidth, ref writer);
        return writer.Written;
    }

    /// <summary>Encodes byte-wide <paramref name="values"/>: levels.</summary>
    internal static int Encode(ReadOnlySpan<byte> values, int bitWidth, Span<byte> destination)
    {
        Writer<byte> writer = new(values, bitWidth, destination);
        Plan(values, bitWidth, ref writer);
        return writer.Written;
    }

    /// <summary>The fewest repeats a repeated run of <paramref name="bitWidth"/> must hold to be taken.</summary>
    internal static int MinimumRun(int bitWidth)
    {
        // A repeated run costs its header and its value; packing it costs its bits, and splitting a
        // packed run around it a second header. Taken only when packing would cost more.
        int valueBytes = (bitWidth + 7) >> 3;
        int overhead = 8 * (1 + valueBytes + 2);
        return bitWidth == 0 ? 8 : Math.Max(8, overhead / bitWidth + 1);
    }

    private interface IRunSink
    {
        void Packed(int start, int count, bool last);

        void Repeated(uint value, int count);
    }

    /// <summary>
    /// The runs of <paramref name="values"/>: a run of at least <see cref="MinimumRun"/> equal values
    /// repeated, once the values before it are packed in whole groups of eight, borrowing from its
    /// start what completes their last group, and every other value packed. Runs are found where
    /// neighbours differ, a vector of pairs compared at a time: thirty-two levels, eight codes. When a
    /// run to repeat must be longer than a vector, only the run that ends at a vector's first change
    /// and the one that starts at its last can be, and the changes between are not visited.
    /// </summary>
    private static void Plan<T, TSink>(ReadOnlySpan<T> values, int bitWidth, ref TSink sink)
        where T : unmanaged, IEquatable<T>
        where TSink : struct, IRunSink, allows ref struct
    {
        int minimum = MinimumRun(bitWidth);
        int count = values.Length;
        int pending = 0;
        int start = 0;
        int i = 1;
        ref T input = ref MemoryMarshal.GetReference(values);
        if (Vector256.IsHardwareAccelerated)
        {
            int lanes = Vector256<T>.Count;
            uint all = lanes == 32 ? uint.MaxValue : (1u << lanes) - 1;
            bool longRuns = minimum > lanes;
            for (; i <= count - lanes; i += lanes)
            {
                // Bit k: the value at i + k differs from the one before it.
                uint changes = ~Vector256.Equals(Vector256.LoadUnsafe(ref input, (nuint)i), Vector256.LoadUnsafe(ref input, (nuint)(i - 1))).ExtractMostSignificantBits() & all;
                if (changes == 0)
                {
                    continue;
                }

                if (longRuns)
                {
                    Close(values, start, i + BitOperations.TrailingZeroCount(changes), minimum, ref pending, ref sink);
                    start = i + 31 - BitOperations.LeadingZeroCount(changes);
                    continue;
                }

                for (; changes != 0; changes &= changes - 1)
                {
                    int end = i + BitOperations.TrailingZeroCount(changes);
                    Close(values, start, end, minimum, ref pending, ref sink);
                    start = end;
                }
            }
        }

        for (; i < count; i++)
        {
            if (!values[i].Equals(values[i - 1]))
            {
                Close(values, start, i, minimum, ref pending, ref sink);
                start = i;
            }
        }

        Close(values, start, count, minimum, ref pending, ref sink);
        if (pending < count)
        {
            sink.Packed(pending, count - pending, last: true);
        }
    }

    /// <summary>The run of equal values from <paramref name="start"/> to <paramref name="end"/>: repeated where it pays, the values before it packed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Close<T, TSink>(ReadOnlySpan<T> values, int start, int end, int minimum, ref int pending, ref TSink sink)
        where T : unmanaged
        where TSink : struct, IRunSink, allows ref struct
    {
        int length = end - start;
        if (length < minimum)
        {
            return;
        }

        int borrow = (8 - ((start - pending) & 7)) & 7;
        if (length - borrow < minimum)
        {
            return;
        }

        if (start + borrow > pending)
        {
            sink.Packed(pending, start + borrow - pending, last: false);
        }

        sink.Repeated(ToUInt32(values[start]), length - borrow);
        pending = end;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ToUInt32<T>(T value)
        where T : unmanaged =>
        Unsafe.SizeOf<T>() == 1 ? Unsafe.As<T, byte>(ref value) : Unsafe.As<T, uint>(ref value);


    private struct Sizer(int bitWidth) : IRunSink
    {
        internal int Bytes;

        public void Packed(int start, int count, bool last)
        {
            int groups = (count + 7) >> 3;
            Bytes += Varint.Size(((uint)groups << 1) | 1) + groups * bitWidth;
        }

        public void Repeated(uint value, int count) =>
            Bytes += Varint.Size((uint)count << 1) + ((bitWidth + 7) >> 3);
    }

    private ref struct Writer<T>(ReadOnlySpan<T> values, int bitWidth, Span<byte> destination) : IRunSink
        where T : unmanaged
    {
        private readonly ReadOnlySpan<T> _values = values;
        private readonly Span<byte> _destination = destination;
        internal int Written;

        public void Packed(int start, int count, bool last)
        {
            int groups = (count + 7) >> 3;
            Written += Varint.Write(_destination[Written..], ((uint)groups << 1) | 1);
            Span<byte> body = _destination.Slice(Written, groups * bitWidth);
            if (Unsafe.SizeOf<T>() == 1)
            {
                ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.Cast<T, byte>(_values.Slice(start, count));
                BitPacking.Pack8(bytes, bitWidth, body);
            }
            else
            {
                ReadOnlySpan<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<T, uint>(_values.Slice(start, count));
                BitPacking.Pack32(words, bitWidth, body);
            }

            // The values that pad the last group are zero.
            int used = (int)BitPacking.PackedBytes(count, bitWidth);
            body[used..].Clear();
            Written += body.Length;
        }

        public void Repeated(uint value, int count)
        {
            Written += Varint.Write(_destination[Written..], (uint)count << 1);
            int valueBytes = (bitWidth + 7) >> 3;
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            buffer[..valueBytes].CopyTo(_destination[Written..]);
            Written += valueBytes;
        }
    }
}
