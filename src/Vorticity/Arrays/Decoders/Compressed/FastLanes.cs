using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// The FastLanes 1024-element block permutations and the bit-packing kernels. Two different
/// permutations live here: the bit-packing index
/// <c>index(row, lane) = order[row / 8] * 16 + (row % 8) * 128 + lane</c>, over
/// <c>1024 / elementBits</c> lanes, and the element transposition
/// <c>transpose(i) = (i % 16) * 64 + order[(i / 16) % 8] * 8 + i / 128</c>, whose lane is
/// <c>i % 16</c> whatever the element width. Only the eight-entry order table is its own inverse;
/// the transposition is not, so untransposing is a separate mapping and never a second
/// transposition, a confusion that round-trips perfectly while producing a file no other
/// implementation can read.
/// </summary>
/// <remarks>
/// That <c>lane</c> is the vector lane is the point of the layout rather than an accident: for a
/// fixed row the packed words a kernel reads are consecutive in lane, and the positions it writes
/// are consecutive and aligned to the lane count at each of the four element widths, so both ends
/// are plain contiguous loads and stores with no gather anywhere. The index function is a
/// permutation, so every element is still touched exactly once whichever order the loops run in,
/// which is what lets the vector form interchange them. The scalar path stays reachable, and its
/// tests run again with hardware intrinsics disabled, so it is exercised rather than merely
/// present.
/// </remarks>
internal static partial class FastLanes
{
    /// <summary>Elements in one FastLanes block.</summary>
    public const int BlockSize = 1024;

    /// <summary>Bytes one 1024-element block occupies at one bit of width: <c>128 * bit_width</c>.</summary>
    public const int BytesPerBlockPerBit = 128;

    // Its own inverse: order[order[i]] == i.
    private static ReadOnlySpan<byte> OrderBytes => [0, 4, 2, 6, 1, 5, 3, 7];

    private static readonly int[] TransposeForward = BuildTranspose();
    private static readonly int[] TransposeInverse = BuildTransposeInverse(TransposeForward);

    // One 1024-entry table per element width.
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
    /// The element transposition:
    /// <c>lane * 64 + order[o] * 8 + row</c> with <c>lane = idx % 16</c>,
    /// <c>o = (idx / 16) % 8</c> and <c>row = idx / 128</c>.
    /// </summary>
    /// <param name="index">A logical index in <c>[0, 1024)</c>.</param>
    /// <returns>The transposed index.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the block.</exception>
    /// <remarks>
    /// Test-facing. Nothing decodes through this: a kernel that needs the permutation walks
    /// <see cref="UntransposeTable"/>, which is the same table without the two argument checks and
    /// the bounds check this pays per element. What this is for is checking the table against the
    /// formula, one index at a time, which is a thing a test does and a decoder never does.
    /// </remarks>
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
    /// <remarks>Test-facing, like <see cref="Transpose"/>; production reads
    /// <see cref="UntransposeTable"/>.</remarks>
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
    /// <remarks>
    /// Test-facing, and the round trip is what it is for: transposing a block and untransposing it
    /// must give the block back, which is the property the two tables have to hold jointly. No
    /// decoder transposes a block on its own -- the permutation is applied while the values are
    /// being unpacked, through <see cref="UntransposeTable"/>, so that the block is walked once.
    /// </remarks>
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

    /// <summary>
    /// The inverse permutation as a span, for a kernel that walks it and has already established
    /// its indices. <c>Untranspose(i) == UntransposeTable[i]</c>, without the two argument checks
    /// and the bounds check that the by-index accessor pays per element.
    /// </summary>
    internal static ReadOnlySpan<int> UntransposeTable => TransposeInverse;

    /// <summary><c>output[transpose(i)] = input[i]</c> over a whole 1024-element block.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="input">Exactly 1024 elements.</param>
    /// <param name="output">Exactly 1024 elements.</param>
    /// <exception cref="ArgumentException">Either span is not exactly 1024 elements.</exception>
    /// <remarks>Test-facing, like <see cref="TransposeBlock{T}"/>; production reads
    /// <see cref="UntransposeTable"/> while it unpacks.</remarks>
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
    /// <c>128 * bit_width</c>, independent of the element width.
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
    /// at a time, because the two index functions are easy to conflate and a pack and unpack pair
    /// written the same wrong way round-trips perfectly while producing a file no other
    /// implementation can read. The tests assert known values, not a round trip.
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

