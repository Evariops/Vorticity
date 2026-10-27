using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Arrays.Decoders.Compressed;

internal static partial class FastLanes
{
    /// <summary>The words of one block's answer: a bit a row, in row order.</summary>
    internal const int BlockWords = BlockSize / 64;

    /// <summary>
    /// The rows of a run of whole packed blocks whose value <c>v</c> has <c>v - low &lt;= span</c>, the subtraction
    /// wrapping in <typeparamref name="T"/>: bit <c>i % 64</c> of word <c>i / 64</c> for row <c>i</c> of the run, every
    /// word of every block written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One test answers every order: <c>[low, low + span]</c> is the range of values selected, an equality a range of
    /// one, and a value outside it wraps past <paramref name="span"/> on either side, in the signed domain as in the
    /// unsigned one. The caller turns the answer over for an inequality.
    /// </para>
    /// <para>
    /// The values never leave the registers: a row of the block is unpacked as the unpack does it, a vector of lanes at
    /// a time, compared, and its lanes' verdicts gathered into bits. A row's lanes are consecutive rows of the block,
    /// from its first at the row's destination, and their count is a multiple of every vector's, so the bits of a
    /// vector land in one word, in row order: the answer needs no transposition.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">Exactly <c>blocks * lanes * bitWidth</c> words.</param>
    /// <param name="bitWidth">Bits per value, from 1 to the element width.</param>
    /// <param name="low">The least value selected.</param>
    /// <param name="span">How far past <paramref name="low"/> the values selected reach.</param>
    /// <param name="words">At least <c>blocks * 16</c> words.</param>
    /// <param name="blocks">How many blocks.</param>
    internal static void InRange<T>(ReadOnlySpan<T> packed, int bitWidth, T low, T span, Span<ulong> words, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = BlockSize / elementBits;
        if (!Vectorizable<T>() || !Vector128.IsHardwareAccelerated)
        {
            InRangeScalar(packed, bitWidth, low, span, words, lanes, blocks);
            return;
        }

        words[..(blocks * BlockWords)].Clear();

        // A full width copies through, and has no shapes: a row is its packed word, shifted by nothing.
        Row<T>[]? shapes = bitWidth < elementBits ? ShapesOf<T>(bitWidth) : null;
        if (Vector512.IsHardwareAccelerated)
        {
            InRange512(packed, bitWidth, shapes, low, span, words, lanes, blocks);
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            InRange256(packed, bitWidth, shapes, low, span, words, lanes, blocks);
        }
        else
        {
            InRange128(packed, bitWidth, shapes, low, span, words, lanes, blocks);
        }
    }

    /// <summary>
    /// The scalar twin of <see cref="InRange{T}"/>, reachable on its own: what the vector kernels are compared
    /// against, and what a machine without vectors runs.
    /// </summary>
    internal static void InRangeScalar<T>(
        ReadOnlySpan<T> packed, int bitWidth, T low, T span, Span<ulong> words, int lanes, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int wordsPerBlock = lanes * bitWidth;
        words[..(blocks * BlockWords)].Clear();
        for (int block = 0; block < blocks; block++)
        {
            ReadOnlySpan<T> source = packed.Slice(block * wordsPerBlock, wordsPerBlock);
            Span<ulong> answer = words.Slice(block * BlockWords, BlockWords);
            for (int row = 0; row < elementBits; row++)
            {
                int destination = RowDestination(row);
                for (int lane = 0; lane < lanes; lane++)
                {
                    T value = bitWidth == elementBits ? source[(lanes * row) + lane] : UnpackAt(source, bitWidth, row, lane);
                    if (value - low <= span)
                    {
                        int at = destination + lane;
                        answer[at >> 6] |= 1UL << (at & 63);
                    }
                }
            }
        }
    }

    /// <summary>The first row of a block that row <paramref name="row"/> of the packing holds; its lanes follow it.</summary>
    private static int RowDestination(int row) => (Order[row / 8] * 16) + ((row % 8) * 128);

