using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays;

/// <summary>
/// The values of a varbinview node, row by row, with its views and, when it has one, its data
/// buffer resolved once rather than through the node's accessors at every row.
/// </summary>
/// <remarks>
/// A node's accessors check its kind and look its record up each time, which a loop over its rows
/// pays per row, twice for a value held out of line. A node of several data buffers still reads
/// them through the accessor, which is also what refuses a view naming a buffer the node lacks.
/// </remarks>
internal readonly ref struct ViewValues
{
    private const int ViewSize = 16;
    private const int InlineBytes = 12;

    private readonly CanonicalNode _node;
    private readonly ReadOnlySpan<byte> _views;
    private readonly ReadOnlySpan<byte> _heap;
    private readonly bool _oneHeap;

    /// <summary>Resolves the views and the data buffer of <paramref name="node"/>.</summary>
    /// <param name="node">A varbinview node.</param>
    internal ViewValues(CanonicalNode node)
    {
        _node = node;
        _views = node.Views.Span;
        _oneHeap = node.DataBufferCount == 1;
        _heap = _oneHeap ? node.GetDataBuffer(0).Span : default;
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
        ReadOnlySpan<byte> heap = _oneHeap && buffer == 0 ? _heap : _node.GetDataBuffer((int)buffer).Span;
        return heap.Slice((int)offset, (int)size);
    }
}
