using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// The values of a varbinview node, row by row, with its views and, when it has one, its data
/// buffer resolved once rather than through the node's accessors at every row.
/// </summary>
/// <remarks>
/// A node's accessors check its kind and look its record up each time, which a loop over its rows
/// pays per row, twice for a value held out of line. A node of several data buffers reads them
/// from their range in the arena, resolved once too.
/// </remarks>
internal readonly ref struct ViewValues
{
    private const int ViewSize = 16;
    private const int InlineBytes = 12;

    private readonly ReadOnlySpan<byte> _views;
    private readonly ReadOnlySpan<byte> _heap;
    private readonly ReadOnlySpan<VortexBuffer> _buffers;
    private readonly bool _oneHeap;

    /// <summary>Resolves the views and the data buffers of <paramref name="node"/>.</summary>
    /// <param name="node">A varbinview node.</param>
    internal ViewValues(CanonicalNode node)
    {
        _views = node.Views.Span;
        _buffers = node.DataBuffers;
        _oneHeap = _buffers.Length == 1;
        _heap = _oneHeap ? _buffers[0].Span : default;
    }

    /// <summary>The bytes of row <paramref name="row"/>: inline in its view, or in a data buffer.</summary>
    /// <param name="row">The row, within the node.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> At(int row)
    {
        ReadOnlySpan<byte> view = _views.Slice(row * ViewSize, ViewSize);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= InlineBytes)
        {
            return view.Slice(4, (int)size);
        }

        uint buffer = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        if (_oneHeap && buffer == 0)
        {
            return _heap.Slice((int)offset, (int)size);
        }

        if (buffer >= (uint)_buffers.Length)
        {
            return ArraysThrow.BufferIndex((int)buffer, _buffers.Length).Span;
        }

        return _buffers[(int)buffer].Span.Slice((int)offset, (int)size);
    }
}