    private static void InRange512<T>(
        ReadOnlySpan<T> packed, int bitWidth, Row<T>[]? shapes, T low, T span, Span<ulong> words, int lanes, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref ulong answers = ref MemoryMarshal.GetReference(words);
        Vector512<T> least = Vector512.Create(low);
        Vector512<T> reach = Vector512.Create(span);
        int step = Vector512<T>.Count;
        int rows = Unsafe.SizeOf<T>() * 8;
        int wordsPerBlock = lanes * bitWidth;
        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            ref ulong answer = ref Unsafe.Add(ref answers, block * BlockWords);
            for (int row = 0; row < rows; row++)
            {
                if (shapes is null)
                {
                    nuint word = blockSource + (nuint)(lanes * row);
                    int first = RowDestination(row);
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector512<T> value = Vector512.LoadUnsafe(ref source, word + (nuint)lane);
                        Place(ref answer, first + lane, Vector512.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                    }

                    continue;
                }

                ref Row<T> shape = ref shapes[row];
                Vector512<T> lowMask = Vector512.Create(shape.LowMask);
                Vector512<T> highMask = Vector512.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                int destination = shape.Destination;
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector512<T> value = ShiftRight(Vector512.LoadUnsafe(ref source, current + (nuint)lane), shift) & lowMask;
                        value |= ShiftLeft(Vector512.LoadUnsafe(ref source, next + (nuint)lane) & highMask, currentBits);
                        Place(ref answer, destination + lane, Vector512.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector512<T> value = ShiftRight(Vector512.LoadUnsafe(ref source, current + (nuint)lane), shift) & lowMask;
                    Place(ref answer, destination + lane, Vector512.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                }
            }
        }
    }

    private static void InRange256<T>(
        ReadOnlySpan<T> packed, int bitWidth, Row<T>[]? shapes, T low, T span, Span<ulong> words, int lanes, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref ulong answers = ref MemoryMarshal.GetReference(words);
        Vector256<T> least = Vector256.Create(low);
        Vector256<T> reach = Vector256.Create(span);
        int step = Vector256<T>.Count;
        int rows = Unsafe.SizeOf<T>() * 8;
        int wordsPerBlock = lanes * bitWidth;
        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            ref ulong answer = ref Unsafe.Add(ref answers, block * BlockWords);
            for (int row = 0; row < rows; row++)
            {
                if (shapes is null)
                {
                    nuint word = blockSource + (nuint)(lanes * row);
                    int first = RowDestination(row);
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector256<T> value = Vector256.LoadUnsafe(ref source, word + (nuint)lane);
                        Place(ref answer, first + lane, Vector256.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                    }

                    continue;
                }

                ref Row<T> shape = ref shapes[row];
                Vector256<T> lowMask = Vector256.Create(shape.LowMask);
                Vector256<T> highMask = Vector256.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                int destination = shape.Destination;
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector256<T> value = ShiftRight(Vector256.LoadUnsafe(ref source, current + (nuint)lane), shift) & lowMask;
                        value |= ShiftLeft(Vector256.LoadUnsafe(ref source, next + (nuint)lane) & highMask, currentBits);
                        Place(ref answer, destination + lane, Vector256.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector256<T> value = ShiftRight(Vector256.LoadUnsafe(ref source, current + (nuint)lane), shift) & lowMask;
                    Place(ref answer, destination + lane, Vector256.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                }
            }
        }
    }

