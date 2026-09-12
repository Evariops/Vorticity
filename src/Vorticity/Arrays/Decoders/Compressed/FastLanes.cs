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
// THE UNPACK KERNEL IS VECTORIZED, and the reason it can be is the whole point of the FastLanes
// layout rather than an accident. `lane` is the SIMD lane: for a fixed `row`, the packed words the
// kernel reads are `packed[lanes * word + lane]` for consecutive lane, and the positions it writes
// are `index(row, lane) = FL_ORDER[row/8] * 16 + (row % 8) * 128 + lane` - also consecutive, and
// aligned to `lanes`, at every one of the four element widths. So both ends are plain contiguous
// loads and stores; there is no gather anywhere in it. The scalar loop iterates lane-major and
// carries a word across rows, so the vector form interchanges the loops and recomputes that word
// from `row` instead. The index function is a permutation, so every output element is still
// written exactly once whichever order the loops run in.
//
// docs/03-architecture.md §4 invariant 4: the scalar path stays, and CI runs the whole suite with
// DOTNET_EnableHWIntrinsic=0 so it is exercised rather than merely present.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    /// The inverse of <see cref="UnpackBlock{T}(ReadOnlySpan{T}, int, Span{T})"/>: packs one
    /// 1024-element block at <paramref name="bitWidth"/> bits per value.
    /// </summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="values">Exactly <see cref="BlockSize"/> values, each below 2^bitWidth.</param>
    /// <param name="bitWidth">Bits per value, in <c>[0, sizeof(T) * 8]</c>.</param>
    /// <param name="packed">Exactly <c>lanes * bitWidth</c> words; overwritten.</param>
    /// <remarks>
    /// Written as the literal inverse of the unpack loop rather than from the paper, one statement
    /// at a time, because the two index functions are easy to conflate and a pack/unpack pair
    /// written the same wrong way round-trips perfectly while producing a file no other
    /// implementation can read. The tests assert the crate's known-value table, not a round trip.
    /// </remarks>
    /// <exception cref="ArgumentException">A span is the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitWidth"/> is out of range.</exception>
    public static void PackBlock<T>(ReadOnlySpan<T> values, int bitWidth, Span<T> packed)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        ArgumentOutOfRangeException.ThrowIfNegative(bitWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitWidth, elementBits);

        if (values.Length != BlockSize)
        {
            throw new ArgumentException(
                $"A FastLanes block packs exactly {BlockSize} elements, not {values.Length}.",
                nameof(values));
        }

        int lanes = BlockSize / elementBits;
        if (packed.Length != lanes * bitWidth)
        {
            throw new ArgumentException(
                $"A {bitWidth}-bit block of {elementBits}-bit elements is {lanes * bitWidth} " +
                $"words, not {packed.Length}.",
                nameof(packed));
        }

        // W == 0 stores nothing: every value is known to be zero.
        if (bitWidth == 0)
        {
            return;
        }

        packed.Clear();
        ReadOnlySpan<int> index = PackedIndexTable(elementBits);

        if (bitWidth == elementBits)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                for (int row = 0; row < elementBits; row++)
                {
                    packed[(lanes * row) + lane] = values[index[(row * lanes) + lane]];
                }
            }

            return;
        }

        for (int lane = 0; lane < lanes; lane++)
        {
            for (int row = 0; row < elementBits; row++)
            {
                T value = values[index[(row * lanes) + lane]] & Mask<T>(bitWidth);
                int currentWord = row * bitWidth / elementBits;
                int nextWord = ((row + 1) * bitWidth) / elementBits;
                int shift = (row * bitWidth) % elementBits;

                packed[(lanes * currentWord) + lane] |= value << shift;

                if (nextWord > currentWord)
                {
                    int remainingBits = ((row + 1) * bitWidth) % elementBits;
                    int currentBits = bitWidth - remainingBits;

                    // Same guard as the unpack, for the same reason: the last row of a lane can
                    // land exactly on the boundary, leaving no next word to spill into.
                    if (nextWord < bitWidth)
                    {
                        packed[(lanes * nextWord) + lane] |= value >> currentBits;
                    }
                }
            }
        }
    }

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

        if (Vectorized(packed, bitWidth, output, elementBits, lanes))
        {
            return;
        }

        UnpackBlockScalar(packed, bitWidth, output, index, elementBits, lanes);
    }

    /// <summary>
    /// The scalar unpack loop, reachable on its own: it is the fallback the vector paths need, and
    /// what they are measured and differentially tested against.
    /// </summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">The packed words.</param>
    /// <param name="bitWidth">Bits per value, strictly between 0 and the element width.</param>
    /// <param name="output">Exactly 1024 elements.</param>
    /// <param name="index">The packing index table for the element width.</param>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    /// <param name="lanes">1024 / <paramref name="elementBits"/>.</param>
    internal static void UnpackBlockScalar<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, ReadOnlySpan<int> index,
        int elementBits, int lanes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
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

    /// <summary>
    /// Unpacks ONE value out of a 1024-element block, without touching the other 1023.
    /// </summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">Exactly <c>lanes * bitWidth</c> words: one block.</param>
    /// <param name="bitWidth">Bits per packed value, in <c>(0, sizeof(T) * 8)</c>.</param>
    /// <param name="index">A logical index in <c>[0, 1024)</c>.</param>
    /// <returns>The value at that index.</returns>
    /// <remarks>
    /// This is what makes `fastlanes.bitpacked` worth specializing for a take, and it is a property
    /// of the layout rather than a trick: the bit-packing index is INVERTIBLE in closed form, so a
    /// logical index maps straight to the (row, lane) pair holding it - `PackedRowTable` and
    /// `PackedLaneTable` are that inverse, precomputed - and from there the same shift and mask the
    /// bulk kernel uses extracts the one value. No block is unpacked and nothing is allocated.
    ///
    /// The two degenerate widths are the caller's to handle: W == 0 means every value is zero and
    /// W == T means the packed word IS the value, and neither reaches the arithmetic below.
    /// </remarks>
    public static T UnpackOne<T>(ReadOnlySpan<T> packed, int bitWidth, int index)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = BlockSize / elementBits;
        int row = PackedRowTable(elementBits)[index];
        int lane = PackedLaneTable(elementBits)[index];

        int currentWord = row * bitWidth / elementBits;
        int nextWord = ((row + 1) * bitWidth) / elementBits;
        int shift = (row * bitWidth) % elementBits;

        if (nextWord == currentWord)
        {
            return (packed[(lanes * currentWord) + lane] >> shift) & Mask<T>(bitWidth);
        }

        int remainingBits = ((row + 1) * bitWidth) % elementBits;
        int currentBits = bitWidth - remainingBits;
        T value = (packed[(lanes * currentWord) + lane] >> shift) & Mask<T>(currentBits);

        // The guard is on the WORD index, not the row: the last row of a lane can spill exactly
        // onto the boundary, leaving no next word to read.
        if (nextWord < bitWidth)
        {
            value |= (packed[(lanes * nextWord) + lane] & Mask<T>(remainingBits)) << currentBits;
        }

        return value;
    }

    /// <summary>
    /// The shape of one row of the unpack, with everything that does not depend on the lane.
    /// </summary>
    /// <remarks>
    /// Hoisting it out is most of the win even before the vectors: the scalar loop recomputes four
    /// divisions and two masks per VALUE, and every one of them is constant across the 16 to 128
    /// lanes of a row.
    /// </remarks>
    private readonly struct Row<T>
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        internal Row(int row, int bitWidth, int elementBits)
        {
            Destination = (Order[row / 8] * 16) + ((row % 8) * 128);
            CurrentWord = row * bitWidth / elementBits;
            Shift = (row * bitWidth) % elementBits;

            int nextWord = ((row + 1) * bitWidth) / elementBits;
            NextWord = nextWord;

            if (nextWord > CurrentWord)
            {
                int remainingBits = ((row + 1) * bitWidth) % elementBits;
                LowMask = Mask<T>(bitWidth - remainingBits);
                CurrentBits = bitWidth - remainingBits;

                // The guard is on the WORD index, not the row: the last row of a lane can spill
                // exactly onto the boundary, leaving no next word to read.
                Spills = nextWord < bitWidth;
                HighMask = Mask<T>(remainingBits);
            }
            else
            {
                LowMask = Mask<T>(bitWidth);
                CurrentBits = 0;
                Spills = false;
                HighMask = default;
            }
        }

        /// <summary>The first output position this row writes: the rest follow it contiguously.</summary>
        internal int Destination { get; }

        internal int CurrentWord { get; }

        internal int NextWord { get; }

        internal int Shift { get; }

        internal int CurrentBits { get; }

        internal T LowMask { get; }

        internal T HighMask { get; }

        internal bool Spills { get; }
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> is one of the four widths the vector types accept.
    /// </summary>
    /// <remarks>
    /// A type test rather than a size test, and it folds to a constant in each specialization:
    /// generics over value types are compiled per type, so the whole vector path disappears from
    /// an instantiation that cannot use it.
    /// </remarks>
    private static bool Vectorizable<T>() =>
        typeof(T) == typeof(byte) || typeof(T) == typeof(ushort)
        || typeof(T) == typeof(uint) || typeof(T) == typeof(ulong);

    /// <summary>Runs whichever vector width is available, or reports that none is.</summary>
    private static bool Vectorized<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int elementBits, int lanes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        if (!Vectorizable<T>())
        {
            return false;
        }

        // `lanes` is 16, 32, 64 or 128 and every vector width divides all four, so the loops below
        // never need a remainder tail.
        if (Vector512.IsHardwareAccelerated)
        {
            Unpack512(packed, bitWidth, output, elementBits, lanes);
            return true;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unpack256(packed, bitWidth, output, elementBits, lanes);
            return true;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            Unpack128(packed, bitWidth, output, elementBits, lanes);
            return true;
        }

        return false;
    }

    private static void Unpack512<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int elementBits, int lanes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        int step = Vector512<T>.Count;

        for (int row = 0; row < elementBits; row++)
        {
            Row<T> shape = new Row<T>(row, bitWidth, elementBits);
            Vector512<T> low = Vector512.Create(shape.LowMask);
            Vector512<T> high = Vector512.Create(shape.HighMask);
            nuint current = (nuint)(lanes * shape.CurrentWord);
            nuint next = (nuint)(lanes * shape.NextWord);
            nuint into = (nuint)shape.Destination;

            for (int lane = 0; lane < lanes; lane += step)
            {
                Vector512<T> value = ShiftRight(
                    Vector512.LoadUnsafe(ref source, current + (nuint)lane), shape.Shift) & low;

                if (shape.Spills)
                {
                    value |= ShiftLeft(
                        Vector512.LoadUnsafe(ref source, next + (nuint)lane) & high, shape.CurrentBits);
                }

                value.StoreUnsafe(ref destination, into + (nuint)lane);
            }
        }
    }

    private static void Unpack256<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int elementBits, int lanes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        int step = Vector256<T>.Count;

        for (int row = 0; row < elementBits; row++)
        {
            Row<T> shape = new Row<T>(row, bitWidth, elementBits);
            Vector256<T> low = Vector256.Create(shape.LowMask);
            Vector256<T> high = Vector256.Create(shape.HighMask);
            nuint current = (nuint)(lanes * shape.CurrentWord);
            nuint next = (nuint)(lanes * shape.NextWord);
            nuint into = (nuint)shape.Destination;

            for (int lane = 0; lane < lanes; lane += step)
            {
                Vector256<T> value = ShiftRight(
                    Vector256.LoadUnsafe(ref source, current + (nuint)lane), shape.Shift) & low;

                if (shape.Spills)
                {
                    value |= ShiftLeft(
                        Vector256.LoadUnsafe(ref source, next + (nuint)lane) & high, shape.CurrentBits);
                }

                value.StoreUnsafe(ref destination, into + (nuint)lane);
            }
        }
    }

    private static void Unpack128<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int elementBits, int lanes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        int step = Vector128<T>.Count;

        for (int row = 0; row < elementBits; row++)
        {
            Row<T> shape = new Row<T>(row, bitWidth, elementBits);
            Vector128<T> low = Vector128.Create(shape.LowMask);
            Vector128<T> high = Vector128.Create(shape.HighMask);
            nuint current = (nuint)(lanes * shape.CurrentWord);
            nuint next = (nuint)(lanes * shape.NextWord);
            nuint into = (nuint)shape.Destination;

            for (int lane = 0; lane < lanes; lane += step)
            {
                Vector128<T> value = ShiftRight(
                    Vector128.LoadUnsafe(ref source, current + (nuint)lane), shape.Shift) & low;

                if (shape.Spills)
                {
                    value |= ShiftLeft(
                        Vector128.LoadUnsafe(ref source, next + (nuint)lane) & high, shape.CurrentBits);
                }

                value.StoreUnsafe(ref destination, into + (nuint)lane);
            }
        }
    }

    // The vector shift intrinsics have one overload per element type and no generic form, so these
    // six dispatch on T. The tests fold away: a generic over a value type is compiled per type, so
    // `typeof(T) == typeof(byte)` is a JIT-time constant and only one arm survives in each
    // instantiation. It is the pattern the BCL uses for the same reason.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> ShiftRight<T>(Vector512<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector512.ShiftRightLogical(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector512.ShiftRightLogical(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector512.ShiftRightLogical(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector512.ShiftRightLogical(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> ShiftLeft<T>(Vector512<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector512.ShiftLeft(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector512.ShiftLeft(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector512.ShiftLeft(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector512.ShiftLeft(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> ShiftRight<T>(Vector256<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector256.ShiftRightLogical(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector256.ShiftRightLogical(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector256.ShiftRightLogical(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector256.ShiftRightLogical(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> ShiftLeft<T>(Vector256<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector256.ShiftLeft(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector256.ShiftLeft(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector256.ShiftLeft(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector256.ShiftLeft(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftRight<T>(Vector128<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector128.ShiftRightLogical(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector128.ShiftRightLogical(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector128.ShiftRightLogical(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector128.ShiftRightLogical(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftLeft<T>(Vector128<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector128.ShiftLeft(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector128.ShiftLeft(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector128.ShiftLeft(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector128.ShiftLeft(value.AsUInt64(), count).As<ulong, T>();
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
