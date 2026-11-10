using System;
using System.Buffers.Binary;
using System.Text;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class ScatteredViewTests
{
    private const int Views = 200;

    // Views far apart in their buffer have their values checked one by one: a value that is not
    // UTF-8 among them is still refused.
    [Fact]
    public void AScatteredValueThatIsNotUtf8IsRefused()
    {
        byte[] heap = Heap(valueBytes: 24, gap: 1_000, out byte[] views);
        heap[(57 * 1_024) + 20] = 0xFF;

        Assert.Throws<VortexFormatException>(() => Validate(heap, views));
    }

    [Fact]
    public void AScatteredValueThatIsUtf8ButNotAsciiIsAccepted()
    {
        byte[] heap = Heap(valueBytes: 24, gap: 1_000, out byte[] views);
        Encoding.UTF8.GetBytes("é").CopyTo(heap, (57 * 1_024) + 20);

        Validate(heap, views);
    }

    // The bytes between the values belong to no row: what they hold is nobody's to refuse.
    [Fact]
    public void BytesNoViewNamesAreNotChecked()
    {
        byte[] heap = Heap(valueBytes: 24, gap: 1_000, out byte[] views);
        heap[(57 * 1_024) + 500] = 0xFF;

        Validate(heap, views);
    }

    // Long values in row order span as much of their buffer as the scattered ones; they are swept,
    // and a byte of one that is not UTF-8 is refused all the same.
    [Fact]
    public void LongValuesInRowOrderAreCheckedWhole()
    {
        byte[] heap = Heap(valueBytes: 400, gap: 0, out byte[] views);
        Validate(heap, views);

        heap[(131 * 400) + 399] = 0xFF;

        Assert.Throws<VortexFormatException>(() => Validate(heap, views));
    }

    // Values in row order past ASCII are swept as UTF-8 in one pass, and each value's edges are checked to fall where a
    // character does: a span that is UTF-8 whole may still cut a character between two values, or between a value and the
    // bytes beside it, and each such value is refused.
    [Fact]
    public void ValuesInRowOrderPastAsciiAreAccepted()
    {
        byte[] heap = Heap(valueBytes: 24, gap: 2, out byte[] views);
        Encoding.UTF8.GetBytes("é€").CopyTo(heap, (57 * 26) + 10);
        Encoding.UTF8.GetBytes("é").CopyTo(heap, (130 * 26) + 22);

        Validate(heap, views);
    }

    [Fact]
    public void AValueThatEndsInsideACharacterIsRefused()
    {
        // "é" is C3 A9: its first byte ends value 57, its second begins the gap after it.
        byte[] heap = Heap(valueBytes: 24, gap: 2, out byte[] views);
        heap[(57 * 26) + 23] = 0xC3;
        heap[(57 * 26) + 24] = 0xA9;

        Assert.Throws<VortexFormatException>(() => Validate(heap, views));
    }

    [Fact]
    public void AValueThatBeginsInsideACharacterIsRefused()
    {
        // The first byte of "é" ends the gap before value 58, its second begins the value (and its prefix).
        byte[] heap = Heap(valueBytes: 24, gap: 2, out byte[] views);
        heap[(58 * 26) - 1] = 0xC3;
        heap[58 * 26] = 0xA9;
        views[(58 * CanonicalSupport.ViewSize) + 4] = 0xA9;

        Assert.Throws<VortexFormatException>(() => Validate(heap, views));
    }

    [Fact]
    public void TwoValuesThatShareACharacterAreRefused()
    {
        // "€" is E2 82 AC: value 57 ends with its first two bytes, value 58 begins with its last; the span is UTF-8 whole.
        byte[] heap = Heap(valueBytes: 24, gap: 0, out byte[] views);
        heap[(57 * 24) + 22] = 0xE2;
        heap[(57 * 24) + 23] = 0x82;
        heap[58 * 24] = 0xAC;
        views[(58 * CanonicalSupport.ViewSize) + 4] = 0xAC;

        Assert.Throws<VortexFormatException>(() => Validate(heap, views));
    }

    private static void Validate(byte[] heap, byte[] views)
    {
        unsafe
        {
            fixed (byte* data = heap)
            {
                ReadOnlySpan<VortexBuffer> buffers = [VortexBuffer.FromPointer(data, heap.Length, 0)];
                VarBinViewDecoder.ValidateViews(views, buffers, ValidityMask.NonNullable, Views, requireUtf8: true);
            }
        }
    }

    /// <summary>Views over ASCII values of <paramref name="valueBytes"/> bytes, <paramref name="gap"/> bytes apart, the gaps filled with ASCII too.</summary>
    private static byte[] Heap(int valueBytes, int gap, out byte[] views)
    {
        int stride = valueBytes + gap;
        byte[] heap = new byte[Views * stride];
        heap.AsSpan().Fill((byte)'-');
        views = new byte[Views * CanonicalSupport.ViewSize];
        for (int i = 0; i < Views; i++)
        {
            Span<byte> value = heap.AsSpan(i * stride, valueBytes);
            value.Fill((byte)('a' + (i % 26)));
            Span<byte> view = views.AsSpan(i * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            BinaryPrimitives.WriteInt32LittleEndian(view, valueBytes);
            value[..4].CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], i * stride);
        }

        return heap;
    }
}