    private static void InRange128<T>(
        ReadOnlySpan<T> packed, int bitWidth, Row<T>[]? shapes, T low, T span, Span<ulong> words, int lanes, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref ulong answers = ref MemoryMarshal.GetReference(words);
        Vector128<T> least = Vector128.Create(low);
        Vector128<T> reach = Vector128.Create(span);
        int step = Vector128<T>.Count;
        int rows = Unsafe.SizeOf<T>() * 8;
        int wordsPerBlock = lanes * bitWidth;
        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            ref ulong answer = ref Unsafe.Add(ref answers, block * BlockWords);
            for (int row = 0; row < rows; row++)
            {
                if (shapes is null)
                {
                    nuint word = blockSource + (nuint)(lanes * row);
                    int first = RowDestination(row);
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector128<T> value = Vector128.LoadUnsafe(ref source, word + (nuint)lane);
                        Place(ref answer, first + lane, Vector128.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                    }

                    continue;
                }

                ref Row<T> shape = ref shapes[row];
                Vector128<T> lowMask = Vector128.Create(shape.LowMask);
                Vector128<T> highMask = Vector128.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                int destination = shape.Destination;
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector128<T> value = ShiftRight(Vector128.LoadUnsafe(ref source, current + (nuint)lane), shift) & lowMask;
                        value |= ShiftLeft(Vector128.LoadUnsafe(ref source, next + (nuint)lane) & highMask, currentBits);
                        Place(ref answer, destination + lane, Vector128.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector128<T> value = ShiftRight(Vector128.LoadUnsafe(ref source, current + (nuint)lane), shift) & lowMask;
                    Place(ref answer, destination + lane, Vector128.LessThanOrEqual(value - least, reach).ExtractMostSignificantBits());
                }
            }
        }
    }

    /// <summary>
    /// The verdicts of a vector of lanes, its lowest bits, set into a block's answer from row <paramref name="at"/>:
    /// a vector's lanes never straddle a word, since their count divides 64 and its first row is a multiple of it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Place(ref ulong answer, int at, ulong verdicts) =>
        Unsafe.Add(ref answer, at >> 6) |= verdicts << (at & 63);

    /// <summary>
    /// The rows of a run of whole packed blocks whose value is one of <paramref name="members"/>, each block unpacked
    /// once into a block of scratch that stays in the first-level cache and compared there in row order with each
    /// member: for a few members, where a compare answers a vector of rows.
    /// </summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">Exactly <c>blocks * lanes * bitWidth</c> words.</param>
    /// <param name="bitWidth">Bits per value, from 0 to the element width.</param>
    /// <param name="members">The values selected, distinct.</param>
    /// <param name="words">At least <c>blocks * 16</c> words, each written.</param>
    /// <param name="blocks">How many blocks.</param>
    [SkipLocalsInit]
    internal static void AnyOf<T>(ReadOnlySpan<T> packed, int bitWidth, ReadOnlySpan<T> members, Span<ulong> words, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int wordsPerBlock = BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();
        Span<T> values = stackalloc T[BlockSize];
        for (int block = 0; block < blocks; block++)
        {
            UnpackBlock(packed.Slice(block * wordsPerBlock, wordsPerBlock), bitWidth, values);
            Span<ulong> answer = words.Slice(block * BlockWords, BlockWords);
            if (Vectorizable<T>() && Vector128.IsHardwareAccelerated)
            {
                AnyOf128(values, members, answer);
            }
            else
            {
                AnyOfScalar(values, members, answer);
            }
        }
    }

    /// <summary>One unpacked block's rows equal to one of the members, a vector of rows a compare.</summary>
    private static void AnyOf128<T>(ReadOnlySpan<T> values, ReadOnlySpan<T> members, Span<ulong> answer)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        answer.Clear();
        ref T from = ref MemoryMarshal.GetReference(values);
        ref ulong into = ref MemoryMarshal.GetReference(answer);
        int step = Vector128<T>.Count;
        for (int m = 0; m < members.Length; m++)
        {
            Vector128<T> member = Vector128.Create(members[m]);
            for (int row = 0; row < BlockSize; row += step)
            {
                Place(ref into, row, Vector128.Equals(Vector128.LoadUnsafe(ref from, (nuint)row), member).ExtractMostSignificantBits());
            }
        }
    }

    /// <summary>The scalar twin of <see cref="AnyOf128{T}"/>.</summary>
    internal static void AnyOfScalar<T>(ReadOnlySpan<T> values, ReadOnlySpan<T> members, Span<ulong> answer)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        answer.Clear();
        for (int row = 0; row < BlockSize; row++)
        {
            T value = values[row];
            for (int m = 0; m < members.Length; m++)
            {
                if (value == members[m])
                {
                    answer[row >> 6] |= 1UL << (row & 63);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The rows of a run of whole packed blocks whose value has its bit set in <paramref name="set"/>, a bit for each
    /// value of the bit width: each block unpacked once into scratch, and each of its values looked up, for a set of
    /// many members over a width of at most 16 bits.
    /// </summary>
    /// <param name="packed">Exactly <c>blocks * lanes * bitWidth</c> words.</param>
    /// <param name="bitWidth">Bits per value, from 0 to 16.</param>
    /// <param name="set">A bit per value of the width: <c>2^bitWidth</c> bits, at least one word.</param>
    /// <param name="words">At least <c>blocks * 16</c> words, each written.</param>
    /// <param name="blocks">How many blocks.</param>
    [SkipLocalsInit]
    internal static void InSet<T>(ReadOnlySpan<T> packed, int bitWidth, ReadOnlySpan<ulong> set, Span<ulong> words, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int wordsPerBlock = BlockByteLength(bitWidth) / Unsafe.SizeOf<T>();
        Span<T> values = stackalloc T[BlockSize];
        for (int block = 0; block < blocks; block++)
        {
            UnpackBlock(packed.Slice(block * wordsPerBlock, wordsPerBlock), bitWidth, values);
            Span<ulong> answer = words.Slice(block * BlockWords, BlockWords);
            for (int w = 0; w < BlockWords; w++)
            {
                ulong verdicts = 0;
                int first = w << 6;
                for (int k = 0; k < 64; k++)
                {
                    int value = int.CreateTruncating(values[first + k]);
                    verdicts |= ((set[value >> 6] >> (value & 63)) & 1) << k;
                }

                answer[w] = verdicts;
            }
        }
    }
}
