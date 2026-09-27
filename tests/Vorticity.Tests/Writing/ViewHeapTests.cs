using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The heap a writer's text candidates gather: each valid row's bytes after the one before, the
/// sizes read from the views. Held to resolving each row, on the values that make the gather's
/// shortcuts hard -- inline values of every size, values near their buffer's end, values in a
/// second buffer, null rows whose views say anything.
/// </summary>
public sealed class ViewHeapTests
{
    private const int ViewSize = 16;

    [Fact]
    public void TheHeapHoldsEachValidRowsBytesInTurn()
    {
        Random random = new Random(20260926);
        const int Rows = 2000;
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();

        byte[][] values = new byte[Rows][];
        int[] buffers = new int[Rows];
        int[] sizes = [0, 0];
        for (int i = 0; i < Rows; i++)
        {
            int length = random.Next(4) == 0 ? random.Next(13) : random.Next(13, 100);
            values[i] = new byte[length];
            random.NextBytes(values[i]);
            buffers[i] = random.Next(5) == 0 ? 1 : 0;
            if (length > 12)
            {
                sizes[buffers[i]] += length;
            }
        }

        // The last out-of-line value of the first buffer ends it, so no block past it can be read.
        VortexBuffer views = arena.Allocate(Rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer first = arena.Allocate(Math.Max(sizes[0], 1), 1, out Span<byte> firstBytes);
        VortexBuffer second = arena.Allocate(Math.Max(sizes[1], 1), 1, out Span<byte> secondBytes);
        int[] at = [0, 0];
        for (int i = 0; i < Rows; i++)
        {
            byte[] bytes = values[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            view.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            int buffer = buffers[i];
            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], (uint)buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)at[buffer]);
            bytes.CopyTo((buffer == 0 ? firstBytes : secondBytes)[at[buffer]..]);
            at[buffer] += bytes.Length;
        }

        // One row in seven is null, its view left as it was: a size the heap must not count.
        VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> bitBytes);
        bitBytes.Fill(0xFF);
        for (int i = 0; i < Rows; i += 7)
        {
            bitBytes[i >> 3] &= (byte)~(1 << (i & 7));
        }

        Validity validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        int index = arena.AddVarBinView(types.Utf8(Nullability.Nullable), Rows, validity, views, [first, second]);
        CanonicalNode node = arena.GetNode(index);

        int[] lengths = new int[Rows];
        long total = ViewHeap.Lengths(node, ValidityReader.Of(arena, validity), lengths);
        long expected = 0;
        for (int i = 0; i < Rows; i++)
        {
            int length = i % 7 == 0 ? 0 : values[i].Length;
            Assert.Equal(length, lengths[i]);
            expected += length;
        }

        Assert.Equal(expected, total);

        byte[] heap = new byte[total + ViewHeap.Slack];
        int[] starts = new int[Rows];
        ViewHeap.Gather(node, lengths, heap, starts);
        int position = 0;
        for (int i = 0; i < Rows; i++)
        {
            Assert.Equal(position, starts[i]);
            Assert.Equal(i % 7 == 0 ? [] : values[i], heap.AsSpan(position, lengths[i]).ToArray());
            position += lengths[i];
        }

        Assert.Throws<ArgumentException>(() => ViewHeap.Gather(arena.GetNode(index), lengths, new byte[total + ViewHeap.Slack - 1], starts));
    }
}
