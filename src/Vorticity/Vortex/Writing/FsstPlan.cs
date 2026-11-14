using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// A column's FSST encoding: the table, the code stream, and the two index children. The plan is
/// built while choosing an encoding because deciding whether FSST pays means compressing the
/// column, so the bytes are kept rather than produced twice.
/// </summary>
internal sealed class FsstPlan
{
    private Block<byte> _codes;
    private Block<int> _offsets;
    private Block<int> _lengths;
    private FsstSymbols? _table;

    private FsstPlan(
        FsstSymbols table, Block<byte> codes, int codeLength, Block<int> offsets, Block<int> lengths,
        long encodedSize)
    {
        _table = table;
        _codes = codes;
        CodeLength = codeLength;
        _offsets = offsets;
        _lengths = lengths;
        EncodedSize = encodedSize;
    }

    /// <summary>The trained table; read before <see cref="Release"/>, which gives it back.</summary>
    /// <exception cref="InvalidOperationException">The plan was released.</exception>
    internal FsstSymbols Table => _table ?? throw new InvalidOperationException("The FSST plan was released.");

    /// <summary>Bytes of the concatenated code stream.</summary>
    internal int CodeLength { get; }

    /// <summary>Where each row's codes begin, plus a final total: <c>rows + 1</c> entries.</summary>
    internal ReadOnlySpan<int> Offsets => _offsets;

    /// <summary>Each row's decoded length; the row boundaries live on the decoded side.</summary>
    internal ReadOnlySpan<int> Lengths => _lengths;

    /// <summary>Total bytes this encoding will occupy, table and children included.</summary>
    internal long EncodedSize { get; }

    /// <summary>
    /// The code stream's block, whose first <see cref="CodeLength"/> bytes are the stream. It
    /// passes to the caller: the plan forgets it, and <see cref="Release"/> no longer hands it back.
    /// </summary>
    internal Block<byte> TakeCodes()
    {
        Block<byte> codes = _codes;
        _codes = default;
        return codes;
    }

    /// <summary>Hands back every block the plan still holds: the code stream, the row tables, the symbol table.</summary>
    /// <remarks>
    /// Called by the writer once the row tables and the symbols are in its buffers, and by the
    /// chooser for a plan nothing will write. Safe twice: the plan forgets each block before it
    /// gives it back, so a second call has nothing left to give.
    /// </remarks>
    internal void Release()
    {
        FsstSymbols? table = _table;
        _table = null;
        table?.Recycle();
        Blocks.Return(_codes);
        Blocks.Return(_offsets);
        Blocks.Return(_lengths);
        _codes = default;
        _offsets = default;
        _lengths = default;
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

        // The sizes are read from the views, a null row's as zero: its bytes are unspecified, so it
        // contributes nothing to the corpus and nothing to the stream, but still occupies a row slot.
        ValidityReader valid = ValidityReader.Of(arena, node.Validity);
        Block<int> lengths = Blocks.Rent<int>(rows);
        long plain = ViewHeap.Lengths(node, valid, lengths);

        if (plain > int.MaxValue - ViewHeap.Slack || CodedFloor(lengths, plain) > sizeCeiling)
        {
            Blocks.Return(lengths);
            return null;
        }

        // Everything is rented: the values live in the arena's pinned blocks and have to be copied to be
        // trained on, and the heap, row tables and code stream are all garbage the moment this
        // column is priced against zstd and loses. A plan that wins keeps the row tables and the
        // code stream, and hands them back once the writer has them.
        // The gather writes past each value, so the heap has its slack; the compressor then reads
        // every position as one eight-byte word however near the end of its value, so eight bytes
        // past the values are cleared.
        int heapBytes = Math.Max((int)plain, 1);
        Block<byte> heap = Blocks.Rent<byte>(heapBytes + ViewHeap.Slack);
        Block<int> starts = Blocks.Rent<int>(rows);
        Block<byte> codes = default;
        Block<int> offsets = default;
        FsstSymbols? table = null;
        bool kept = false;
        try
        {
            int at = (int)plain;
            ViewHeap.Gather(node, lengths, heap, starts);

            // The training is handed the ceiling too: a column its sample says is past it by far is
            // given up before the training ends, as the compression gives up past it below.
            table = FsstSymbols.Train(heap.AsSpan(0, heapBytes), starts, lengths, plain, sizeCeiling);
            if (table is null)
            {
                return null;
            }

            // An escape costs two bytes, so the worst case is twice the input.
            codes = Blocks.Rent<byte>((int)Math.Max(plain * 2, 1));
            offsets = Blocks.Rent<int>(rows + 1);
            heap.AsSpan(at, FsstSymbols.MaxSymbolLength).Clear();
            int written = table.CompressAll(
                heap.AsSpan(0, at + FsstSymbols.MaxSymbolLength), starts, lengths, codes, offsets, sizeCeiling);
            if (written < 0)
            {
                return null;
            }

            long encoded = ((long)table.Count * (FsstSymbols.MaxSymbolLength + 1)) + written
                + RowTableBytes(rows, MaxOf(lengths))
                + RowTableBytes(rows + 1, written);
            if (encoded > sizeCeiling)
            {
                return null;
            }

            // The code stream stays in its worst-case block rather than being copied out: the
            // writer hands the block to the blob as it is, and the blob gives it back once laid out.
            FsstPlan plan = new FsstPlan(table, codes, written, offsets, lengths, encoded);
            kept = true;
            return plan;
        }
        finally
        {
            if (!kept)
            {
                table?.Recycle();
                Blocks.Return(codes);
                Blocks.Return(offsets);
                Blocks.Return(lengths);
            }

            Blocks.Return(starts);
            Blocks.Return(heap);
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

    /// <summary>
    /// The fewest bytes the encoding of rows of <paramref name="lengths"/>, <paramref name="plain"/>
    /// bytes in all, can take, known before any training: a code stands for eight bytes at most, so
    /// the code stream holds an eighth of the column at least, and the two row tables are priced
    /// on it as <see cref="TryBuild"/> prices them. A column a zstd frame shrinks past what FSST
    /// could at its best is turned down on it, rather than after a training run and part of a
    /// compression; a floor, so no winner is dropped.
    /// </summary>
    private static long CodedFloor(ReadOnlySpan<int> lengths, long plain)
    {
        long codes = (plain + FsstSymbols.MaxSymbolLength - 1) / FsstSymbols.MaxSymbolLength;
        return codes + RowTableBytes(lengths.Length, MaxOf(lengths)) + RowTableBytes(lengths.Length + 1, codes);
    }

    /// <summary>
    /// A row table's bytes once the integer schemes have it: <paramref name="count"/> values up to
    /// <paramref name="maximum"/>, packed to the bits the largest takes, as a frame of reference
    /// packs lengths and offsets; the plain table's when that is smaller.
    /// </summary>
    internal static long RowTableBytes(long count, long maximum)
    {
        int bits = maximum <= 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)maximum);
        return Math.Min(((count * bits) + 7) / 8, count * IndexPType(maximum).ByteWidth());
    }
}
