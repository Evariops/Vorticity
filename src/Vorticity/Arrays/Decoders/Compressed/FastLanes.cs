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

    /// <summary>
    /// The inverse permutation as a span, for a kernel that walks it and has already established
    /// its indices. <c>Untranspose(i) == UntransposeTable[i]</c>, without the two argument checks
    /// and the bounds check that the by-index accessor pays PER ELEMENT.
    /// </summary>
    internal static ReadOnlySpan<int> UntransposeTable => TransposeInverse;

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

        // PERF-AUDIT-v2.md W-14. THE LOOPS ARE NESTED ROW-OUTER, AND THAT IS THE WHOLE CHANGE.
        // Everything the body computes from `row` -- the two word indices, the shift, the spill
        // width -- does not depend on `lane` at all, and the mask does not depend on either; nested
        // lane-outer, all five were recomputed for every one of the 1 024 elements of a block,
        // including two integer divisions and two modulos. Hoisted, the inner loop is a table read,
        // an AND, a shift and an OR.
        //
        // REORDERING IS EXACT, not an approximation: each (lane, row) pair ORs into
        // `packed[lanes * word + lane]`, the writes for one row are disjoint across lanes, and `|=`
        // over rows is commutative -- so the same bits land in the same words in any order. The
        // access pattern gets better as a side effect: consecutive lanes are consecutive words,
        // where lane-outer strode by `lanes` on both the read and the write.
        //
        // Measured by doubling the call on `--throughput --write` at a million rows: `PackBlock` is
        // **at least 6,8 %** of a `fastlanes_bitpacked` write (18 118 us against 19 350 doubled),
        // 1,5 % of `fastlanes_for` and nothing measurable on `primitive`. "At least" because a
        // doubling measures a floor -- §1.6, and W-7b is where that was learned.
        //
        // AND THE FLOOR WAS A FLOOR: vectorizing this took that write from 2 497 to 2 155 us and
        // 2 533 to 2 186, thirteen and fourteen per cent, twice. The loop below is what runs
        // without the intrinsics, and the suite runs under DOTNET_EnableHWIntrinsic=0 so it is
        // exercised rather than merely present.
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

        // THE VECTOR FORM IS THE UNPACK'S, RUN BACKWARDS, and it rests on the same contiguity proof
        // this file opens with: for a fixed row the values it reads are `index(row, lane)` for
        // consecutive lane -- contiguous and aligned to `lanes` -- and the words it writes are
        // `packed[lanes * word + lane]`, also consecutive. So both ends are plain loads and stores.
        // Where the unpack stores, this ORs, because two rows can reach the same word: a row's
        // spill lands in the word the next row starts in. The buffer was cleared above, the rows
        // run in order, and `|=` is commutative, so the same bits land in the same words.
        if (Vectorizable<T>() && Vector128.IsHardwareAccelerated)
        {
            Span<Row<T>> shapes = stackalloc Row<T>[elementBits];
            BuildRows(bitWidth, elementBits, shapes);
            PackVectorized(values, shapes, packed, lanes, bitWidth);
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

        // W == T copies straight through: packed[LANES * row + lane] is the value. AND IT IS A
        // BLOCK COPY, not a gather -- `BuildPackedIndex` writes
        // `table[row * lanes + lane] = Order[row / 8] * 16 + (row % 8) * 128 + lane`, so for a
        // fixed row the destination runs contiguously in `lane`, exactly as the source does. The
        // by-element form was 1024 bounds-checked loads through an index table to move what is
        // `elementBits` memcpys of 128 bytes. (PERF-AUDIT, §"FastLanes.UnpackBlock:280".)
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
            Span<Row<T>> shapes = stackalloc Row<T>[elementBits];
            BuildRows(bitWidth, elementBits, shapes);
            Vectorized(packed, shapes, output, lanes, lanes * bitWidth, 1);
            return;
        }

        UnpackBlockScalar(packed, bitWidth, output, index, elementBits, lanes);
    }

    /// <summary>
    /// Unpacks a RUN of consecutive blocks into consecutive output, with the per-row shape
    /// computed ONCE for the whole run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every block of a node shares one bit width, so all <c>elementBits</c> row shapes are the
    /// same for all of them -- and the per-block form rebuilt every one of them per block: four
    /// multiply-shift pairs and two mask constructions per row, 32 rows for a u32, against an
    /// inner loop of eight vector iterations per row. It was a quarter of the kernel spent
    /// recomputing a table that never changed.
    /// </para>
    /// <para>
    /// The block stays the OUTER loop even though hoisting the rows outside it would also hoist
    /// the two <c>Vector128.Create</c> broadcasts. A row writes only <c>lanes</c> contiguous
    /// elements per block, so row-outer would sweep the whole output once per row -- 32 passes
    /// over a megabyte column instead of one. The broadcasts are a register move; the sweep is
    /// memory bandwidth.
    /// </para>
    /// <para>
    /// THREE SHAPES WERE MEASURED AGAINST THIS ONE on 2026-09-18 (PERF-GAPS.md E1), on 64 blocks at
    /// widths 5, 10, 17 and 25 for u32 and 17, 33 and 52 for u64, interleaved in one process. All
    /// three are recorded here so the next reader does not pay for them again.
    /// </para>
    /// <para>
    /// <b>Lane groups outside, rows inside, the packed word carried in a register</b> -- upstream's
    /// shape, and what "le mot reste en registre" asks for: <b>1.67x to 1.94x SLOWER</b>. Holding
    /// the word costs a data-dependent branch per row (`is this still the word I have?`), re-reads
    /// the shape table once per lane group instead of once per block, and moves the two mask
    /// broadcasts inside the row loop. The loads it saves are all L1 hits -- the packed side of a
    /// block is 1.3 kB -- so it trades free loads for real branches.
    /// </para>
    /// <para>
    /// <b>The mask vectors precomputed once per run into an array</b>, indexed by row instead of
    /// broadcast per row per block: <b>1.11x to 1.15x SLOWER</b>. `Vector128.Create(scalar)` is one
    /// `dup` from a register; a 16-byte load from a table is worse. The broadcasts were already
    /// free.
    /// </para>
    /// <para>
    /// <b>The same loop with the shift and the masks as LITERALS</b> -- output deliberately wrong,
    /// run only to price the mechanism -- reads <b>0.80x to 0.93x</b>. That is the whole ceiling of
    /// a generator monomorphised by bit width, the C# answer to upstream's `seq_t!`: ten to twenty
    /// per cent of the kernel, and only of the kernel. Reaching it means emitting code per (width,
    /// row) pair -- shifts depend on the row, not on the width alone -- which is 64 widths x 4
    /// element types x the rows of each, thousands of lines of generated source. NOT WRITTEN: the
    /// axes where this library still loses to upstream are counted in milliseconds (`fsst` 7.2 ms,
    /// `zstd` 7.1 ms) and this one in tens of microseconds. The measurement is the argument; if the
    /// balance changes, the ceiling above is what to expect.
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

        // A zero bit width packs nothing at all: the whole run is zeros, and it is ONE clear
        // rather than `blocks` of them. The per-block form was calling into `memset` once per
        // 1024 elements for a buffer that is contiguous.
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

        Span<Row<T>> shapes = stackalloc Row<T>[elementBits];
        BuildRows(bitWidth, elementBits, shapes);
        Vectorized(packed, shapes, output, lanes, wordsPerBlock, blocks);
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

    /// <summary>Runs whichever vector width is available over a run of prepared blocks.</summary>
    private static void Vectorized<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        // `lanes` is 16, 32, 64 or 128 and every vector width divides all four, so the loops below
        // never need a remainder tail.
        if (Vector512.IsHardwareAccelerated)
        {
            Unpack512(packed, shapes, output, lanes, wordsPerBlock, blocks);
            return;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unpack256(packed, shapes, output, lanes, wordsPerBlock, blocks);
            return;
        }

        Unpack128(packed, shapes, output, lanes, wordsPerBlock, blocks);
    }

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
                // See `Unpack128`: the spill test is per ROW, so it is hoisted out of the lanes.
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
                // See `Unpack128`: the spill test is per ROW, so it is hoisted out of the lanes.
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

    private static void Unpack128<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        int rows = shapes.Length;
        int step = Vector128<T>.Count;

        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            nuint blockDestination = (nuint)((nint)block * BlockSize);

            for (int row = 0; row < rows; row++)
            {
                ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
                Vector128<T> low = Vector128.Create(shape.LowMask);
                Vector128<T> high = Vector128.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                nuint into = blockDestination + (nuint)shape.Destination;
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                // THE SPILL TEST IS A PROPERTY OF THE ROW, SO IT IS NOT IN THE LANE LOOP. Whether
                // this row straddles two packed words is decided by `Row<T>`'s constructor and is
                // the same for all 16 to 128 lanes; testing it inside meant one branch per vector
                // iteration of the innermost loop of the encoding. Two loops, one test.
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector128<T> value = ShiftRight(
                            Vector128.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                        value |= ShiftLeft(
                            Vector128.LoadUnsafe(ref source, next + (nuint)lane) & high, currentBits);
                        value.StoreUnsafe(ref destination, into + (nuint)lane);
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector128<T> value = ShiftRight(
                        Vector128.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
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
