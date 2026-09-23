using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Typed row movement: gather, tile and masked gather, with the physical-type dispatch done once
/// per call instead of once per row. Several encodings move canonical rows around rather than
/// compute them, and in such a loop the dispatch is the body, so each kernel switches on the code
/// type once and on the value width once, then calls a loop generic in both that the runtime
/// specializes into a plain load and store.
/// </summary>
/// <remarks>
/// Value widths are 1, 2, 4 and 8 for primitives and decimals, 16 for a view or a 128-bit decimal,
/// and 32 for a 256-bit one. <c>Vector128&lt;byte&gt;</c> stands in for the 16-byte unit because it
/// is a real unmanaged type whose assignment already compiles to one pair of instructions, and,
/// being a type rather than an intrinsic call, it stays correct when hardware intrinsics are
/// disabled. Every code is range-checked against the value count before it indexes anything: a
/// dictionary's claim that all its values are referenced is a hint, and must never let that check
/// be skipped.
/// </remarks>
internal static class RowKernels
{
    /// <summary>Value widths this file has a typed kernel for.</summary>
    /// <param name="width">Bytes per row.</param>
    internal static bool HasTypedWidth(int width) => width is 1 or 2 or 4 or 8 or 16 or 32;

    /// <summary>
    /// Fills <paramref name="destination"/> with repeats of <paramref name="element"/>.
    /// </summary>
    /// <param name="destination">A whole number of elements.</param>
    /// <param name="element">One element; its length is the stride.</param>
    /// <remarks>
    /// The widths that fit a primitive go through <c>Span&lt;T&gt;.Fill</c>, which is the vectorized
    /// memset. The rest double: write one element, then copy what is written over what is not, so
    /// filling costs a logarithmic number of block copies rather than one copy per element.
    /// </remarks>
    internal static void Tile(Span<byte> destination, ReadOnlySpan<byte> element)
    {
        int width = element.Length;
        if (width == 0 || destination.IsEmpty)
        {
            return;
        }

        switch (width)
        {
            case 1:
                destination.Fill(element[0]);
                return;
            case 2:
                MemoryMarshal.Cast<byte, ushort>(destination)
                    .Fill(MemoryMarshal.Read<ushort>(element));
                return;
            case 4:
                MemoryMarshal.Cast<byte, uint>(destination)
                    .Fill(MemoryMarshal.Read<uint>(element));
                return;
            case 8:
                MemoryMarshal.Cast<byte, ulong>(destination)
                    .Fill(MemoryMarshal.Read<ulong>(element));
                return;
            default:
                break;
        }

        element.CopyTo(destination);
        int filled = width;
        while (filled < destination.Length)
        {
            int chunk = Math.Min(filled, destination.Length - filled);
            destination[..chunk].CopyTo(destination.Slice(filled, chunk));
            filled += chunk;
        }
    }

    /// <summary>
    /// Writes <paramref name="count"/> copies of row <paramref name="sourceRow"/> at row
    /// <paramref name="destinationRow"/>.
    /// </summary>
    /// <param name="width">Bytes per row.</param>
    /// <param name="source">The source rows.</param>
    /// <param name="sourceRow">The row to repeat, already bounds-checked.</param>
    /// <param name="destination">The destination rows.</param>
    /// <param name="destinationRow">First destination row.</param>
    /// <param name="count">How many rows.</param>
    internal static void TileRow(
        int width, ReadOnlySpan<byte> source, int sourceRow,
        Span<byte> destination, int destinationRow, int count) =>
        Tile(
            destination.Slice(destinationRow * width, count * width),
            source.Slice(sourceRow * width, width));

    /// <summary>
    /// Gathers <paramref name="count"/> rows of <paramref name="values"/> through
    /// <paramref name="codes"/>, checking every code against <paramref name="valuesLength"/>.
    /// </summary>
    /// <param name="codes">The codes, as their own physical type.</param>
    /// <param name="codesPType">The codes' physical type; must be an integer.</param>
    /// <param name="values">The value rows.</param>
    /// <param name="width">Bytes per value row.</param>
    /// <param name="valuesLength">Rows in <paramref name="values"/>.</param>
    /// <param name="destination">Exactly <paramref name="count"/> rows of room.</param>
    /// <param name="count">Rows to gather.</param>
    /// <returns>
    /// The row of the first out-of-range code, or -1 when every code was in range. The caller
    /// raises, because only it knows the encoding's name and the code's value.
    /// </returns>
    internal static int Gather(
        ReadOnlySpan<byte> codes, PType codesPType, ReadOnlySpan<byte> values, int width,
        int valuesLength, Span<byte> destination, int count) => codesPType switch
        {
            PType.U8 => GatherCodes<byte>(codes, values, width, valuesLength, destination, count),
            PType.U16 => GatherCodes<ushort>(codes, values, width, valuesLength, destination, count),
            PType.U32 => GatherCodes<uint>(codes, values, width, valuesLength, destination, count),
            PType.U64 => GatherCodes<ulong>(codes, values, width, valuesLength, destination, count),
            PType.I8 => GatherCodes<sbyte>(codes, values, width, valuesLength, destination, count),
            PType.I16 => GatherCodes<short>(codes, values, width, valuesLength, destination, count),
            PType.I32 => GatherCodes<int>(codes, values, width, valuesLength, destination, count),
            _ => GatherCodes<long>(codes, values, width, valuesLength, destination, count),
        };