        // The loops are nested row-outer. Everything the body computes from `row` -- the two word
        // indices, the shift, the spill width -- does not depend on `lane`, and the mask depends on
        // neither; nested lane-outer, all of them would be recomputed for every element of a block,
        // two integer divisions and two modulos among them. Hoisted, the inner loop is a table
        // read, an and, a shift and an or.
        //
        // The reordering is exact, not an approximation: each (lane, row) pair ors into
        // `packed[lanes * word + lane]`, the writes for one row are disjoint across lanes, and `|=`
        // over rows is commutative, so the same bits land in the same words in any order. The
        // access pattern improves as a side effect: consecutive lanes are consecutive words, where
        // lane-outer strode by `lanes` on both the read and the write.
        if (bitWidth == elementBits)
        {
            for (int row = 0; row < elementBits; row++)
            {
                ReadOnlySpan<int> rowIndex = index.Slice(row * lanes, lanes);
                Span<T> target = packed.Slice(lanes * row, lanes);
                for (int lane = 0; lane < lanes; lane++)
                {
                    target[lane] = values[rowIndex[lane]];
                }
            }

            return;
        }

        // The vector form is the unpack's, run backwards, and it rests on the same contiguity: for
        // a fixed row the values it reads are `index(row, lane)` for consecutive lane -- contiguous
        // and aligned to `lanes` -- and the words it writes are `packed[lanes * word + lane]`, also
        // consecutive. So both ends are plain loads and stores.
        // Where the unpack stores, this ORs, because two rows can reach the same word: a row's
        // spill lands in the word the next row starts in. The buffer was cleared above, the rows
        // run in order, and `|=` is commutative, so the same bits land in the same words.
        if (Vectorizable<T>() && Vector128.IsHardwareAccelerated)
        {
            PackVectorized(values, ShapesOf<T>(bitWidth), packed, lanes, bitWidth);
            return;
        }

