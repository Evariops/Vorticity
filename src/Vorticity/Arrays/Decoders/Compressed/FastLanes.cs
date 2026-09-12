// The two FastLanes permutations, which are NOT the same one - spec/REFERENCE.md §"FastLanes: the
// two index functions, which are not the same", derived from fastlanes-0.7.2/src/bitpacking.rs,
// src/macros.rs and src/transpose.rs.
//
//   1. the BIT-PACKING index, used by fastlanes.bitpacked:
//          index(row, lane) = FL_ORDER[row / 8] * 16 + (row % 8) * 128 + lane
//      with `lane` running over T::LANES = 1024 / T::T lanes;
//   2. the ELEMENT TRANSPOSITION, used by the transposed encodings, where `lane` is `% 16`
//      whatever the element width:
//          transpose(i) = (i % 16) * 64 + FL_ORDER[(i / 16) % 8] * 8 + i / 128
//
// Only FL_ORDER is its own inverse. `transpose` is not: transpose(1) == 64 but transpose(64) == 8,
// so Untranspose is the inverse MAPPING (output[transpose(i)] = input[i]) and never a second
// Transpose. That bug survives a round trip written the same wrong way in both directions, which
// is why the tests assert the crate's known-value table literally.
//
// Scalar only: SIMD is Phase 2 (docs/03-architecture.md §4 invariant 4 requires the scalar path to
// exist and be tested on its own regardless).
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// The FastLanes 1024-element block permutations and the scalar bit-unpacking kernel.
/// </summary>
internal static class FastLanes
{
    /// <summary>Elements in one FastLanes block. <c>FL_CHUNK_SIZE</c> upstream.</summary>
    public const int BlockSize = 1024;

    /// <summary>Bytes one 1024-element block occupies at one bit of width: <c>128 * bit_width</c>.</summary>
    public const int BytesPerBlockPerBit = 128;

    // fastlanes-0.7.2/src/lib.rs. Its own inverse: FL_ORDER[FL_ORDER[i]] == i.
    private static ReadOnlySpan<byte> OrderBytes => [0, 4, 2, 6, 1, 5, 3, 7];

    private static readonly int[] TransposeForward = BuildTranspose();
    private static readonly int[] TransposeInverse = BuildTransposeInverse(TransposeForward);

    // One 1024-entry table per element width, exactly as the crate precomputes them.
    private static readonly int[] PackedIndex8 = BuildPackedIndex(8);
    private static readonly int[] PackedIndex16 = BuildPackedIndex(16);
    private static readonly int[] PackedIndex32 = BuildPackedIndex(32);
    private static readonly int[] PackedIndex64 = BuildPackedIndex(64);

    private static readonly int[] PackedRow8 = BuildPackedRows(8);
    private static readonly int[] PackedRow16 = BuildPackedRows(16);
    private static readonly int[] PackedRow32 = BuildPackedRows(32);
    private static readonly int[] PackedRow64 = BuildPackedRows(64);

    private static readonly int[] PackedLane8 = BuildPackedLanes(8);
    private static readonly int[] PackedLane16 = BuildPackedLanes(16);
    private static readonly int[] PackedLane32 = BuildPackedLanes(32);
    private static readonly int[] PackedLane64 = BuildPackedLanes(64);

    /// <summary><c>FL_ORDER</c>, the eight-entry permutation both index functions share.</summary>
    public static ReadOnlySpan<byte> Order => OrderBytes;

