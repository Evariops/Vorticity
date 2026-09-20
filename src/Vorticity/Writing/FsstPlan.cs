using System;
using System.Buffers;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// A column's FSST encoding: the table, the code stream, and the two index children. The plan is
/// built while choosing an encoding because deciding whether FSST pays means compressing the
/// column, so the bytes are kept rather than produced twice.
/// </summary>
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

    internal FsstSymbols Table { get; }

    /// <summary>The concatenated code stream; only the first <see cref="CodeLength"/> bytes are live.</summary>
    internal byte[] Codes { get; }

    internal int CodeLength { get; }

    /// <summary>Where each row's codes begin, plus a final total: <c>rows + 1</c> entries.</summary>
    internal int[] Offsets { get; }

    /// <summary>Each row's decoded length; the row boundaries live on the decoded side.</summary>
    internal int[] Lengths { get; }

    /// <summary>Total bytes this encoding will occupy, table and children included.</summary>
    internal long EncodedSize { get; }

    /// <summary>
    /// Trains a table on the column and compresses it, or returns null when FSST does not pay.
    /// <paramref name="sizeCeiling"/> is also an abort threshold: the caller derives it from every
    /// bar the plan has to clear, and since the encoded size is never below the code stream's own
    /// length, a stream past the ceiling is already a decided loss.
    /// </summary>
    internal static FsstPlan? TryBuild(CanonicalArena arena, int nodeIndex, long sizeCeiling)
    {
        if (sizeCeiling <= 0)
        {
            return null;
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;

        // The two row tables alone put a floor of `2 * rows + 1` on the encoded size, since every
        // index width is at least one byte. This is a floor on the real value rather than an
        // estimate, so it cannot drop a winner: past the ceiling, no training run can bring it back.
        if ((2L * rows) + 1 > sizeCeiling)
        {
            return null;
        }

        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        long plain = 0;
        for (int i = 0; i < rows; i++)
        {
            if (valid.IsValid(i))
            {
                plain += ValueOf(node, i).Length;
            }
        }

        if (plain > int.MaxValue)
        {
            return null;
        }

        // Everything transient is rented: the values live in native arena buffers and have to be
        // copied to be trained on, and the heap, row table and code stream are all garbage the
        // moment this column is priced against zstd and loses.
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
                // A null row's bytes are unspecified, so it contributes nothing to the corpus and
                // nothing to the stream, but it still occupies a row slot of length zero.
                starts[i] = at;
                if (!valid.IsValid(i))
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

            // An escape costs two bytes, so the worst case is twice the input.
            codes = ArrayPool<byte>.Shared.Rent((int)Math.Max(plain * 2, 1));
            int[] offsets = new int[rows + 1];
            int written = 0;

            for (int i = 0; i < rows; i++)
            {
                offsets[i] = written;
                written += table.Compress(
                    heap.AsSpan(starts[i], lengths[i]), codes.AsSpan(written));

                if (written > sizeCeiling)
                {
                    return null;
                }
            }

            offsets[rows] = written;

            long encoded = ((long)table.Count * (FsstSymbols.MaxSymbolLength + 1)) + written
                + ((long)rows * Width(MaxOf(lengths)))
                + ((long)(rows + 1) * Width(written));

            // The rental is sized for the worst case, so the kept array is an exact copy of what is
            // live rather than the rest of the rental carried into the writer.
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
    internal static PType IndexPType(long maximum) => maximum switch
    {
        <= byte.MaxValue => PType.U8,
        <= ushort.MaxValue => PType.U16,
        <= uint.MaxValue => PType.U32,
        _ => PType.U64,
    };

    /// <summary>The largest value in <paramref name="values"/>, or zero.</summary>
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

    /// <summary>
    /// Whether row <paramref name="row"/> of a canonical varbinview node holds a value. This is for
    /// callers asking about one row; a run of rows should resolve a <see cref="ValidityReader"/>
    /// once per node instead of switching on the validity kind per row.
    /// </summary>
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
