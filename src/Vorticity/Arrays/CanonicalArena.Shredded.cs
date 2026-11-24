using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays;

internal partial class CanonicalArena
{
    /// <summary>
    /// Adds a node of the values of a shredded variant's rows not put back together: their views, hollow for a value
    /// not written (<see cref="CanonicalSupport.HollowBuffer"/>), over <paramref name="dataBuffers"/> for the others, and
    /// the metadata of the same rows. What took its rows is told beside it, in an arena that keeps such things
    /// (<see cref="NoteTaken"/>), or given as its values are put back
    /// (<see cref="MaterializeShredded(int, ITakenRows, int)"/>).
    /// </summary>
    /// <param name="dtype">The dtype this node produces, a binary.</param>
    /// <param name="length">Row count.</param>
    /// <param name="validity">Per-row validity.</param>
    /// <param name="views">Exactly <c>length * 16</c> bytes.</param>
    /// <param name="dataBuffers">The buffers the views that are not hollow name.</param>
    /// <param name="metadataIndex">The node of the rows' metadata, in this arena.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="VortexFormatException"><paramref name="views"/> is the wrong length.</exception>
    internal int AddShredded(
        DType dtype, int length, Validity validity, VortexBuffer views, ReadOnlySpan<VortexBuffer> dataBuffers, int metadataIndex)
    {
        RequireExactLength(views.Length, length, CanonicalSupport.ViewSize, "Shredded");
        RequireChild(metadataIndex);
        CanonicalRecord r = New(CanonicalKind.Shredded, dtype, length, validity);
        r.BufferA = views;
        r.ChildStart = AddChild(metadataIndex);
        r.ChildCount = 1;
        r.DataBufferStart = _dataBufferCount;
        r.DataBufferCount = dataBuffers.Length;
        for (int i = 0; i < dataBuffers.Length; i++)
        {
            AddDataBuffer(dataBuffers[i]);
        }

        return Commit(ref r);
    }

    /// <summary>The twin of <see cref="CanonicalKind.Shredded"/> node <paramref name="nodeIndex"/> once its values were put back, -1 before.</summary>
    internal int PutBack(int nodeIndex) => RecordRef(nodeIndex).Kind == CanonicalKind.Shredded ? RecordRef(nodeIndex).Materialized : -1;

    /// <summary>
    /// The twin of <see cref="CanonicalKind.Shredded"/> node <paramref name="nodeIndex"/> whose every view names bytes
    /// (<see cref="MaterializeShredded(int, ITakenRows, int)"/>), its values put back by what the arena was told took its
    /// rows (<see cref="NoteTaken"/>), or put back already.
    /// </summary>
    /// <exception cref="VortexFormatException">The node is of another kind.</exception>
    /// <exception cref="InvalidOperationException">Nothing is told to have taken the node's rows, and its values were not put back.</exception>
    internal int MaterializeShredded(int nodeIndex)
    {
        ref readonly CanonicalRecord source = ref RecordRef(nodeIndex);
        if (source.Kind != CanonicalKind.Shredded)
        {
            ArraysThrow.Kind(source.Kind, nameof(CanonicalKind.Shredded));
        }

        if (source.Materialized >= 0)
        {
            return source.Materialized;
        }

        if (!TryTaken(nodeIndex, out ITakenRows? rows, out int first) || rows is null)
        {
            throw new InvalidOperationException("The values of rows nothing is told to have taken are not put back.");
        }

        return MaterializeShredded(nodeIndex, rows, first);
    }

