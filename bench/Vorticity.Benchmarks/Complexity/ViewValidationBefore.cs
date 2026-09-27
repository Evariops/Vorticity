// The validation of a varbinview's views as it swept, for Utf8, the whole span each data buffer's
// views reach, however few bytes they name in it: the original that `ViewValidationBenchmarks`
// measures the library against.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Unicode;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Complexity;

internal static class ViewValidationBefore
{
    /// <summary>The validation itself, over the rows <paramref name="mask"/> says are valid.</summary>
    internal static void ValidateViews(
        ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, in ValidityMask mask, int length, bool requireUtf8)
    {
        if (mask.AllValid && dataBuffers.Length <= MaxSweptBuffers && Swept(views, dataBuffers, length, requireUtf8))
        {
            return;
        }

        ValidateEach(views, dataBuffers, mask, length, requireUtf8);
    }

    /// <summary>Data buffers whose referenced spans the sweeping pass tracks on the stack.</summary>
    private const int MaxSweptBuffers = 64;

    private static bool Swept(ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, int length, bool requireUtf8)
    {
        Span<uint> lowest = stackalloc uint[MaxSweptBuffers];
        Span<uint> highest = stackalloc uint[MaxSweptBuffers];
        lowest.Fill(uint.MaxValue);
        highest.Clear();

        ref byte view = ref MemoryMarshal.GetReference(views);
        Vector128<byte> inline = Vector128<byte>.Zero;
        uint prefixes = 0;
        uint bufferCount = (uint)dataBuffers.Length;
        uint current = uint.MaxValue;
        ref byte currentBase = ref Unsafe.NullRef<byte>();
        ulong currentLength = 0;
        uint low = uint.MaxValue;
        uint high = 0;
        int i = 0;
        int scalarUntil = 0;
        while (i < length)
        {
            if (i >= scalarUntil && i <= length - 4)
            {
                Vector128<byte> a = Vector128.LoadUnsafe(ref view);
                Vector128<byte> b = Vector128.LoadUnsafe(ref view, (nuint)CanonicalSupport.ViewSize);
                Vector128<byte> c = Vector128.LoadUnsafe(ref view, (nuint)(2 * CanonicalSupport.ViewSize));
                Vector128<byte> d = Vector128.LoadUnsafe(ref view, (nuint)(3 * CanonicalSupport.ViewSize));
                Vector128<uint> longest = Vector128.Max(
                    Vector128.Max(a.AsUInt32(), b.AsUInt32()), Vector128.Max(c.AsUInt32(), d.AsUInt32()));
                if (longest.ToScalar() <= CanonicalSupport.MaxInlineViewLength)
                {
                    inline |= (a | b) | (c | d);
                    i += 4;
                    view = ref Unsafe.Add(ref view, 4 * CanonicalSupport.ViewSize);
                    continue;
                }

                scalarUntil = i + 4;
            }

            uint size = Unsafe.ReadUnaligned<uint>(ref view);
            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                inline |= Vector128.LoadUnsafe(ref view);
                i++;
                view = ref Unsafe.Add(ref view, CanonicalSupport.ViewSize);
                continue;
            }

            uint bufferIndex = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 8));
            uint offset = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12));
            if (bufferIndex != current)
            {
                if (bufferIndex >= bufferCount)
                {
                    ThrowBufferIndex(i, bufferIndex, dataBuffers.Length);
                }

                if (current != uint.MaxValue)
                {
                    lowest[(int)current] = low;
                    highest[(int)current] = high;
                }

                current = bufferIndex;
                ReadOnlySpan<byte> target = dataBuffers[(int)bufferIndex].Span;
                currentBase = ref MemoryMarshal.GetReference(target);
                currentLength = (uint)target.Length;
                low = lowest[(int)bufferIndex];
                high = highest[(int)bufferIndex];
            }

            ulong end = (ulong)offset + size;
            if (end > currentLength)
            {
                ThrowViewRange(i, offset, size, (int)currentLength);
            }

            prefixes |= Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 4)) ^
                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref currentBase, offset));
            low = Math.Min(low, offset);
            high = Math.Max(high, (uint)end);
            i++;
            view = ref Unsafe.Add(ref view, CanonicalSupport.ViewSize);
        }

        if (current != uint.MaxValue)
        {
            lowest[(int)current] = low;
            highest[(int)current] = high;
        }

        if (prefixes != 0)
        {
            return false;
        }

        if (!requireUtf8)
        {
            return true;
        }

        if (inline.ExtractMostSignificantBits() != 0)
        {
            return false;
        }

        for (int b = 0; b < dataBuffers.Length; b++)
        {
            if (highest[b] > lowest[b] &&
                !System.Text.Ascii.IsValid(dataBuffers[b].Span[(int)lowest[b]..(int)highest[b]]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateEach(
        ReadOnlySpan<byte> views, ReadOnlySpan<VortexBuffer> dataBuffers, in ValidityMask mask, int length, bool requireUtf8)
    {
        if (mask.AllInvalid)
        {
            return;
        }

        bool allValid = mask.AllValid;
        for (int i = 0; i < length; i++)
        {
            if (!allValid && !mask.IsValid(i))
            {
                continue;
            }

            ReadOnlySpan<byte> view = views.Slice(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);

            if (size <= CanonicalSupport.MaxInlineViewLength)
            {
                if (requireUtf8 && !IsInlineAscii(view, (int)size)
                    && !Utf8.IsValid(view.Slice(4, (int)size)))
                {
                    ThrowNotUtf8(i);
                }

                continue;
            }

            uint bufferIndex = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);

            if (bufferIndex >= (uint)dataBuffers.Length)
            {
                ThrowBufferIndex(i, bufferIndex, dataBuffers.Length);
            }

            VortexBuffer target = dataBuffers[(int)bufferIndex];
            if ((ulong)offset + size > (ulong)(uint)target.Length)
            {
                ThrowViewRange(i, offset, size, target.Length);
            }

            ReadOnlySpan<byte> value = target.Span.Slice((int)offset, (int)size);
            if (!view.Slice(4, 4).SequenceEqual(value[..4]))
            {
                ThrowPrefixMismatch(i);
            }

            if (requireUtf8 && !Utf8.IsValid(value))
            {
                ThrowNotUtf8(i);
            }
        }
    }

    private static bool IsInlineAscii(ReadOnlySpan<byte> view, int size)
    {
        ulong low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(view.Slice(4, 8));
        ulong high = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));
        ulong lowMask = size >= 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;
        ulong highMask = size <= 8 ? 0UL : (1UL << ((size - 8) * 8)) - 1;
        const ulong HighBits = 0x8080_8080_8080_8080UL;
        return (((low & lowMask) | (high & highMask)) & HighBits) == 0;
    }

    private static void ThrowPrefixMismatch(int row) =>
        throw new VortexFormatException($"Row {row}'s view prefix does not match the value it references.");

    private static void ThrowNotUtf8(int row) =>
        throw new VortexFormatException($"Row {row} of a Utf8 array is not valid UTF-8.");

    private static void ThrowBufferIndex(int row, uint bufferIndex, int count) =>
        throw new VortexFormatException($"Row {row} references data buffer {bufferIndex}; the array has {count}.");

    private static void ThrowViewRange(int row, uint offset, uint size, int bufferLength) =>
        throw new VortexFormatException(
            $"Row {row} spans [{offset}, {(ulong)offset + size}) of a data buffer holding {bufferLength} bytes.");
}
