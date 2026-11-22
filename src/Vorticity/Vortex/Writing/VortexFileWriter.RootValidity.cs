using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>
/// The validity of a root that is a nullable struct: which rows are present. The format holds it as the root struct's first
/// child, before the columns, a layout of its own whose chunks need not follow theirs; the reference writes it as one flat
/// array of bools (Vortex 0.93.0). The writer takes it as the rows come, at the two doors every row goes through in the file's
/// order, and writes it after the columns' chunks.
/// </summary>
public sealed partial class VortexFileWriter
{
    /// <summary>The most rows one flat of a nullable root's validity holds: 2 MiB of bits.</summary>
    private const int RootValidityChunkRows = 1 << 24;

    /// <summary>Takes the validity of rows <paramref name="start"/> to <paramref name="start"/> + <paramref name="rows"/> of the root, when it is nullable.</summary>
    private void TakeRootValidity(CanonicalArena arena, int root, int start, int rows) =>
        _rootValidity?.Take(arena, root, start, rows);

    /// <summary>
    /// Writes a nullable root's validity, a flat of bools of up to <see cref="RootValidityChunkRows"/> rows each, as segments
    /// beside the columns' chunks: what <see cref="RootValidityLayout"/> names.
    /// </summary>
    private async ValueTask WriteRootValidityAsync(CancellationToken cancellationToken)
    {
        if (_rootValidity is not { } validity)
        {
            return;
        }

        if (validity.Rows != _rowCount)
        {
            throw new InvalidOperationException(
                $"The root's validity covers {validity.Rows} rows and the file holds {_rowCount}: a row reached the file by a door that does not take it.");
        }

        int chunks = (int)((_rowCount + RootValidityChunkRows - 1) / RootValidityChunkRows);
        int[] segments = validity.Segments = new int[chunks];
        ZoneMapWriter.Scratch scratch = ZoneMapWriter.Scratch.Take(Account);
        try
        {
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                long start = (long)chunk * RootValidityChunkRows;
                int rows = (int)Math.Min(RootValidityChunkRows, _rowCount - start);
                VortexBuffer bits = scratch.Arena.Allocate(CanonicalSupport.BitmapByteCount(rows), 1, out Span<byte> bytes);
                validity.CopyTo(start, rows, bytes);
                int node = scratch.Arena.AddBool(scratch.Types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0);
                using ArrayBlobWriter.BlobLease blob = ArrayBlobWriter.Write(Blobs, scratch.Arena, node, _arrayEncodings);
                segments[chunk] = await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false);
                scratch.Arena.Reset();
                scratch.Types.Clear();
            }
        }
        finally
        {
            scratch.Give();
        }
    }

    /// <summary>The layout of a nullable root's validity: one flat, or a chunked layout of them.</summary>
    private int RootValidityLayout(FlatBufferBuilder builder, ushort flat, ushort chunked, ReadOnlySpan<byte> chunkedMetadata)
    {
        int[] segments = _rootValidity!.Segments!;
        Span<uint> segment = stackalloc uint[1];
        if (segments.Length == 1)
        {
            segment[0] = (uint)segments[0];
            return LayoutWriter.Write(builder, flat, (ulong)_rowCount, default, [], segment);
        }

        int[] flats = new int[segments.Length];
        for (int chunk = 0; chunk < segments.Length; chunk++)
        {
            segment[0] = (uint)segments[chunk];
            long rows = Math.Min(RootValidityChunkRows, _rowCount - ((long)chunk * RootValidityChunkRows));
            flats[chunk] = LayoutWriter.Write(builder, flat, (ulong)rows, default, [], segment);
        }

        return LayoutWriter.Write(builder, chunked, (ulong)_rowCount, chunkedMetadata, flats, []);
    }

    /// <summary>
    /// The validity of a nullable root, a bit a row in the order the rows reach the file. No bitmap is kept while every row
    /// is present, which is every row a caller's records make; the first absent one makes it, every row before it set.
    /// </summary>
    private sealed class RootValidity(MemoryAccount account)
    {
        /// <summary>A bit a row taken, set where the row is present; empty while every one is. Bits past the rows are clear.</summary>
        private Block<byte> _bits;

        /// <summary>The rows taken.</summary>
        internal long Rows { get; private set; }

        /// <summary>The segments it was written to, a flat each; null until the completion writes them.</summary>
        internal int[]? Segments { get; set; }

        /// <summary>Takes the validity of rows <paramref name="start"/> to <paramref name="start"/> + <paramref name="rows"/> of <paramref name="root"/>.</summary>
        internal void Take(CanonicalArena arena, int root, int start, int rows)
        {
            long at = Rows;
            Rows += rows;
            Validity validity = arena.GetNode(root).Validity;
            switch (validity.Kind)
            {
                case ValidityKind.NonNullable:
                case ValidityKind.AllValid:
                    if (!_bits.IsEmpty)
                    {
                        Cover(at, rows);
                        CanonicalSupport.SetBits(_bits.AsSpan((int)(at >> 3)), (int)(at & 7), rows);
                    }

                    return;
                case ValidityKind.AllInvalid:
                    // The bits past the rows taken are clear: covering them is all an absent run asks.
                    Cover(at, rows);
                    return;
                default:
                    CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
                    ReadOnlySpan<byte> source = bits.Bits.Span;
                    int offset = bits.BitOffset + start;
                    if (_bits.IsEmpty && BitmapKernels.CountSet(source, offset, rows) == rows)
                    {
                        return;
                    }

                    Cover(at, rows);
                    CanonicalSupport.CopyBits(source, offset, _bits.AsSpan((int)(at >> 3)), (int)(at & 7), rows);
                    return;
            }
        }

        /// <summary>Copies the bits of rows <paramref name="start"/> to <paramref name="start"/> + <paramref name="rows"/>, a multiple of eight, into <paramref name="destination"/>.</summary>
        internal void CopyTo(long start, int rows, Span<byte> destination)
        {
            int bytes = CanonicalSupport.BitmapByteCount(rows);
            if (_bits.IsEmpty)
            {
                destination[..bytes].Clear();
                CanonicalSupport.SetBits(destination, 0, rows);
                return;
            }

            _bits.AsSpan((int)(start >> 3), bytes).CopyTo(destination);
        }

        /// <summary>Gives the bitmap back.</summary>
        internal void Release()
        {
            Blocks.Return(account, _bits);
            _bits = default;
        }

        /// <summary>
        /// Makes the bitmap hold the bits of rows up to <paramref name="at"/> + <paramref name="rows"/>, the new ones clear: made
        /// at the first absent row, every row before it set.
        /// </summary>
        private void Cover(long at, int rows)
        {
            int needed = checked((int)((at + rows + 7) >> 3));
            if (_bits.IsEmpty)
            {
                _bits = Blocks.Rent<byte>(account, Math.Max(needed, 4096));
                _bits.AsSpan().Clear();
                int whole = (int)(at >> 3);
                _bits.AsSpan(0, whole).Fill(0xFF);
                if ((at & 7) != 0)
                {
                    _bits.AsSpan()[whole] = (byte)((1 << (int)(at & 7)) - 1);
                }

                return;
            }

            if (needed > _bits.Length)
            {
                int kept = _bits.Length;
                Blocks.Grow(account, ref _bits, Math.Max(needed, kept * 2));
                _bits.AsSpan(kept).Clear();
            }
        }
    }
}