    /// <summary>
    /// The twin of <see cref="CanonicalKind.Shredded"/> node <paramref name="nodeIndex"/> whose every view names bytes:
    /// the values not written put back by <paramref name="rows"/>, which took the node's rows, its row 0 the row
    /// <paramref name="first"/> of them (<see cref="ITakenRows.Rebuild"/>), in one buffer after the node's own, each where
    /// its view said it would lie from the first of them on. Once, memoized on the record as a constant's twin is: a
    /// reader that asks for the node's views then reads the twin's, and writes nothing.
    /// </summary>
    /// <returns>The twin's index: a <see cref="CanonicalKind.VarBinView"/> of the same rows and validity.</returns>
    /// <exception cref="VortexFormatException">The node is of another kind.</exception>
    internal int MaterializeShredded(int nodeIndex, ITakenRows rows, int first)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ref CanonicalRecord source = ref RecordRefMutable(nodeIndex);
        if (source.Kind != CanonicalKind.Shredded)
        {
            ArraysThrow.Kind(source.Kind, nameof(CanonicalKind.Shredded));
        }

        if (source.Materialized >= 0)
        {
            return source.Materialized;
        }

        // By value: the twin's records, and the metadata's when it is decoded, may grow the array under a reference.
        CanonicalRecord node = source;
        int length = node.Length;
        int viewBytes = length * CanonicalSupport.ViewSize;
        ReadOnlySpan<byte> hollow = node.BufferA.Span[..viewBytes];
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(hollow);

        // The bytes the values not written take: from the least offset to the end of the furthest.
        uint low = uint.MaxValue;
        ulong high = 0;
        for (int row = 0; row < length; row++)
        {
            int w = row * 4;
            if (words[w + 2] == CanonicalSupport.HollowBuffer && words[w] > CanonicalSupport.MaxInlineViewLength)
            {
                low = Math.Min(low, words[w + 3]);
                high = Math.Max(high, (ulong)words[w + 3] + words[w]);
            }
        }

        // The buffers the twin takes, in a block of the pool given back once they are added: an array of them was made at
        // each reconstitution.
        int own = node.DataBufferCount;
        Block<VortexBuffer> buffers = Blocks.Rent<VortexBuffer>(own + 1);
        try
        {
            return MaterializeShredded(nodeIndex, in node, rows, first, length, hollow, words, viewBytes, low, high, own, buffers.AsSpan());
        }
        finally
        {
            Blocks.Return(buffers);
        }
    }

    private int MaterializeShredded(
        int nodeIndex, in CanonicalRecord node, ITakenRows rows, int first, int length, ReadOnlySpan<byte> hollow, ReadOnlySpan<uint> words,
        int viewBytes, uint low, ulong high, int own, Span<VortexBuffer> buffers)
    {
        DataBufferRange(node.DataBufferStart, own).CopyTo(buffers);
        if (low == uint.MaxValue)
        {
            // No value to put back: the views as they are.
            int same = AddVarBinView(node.DType, length, node.Validity, node.BufferA.Slice(0, viewBytes), buffers[..own]);
            RecordRefMutable(nodeIndex).Materialized = same;
            return same;
        }

        int size = checked((int)(high - low));
        VortexBuffer data = AllocateUninitialized(size, CanonicalSupport.MaxRequiredAlignment, out Span<byte> into).Slice(0, size);
        into = into[..size];
        CanonicalNode metadata = GetNode(_children[node.ChildStart]);
        rows.Rebuild(first, length, hollow, metadata.Views.Span, metadata.DataBuffers, into, checked((int)low));

        VortexBuffer views = AllocateUninitialized(viewBytes, CanonicalSupport.ViewSize, out Span<byte> laid);
        hollow.CopyTo(laid);
        for (int row = 0; row < length; row++)
        {
            int w = row * 4;
            if (words[w + 2] == CanonicalSupport.HollowBuffer && words[w] > CanonicalSupport.MaxInlineViewLength)
            {
                int offset = (int)(words[w + 3] - low);
                CanonicalSupport.WriteView(laid.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize), into[offset..], (int)words[w], own, offset);
            }
        }

        buffers[own] = data;
        int twin = AddVarBinView(node.DType, length, node.Validity, views.Slice(0, viewBytes), buffers);
        RecordRefMutable(nodeIndex).Materialized = twin;
        return twin;
    }
}