    /// <summary>
    /// The element transposition of fastlanes-0.7.2/src/transpose.rs:
    /// <c>lane * 64 + FL_ORDER[order] * 8 + row</c> with <c>lane = idx % 16</c>,
    /// <c>order = (idx / 16) % 8</c> and <c>row = idx / 128</c>.
    /// </summary>
    /// <param name="index">A logical index in <c>[0, 1024)</c>.</param>
    /// <returns>The transposed index.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the block.</exception>
    public static int Transpose(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockSize);
        return TransposeForward[index];
    }

    /// <summary>
    /// The inverse of <see cref="Transpose"/>: <c>Untranspose(Transpose(i)) == i</c>. It is a
    /// different permutation, <b>not</b> a second <see cref="Transpose"/>.
    /// </summary>
    /// <param name="index">A transposed index in <c>[0, 1024)</c>.</param>
    /// <returns>The logical index it came from.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the block.</exception>
    public static int Untranspose(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BlockSize);
        return TransposeInverse[index];
    }

    /// <summary><c>output[i] = input[transpose(i)]</c> over a whole 1024-element block.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="input">Exactly 1024 elements.</param>
    /// <param name="output">Exactly 1024 elements.</param>
    /// <exception cref="ArgumentException">Either span is not exactly 1024 elements.</exception>
    public static void TransposeBlock<T>(ReadOnlySpan<T> input, Span<T> output)
        where T : unmanaged
    {
        RequireBlock(input.Length, output.Length);
        ReadOnlySpan<int> table = TransposeForward;
        for (int i = 0; i < BlockSize; i++)
        {
            output[i] = input[table[i]];
        }
    }

    /// <summary><c>output[transpose(i)] = input[i]</c> over a whole 1024-element block.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="input">Exactly 1024 elements.</param>
    /// <param name="output">Exactly 1024 elements.</param>
    /// <exception cref="ArgumentException">Either span is not exactly 1024 elements.</exception>
    public static void UntransposeBlock<T>(ReadOnlySpan<T> input, Span<T> output)
        where T : unmanaged
    {
        RequireBlock(input.Length, output.Length);
        ReadOnlySpan<int> table = TransposeForward;
        for (int i = 0; i < BlockSize; i++)
        {
            output[table[i]] = input[i];
        }
    }

    /// <summary>
    /// The bit-packing index table for an element width: entry <c>row * lanes + lane</c> is the
    /// logical position the kernel touches at that <c>(row, lane)</c>.
    /// </summary>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is not one of the four.</exception>
    public static ReadOnlySpan<int> PackedIndexTable(int elementBits) => elementBits switch
    {
        8 => PackedIndex8,
        16 => PackedIndex16,
        32 => PackedIndex32,
        64 => PackedIndex64,
        _ => ThrowWidth<int[]>(elementBits),
    };

    /// <summary>The inverse table: the FastLanes <c>row</c> a logical index belongs to.</summary>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is not one of the four.</exception>
    public static ReadOnlySpan<int> PackedRowTable(int elementBits) => elementBits switch
    {
        8 => PackedRow8,
        16 => PackedRow16,
        32 => PackedRow32,
        64 => PackedRow64,
        _ => ThrowWidth<int[]>(elementBits),
    };

    /// <summary>The inverse table: the FastLanes <c>lane</c> a logical index belongs to.</summary>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width is not one of the four.</exception>
    public static ReadOnlySpan<int> PackedLaneTable(int elementBits) => elementBits switch
    {
        8 => PackedLane8,
        16 => PackedLane16,
        32 => PackedLane32,
        64 => PackedLane64,
        _ => ThrowWidth<int[]>(elementBits),
    };

    /// <summary>
    /// Bytes one 1024-element block of <paramref name="bitWidth"/>-bit values occupies.
    /// <c>128 * bit_width</c>, independent of the element width
    /// (vortex-fastlanes-0.86.1/src/bitpacking/array/mod.rs <c>validate</c>).
    /// </summary>
    /// <param name="bitWidth">0..64.</param>
    public static int BlockByteLength(int bitWidth) => BytesPerBlockPerBit * bitWidth;

    /// <summary>
    /// Unpacks one 1024-element FastLanes block. Transcribed from the
    /// <c>unpack!</c> macro of fastlanes-0.7.2/src/macros.rs, whose iteration order is the wire
    /// contract - the crate warns it is deliberately not the FastLanes paper's order.
    /// </summary>
    /// <typeparam name="T">The unsigned element type: byte, ushort, uint or ulong.</typeparam>
    /// <param name="packed">Exactly <c>1024 * bitWidth / (8 * sizeof(T))</c> elements.</param>
    /// <param name="bitWidth">Bits per packed value, in <c>[0, 8 * sizeof(T)]</c>.</param>
    /// <param name="output">Exactly 1024 elements; every one is written.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bit width exceeds the element width.</exception>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    public static void UnpackBlock<T>(ReadOnlySpan<T> packed, int bitWidth, Span<T> output)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        ArgumentOutOfRangeException.ThrowIfNegative(bitWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitWidth, elementBits);

        if (output.Length != BlockSize)
        {
            throw new ArgumentException(
                $"A FastLanes block unpacks to exactly {BlockSize} elements, not {output.Length}.",
                nameof(output));
        }

        int lanes = BlockSize / elementBits;
        if (packed.Length != lanes * bitWidth)
        {
            throw new ArgumentException(
                $"A {bitWidth}-bit block of {elementBits}-bit elements is {lanes * bitWidth} " +
                $"words, not {packed.Length}.",
                nameof(packed));
        }

        // W == 0 packs nothing at all: every value is zero (macros.rs, "Special case for W=0").
        if (bitWidth == 0)
        {
            output.Clear();
            return;
        }

        ReadOnlySpan<int> index = PackedIndexTable(elementBits);

        // W == T copies straight through: packed[LANES * row + lane] is the value.
        if (bitWidth == elementBits)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                for (int row = 0; row < elementBits; row++)
                {
                    output[index[(row * lanes) + lane]] = packed[(lanes * row) + lane];
                }
            }

            return;
        }

        for (int lane = 0; lane < lanes; lane++)
        {
            T src = packed[lane];
            for (int row = 0; row < elementBits; row++)
            {
                int currentWord = row * bitWidth / elementBits;
                int nextWord = ((row + 1) * bitWidth) / elementBits;
                int shift = (row * bitWidth) % elementBits;

                T value;
                if (nextWord > currentWord)
                {
                    int remainingBits = ((row + 1) * bitWidth) % elementBits;
                    int currentBits = bitWidth - remainingBits;
                    value = (src >> shift) & Mask<T>(currentBits);

                    // The guard is on the WORD index, not the row: the last row of a lane can
                    // spill exactly onto the boundary, in which case remainingBits is 0 and there
                    // is no next word to read.
                    if (nextWord < bitWidth)
                    {
                        src = packed[(lanes * nextWord) + lane];
                        value |= (src & Mask<T>(remainingBits)) << currentBits;
                    }
                }
                else
                {
                    value = (src >> shift) & Mask<T>(bitWidth);
                }

                output[index[(row * lanes) + lane]] = value;
            }
        }
    }

    // Every shift below is strictly less than the element width - the W == T and W == 0 cases are
    // branched out above - so this never has to reason about C#'s shift-count masking.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Mask<T>(int width)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> =>
        (T.One << width) - T.One;

    private static int[] BuildTranspose()
    {
        int[] table = new int[BlockSize];
        ReadOnlySpan<byte> order = OrderBytes;
        for (int i = 0; i < BlockSize; i++)
        {
            int lane = i % 16;
            int o = (i / 16) % 8;
            int row = i / 128;
            table[i] = (lane * 64) + (order[o] * 8) + row;
        }

        return table;
    }

    private static int[] BuildTransposeInverse(int[] forward)
    {
        int[] table = new int[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            table[forward[i]] = i;
        }

        return table;
    }

    private static int[] BuildPackedIndex(int elementBits)
    {
        int lanes = BlockSize / elementBits;
        int[] table = new int[BlockSize];
        ReadOnlySpan<byte> order = OrderBytes;
        for (int row = 0; row < elementBits; row++)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                table[(row * lanes) + lane] = (order[row / 8] * 16) + ((row % 8) * 128) + lane;
            }
        }

        return table;
    }

    // spec/REFERENCE.md's inverse derivation, kept as its own computation rather than as a scan of
    // the forward table so the two can be cross-checked against each other in the tests.
    private static int[] BuildPackedRows(int elementBits)
    {
        int lanes = BlockSize / elementBits;
        int[] table = new int[BlockSize];
        ReadOnlySpan<byte> order = OrderBytes;
        for (int i = 0; i < BlockSize; i++)
        {
            int lane = i % lanes;
            int s = i / 128;
            int flOrder = (i - (s * 128) - lane) / 16;
            table[i] = (order[flOrder] * 8) + s;
        }

        return table;
    }

    private static int[] BuildPackedLanes(int elementBits)
    {
        int lanes = BlockSize / elementBits;
        int[] table = new int[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            table[i] = i % lanes;
        }

        return table;
    }

    private static void RequireBlock(int inputLength, int outputLength)
    {
        if (inputLength != BlockSize)
        {
            throw new ArgumentException(
                $"A FastLanes block is exactly {BlockSize} elements, not {inputLength}.", "input");
        }

        if (outputLength != BlockSize)
        {
            throw new ArgumentException(
                $"A FastLanes block is exactly {BlockSize} elements, not {outputLength}.", "output");
        }
    }

    private static T ThrowWidth<T>(int elementBits) =>
        throw new ArgumentOutOfRangeException(
            nameof(elementBits), elementBits, "FastLanes element widths are 8, 16, 32 and 64.");
}
