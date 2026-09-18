// One column, compressed with FSST, ready to serialize.
//
// The plan is built during Choose rather than during the write for one reason: deciding whether
// FSST pays MEANS compressing the column. There is no cheap estimate - the whole question is how
// well a trained table covers this particular data - so the work is done once and the bytes are
// kept, rather than done twice.
using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>A column's FSST encoding: the table, the code stream, and the two index children.</summary>
internal sealed class FsstPlan
{
    private FsstPlan(
        FsstSymbols table, byte[] codes, int codeLength, int[] offsets, int[] lengths, long encodedSize)
    {
        Table = table;
        Codes = codes;
        CodeLength = codeLength;
        Offsets = offsets;
        Lengths = lengths;
        EncodedSize = encodedSize;
    }

    /// <summary>The trained symbol table.</summary>
    internal FsstSymbols Table { get; }

    /// <summary>The concatenated code stream; only the first <see cref="CodeLength"/> bytes are live.</summary>
    internal byte[] Codes { get; }

    /// <summary>How many bytes of <see cref="Codes"/> are live.</summary>
    internal int CodeLength { get; }

    /// <summary>Where each row's codes begin, plus a final total: <c>rows + 1</c> entries.</summary>
    internal int[] Offsets { get; }

    /// <summary>Each row's DECODED length. The row boundaries live on the decoded side.</summary>
    internal int[] Lengths { get; }

    /// <summary>Total bytes this encoding will occupy, table and children included.</summary>
    internal long EncodedSize { get; }

