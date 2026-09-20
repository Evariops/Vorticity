// vortex.zstd on the WRITE side, which this library has never had.
//
// The gap was found by reading where the corpus loses bytes rather than by reading the plan:
// `distributions/huge_string_r16` wrote 138 852 bytes against the reference's 4 104 - 33.8x, and
// more than a third of everything the twelve worst files lose combined, on a file of sixteen rows.
// ColumnCompressor offered sequence, runend, bit-packing, dictionary, ALP and FSST, and never zstd.
// Those sixteen values are 1 100 175 bytes raw and compress to 118.
//
// THE FORMAT IS THE DECODER'S, read backwards. `ZstdDecoder`'s header states it: for utf8 and
// binary the stored stream is NOT an Arrow layout but a bare sequence of `[u32 little-endian
// length][bytes]` records, and NULLS ARE NOT STORED - the frames hold only the valid values, back
// to back. One frame is written here; the format allows several so a slice can decompress part of
// a column, and nothing in this writer slices.
//
// WHY IT IS PRICED LAST. The profiling session found FSST symbol training at 56% of the whole write
// path, spent pricing a candidate that usually loses. Compressing every column with zstd to find out
// whether zstd wins would be that same mistake in a new place, so this runs only after the cheaper
// candidates have failed to beat the canonical form.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>A zstd-compressed frame over a column's valid values, with its priced size.</summary>
internal sealed class ZstdPlan
{
    private ZstdPlan(byte[] frame, int frameLength, int uncompressedSize, int valueCount)
    {
        Frame = frame;
        FrameLength = frameLength;
        UncompressedSize = uncompressedSize;
        ValueCount = valueCount;
    }

    /// <summary>The compressed frame, exactly as it goes into the buffer.</summary>
    internal byte[] Frame { get; }

    /// <summary>Bytes of <see cref="Frame"/> that are the frame.</summary>
    /// <remarks>
    /// PERF-AUDIT-v2.md W-5. `Frame` is the buffer zstd compressed INTO, straight from
    /// `ArrayPool&lt;byte&gt;.Shared`, so it is longer than the frame -- usually much longer, since the
    /// rental is sized for the worst case `GetMaxCompressedLength` allows. Copying it out to a
    /// right-sized array was measured at **6,6 % of a 1 M-row utf8 file's whole write** (906 181
    /// bytes over 123 chunks), 4,0 % on `varbin` and 2,0 % on `table_mixed`.
    ///
    /// OWNERSHIP MOVES TO WHOEVER WRITES IT. `ArrayBlobWriter` takes the array as a rented
    /// `PendingBuffer` and returns it once the blob is laid out; a plan that is built and then NOT
    /// chosen has to give it back itself, which is what <see cref="Release"/> is for. After either,
    /// `Frame` names memory that belongs to somebody else and must not be read.
    /// </remarks>
    internal int FrameLength { get; }

    /// <summary>Bytes the frame decompresses to, which the decoder allocates against a cap.</summary>
    internal int UncompressedSize { get; }

    /// <summary>Values stored in the frame: the column's VALID rows, nulls excluded.</summary>
    internal int ValueCount { get; }

    /// <summary>Hands <see cref="Frame"/> back to the pool, for a plan nobody wrote.</summary>
    /// <remarks>
    /// `ColumnCompressor` prices zstd and may then prefer FSST, and the loser still holds a rental.
    /// Not returning it would not corrupt anything -- the array is simply collected instead of
    /// reused -- but it drains the pool one buffer per column, which is the shape of problem that
    /// only shows up as a slow leak under load.
    /// </remarks>
    internal void Release() => ArrayPool<byte>.Shared.Return(Frame);

