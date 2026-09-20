using System;
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