        T mask = Mask<T>(bitWidth);
        for (int row = 0; row < elementBits; row++)
        {
            int currentWord = row * bitWidth / elementBits;
            int nextWord = ((row + 1) * bitWidth) / elementBits;
            int shift = (row * bitWidth) % elementBits;
            int currentBits = bitWidth - (((row + 1) * bitWidth) % elementBits);

            // Same guard as the unpack, for the same reason: the last row of a lane can land
            // exactly on the boundary, leaving no next word to spill into.
            bool spills = nextWord > currentWord && nextWord < bitWidth;

            ReadOnlySpan<int> rowIndex = index.Slice(row * lanes, lanes);
            Span<T> current = packed.Slice(lanes * currentWord, lanes);
            Span<T> next = spills ? packed.Slice(lanes * nextWord, lanes) : default;

            for (int lane = 0; lane < lanes; lane++)
            {
                T value = values[rowIndex[lane]] & mask;
                current[lane] |= value << shift;
                if (spills)
                {
                    next[lane] |= value >> currentBits;
                }
            }
        }
    }

    /// <summary>
    /// Unpacks one 1024-element FastLanes block. The iteration order is the wire contract, and is
    /// deliberately not the order the FastLanes paper describes.
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

        // A zero bit width packs nothing at all: every value is zero.
        if (bitWidth == 0)
        {
            output.Clear();
            return;
        }

        ReadOnlySpan<int> index = PackedIndexTable(elementBits);

        // A bit width equal to the element width copies straight through: `packed[lanes * row +
        // lane]` is the value. It is a block copy rather than a gather, because for a fixed row the
        // destination `order[row / 8] * 16 + (row % 8) * 128 + lane` runs contiguously in `lane`,
        // exactly as the source does, so one copy per row moves what an index table would move one
        // bounds-checked element at a time.
        if (bitWidth == elementBits)
        {
            ReadOnlySpan<byte> order = OrderBytes;
            for (int row = 0; row < elementBits; row++)
            {
                int destination = (order[row / 8] * 16) + ((row % 8) * 128);
                packed.Slice(lanes * row, lanes).CopyTo(output.Slice(destination, lanes));
            }

            return;
        }

        if (Vectorizable<T>() && Vector128.IsHardwareAccelerated)
        {
            UnpackVectorized(packed, bitWidth, output, lanes, 1);
            return;
        }

        UnpackBlockScalar(packed, bitWidth, output, index, elementBits, lanes);
    }

    /// <summary>
    /// Unpacks a run of consecutive blocks into consecutive output, with what depends on the bit
    /// width laid out once for the whole run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every block of a node shares one bit width, so the steps the kernel takes are the same for
    /// all of them and are built once per width rather than once per block.
    /// </para>
    /// <para>
    /// The block stays the outer loop: a block's output is a few kilobytes, written whole while it
    /// sits in the first-level cache, where a loop over rows or words outside the blocks would sweep
    /// the whole output once per row.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">Exactly <c>blocks * lanes * bitWidth</c> words.</param>
    /// <param name="bitWidth">Bits per packed value, in <c>[0, 8 * sizeof(T)]</c>.</param>
    /// <param name="output">Exactly <c>blocks * 1024</c> elements; every one is written.</param>
    /// <param name="blocks">How many consecutive blocks to unpack.</param>
    internal static void UnpackBlocks<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = BlockSize / elementBits;
        int wordsPerBlock = lanes * bitWidth;

        // A zero bit width packs nothing at all: the whole run is zeros, and the output is
        // contiguous, so it is one clear rather than one per block.
        if (bitWidth == 0)
        {
            output[..(blocks * BlockSize)].Clear();
            return;
        }

        // The remaining degenerate width and the platforms without vectors keep the per-block
        // form: neither has a row shape to hoist.
        if (bitWidth == elementBits || !Vectorizable<T>() || !Vector128.IsHardwareAccelerated)
        {
            for (int block = 0; block < blocks; block++)
            {
                UnpackBlock(
                    packed.Slice(block * wordsPerBlock, wordsPerBlock),
                    bitWidth,
                    output.Slice(block * BlockSize, BlockSize));
            }

            return;
        }

        UnpackVectorized(packed, bitWidth, output, lanes, blocks);
    }

    /// <summary>Runs the vector kernel this machine is fastest with over a run of whole blocks.</summary>
    /// <remarks>
    /// The word-at-a-time kernel is written over 128-bit vectors; where wider ones are accelerated,
    /// the row-at-a-time kernel runs at their width instead.
    /// </remarks>
    private static void UnpackVectorized<T>(
        ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int lanes, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        // `lanes` is 16, 32, 64 or 128 and every vector width divides all four, so the loops of the
        // row-at-a-time kernels never need a remainder tail.
        if (Vector512.IsHardwareAccelerated)
        {
            Unpack512(packed, ShapesOf<T>(bitWidth), output, lanes, lanes * bitWidth, blocks);
            return;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unpack256(packed, ShapesOf<T>(bitWidth), output, lanes, lanes * bitWidth, blocks);
            return;
        }

        UnpackWords(
            ref MemoryMarshal.GetReference(packed), bitWidth, ref MemoryMarshal.GetReference(output), blocks);
    }

    /// <summary>
    /// Rows of a block from which unpacking it whole and reading them out of it beats reading each
    /// one where it lies.
    /// </summary>
    /// <remarks>
    /// Measured on 64 columns of 12-bit values, a filter keeping a share of every block: at 51 rows
    /// a block, the reads one by one take 45 % less time than the whole blocks, at 102 rows 23 %
    /// less; at 154 the two tie; at 205 rows the whole blocks take 14 % less, at 256 rows 27 % less.
    /// </remarks>
    internal const int WholeBlockRows = 128;

    /// <summary>
    /// The values at <paramref name="wanted"/> rows of a run of packed blocks, each block read the
    /// way its share of the rows pays for: unpacked whole into a scratch block that stays in the
    /// first-level cache when it holds <see cref="WholeBlockRows"/> of them or more, read a value
    /// at a time where each lies when it holds fewer, and not touched when it holds none.
    /// </summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">The packed blocks.</param>
    /// <param name="bitWidth">Bits per value, strictly between 0 and the element width.</param>
    /// <param name="offset">The row of the first block that is row 0 of <paramref name="wanted"/>.</param>
    /// <param name="wanted">The rows, in any order; rows of one block next to each other read it once.</param>
    /// <param name="destination">One value per wanted row.</param>
    /// <remarks>
    /// The scratch block is written whole before it is read and the reads one by one never touch
    /// it, so it is not zeroed: a take of a row or two a block would clear its 8 KiB for nothing at
    /// every call.
    /// </remarks>
    [SkipLocalsInit]
    internal static void GatherRows<T>(
        ReadOnlySpan<T> packed, int bitWidth, int offset, ReadOnlySpan<int> wanted, Span<T> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = BlockSize / elementBits;
        int wordsPerBlock = lanes * bitWidth;
        ReadOnlySpan<int> rowOf = PackedRowTable(elementBits);
        ReadOnlySpan<int> laneOf = PackedLaneTable(elementBits);
        bool vectorized = Vectorizable<T>() && Vector128.IsHardwareAccelerated;
        Span<T> scratch = stackalloc T[BlockSize];

        // One pass over the rows: whether a block is read whole is judged on the row a block's worth
        // of rows ahead, which in an ascending selection is in the same block exactly when the block
        // holds that many; each loop then runs while its rows stay in the block, whatever the order.
        int i = 0;
        while (i < wanted.Length)
        {
            int block = (wanted[i] + offset) / BlockSize;
            int first = (block * BlockSize) - offset;
            ReadOnlySpan<T> source = packed.Slice(block * wordsPerBlock, wordsPerBlock);
            int ahead = i + WholeBlockRows - 1;
            if (ahead < wanted.Length && (uint)(wanted[ahead] - first) < BlockSize)
            {
                if (vectorized)
                {
                    UnpackVectorized(source, bitWidth, scratch, lanes, 1);
                }
                else
                {
                    UnpackBlock(source, bitWidth, scratch);
                }

                i = FromBlock(scratch, wanted, i, first, destination);
            }
            else
            {
                i = OneByOne(source, bitWidth, rowOf, laneOf, wanted, i, first, destination);
            }
        }
    }

    /// <summary>
    /// The wanted rows of an unpacked block, from row <paramref name="i"/> while they stay in it.
    /// </summary>
    /// <returns>The first row past the block's.</returns>
    /// <remarks>
    /// A method of its own, so the loop has registers of its own: inlined into a body that also
    /// calls the unpack, its spans would be reloaded from the stack at every row.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FromBlock<T>(
        ReadOnlySpan<T> block, ReadOnlySpan<int> wanted, int i, int first, Span<T> destination)
        where T : unmanaged
    {
        for (; i < wanted.Length; i++)
        {
            uint within = (uint)(wanted[i] - first);
            if (within >= (uint)block.Length)
            {
                break;
            }

            destination[i] = block[(int)within];
        }

        return i;
    }

    /// <summary>
    /// The wanted rows of a packed block, each read where it lies, from row <paramref name="i"/>
    /// while they stay in it.
    /// </summary>
    /// <returns>The first row past the block's.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OneByOne<T>(
        ReadOnlySpan<T> packed, int bitWidth, ReadOnlySpan<int> rowOf, ReadOnlySpan<int> laneOf,
        ReadOnlySpan<int> wanted, int i, int first, Span<T> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        for (; i < wanted.Length; i++)
        {
            uint within = (uint)(wanted[i] - first);
            if (within >= BlockSize)
            {
                break;
            }

            destination[i] = UnpackAt(packed, bitWidth, rowOf[(int)within], laneOf[(int)within]);
        }

        return i;
    }

    /// <summary>The row shapes of one bit width, built at the first use of that width and kept for the process.</summary>
    /// <remarks>
    /// A shape depends on the element width and the bit width alone, and building one is a few
    /// divisions a row: a decode taking a block at a time would rebuild them per block.
    /// </remarks>
    private static Row<T>[] ShapesOf<T>(int bitWidth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        Row<T>[]?[] widths = Volatile.Read(ref Shapes<T>.ByWidth)
            ?? Interlocked.CompareExchange(ref Shapes<T>.ByWidth, new Row<T>[]?[elementBits + 1], null)
            ?? Shapes<T>.ByWidth!;
        Row<T>[]? shapes = Volatile.Read(ref widths[bitWidth]);
        if (shapes is null)
        {
            shapes = new Row<T>[elementBits];
            BuildRows(bitWidth, elementBits, shapes);
            Volatile.Write(ref widths[bitWidth], shapes);
        }

        return shapes;
    }

    /// <summary>The row shapes of each bit width of one element type; no initializer, so no static constructor.</summary>
    private static class Shapes<T>
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        internal static Row<T>[]?[]? ByWidth;
    }

    /// <summary>Fills the per-row shapes for one bit width.</summary>
    private static void BuildRows<T>(int bitWidth, int elementBits, Span<Row<T>> shapes)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        for (int row = 0; row < elementBits; row++)
        {
            shapes[row] = new Row<T>(row, bitWidth, elementBits);
        }
    }

    /// <summary>
    /// The scalar unpack loop, reachable on its own: it is the fallback the vector paths need, and
    /// what they are compared against value for value.
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

                    // The guard is on the word index, not the row: the last row of a lane can
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
    /// Unpacks one value out of a 1024-element block, without touching the other 1023.
    /// </summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">Exactly <c>lanes * bitWidth</c> words: one block.</param>
    /// <param name="bitWidth">Bits per packed value, in <c>(0, sizeof(T) * 8)</c>.</param>
    /// <param name="index">A logical index in <c>[0, 1024)</c>.</param>
    /// <returns>The value at that index.</returns>
    /// <remarks>
    /// This is what makes `fastlanes.bitpacked` worth specializing for a take, and it is a property
    /// of the layout rather than a trick: the bit-packing index is invertible in closed form, so a
    /// logical index maps straight to the (row, lane) pair holding it -- `PackedRowTable` and
    /// `PackedLaneTable` are that inverse, precomputed -- and from there the same shift and mask
    /// the bulk kernel uses extracts the one value. No block is unpacked and nothing is allocated.
    ///
    /// The two degenerate widths are the caller's to handle: a zero bit width means every value is
    /// zero, and a bit width equal to the element width means the packed word is the value.
    /// Neither reaches the arithmetic below.
    /// </remarks>
    public static T UnpackOne<T>(ReadOnlySpan<T> packed, int bitWidth, int index)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        return UnpackAt(packed, bitWidth, PackedRowTable(elementBits)[index], PackedLaneTable(elementBits)[index]);
    }

    /// <summary>The value at <c>(row, lane)</c> of one packed block.</summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">Exactly <c>lanes * bitWidth</c> words: one block.</param>
    /// <param name="bitWidth">Bits per packed value, in <c>(0, sizeof(T) * 8)</c>.</param>
    /// <param name="row">The value's row, from <see cref="PackedRowTable"/>.</param>
    /// <param name="lane">The value's lane, from <see cref="PackedLaneTable"/>.</param>
    /// <returns>The value.</returns>
    /// <remarks>For a caller taking many values, which looks the two tables up once rather than once a value.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T UnpackAt<T>(ReadOnlySpan<T> packed, int bitWidth, int row, int lane)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = BlockSize / elementBits;
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

        // The guard is on the word index, not the row: the last row of a lane can spill exactly
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
    /// divisions and two masks per value, and every one of them is constant across the 16 to 128
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

                // The guard is on the word index, not the row: the last row of a lane can spill
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

    /// <summary>Runs whichever vector width is available over one block's rows.</summary>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="values">The block's 1024 values.</param>
    /// <param name="shapes">One shape per row, as the unpack builds them.</param>
    /// <param name="packed">The cleared destination.</param>
    /// <param name="lanes">Elements per row.</param>
    /// <param name="bitWidth">Bits per packed value, strictly between 0 and the element width.</param>
    private static void PackVectorized<T>(
        ReadOnlySpan<T> values, ReadOnlySpan<Row<T>> shapes, Span<T> packed, int lanes, int bitWidth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        if (Vector512.IsHardwareAccelerated && lanes >= Vector512<T>.Count)
        {
            Pack512(values, shapes, packed, lanes, bitWidth);
            return;
        }

        if (Vector256.IsHardwareAccelerated && lanes >= Vector256<T>.Count)
        {
            Pack256(values, shapes, packed, lanes, bitWidth);
            return;
        }

        Pack128(values, shapes, packed, lanes, bitWidth);
    }

    private static void Pack512<T>(
        ReadOnlySpan<T> values, ReadOnlySpan<Row<T>> shapes, Span<T> packed, int lanes, int bitWidth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(values);
        ref T destination = ref MemoryMarshal.GetReference(packed);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        Vector512<T> mask = Vector512.Create(Mask<T>(bitWidth));
        int rows = shapes.Length;
        int step = Vector512<T>.Count;

        for (int row = 0; row < rows; row++)
        {
            ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
            nuint from = (nuint)shape.Destination;
            nuint current = (nuint)(lanes * shape.CurrentWord);
            nuint next = (nuint)(lanes * shape.NextWord);
            int shift = shape.Shift;
            int currentBits = shape.CurrentBits;

            // The spill test is a property of the row, exactly as on the unpack side.
            if (shape.Spills)
            {
                for (int lane = 0; lane < lanes; lane += step)
                {
                    nuint at = (nuint)lane;
                    Vector512<T> value = Vector512.LoadUnsafe(ref source, from + at) & mask;
                    (Vector512.LoadUnsafe(ref destination, current + at) | ShiftLeft(value, shift))
                        .StoreUnsafe(ref destination, current + at);
                    (Vector512.LoadUnsafe(ref destination, next + at) | ShiftRight(value, currentBits))
                        .StoreUnsafe(ref destination, next + at);
                }

                continue;
            }

            for (int lane = 0; lane < lanes; lane += step)
            {
                nuint at = (nuint)lane;
                Vector512<T> value = Vector512.LoadUnsafe(ref source, from + at) & mask;
                (Vector512.LoadUnsafe(ref destination, current + at) | ShiftLeft(value, shift))
                    .StoreUnsafe(ref destination, current + at);
            }
        }
    }

    private static void Pack256<T>(
        ReadOnlySpan<T> values, ReadOnlySpan<Row<T>> shapes, Span<T> packed, int lanes, int bitWidth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(values);
        ref T destination = ref MemoryMarshal.GetReference(packed);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        Vector256<T> mask = Vector256.Create(Mask<T>(bitWidth));
        int rows = shapes.Length;
        int step = Vector256<T>.Count;

        for (int row = 0; row < rows; row++)
        {
            ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
            nuint from = (nuint)shape.Destination;
            nuint current = (nuint)(lanes * shape.CurrentWord);
            nuint next = (nuint)(lanes * shape.NextWord);
            int shift = shape.Shift;
            int currentBits = shape.CurrentBits;

            if (shape.Spills)
            {
                for (int lane = 0; lane < lanes; lane += step)
                {
                    nuint at = (nuint)lane;
                    Vector256<T> value = Vector256.LoadUnsafe(ref source, from + at) & mask;
                    (Vector256.LoadUnsafe(ref destination, current + at) | ShiftLeft(value, shift))
                        .StoreUnsafe(ref destination, current + at);
                    (Vector256.LoadUnsafe(ref destination, next + at) | ShiftRight(value, currentBits))
                        .StoreUnsafe(ref destination, next + at);
                }

                continue;
            }

            for (int lane = 0; lane < lanes; lane += step)
            {
                nuint at = (nuint)lane;
                Vector256<T> value = Vector256.LoadUnsafe(ref source, from + at) & mask;
                (Vector256.LoadUnsafe(ref destination, current + at) | ShiftLeft(value, shift))
                    .StoreUnsafe(ref destination, current + at);
            }
        }
    }

    private static void Pack128<T>(
        ReadOnlySpan<T> values, ReadOnlySpan<Row<T>> shapes, Span<T> packed, int lanes, int bitWidth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(values);
        ref T destination = ref MemoryMarshal.GetReference(packed);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        Vector128<T> mask = Vector128.Create(Mask<T>(bitWidth));
        int rows = shapes.Length;
        int step = Vector128<T>.Count;

        for (int row = 0; row < rows; row++)
        {
            ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
            nuint from = (nuint)shape.Destination;
            nuint current = (nuint)(lanes * shape.CurrentWord);
            nuint next = (nuint)(lanes * shape.NextWord);
            int shift = shape.Shift;
            int currentBits = shape.CurrentBits;

            if (shape.Spills)
            {
                for (int lane = 0; lane < lanes; lane += step)
                {
                    nuint at = (nuint)lane;
                    Vector128<T> value = Vector128.LoadUnsafe(ref source, from + at) & mask;
                    (Vector128.LoadUnsafe(ref destination, current + at) | ShiftLeft(value, shift))
                        .StoreUnsafe(ref destination, current + at);
                    (Vector128.LoadUnsafe(ref destination, next + at) | ShiftRight(value, currentBits))
                        .StoreUnsafe(ref destination, next + at);
                }

                continue;
            }

            for (int lane = 0; lane < lanes; lane += step)
            {
                nuint at = (nuint)lane;
                Vector128<T> value = Vector128.LoadUnsafe(ref source, from + at) & mask;
                (Vector128.LoadUnsafe(ref destination, current + at) | ShiftLeft(value, shift))
                    .StoreUnsafe(ref destination, current + at);
            }
        }
    }

    private static void Unpack512<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        int rows = shapes.Length;
        int step = Vector512<T>.Count;

        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            nuint blockDestination = (nuint)((nint)block * BlockSize);

            for (int row = 0; row < rows; row++)
            {
                ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
                Vector512<T> low = Vector512.Create(shape.LowMask);
                Vector512<T> high = Vector512.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                nuint into = blockDestination + (nuint)shape.Destination;
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                // Whether a row straddles two packed words is the same for all its lanes, so the
                // test is hoisted out of the lane loop.
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector512<T> value = ShiftRight(
                            Vector512.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                        value |= ShiftLeft(
                            Vector512.LoadUnsafe(ref source, next + (nuint)lane) & high, currentBits);
                        value.StoreUnsafe(ref destination, into + (nuint)lane);
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector512<T> value = ShiftRight(
                        Vector512.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                    value.StoreUnsafe(ref destination, into + (nuint)lane);
                }
            }
        }
    }

    private static void Unpack256<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        int rows = shapes.Length;
        int step = Vector256<T>.Count;

        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            nuint blockDestination = (nuint)((nint)block * BlockSize);

            for (int row = 0; row < rows; row++)
            {
                ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
                Vector256<T> low = Vector256.Create(shape.LowMask);
                Vector256<T> high = Vector256.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                nuint into = blockDestination + (nuint)shape.Destination;
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                // Whether a row straddles two packed words is the same for all its lanes, so the
                // test is hoisted out of the lane loop.
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector256<T> value = ShiftRight(
                            Vector256.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                        value |= ShiftLeft(
                            Vector256.LoadUnsafe(ref source, next + (nuint)lane) & high, currentBits);
                        value.StoreUnsafe(ref destination, into + (nuint)lane);
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector256<T> value = ShiftRight(
                        Vector256.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                    value.StoreUnsafe(ref destination, into + (nuint)lane);
                }
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

    // The inverse of the packing order, derived on its own rather than read off the forward table,
    // so the tests can check one against the other.
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