    /// <summary>
    /// Gathers rows whose code is non-null, clearing the rest, and writes the output validity.
    /// </summary>
    /// <param name="codes">The codes, as their own physical type.</param>
    /// <param name="codesPType">The codes' physical type.</param>
    /// <param name="values">The value rows.</param>
    /// <param name="width">Bytes per value row.</param>
    /// <param name="valuesLength">Rows in <paramref name="values"/>.</param>
    /// <param name="destination">Exactly <paramref name="count"/> rows of room.</param>
    /// <param name="count">Rows to produce.</param>
    /// <param name="codeBits">
    /// The codes' validity bitmap, or empty when every code is valid. A row whose bit is clear
    /// never reads its code -- upstream lets a null position carry any code at all, because the
    /// codes child may have been compressed further -- and its value bytes are zeroed rather than
    /// left as the allocator found them.
    /// </param>
    /// <param name="codeBitOffset">Bit position of row 0 in <paramref name="codeBits"/>.</param>
    /// <param name="valueBits">
    /// The values' validity bitmap, or empty when <paramref name="valuesAllValid"/> settles it.
    /// </param>
    /// <param name="valueBitOffset">Bit position of value 0 in <paramref name="valueBits"/>.</param>
    /// <param name="valuesAllValid">Whether every value row is valid.</param>
    /// <param name="outputBits">
    /// The output validity, starting all-clear; empty when the output tracks no validity.
    /// </param>
    /// <returns>The row of the first out-of-range code, or -1.</returns>
    internal static int GatherMasked(
        ReadOnlySpan<byte> codes, PType codesPType, ReadOnlySpan<byte> values, int width,
        int valuesLength, Span<byte> destination, int count,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid,
        Span<byte> outputBits) => codesPType switch
        {
            PType.U8 => MaskedCodes<byte>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            PType.U16 => MaskedCodes<ushort>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            PType.U32 => MaskedCodes<uint>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            PType.U64 => MaskedCodes<ulong>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            PType.I8 => MaskedCodes<sbyte>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            PType.I16 => MaskedCodes<short>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            PType.I32 => MaskedCodes<int>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            _ => MaskedCodes<long>(
                codes, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
        };

    private static int MaskedCodes<TCode>(
        ReadOnlySpan<byte> codes, ReadOnlySpan<byte> values, int width, int valuesLength,
        Span<byte> destination, int count,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid,
        Span<byte> outputBits)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> typed = MemoryMarshal.Cast<byte, TCode>(codes)[..count];
        return width switch
        {
            1 => MaskedCore<TCode, byte>(
                typed, values, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            2 => MaskedCore<TCode, ushort>(
                typed, values, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            4 => MaskedCore<TCode, uint>(
                typed, values, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            8 => MaskedCore<TCode, ulong>(
                typed, values, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            16 => MaskedCore<TCode, Vector128<byte>>(
                typed, values, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            32 => MaskedCore<TCode, Block32>(
                typed, values, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
            _ => MaskedWide(
                typed, values, width, valuesLength, destination, count, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, outputBits),
        };
    }

    /// <summary>
    /// The nullable-values gather with the dictionary's validity already one byte per entry.
    /// </summary>
    /// <remarks>
    /// Every access is provable before the loop: <paramref name="codes"/> and
    /// <paramref name="target"/> are both exactly the row count, a code past
    /// <paramref name="limit"/> returns before it indexes anything, and
    /// <paramref name="flags"/> has one entry per value -- which is what
    /// <paramref name="limit"/> counts.
    /// </remarks>
    private static int MaskedExpanded<TCode, TValue>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<TValue> source, Span<TValue> target,
        ReadOnlySpan<byte> flags, uint limit, Span<byte> outputBits)
        where TCode : unmanaged
        where TValue : unmanaged
    {
        ref TCode codeRef = ref MemoryMarshal.GetReference(codes);
        ref TValue sourceRef = ref MemoryMarshal.GetReference(source);
        ref TValue targetRef = ref MemoryMarshal.GetReference(target);
        ref byte flagRef = ref MemoryMarshal.GetReference(flags);

        // The gather and the mask stay in one body rather than becoming two passes: a second walk
        // over the codes costs more than the register pressure it relieves, and there is no
        // dependency between the value store and the mask arithmetic for a split to break.
        //
        // The full block is its own loop with a constant eight iterations, for the reason the
        // bitmap form below gives: a variable trip count costs the unroll, and with it the constant
        // shift amounts and the eight independent gathers in flight at once.
        int whole = target.Length & ~7;
        for (int block = 0; block < whole; block += 8)
        {
            int mask = 0;
            for (int k = 0; k < 8; k++)
            {
                int row = block + k;
                uint code = WidenCode(Unsafe.Add(ref codeRef, row));
                if (code >= limit)
                {
                    return row;
                }

                Unsafe.Add(ref targetRef, row) = Unsafe.Add(ref sourceRef, (nint)code);
                mask |= Unsafe.Add(ref flagRef, (nint)code) << k;
            }

            outputBits[block >> 3] = (byte)mask;
        }

        if (whole < target.Length)
        {
            int mask = 0;
            for (int row = whole; row < target.Length; row++)
            {
                uint code = WidenCode(Unsafe.Add(ref codeRef, row));
                if (code >= limit)
                {
                    return row;
                }

                Unsafe.Add(ref targetRef, row) = Unsafe.Add(ref sourceRef, (nint)code);
                mask |= Unsafe.Add(ref flagRef, (nint)code) << (row - whole);
            }

            outputBits[whole >> 3] = (byte)mask;
        }

        return -1;
    }

    private static int MaskedCore<TCode, TValue>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<byte> values, int valuesLength,
        Span<byte> destination, int count,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid,
        Span<byte> outputBits)
        where TCode : unmanaged
        where TValue : unmanaged
    {
        ReadOnlySpan<TValue> source = MemoryMarshal.Cast<byte, TValue>(values)[..valuesLength];
        Span<TValue> target = MemoryMarshal.Cast<byte, TValue>(destination)[..count];
        uint limit = (uint)valuesLength;
        bool codesAllValid = codeBits.IsEmpty;
        bool tracked = !outputBits.IsEmpty;

        // Every code valid, the values possibly not: a dictionary whose values are nullable. There
        // is no code mask to test, and the output validity is a bit per row written in order, so it
        // is accumulated a byte at a time and stored once instead of eight read-modify-writes of
        // the same byte.
        if (codesAllValid && tracked && !valuesAllValid)
        {
            // Four bounds checks per row become one for the whole loop. Every access inside the
            // block below is provable from a fact established before it, so the checks are not
            // removed on trust - they are removed because they are redundant:
            //
            //   * codes[row]        - `codes` is sliced to exactly the row count here;
            //   * source[code]      - `code >= limit` returns first, and source.Length == limit;
            //   * target[row]       - `target` is already sliced to the row count;
            //   * valueBits[bit>>3] - `bit < valueBitOffset + valuesLength`, and the guard below
            //                         checks the bitmap covers exactly that many bits.
            //
            // The guard failing is not an error: it falls through to the general loop, which
            // checks everything per row and reports a malformed file. That matters, because the
            // bitmap's extent comes from the file and this is a reader hostile input is aimed at:
            // the checks go away when they are provably redundant, not when they are merely
            // unlikely to fire.
            if (codes.Length >= target.Length
                && !valueBits.IsEmpty
                && (long)valueBits.Length * 8 >= (long)valueBitOffset + valuesLength)
            {
                ReadOnlySpan<TCode> rowCodes = codes[..target.Length];

                // One byte per dictionary entry, expanded once. The row body gathers twice from the
                // dictionary, the value and the value's validity bit, and extracting that bit from
                // a bitmap takes several operations where a table of bytes takes a load, a shift
                // and an or.
                //
                // The expansion costs one pass over the dictionary against one per row, so it is
                // taken only when the dictionary is the smaller of the two, which is the case
                // dictionary encoding is for. A dictionary larger than the column it encodes keeps
                // the bitmap form below, where the table would cost more cache than the bit
                // arithmetic it saves.
                if (valuesLength <= target.Length)
                {
                    Scratch<byte> flagScratch = new Scratch<byte>(valuesLength, default);
                    try
                    {
                        Span<byte> flags = flagScratch.Span;
                        for (int entry = 0; entry < valuesLength; entry++)
                        {
                            int at = valueBitOffset + entry;
                            flags[entry] = (byte)((valueBits[at >> 3] >> (at & 7)) & 1);
                        }

                        return MaskedExpanded(rowCodes, source, target, flags, limit, outputBits);
                    }
                    finally
                    {
                        flagScratch.Dispose();
                    }
                }

                ref TCode codeRef = ref MemoryMarshal.GetReference(rowCodes);
                ref TValue sourceRef = ref MemoryMarshal.GetReference(source);
                ref TValue targetRef = ref MemoryMarshal.GetReference(target);
                ref byte bitsRef = ref MemoryMarshal.GetReference(valueBits);

                // The full block is its own loop with a constant eight iterations. Sharing one loop
                // with the tail would make the trip count a variable, which costs the unroll, and
                // with it the constant shift amounts and the chance for eight independent gathers
                // to be in flight at once. Only the last block of a column is ever short.
                int whole = target.Length & ~7;
                for (int block = 0; block < whole; block += 8)
                {
                    int mask = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int row = block + k;
                        uint code = WidenCode(Unsafe.Add(ref codeRef, row));
                        if (code >= limit)
                        {
                            return row;
                        }

                        Unsafe.Add(ref targetRef, row) = Unsafe.Add(ref sourceRef, (nint)code);
                        int bit = valueBitOffset + (int)code;
                        mask |= ((Unsafe.Add(ref bitsRef, bit >> 3) >> (bit & 7)) & 1) << k;
                    }

                    outputBits[block >> 3] = (byte)mask;
                }

                if (whole < target.Length)
                {
                    int mask = 0;
                    for (int row = whole; row < target.Length; row++)
                    {
                        uint code = WidenCode(Unsafe.Add(ref codeRef, row));
                        if (code >= limit)
                        {
                            return row;
                        }

                        Unsafe.Add(ref targetRef, row) = Unsafe.Add(ref sourceRef, (nint)code);
                        int bit = valueBitOffset + (int)code;
                        mask |= ((Unsafe.Add(ref bitsRef, bit >> 3) >> (bit & 7)) & 1) << (row - whole);
                    }

                    outputBits[whole >> 3] = (byte)mask;
                }

                return -1;
            }

            for (int block = 0; block < target.Length; block += 8)
            {
                int rows = Math.Min(8, target.Length - block);
                int mask = 0;
                for (int k = 0; k < rows; k++)
                {
                    int row = block + k;
                    uint code = WidenCode(codes[row]);
                    if (code >= limit)
                    {
                        return row;
                    }

                    target[row] = source[(int)code];
                    int bit = valueBitOffset + (int)code;
                    mask |= ((valueBits[bit >> 3] >> (bit & 7)) & 1) << k;
                }

                outputBits[block >> 3] = (byte)mask;
            }

            return -1;
        }

        // Nullable codes: sixty-four rows of the codes' validity at a time, when every access can be
        // proven before the loop, as above.
        if (!codesAllValid
            && limit > 0
            && codes.Length >= target.Length
            && (long)codeBits.Length * 8 >= (long)codeBitOffset + target.Length
            && (!tracked || outputBits.Length >= (target.Length + 7) / 8)
            && (valuesAllValid || (!valueBits.IsEmpty && (long)valueBits.Length * 8 >= (long)valueBitOffset + valuesLength)))
        {
            return MaskedWords(
                codes[..target.Length], source, target, codeBits, codeBitOffset,
                valueBits, valueBitOffset, valuesAllValid, limit, tracked ? outputBits : default);
        }

        for (int row = 0; row < target.Length; row++)
        {
            if (!codesAllValid &&
                (codeBits[(codeBitOffset + row) >> 3] & (1 << ((codeBitOffset + row) & 7))) == 0)
            {
                target[row] = default;
                continue;
            }

            uint code = WidenCode(codes[row]);
            if (code >= limit)
            {
                return row;
            }

            target[row] = source[(int)code];
            if (!tracked)
            {
                continue;
            }

            int bit = valueBitOffset + (int)code;
            if (valuesAllValid ||
                (!valueBits.IsEmpty && (valueBits[bit >> 3] & (1 << (bit & 7))) != 0))
            {
                outputBits[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        return -1;
    }

    /// <summary>
    /// The gather with nullable codes, sixty-four rows of their validity at a time and no branch on
    /// a row.
    /// </summary>
    /// <returns>The row of the first out-of-range code under a valid row, or -1.</returns>
    /// <remarks>
    /// <para>
    /// A row's validity decides whether its code is read, and a branch on it mispredicts as often
    /// as the nulls fall irregularly. Here every row is gathered: a null row's code, and a code
    /// past the dictionary, reads entry zero instead, which a dictionary that is not empty holds,
    /// and the null rows are emptied once their word is done. A code past the dictionary under a
    /// valid row is the one fault; it is noted as the word goes and its row found after.
    /// </para>
    /// <para>
    /// A row is valid when its code is and its value is: the codes' own word when every value is
    /// valid, and otherwise each row's bit ANDed with its value's flag, a byte per dictionary
    /// entry expanded once. The output takes a word at a time.
    /// </para>
    /// </remarks>
    private static int MaskedWords<TCode, TValue>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<TValue> source, Span<TValue> target,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid, uint limit,
        Span<byte> outputBits)
        where TCode : unmanaged
        where TValue : unmanaged
    {
        int entries = (int)limit;
        Scratch<byte> flagScratch = new Scratch<byte>(valuesAllValid ? 0 : entries, default);
        try
        {
            Span<byte> flags = flagScratch.Span;
            if (!valuesAllValid)
            {
                for (int entry = 0; entry < entries; entry++)
                {
                    int at = valueBitOffset + entry;
                    flags[entry] = (byte)((valueBits[at >> 3] >> (at & 7)) & 1);
                }
            }

            ref TCode codeRef = ref MemoryMarshal.GetReference(codes);
            ref TValue sourceRef = ref MemoryMarshal.GetReference(source);
            ref TValue targetRef = ref MemoryMarshal.GetReference(target);
            ref byte flagRef = ref MemoryMarshal.GetReference(flags);
            for (int row = 0; row < target.Length; row += 64)
            {
                int span = Math.Min(64, target.Length - row);
                ulong all = BitWords.Mask(span);
                ulong valid = BitWords.Load(codeBits, codeBitOffset + row) & all;
                ref TValue rows = ref Unsafe.Add(ref targetRef, row);
                bool faulted;
                ulong output = valuesAllValid
                    ? GatherWord<TCode, TValue, AllValuesValid>(
                        ref Unsafe.Add(ref codeRef, row), ref sourceRef, ref rows, ref flagRef, span, valid, limit, out faulted)
                    : GatherWord<TCode, TValue, ValuesFlagged>(
                        ref Unsafe.Add(ref codeRef, row), ref sourceRef, ref rows, ref flagRef, span, valid, limit, out faulted);
                if (faulted)
                {
                    return FirstFault(codes, valid, limit, row, span);
                }

                for (ulong nulls = ~valid & all; nulls != 0; nulls &= nulls - 1)
                {
                    Unsafe.Add(ref rows, BitOperations.TrailingZeroCount(nulls)) = default;
                }

                if (!outputBits.IsEmpty)
                {
                    Span<byte> bytes = outputBits.Slice(row >> 3, (span + 7) >> 3);
                    for (int b = 0; b < bytes.Length; b++)
                    {
                        bytes[b] = (byte)(output >> (b * 8));
                    }
                }
            }

            return -1;
        }
        finally
        {
            flagScratch.Dispose();
        }
    }

    /// <summary>
    /// Gathers <paramref name="count"/> rows whose codes' validity is <paramref name="valid"/>,
    /// without a branch on a row; the rows' validity, and whether a valid row's code is past the
    /// dictionary.
    /// </summary>
    /// <remarks>
    /// Calls nothing, so that its state stays in registers. The null rows' slots are left holding
    /// entry zero, for the caller to empty.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong GatherWord<TCode, TValue, TValidity>(
        ref TCode codes, ref TValue source, ref TValue target, ref byte flags, int count, ulong valid,
        uint limit, out bool faulted)
        where TCode : unmanaged
        where TValue : unmanaged
        where TValidity : struct, IValueValidity
    {
        ulong output = 0;
        uint fault = 0;
        for (int k = 0; k < count; k++)
        {
            uint bit = (uint)(valid >> k) & 1;
            uint raw = WidenCode(Unsafe.Add(ref codes, k));
            uint inside = raw < limit ? 1u : 0u;
            fault |= bit & (inside ^ 1);
            uint code = raw & (0u - (bit & inside));
            Unsafe.Add(ref target, k) = Unsafe.Add(ref source, (nint)code);
            if (TValidity.Flagged)
            {
                output |= (ulong)(bit & Unsafe.Add(ref flags, (nint)code)) << k;
            }
        }

        faulted = fault != 0;
        return TValidity.Flagged ? output : valid;
    }

    /// <summary>The first valid row of a word whose code is past the dictionary; an error path.</summary>
    private static int FirstFault<TCode>(ReadOnlySpan<TCode> codes, ulong valid, uint limit, int row, int span)
        where TCode : unmanaged
    {
        for (int k = 0; k < span; k++)
        {
            if (((valid >> k) & 1) != 0 && WidenCode(codes[row + k]) >= limit)
            {
                return row + k;
            }
        }

        return row;
    }

    /// <summary>Whether a gather's values carry validity of their own, as a type.</summary>
    private interface IValueValidity
    {
        static abstract bool Flagged { get; }
    }

    /// <summary>Every value valid: a row is valid when its code is.</summary>
    private readonly struct AllValuesValid : IValueValidity
    {
        public static bool Flagged => false;
    }

    /// <summary>Values with validity: a row is valid when its code and its value are.</summary>
    private readonly struct ValuesFlagged : IValueValidity
    {
        public static bool Flagged => true;
    }

    private static int MaskedWide<TCode>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<byte> values, int width, int valuesLength,
        Span<byte> destination, int count,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid,
        Span<byte> outputBits)
        where TCode : unmanaged
    {
        uint limit = (uint)valuesLength;
        bool codesAllValid = codeBits.IsEmpty;
        bool tracked = !outputBits.IsEmpty;

        for (int row = 0; row < count; row++)
        {
            if (!codesAllValid &&
                (codeBits[(codeBitOffset + row) >> 3] & (1 << ((codeBitOffset + row) & 7))) == 0)
            {
                destination.Slice(row * width, width).Clear();
                continue;
            }

            uint code = WidenCode(codes[row]);
            if (code >= limit)
            {
                return row;
            }

            values.Slice((int)code * width, width).CopyTo(destination.Slice(row * width, width));
            if (!tracked)
            {
                continue;
            }

            int bit = valueBitOffset + (int)code;
            if (valuesAllValid ||
                (!valueBits.IsEmpty && (valueBits[bit >> 3] & (1 << (bit & 7))) != 0))
            {
                outputBits[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        return -1;
    }

    /// <summary>
    /// Widens <paramref name="destination"/>'s length of codes to <see cref="uint"/>, checking every
    /// non-null code against <paramref name="valuesLength"/>, and writes the row validity of a
    /// dictionary kept encoded.
    /// </summary>
    /// <param name="codes">The codes, as their own physical type.</param>
    /// <param name="codesPType">The codes' physical type.</param>
    /// <param name="valuesLength">Rows in the dictionary.</param>
    /// <param name="destination">One code per row; a null code is written as 0.</param>
    /// <param name="codeBits">The codes' validity bitmap, or empty when every code is valid.</param>
    /// <param name="codeBitOffset">Bit position of row 0 in <paramref name="codeBits"/>.</param>
    /// <param name="valueBits">The values' validity bitmap, or empty when <paramref name="valuesAllValid"/> settles it.</param>
    /// <param name="valueBitOffset">Bit position of value 0 in <paramref name="valueBits"/>.</param>
    /// <param name="valuesAllValid">Whether every value row is valid.</param>
    /// <param name="outputBits">The row validity, starting all-clear; empty when no row can be null.</param>
    /// <returns>The row of the first out-of-range code, or -1.</returns>
    internal static int WidenCodes(
        ReadOnlySpan<byte> codes, PType codesPType, int valuesLength, Span<uint> destination,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid,
        Span<byte> outputBits) => codesPType switch
        {
            PType.U8 => WidenTyped<byte>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            PType.U16 => WidenTyped<ushort>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            PType.U32 => WidenTyped<uint>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            PType.U64 => WidenTyped<ulong>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            PType.I8 => WidenTyped<sbyte>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            PType.I16 => WidenTyped<short>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            PType.I32 => WidenTyped<int>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
            _ => WidenTyped<long>(
                codes, valuesLength, destination, codeBits, codeBitOffset, valueBits, valueBitOffset,
                valuesAllValid, outputBits),
        };

    /// <summary>The first code at or above <paramref name="limit"/>, or -1.</summary>
    /// <param name="codes">Codes already 32 bits wide, a negative signed one reading as a large one.</param>
    /// <param name="limit">Rows in the dictionary.</param>
    internal static int FirstCodeOutside(ReadOnlySpan<uint> codes, uint limit)
    {
        int row = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref uint source = ref MemoryMarshal.GetReference(codes);
            Vector128<uint> bound = Vector128.Create(limit);
            for (; row + Vector128<uint>.Count <= codes.Length; row += Vector128<uint>.Count)
            {
                if (Vector128.GreaterThanOrEqualAny(Vector128.LoadUnsafe(ref source, (nuint)row), bound))
                {
                    break;
                }
            }
        }

        for (; row < codes.Length; row++)
        {
            if (codes[row] >= limit)
            {
                return row;
            }
        }

        return -1;
    }

    private static int WidenTyped<TCode>(
        ReadOnlySpan<byte> codes, int valuesLength, Span<uint> destination,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid,
        Span<byte> outputBits)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> typed = MemoryMarshal.Cast<byte, TCode>(codes)[..destination.Length];
        uint limit = (uint)valuesLength;
        bool tracked = !outputBits.IsEmpty;
        if (codeBits.IsEmpty && !tracked)
        {
            return WidenDense(typed, limit, destination);
        }

        bool codesAllValid = codeBits.IsEmpty;
        for (int row = 0; row < destination.Length; row++)
        {
            if (!codesAllValid &&
                (codeBits[(codeBitOffset + row) >> 3] & (1 << ((codeBitOffset + row) & 7))) == 0)
            {
                // A null position may carry any code at all, so it is never read.
                destination[row] = 0;
                continue;
            }

            uint code = WidenCode(typed[row]);
            if (code >= limit)
            {
                return row;
            }

            destination[row] = code;
            if (!tracked)
            {
                continue;
            }

            int bit = valueBitOffset + (int)code;
            if (valuesAllValid ||
                (!valueBits.IsEmpty && (valueBits[bit >> 3] & (1 << (bit & 7))) != 0))
            {
                outputBits[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        return -1;
    }

    /// <summary>The widen with no validity on either side: a vector pass for the narrow codes, then the rest.</summary>
    private static int WidenDense<TCode>(ReadOnlySpan<TCode> codes, uint limit, Span<uint> destination)
        where TCode : unmanaged
    {
        // The vector pass stops at the first block holding a code out of range, and the scalar loop
        // resumes there, so the row it reports is the exact one.
        int row = 0;
        if (typeof(TCode) == typeof(byte))
        {
            row = WidenBytes(MemoryMarshal.Cast<TCode, byte>(codes), limit, destination);
        }
        else if (typeof(TCode) == typeof(ushort))
        {
            row = WidenShorts(MemoryMarshal.Cast<TCode, ushort>(codes), limit, destination);
        }

        for (; row < destination.Length; row++)
        {
            uint code = WidenCode(codes[row]);
            if (code >= limit)
            {
                return row;
            }

            destination[row] = code;
        }

        return -1;
    }

    private static int WidenBytes(ReadOnlySpan<byte> codes, uint limit, Span<uint> destination)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        // A byte code is always below 256, so only a smaller dictionary needs the compare.
        bool bounded = limit < 256;
        Vector128<byte> bound = Vector128.Create((byte)Math.Min(limit, 255u));
        ref byte source = ref MemoryMarshal.GetReference(codes);
        ref uint target = ref MemoryMarshal.GetReference(destination);
        int row = 0;
        for (; row + 16 <= destination.Length; row += 16)
        {
            Vector128<byte> block = Vector128.LoadUnsafe(ref source, (nuint)row);
            if (bounded && Vector128.GreaterThanOrEqualAny(block, bound))
            {
                return row;
            }

            (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(block);
            (Vector128<uint> first, Vector128<uint> second) = Vector128.Widen(low);
            (Vector128<uint> third, Vector128<uint> fourth) = Vector128.Widen(high);
            first.StoreUnsafe(ref target, (nuint)row);
            second.StoreUnsafe(ref target, (nuint)(row + 4));
            third.StoreUnsafe(ref target, (nuint)(row + 8));
            fourth.StoreUnsafe(ref target, (nuint)(row + 12));
        }

        return row;
    }

    private static int WidenShorts(ReadOnlySpan<ushort> codes, uint limit, Span<uint> destination)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        bool bounded = limit < 65536;
        Vector128<ushort> bound = Vector128.Create((ushort)Math.Min(limit, 65535u));
        ref ushort source = ref MemoryMarshal.GetReference(codes);
        ref uint target = ref MemoryMarshal.GetReference(destination);
        int row = 0;
        for (; row + 8 <= destination.Length; row += 8)
        {
            Vector128<ushort> block = Vector128.LoadUnsafe(ref source, (nuint)row);
            if (bounded && Vector128.GreaterThanOrEqualAny(block, bound))
            {
                return row;
            }

            (Vector128<uint> low, Vector128<uint> high) = Vector128.Widen(block);
            low.StoreUnsafe(ref target, (nuint)row);
            high.StoreUnsafe(ref target, (nuint)(row + 4));
        }

        return row;
    }

    /// <summary>
    /// Reads code <paramref name="index"/> as an unsigned value, saturating a negative or
    /// over-large one to <see cref="uint.MaxValue"/> so the caller's range check rejects it.
    /// </summary>
    /// <param name="codes">The codes.</param>
    /// <param name="codesPType">Their physical type.</param>
    /// <param name="index">The code, already bounds-checked.</param>
    internal static uint CodeAt(ReadOnlySpan<byte> codes, PType codesPType, int index) =>
        codesPType switch
        {
            PType.U8 => codes[index],
            PType.U16 => MemoryMarshal.Cast<byte, ushort>(codes)[index],
            PType.U32 => MemoryMarshal.Cast<byte, uint>(codes)[index],
            PType.U64 => Widen(MemoryMarshal.Cast<byte, ulong>(codes)[index]),
            PType.I8 => Widen((long)MemoryMarshal.Cast<byte, sbyte>(codes)[index]),
            PType.I16 => Widen((long)MemoryMarshal.Cast<byte, short>(codes)[index]),
            PType.I32 => Widen((long)MemoryMarshal.Cast<byte, int>(codes)[index]),
            _ => Widen(MemoryMarshal.Cast<byte, long>(codes)[index]),
        };

    private static int GatherCodes<TCode>(
        ReadOnlySpan<byte> codes, ReadOnlySpan<byte> values, int width, int valuesLength,
        Span<byte> destination, int count)
        where TCode : unmanaged
    {
        ReadOnlySpan<TCode> typed = MemoryMarshal.Cast<byte, TCode>(codes)[..count];
        return width switch
        {
            1 => GatherCore<TCode, byte>(typed, values, valuesLength, destination, count),
            2 => GatherCore<TCode, ushort>(typed, values, valuesLength, destination, count),
            4 => GatherCore<TCode, uint>(typed, values, valuesLength, destination, count),
            8 => GatherCore<TCode, ulong>(typed, values, valuesLength, destination, count),
            16 => GatherCore<TCode, Vector128<byte>>(typed, values, valuesLength, destination, count),
            32 => GatherCore<TCode, Block32>(typed, values, valuesLength, destination, count),
            _ => GatherWide(typed, values, width, valuesLength, destination, count),
        };
    }

    private static int GatherCore<TCode, TValue>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<byte> values, int valuesLength,
        Span<byte> destination, int count)
        where TCode : unmanaged
        where TValue : unmanaged
    {
        ReadOnlySpan<TValue> source = MemoryMarshal.Cast<byte, TValue>(values)[..valuesLength];
        Span<TValue> target = MemoryMarshal.Cast<byte, TValue>(destination)[..count];
        uint limit = (uint)valuesLength;

        for (int row = 0; row < target.Length; row++)
        {
            uint code = WidenCode(codes[row]);
            if (code >= limit)
            {
                return row;
            }

            target[row] = source[(int)code];
        }

        return -1;
    }

    /// <summary>The widths no primitive covers: decimals of an odd storage, if one ever appears.</summary>
    private static int GatherWide<TCode>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<byte> values, int width, int valuesLength,
        Span<byte> destination, int count)
        where TCode : unmanaged
    {
        uint limit = (uint)valuesLength;
        for (int row = 0; row < count; row++)
        {
            uint code = WidenCode(codes[row]);
            if (code >= limit)
            {
                return row;
            }

            values.Slice((int)code * width, width).CopyTo(destination.Slice(row * width, width));
        }

        return -1;
    }

    /// <summary>
    /// Widens one code to <see cref="uint"/>, saturating anything outside its range so the range
    /// check rejects it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A negative code must saturate up, not down, so that the caller's range check refuses it.
    /// Truncating would be a silent correctness change: a large negative code truncates to 0 and
    /// would gather row 0 from a file that declares nonsense. <see cref="uint"/>'s own
    /// <c>CreateSaturating</c> is no good here either, since it clamps negatives to 0.
    /// </para>
    /// <para>
    /// <c>Unsafe.As</c> rather than a cast through <c>object</c>: the typeof comparisons are
    /// compile-time constants for a value-type instantiation and the JIT drops the dead branches,
    /// but a cast through <c>object</c> would box on every row before it did so.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint WidenCode<TCode>(TCode code)
        where TCode : unmanaged
    {
        if (typeof(TCode) == typeof(byte))
        {
            return Unsafe.As<TCode, byte>(ref code);
        }

        if (typeof(TCode) == typeof(ushort))
        {
            return Unsafe.As<TCode, ushort>(ref code);
        }

        if (typeof(TCode) == typeof(uint))
        {
            return Unsafe.As<TCode, uint>(ref code);
        }

        if (typeof(TCode) == typeof(ulong))
        {
            return Widen(Unsafe.As<TCode, ulong>(ref code));
        }

        if (typeof(TCode) == typeof(sbyte))
        {
            return Widen((long)Unsafe.As<TCode, sbyte>(ref code));
        }

        if (typeof(TCode) == typeof(short))
        {
            return Widen((long)Unsafe.As<TCode, short>(ref code));
        }

        if (typeof(TCode) == typeof(int))
        {
            return Widen((long)Unsafe.As<TCode, int>(ref code));
        }

        return Widen(Unsafe.As<TCode, long>(ref code));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Widen(ulong value) => value > uint.MaxValue ? uint.MaxValue : (uint)value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Widen(long value) =>
        value < 0 || value > uint.MaxValue ? uint.MaxValue : (uint)value;

    /// <summary>32 bytes moved as one unit, for an <c>i256</c> decimal.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Block32
    {
        private Vector128<byte> _low;
        private Vector128<byte> _high;
    }
}
