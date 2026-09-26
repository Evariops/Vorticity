using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Writing;

/// <summary>
/// A text column's valid values laid back to back in one heap, as a writer's candidates need them:
/// FSST trains and compresses over them, varbin stores them.
/// </summary>
/// <remarks>
/// <para>
/// The sizes come from the views, where a view keeps them, rather than from resolving each row's
/// value. The copy writes past each value into the room the next one takes: an inline value is its
/// view's twelve payload bytes as two words, an out-of-line one whole blocks of 32 or 16 bytes read
/// from its buffer, where the buffer has the bytes past it to read. A value near its buffer's end,
/// or in a buffer but the first, is resolved and copied exactly, which also holds its view to its
/// buffer.
/// </para>
/// <para>
/// So the heap is <see cref="Slack"/> bytes longer than the values it holds, and a row costs a few
/// loads and stores where resolving it and calling the library's copy costs a call.
/// </para>
/// </remarks>
internal static class ViewHeap
{
    /// <summary>Bytes past the values a gather may write over.</summary>
    internal const int Slack = 32;

    private const int ViewSize = 16;

    private const int Inline = 12;

    /// <summary>Each row's length, zero for a null row, and their total.</summary>
    /// <param name="node">A varbinview node.</param>
    /// <param name="valid">Its validity.</param>
    /// <param name="lengths">One per row.</param>
    internal static long Lengths(CanonicalNode node, in ValidityReader valid, Span<int> lengths)
    {
        int rows = node.Length;
        ref byte view = ref MemoryMarshal.GetReference(node.Views.Span[..(rows * ViewSize)]);
        ref int into = ref MemoryMarshal.GetReference(lengths[..rows]);
        bool all = valid.IsAllValid;
        long total = 0;
        for (int i = 0; i < rows; i++)
        {
            int length = all || valid.IsValid(i) ? (int)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, i * ViewSize)) : 0;
            Unsafe.Add(ref into, i) = length;
            total += (uint)length;
        }

        return total;
    }

    /// <summary>
    /// Copies each row of <paramref name="lengths"/> into <paramref name="heap"/> after the one
    /// before, recording where each starts.
    /// </summary>
    /// <param name="node">The varbinview node <see cref="Lengths"/> measured.</param>
    /// <param name="lengths">Its lengths, zero for a row that holds nothing.</param>
    /// <param name="heap">Room for the lengths' total and <see cref="Slack"/> bytes more.</param>
    /// <param name="starts">One per row.</param>
    internal static void Gather(CanonicalNode node, ReadOnlySpan<int> lengths, Span<byte> heap, Span<int> starts)
    {
        int rows = lengths.Length;
        long total = 0;
        foreach (int length in lengths)
        {
            total += (uint)length;
        }

        if (heap.Length < total + Slack || starts.Length < rows)
        {
            throw new ArgumentException("The heap cannot hold the values and their slack.", nameof(heap));
        }

        ViewValues values = new ViewValues(node);
        ReadOnlySpan<byte> data = node.DataBufferCount >= 1 ? node.GetDataBuffer(0).Span : default;
        ref byte view0 = ref MemoryMarshal.GetReference(node.Views.Span[..(rows * ViewSize)]);
        ref byte from0 = ref MemoryMarshal.GetReference(data);
        ref byte into = ref MemoryMarshal.GetReference(heap);
        bool wide = Vector256.IsHardwareAccelerated;
        int at = 0;
        for (int i = 0; i < rows; i++)
        {
            starts[i] = at;
            int size = lengths[i];
            if (size == 0)
            {
                continue;
            }

            ref byte view = ref Unsafe.Add(ref view0, i * ViewSize);
            ref byte to = ref Unsafe.Add(ref into, at);
            if (size <= Inline)
            {
                Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref view, 4)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, 8), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12)));
                at += size;
                continue;
            }

            uint buffer = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 8));
            long offset = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12));
            if (buffer == 0 && offset + size + Slack <= data.Length)
            {
                ref byte from = ref Unsafe.Add(ref from0, (nint)offset);
                if (wide)
                {
                    for (int k = 0; k < size; k += 32)
                    {
                        Vector256.LoadUnsafe(ref from, (nuint)k).StoreUnsafe(ref to, (nuint)k);
                    }
                }
                else
                {
                    for (int k = 0; k < size; k += 16)
                    {
                        Vector128.LoadUnsafe(ref from, (nuint)k).StoreUnsafe(ref to, (nuint)k);
                    }
                }
            }
            else
            {
                ReadOnlySpan<byte> value = values.At(i);
                value.CopyTo(heap.Slice(at, value.Length));
            }

            at += size;
        }
    }
}