    /// <summary>
    /// Trains a table on the column and compresses it, or returns null when FSST does not pay.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">A canonical VarBinView node.</param>
    /// <param name="sizeCeiling">
    /// The largest <see cref="EncodedSize"/> worth returning. The caller derives it from every bar
    /// the plan has to clear, so this method neither knows nor applies a margin of its own.
    /// </param>
    /// <returns>The plan, or null when it cannot come in at or below the ceiling.</returns>
    /// <remarks>
    /// THE CEILING IS ALSO AN ABORT THRESHOLD, and that is most of what it is for. Pricing FSST
    /// means training a symbol table and then compressing the ENTIRE column, which was 54% of the
    /// write profile on a file whose one text column FSST then LOST - the caller prices zstd too,
    /// and threw the whole result away. <see cref="EncodedSize"/> is never below the code stream's
    /// own length, so the moment the stream passes the ceiling the plan is already rejected and the
    /// remaining rows cannot change that. Stopping there is not an approximation: the answer is the
    /// same null it would have returned after compressing the rest.
    /// </remarks>
    internal static FsstPlan? TryBuild(CanonicalArena arena, int nodeIndex, long sizeCeiling)
    {
        if (sizeCeiling <= 0)
        {
            return null;
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;

        // ONE HEAP, NOT ONE ARRAY PER ROW. This was `ValueOf(node, i).ToArray()` per row: a managed
        // allocation for every string in the column, 65 536 of them on the witness file, which is
        // most of what puts the write path at 314x the read path's allocation and 43% of its time
        // inside the finalizer queue. The values have to be copied at all only because they live in
        // NATIVE arena buffers and `ReadOnlyMemory<byte>` cannot point at those -- so they are
        // copied once, contiguously, and the rows become slices of that.
        long plain = 0;
        for (int i = 0; i < rows; i++)
        {
            if (IsValid(arena, node, i))
            {
                plain += ValueOf(node, i).Length;
            }
        }

        if (plain > int.MaxValue)
        {
            return null;
        }

        // EVERYTHING TRANSIENT IS RENTED, because most of this work is thrown away: FSST is PRICED
        // against zstd and against the plain form, and on a column it loses the heap, the row
        // table and the code stream are all garbage the moment `null` is returned. On a megabyte
        // text column each of those is a large-object allocation, and together they were most of
        // why the write profile spent 14% of itself inside the pool trimmer that a Gen2 collection
        // runs.
        //
        // A row is two ints into the heap rather than a `ReadOnlyMemory<byte>`, which also removes
        // the per-row memory struct and the `List` that held them.
        int heapBytes = Math.Max((int)plain, 1);
        byte[] heap = ArrayPool<byte>.Shared.Rent(heapBytes);
        int[] starts = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        int[] lengths = new int[rows];
        byte[]? codes = null;
        try
        {
            int at = 0;
            for (int i = 0; i < rows; i++)
            {
                // A null row contributes nothing to the corpus AND nothing to the stream: its
                // bytes are unspecified, and encoding them would pay for values no reader will
                // ever ask for. It still occupies a row slot, of length zero.
                starts[i] = at;
                if (!IsValid(arena, node, i))
                {
                    lengths[i] = 0;
                    continue;
                }

                ReadOnlySpan<byte> value = ValueOf(node, i);
                value.CopyTo(heap.AsSpan(at));
                lengths[i] = value.Length;
                at += value.Length;
            }

            FsstSymbols? table = FsstSymbols.Train(
                heap.AsSpan(0, heapBytes), starts.AsSpan(0, rows), lengths);
            if (table is null)
            {
                return null;
            }

            // An escape costs two bytes, so the worst case is twice the input; sized once for the
            // whole column rather than per row.
            codes = ArrayPool<byte>.Shared.Rent((int)Math.Max(plain * 2, 1));
            int[] offsets = new int[rows + 1];
            int written = 0;

            for (int i = 0; i < rows; i++)
            {
                offsets[i] = written;
                written += table.Compress(
                    heap.AsSpan(starts[i], lengths[i]), codes.AsSpan(written));

                // The code stream alone is already a lower bound on EncodedSize, so a stream past
                // the ceiling is a decided loss whatever the remaining rows do.
                if (written > sizeCeiling)
                {
                    return null;
                }
            }

            offsets[rows] = written;

            long encoded = ((long)table.Count * (FsstSymbols.MaxSymbolLength + 1)) + written
                + ((long)rows * Width(MaxOf(lengths)))
                + ((long)(rows + 1) * Width(written));

            // THE KEPT ARRAY IS EXACT. The rental is sized for the worst case -- twice the column
            // -- and a stream that compressed at all uses a fraction of it, so the plan copies out
            // what is live instead of carrying the rest into the writer. `ArrayBlobWriter` used to
            // make that copy itself, one line later.
            return encoded <= sizeCeiling
                ? new FsstPlan(table, codes.AsSpan(0, written).ToArray(), written, offsets, lengths, encoded)
                : null;
        }
        finally
        {
            if (codes is not null)
            {
                ArrayPool<byte>.Shared.Return(codes);
            }

            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<byte>.Shared.Return(heap);
        }
    }

    /// <summary>The narrowest unsigned physical type that holds <paramref name="maximum"/>.</summary>
    /// <param name="maximum">The largest value the array carries.</param>
    /// <returns>The physical type.</returns>
    internal static PType IndexPType(long maximum) => maximum switch
    {
        <= byte.MaxValue => PType.U8,
        <= ushort.MaxValue => PType.U16,
        <= uint.MaxValue => PType.U32,
        _ => PType.U64,
    };

    /// <summary>The largest value in <paramref name="values"/>, or zero.</summary>
    /// <param name="values">The array to scan.</param>
    /// <returns>The maximum.</returns>
    internal static long MaxOf(int[] values)
    {
        long maximum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > maximum)
            {
                maximum = values[i];
            }
        }

        return maximum;
    }

    private static int Width(long maximum) => IndexPType(maximum).ByteWidth();

    /// <summary>Whether row <paramref name="row"/> of a canonical varbinview node holds a value.</summary>
    internal static bool IsValid(CanonicalArena arena, CanonicalNode node, int row)
    {
        Validity validity = node.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return true;
            case ValidityKind.AllInvalid:
                return false;
            default:
            {
                CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
                int bit = bits.BitOffset + row;
                ReadOnlySpan<byte> span = bits.Bits.Span;
                return (uint)(bit >> 3) < (uint)span.Length
                    && (span[bit >> 3] & (1 << (bit & 7))) != 0;
            }
        }
    }

    /// <summary>The bytes of row <paramref name="row"/> of a canonical varbinview node: inline or in a data buffer.</summary>
    internal static ReadOnlySpan<byte> ValueOf(CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= 12)
        {
            return view.Slice(4, (int)size);
        }

        uint buffer = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer((int)buffer).Span.Slice((int)offset, (int)size);
    }
}
