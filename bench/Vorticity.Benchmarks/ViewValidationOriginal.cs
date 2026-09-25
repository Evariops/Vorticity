using System;
using System.Buffers.Binary;
using System.Text.Unicode;

namespace Vorticity.Benchmarks;

/// <summary>
/// The view-at-a-time validation of a <c>vortex.varbinview</c> node's views, kept here unchanged as
/// the baseline every change to <c>VarBinViewDecoder.ValidateViews</c> is measured against in the
/// same process: each view's bounds, its prefix against the value, and UTF-8, every row valid.
/// </summary>
/// <remarks>
/// Self-contained on purpose: nothing here calls into the library, so editing the library's kernel
/// can never move this arm.
/// </remarks>
internal static class ViewValidationOriginal
{
    private const int ViewSize = 16;
    private const int MaxInlineViewLength = 12;

    /// <summary>Validates <paramref name="length"/> views over <paramref name="buffers"/>.</summary>
    internal static void Validate(ReadOnlySpan<byte> views, byte[][] buffers, int length, bool requireUtf8)
    {
        for (int i = 0; i < length; i++)
        {
            ReadOnlySpan<byte> view = views.Slice(i * ViewSize, ViewSize);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);

            if (size <= MaxInlineViewLength)
            {
                if (requireUtf8 && !IsInlineAscii(view, (int)size) && !Utf8.IsValid(view.Slice(4, (int)size)))
                {
                    throw new InvalidOperationException($"row {i} is not UTF-8");
                }

                continue;
            }

            uint bufferIndex = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
            if (bufferIndex >= (uint)buffers.Length)
            {
                throw new InvalidOperationException($"row {i} references buffer {bufferIndex}");
            }

            byte[] target = buffers[(int)bufferIndex];
            if ((ulong)offset + size > (ulong)(uint)target.Length)
            {
                throw new InvalidOperationException($"row {i} runs past its buffer");
            }

            ReadOnlySpan<byte> value = target.AsSpan((int)offset, (int)size);
            if (!view.Slice(4, 4).SequenceEqual(value[..4]))
            {
                throw new InvalidOperationException($"row {i}'s prefix differs");
            }

            if (requireUtf8 && !Utf8.IsValid(value))
            {
                throw new InvalidOperationException($"row {i} is not UTF-8");
            }
        }
    }

    private static bool IsInlineAscii(ReadOnlySpan<byte> view, int size)
    {
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(view.Slice(4, 8));
        ulong high = BinaryPrimitives.ReadUInt32LittleEndian(view.Slice(12, 4));
        ulong lowMask = size >= 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;
        ulong highMask = size <= 8 ? 0UL : (1UL << ((size - 8) * 8)) - 1;
        const ulong HighBits = 0x8080_8080_8080_8080UL;
        return (((low & lowMask) | (high & highMask)) & HighBits) == 0;
    }
}
