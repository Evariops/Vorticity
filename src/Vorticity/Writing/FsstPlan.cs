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
    private byte[] _codes;
    private int[] _offsets;
    private int[] _lengths;
    private FsstSymbols? _table;
    private readonly int _rows;

    private FsstPlan(
        FsstSymbols table, byte[] codes, int codeLength, int[] offsets, int[] lengths, int rows,
        long encodedSize)
    {
        _table = table;
        _codes = codes;
        CodeLength = codeLength;
        _offsets = offsets;
        _lengths = lengths;
        _rows = rows;
        EncodedSize = encodedSize;
    }

    /// <summary>The trained table; read before <see cref="Release"/>, which gives it back.</summary>
    /// <exception cref="InvalidOperationException">The plan was released.</exception>
    internal FsstSymbols Table => _table ?? throw new InvalidOperationException("The FSST plan was released.");

    /// <summary>Bytes of the concatenated code stream.</summary>
    internal int CodeLength { get; }

    /// <summary>Where each row's codes begin, plus a final total: <c>rows + 1</c> entries.</summary>
    internal ReadOnlySpan<int> Offsets => _offsets.Length == 0 ? [] : _offsets.AsSpan(0, _rows + 1);

    /// <summary>Each row's decoded length; the row boundaries live on the decoded side.</summary>
    internal ReadOnlySpan<int> Lengths => _lengths.Length == 0 ? [] : _lengths.AsSpan(0, _rows);

    /// <summary>Total bytes this encoding will occupy, table and children included.</summary>
    internal long EncodedSize { get; }

    /// <summary>
    /// The code stream's rental, whose first <see cref="CodeLength"/> bytes are the stream. It
    /// passes to the caller: the plan forgets it, and <see cref="Release"/> no longer hands it back.
    /// </summary>
    internal byte[] TakeCodes()
    {
        byte[] codes = _codes;
        _codes = [];
        return codes;
    }

    /// <summary>Hands back every rental the plan still holds: the code stream, the row tables, the symbol table.</summary>
    /// <remarks>
    /// Called by the writer once the row tables and the symbols are in its buffers, and by the
    /// chooser for a plan nothing will write. Safe twice: the plan forgets each rental before it
    /// gives it back, so a second call has nothing left to give.
    /// </remarks>
    internal void Release()
    {
        byte[] codes = _codes;
        int[] offsets = _offsets;
        int[] lengths = _lengths;
        FsstSymbols? table = _table;
        _codes = [];
        _offsets = [];
        _lengths = [];
        _table = null;
        table?.Recycle();
        if (codes.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(codes);
        }

        if (offsets.Length > 0)
        {
            ArrayPool<int>.Shared.Return(offsets);
        }

        if (lengths.Length > 0)
        {
            ArrayPool<int>.Shared.Return(lengths);
        }
    }

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
        ViewValues values = new ViewValues(node);
        long plain = 0;
        for (int i = 0; i < rows; i++)
        {
            if (valid.IsValid(i))
            {
                plain += values.At(i).Length;
            }
        }

        if (plain > int.MaxValue)
        {
            return null;
        }

        // Everything is rented: the values live in native arena buffers and have to be copied to be
        // trained on, and the heap, row tables and code stream are all garbage the moment this
        // column is priced against zstd and loses. A plan that wins keeps the row tables and the
        // code stream, and hands them back once the writer has them.
        // Eight bytes of slack past the values, cleared, so the compressor reads every position as
        // one eight-byte word however near the end of its value.
        int heapBytes = Math.Max((int)plain, 1);
        byte[] heap = ArrayPool<byte>.Shared.Rent(heapBytes + FsstSymbols.MaxSymbolLength);
        int[] starts = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        int[] lengths = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        byte[]? codes = null;
        int[]? offsets = null;
        FsstSymbols? table = null;
        bool kept = false;
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

                ReadOnlySpan<byte> value = values.At(i);
                value.CopyTo(heap.AsSpan(at));
                lengths[i] = value.Length;
                at += value.Length;
            }

            table = FsstSymbols.Train(
                heap.AsSpan(0, heapBytes), starts.AsSpan(0, rows), lengths.AsSpan(0, rows));
            if (table is null)
            {
                return null;
            }

            // An escape costs two bytes, so the worst case is twice the input.
            int codeRoom = (int)Math.Max(plain * 2, 1);
            codes = ArrayPool<byte>.Shared.Rent(codeRoom);
            offsets = ArrayPool<int>.Shared.Rent(rows + 1);
            heap.AsSpan(at, FsstSymbols.MaxSymbolLength).Clear();
            int written = table.CompressAll(
                heap.AsSpan(0, at + FsstSymbols.MaxSymbolLength), starts.AsSpan(0, rows), lengths.AsSpan(0, rows),
                codes.AsSpan(0, codeRoom), offsets.AsSpan(0, rows + 1), sizeCeiling);
            if (written < 0)
            {
                return null;
            }

            long encoded = ((long)table.Count * (FsstSymbols.MaxSymbolLength + 1)) + written
                + ((long)rows * Width(MaxOf(lengths.AsSpan(0, rows))))
                + ((long)(rows + 1) * Width(written));
            if (encoded > sizeCeiling)
            {
                return null;
            }

            // The code stream stays in its worst-case rental rather than being copied out: the
            // writer hands the rental to the blob as it is, and the blob gives it back once laid out.
            FsstPlan plan = new FsstPlan(table, codes, written, offsets, lengths, rows, encoded);
            kept = true;
            return plan;
        }
        finally
        {
            if (!kept)
            {
                table?.Recycle();
                if (codes is not null)
                {
                    ArrayPool<byte>.Shared.Return(codes);
                }

                if (offsets is not null)
                {
                    ArrayPool<int>.Shared.Return(offsets);
                }

                ArrayPool<int>.Shared.Return(lengths);
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
    internal static long MaxOf(ReadOnlySpan<int> values)
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
}