    /// <summary>
    /// Compresses a varbin column's valid values, and keeps the result only if it is worth reading.
    /// </summary>
    /// <param name="arena">The arena holding the canonical node.</param>
    /// <param name="nodeIndex">The node to encode; a <c>VarBinView</c> or a <c>Primitive</c>.</param>
    /// <param name="canonicalSize">What the plain form costs, which this must beat.</param>
    /// <returns>The plan, or <see langword="null"/> when zstd is not worth it.</returns>
    /// <remarks>
    /// The margin is the same judgement FSST's is: a zstd array costs a decompression pass and a
    /// scatter across null slots on every read, so shaving a few percent off the file is not worth
    /// making every reader pay for it.
    /// </remarks>
    internal static ZstdPlan? TryBuild(CanonicalArena arena, int nodeIndex, long canonicalSize)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind == CanonicalKind.Primitive)
        {
            return TryBuildPrimitive(arena, node, canonicalSize);
        }

        if (node.Kind != CanonicalKind.VarBinView)
        {
            return null;
        }

        int rows = node.Length;
        long streamBytes = 0;
        int valueCount = 0;
        for (int i = 0; i < rows; i++)
        {
            if (!IsValid(arena, node, i))
            {
                continue;
            }

            valueCount++;
            streamBytes += sizeof(uint) + ValueOf(node, i).Length;
            if (streamBytes > int.MaxValue)
            {
                return null;
            }
        }

        if (valueCount == 0)
        {
            return null;
        }

        // RENTED, not allocated. Pricing zstd on a column means materializing the whole value
        // stream and a worst-case destination beside it - two buffers the size of the column, for a
        // candidate that may lose. Allocating them turned `encodings/fsst` 17% heavier to write and
        // WriteAllocationTests red; the ceiling stayed where it was.
        byte[] stream = ArrayPool<byte>.Shared.Rent((int)streamBytes);
        byte[] destination = ArrayPool<byte>.Shared.Rent(
            checked((int)ZstandardEncoder.GetMaxCompressedLength((int)streamBytes)));
        bool kept = false;
        try
        {
        int offset = 0;
        for (int i = 0; i < rows; i++)
        {
            if (!IsValid(arena, node, i))
            {
                continue;
            }

            ReadOnlySpan<byte> value = ValueOf(node, i);
            BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(offset, sizeof(uint)), (uint)value.Length);
            offset += sizeof(uint);
            value.CopyTo(stream.AsSpan(offset));
            offset += value.Length;
        }

        // THE ONE-SHOT BUILDS A NATIVE COMPRESSION CONTEXT PER CALL, and reusing one instead is an
        // open question with its first gate already passed: a `ZstandardEncoder` held across calls
        // and `Reset` between them produces byte-identical output to this, checked on a run of
        // a hundred thousand equal bytes, fifty thousand pseudo-random ones and four thousand
        // URLs. What is not yet known is what it buys. Doubling this compression costs 15,9 ms of
        // a 54,7 ms write of a million `fsst` rows and 11,9 of 33,7 on `varbin` -- a third of the
        // axis -- but that is the compression itself, which has to happen; only the context build
        // would go, and how many of those there are per write has not been counted.
        if (!ZstandardEncoder.TryCompress(
                stream.AsSpan(0, (int)streamBytes), destination, out int written) || written <= 0)
        {
            return null;
        }

        // Priced against the plain form the same way every other candidate is, with the frame's own
        // bytes as the whole cost: the metadata is two varints and the validity child is written
        // either way.
        if (written * (long)MarginDenominator >= canonicalSize * (long)MarginNumerator)
        {
            return null;
        }

        // THE RENTAL IS NOT RETURNED HERE on the success path: the plan takes it. `kept` is what
        // tells the `finally` which of the two happened, and every early return above leaves it
        // false, so a plan that never existed never keeps a buffer.
        kept = true;
        return new ZstdPlan(destination, written, (int)streamBytes, valueCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stream);
            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(destination);
            }
        }
    }

    /// <summary>
    /// A primitive column: the stored stream is the valid values back to back, no length prefixes.
    /// </summary>
    /// <remarks>
    /// The varbin form writes `[u32 length][bytes]` per value because the decoder rebuilds views
    /// from it. A fixed-width column needs none of that - `ZstdDecoder` scatters the decompressed
    /// values across the null slots using the width alone - so the stream is simply the values.
    ///
    /// Worth having because the alternative for these columns is nothing at all. `types/f32_*` sit
    /// at 1.43-1.46x the reference and are written CANONICALLY: ALP declines them, because ALP
    /// encodes f32 to i32 and wins only when those integers bit-pack small, and `vortex.alprd` -
    /// the encoding built for floats ALP cannot take - is one this library reads and has never
    /// written. Zstd takes that column's 32 764 bytes to 23 465, against a reference file of 23 204.
    /// </remarks>
    private static ZstdPlan? TryBuildPrimitive(CanonicalArena arena, CanonicalNode node, long canonicalSize)
    {
        int width = node.PType.ByteWidth();
        if (width == 0)
        {
            return null;
        }

        int rows = node.Length;
        ReadOnlySpan<byte> source = node.Values.Span;

        // WHEN NO ROW IS NULL THE STREAM IS THE SOURCE, and this loop was copying it to itself.
        // The compacting loop below writes `source[i*width .. +width]` to `stream[i*width .. +width]`
        // for every row and skips nothing, so `stream[0 .. rows*width]` comes out byte for byte
        // equal to `source[0 .. rows*width]` - and it paid a validity call and a `CopyTo` of four
        // or eight bytes per row to get there. Compressing the source in place produces the SAME
        // BYTES, which is what lets this be a plain speed-up rather than a format change.
        //
        // SLICED AT `rows * width` and not handed over whole: `node.Values.Span` is the arena's
        // block, which can be longer than the rows this node owns, and a longer input is a
        // different frame.
        bool allValid = node.Validity.IsAllValid;
        int valueCount = allValid ? rows : 0;
        byte[]? stream = allValid ? null : ArrayPool<byte>.Shared.Rent(rows * width);
        byte[] destination = ArrayPool<byte>.Shared.Rent(
            checked((int)ZstandardEncoder.GetMaxCompressedLength(rows * width)));
        bool kept = false;
        try
        {
            if (stream is not null)
            {
                for (int i = 0; i < rows; i++)
                {
                    if (!IsValid(arena, node, i))
                    {
                        continue;
                    }

                    source.Slice(i * width, width).CopyTo(stream.AsSpan(valueCount * width, width));
                    valueCount++;
                }
            }

            if (valueCount == 0)
            {
                return null;
            }

            int streamBytes = valueCount * width;
            ReadOnlySpan<byte> input = stream is null
                ? source[..streamBytes]
                : stream.AsSpan(0, streamBytes);
            if (!ZstandardEncoder.TryCompress(input, destination, out int written) || written <= 0)
            {
                return null;
            }

            // A MUCH LARGER MARGIN THAN VARBIN, and it is a read-time decision rather than a size
            // one. A primitive column's alternative is bit-packing, which decodes several times
            // faster than zstd: measured on our own output, `high_cardinality_i64_r8193` came out
            // 2.5% smaller and 42% SLOWER TO SCAN when zstd took it, while `f32_nonnull_r8191` came
            // out 28% smaller and slightly faster. Size saving alone does not separate those, so the
            // gate is set where the big wins are and the marginal ones are left to bit-packing.
            //
            // The written-size target is <=105% of the reference and the corpus sits near 0.67x, so
            // size is not the binding constraint any more - read time is. This is the first encoding
            // decision in the writer made on that basis.
            if (written * (long)PrimitiveMarginDenominator >= canonicalSize * (long)PrimitiveMarginNumerator)
            {
                return null;
            }

            kept = true;
            return new ZstdPlan(destination, written, streamBytes, valueCount);
        }
        finally
        {
            if (stream is not null)
            {
                ArrayPool<byte>.Shared.Return(stream);
            }

            if (!kept)
            {
                ArrayPool<byte>.Shared.Return(destination);
            }
        }
    }

    /// <summary>On a primitive column, keep zstd only when it saves at least a quarter.</summary>
    private const int PrimitiveMarginNumerator = 3;

    /// <summary>Denominator of <see cref="PrimitiveMarginNumerator"/>.</summary>
    private const int PrimitiveMarginDenominator = 4;

    /// <summary>Keep zstd only when it saves at least a tenth of the plain form.</summary>
    private const int MarginNumerator = 9;

    /// <summary>Denominator of <see cref="MarginNumerator"/>.</summary>
    private const int MarginDenominator = 10;

    // The row helpers are FsstPlan's: both plans walk the same canonical shape.
    private static bool IsValid(CanonicalArena arena, CanonicalNode node, int row) => FsstPlan.IsValid(arena, node, row);

    private static ReadOnlySpan<byte> ValueOf(CanonicalNode node, int row) => FsstPlan.ValueOf(node, row);
}
