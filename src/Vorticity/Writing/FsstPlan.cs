// One column, compressed with FSST, ready to serialize.
//
// The plan is built during Choose rather than during the write for one reason: deciding whether
// FSST pays MEANS compressing the column. There is no cheap estimate - the whole question is how
// well a trained table covers this particular data - so the work is done once and the bytes are
// kept, rather than done twice.
using System;
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
    /// <param name="canonicalSize">What the column costs written as it is.</param>
    /// <returns>The plan, or null.</returns>
    internal static FsstPlan? TryBuild(CanonicalArena arena, int nodeIndex, long canonicalSize)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int rows = node.Length;
        List<ReadOnlyMemory<byte>> values = new List<ReadOnlyMemory<byte>>(rows);
        long plain = 0;
        for (int i = 0; i < rows; i++)
        {
            // A null row contributes nothing to the corpus AND nothing to the stream: its bytes
            // are unspecified, and encoding them would pay for values no reader will ever ask for.
            ReadOnlyMemory<byte> value = IsValid(arena, node, i)
                ? new ReadOnlyMemory<byte>(ValueOf(node, i).ToArray())
                : ReadOnlyMemory<byte>.Empty;
            values.Add(value);
            plain += value.Length;
        }

        FsstSymbols? table = FsstSymbols.Train(values);
        if (table is null)
        {
            return null;
        }

        // An escape costs two bytes, so the worst case is twice the input; sized once for the whole
        // column rather than per row.
        byte[] codes = new byte[Math.Max(plain * 2, 1)];
        int[] offsets = new int[rows + 1];
        int[] lengths = new int[rows];
        int written = 0;

        for (int i = 0; i < rows; i++)
        {
            offsets[i] = written;
            ReadOnlySpan<byte> value = values[i].Span;
            lengths[i] = value.Length;
            written += table.Compress(value, codes.AsSpan(written));
        }

        offsets[rows] = written;

        long encoded = ((long)table.Count * (FsstSymbols.MaxSymbolLength + 1)) + written
            + ((long)rows * Width(MaxOf(lengths)))
            + ((long)(rows + 1) * Width(written));

        // A margin, not a tie-break: FSST costs a symbol table and two index children on every
        // read, so a 1% saving is not worth making every reader pay for the indirection.
        return encoded * 10 <= canonicalSize * 9
            ? new FsstPlan(table, codes, written, offsets, lengths, encoded)
            : null;
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

    private static bool IsValid(CanonicalArena arena, CanonicalNode node, int row)
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

    private static ReadOnlySpan<byte> ValueOf(CanonicalNode node, int row)
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
