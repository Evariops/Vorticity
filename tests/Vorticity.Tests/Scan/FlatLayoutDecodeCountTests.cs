// A chunk larger than a batch is decoded once per scan, not once per batch: decoding the whole
// node for every batch and slicing that batch's rows out of it is correct and quadratic in the
// row count.
//
// A flat layout is indivisible for READING -- its bytes cannot be fetched in parts -- and the
// reference implementation says so too. Decoding them once per batch is a different claim, and
// it is the one that costs, so FlatLayoutReader decodes the chunk once and retains it for the
// batches that follow.
//
// NO SHIPPED FILE SHOWS IT. Every conformance corpus file is 8193 rows or fewer, and the benchmark
// file is 64 splits of 1024 rows, so a chunk is never bigger than the 8192-row batch and the
// factor there is exactly 1.
//
// THIS TEST NEEDS NO LARGE FILE. The quantity is exact and machine-independent -- values
// materialized per scan -- so a few tens of thousands of rows show the shape that a timing would
// need millions to show through the noise. It is pinned at `Ideal`, each value once, so a return
// of the per-batch decode shows as a rise.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Editions;
using Vorticity.Tests.Writing;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>How many values a scan materializes, on a file whose chunk exceeds one batch.</summary>
[Collection(nameof(AllocationCollection))]
public sealed class FlatLayoutDecodeCountTests
{
    /// <summary>Rows in the single chunk. Comfortably more than the 8192-row batch.</summary>
    private const int Rows = 50_000;

    /// <summary>What a correct reader materializes: each value once. Now the assertion.</summary>
    private const long Ideal = Rows;

    /// <summary>A scan of one oversized chunk decodes it ONCE, not once per batch.</summary>
    [Fact]
    public async Task AScanMaterializesEveryValueOncePerBatch()
    {
        string path = WriteOneChunk(Rows);
        try
        {

        FlatLayoutReader.ValuesDecoded = 0;
        long batches = 0;
        long rows = 0;
        await using (VortexFile opened = await VortexFile.OpenAsync(path, CancellationToken.None))
        {
            await foreach (RecordBatch batch in opened.Scan().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                batches++;
                rows += batch.RowCount;
            }
        }

        long decoded = FlatLayoutReader.ValuesDecoded;
        Console.Out.Write(
            "FLAT DECODE: " + rows.ToString(CultureInfo.InvariantCulture) + " rows in one chunk, read as " +
            batches.ToString(CultureInfo.InvariantCulture) + " batches, materialized " +
            decoded.ToString(CultureInfo.InvariantCulture) + " values -- " +
            ((double)decoded / Ideal).ToString("F1", CultureInfo.InvariantCulture) + "x the " +
            Ideal.ToString(CultureInfo.InvariantCulture) + " a correct reader would.\n" +
            "A factor above 1.0 is the batch count, and would grow with the row count.\n");

        Assert.Equal(Rows, rows);
        Assert.True(batches > 1, "the chunk must exceed one batch for this test to mean anything");

        // THE RATCHET, DROPPED FROM `batches * Rows` TO `Rows` when the retained decode landed.
        // Equality, not a bound: the quantity is exact, and a bound would let it drift back up
        // inside the slack. At 50 000 rows over 7 batches the defect read 350 000.
        Assert.Equal(Ideal, decoded);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A window onto a retained LIST chunk costs records, not the elements child's bytes.
    /// </summary>
    /// <remarks>
    /// THE SECOND HALF OF THE SAME DEFECT, and it survived the first fix for a year. Every canonical
    /// kind narrows across arenas for free -- a slice is a record whose buffers are views -- except
    /// ListView, whose offsets are absolute into an elements CHILD named by an arena index, and an
    /// index means nothing in another arena. The child was therefore COPIED, wholesale, for every
    /// batch of the chunk: `CanonicalSlice`'s own comment said "ListView is the one kind that cannot
    /// borrow", and the 1M-row axis read `vortex.list` at 16.9x the reference with the quadratic
    /// supposedly fixed.
    ///
    /// Re-creating the child's RECORDS while its buffers stay views costs neither correctness nor
    /// bytes, and the quantity that proves it is exact: ZERO bytes materialized for a whole scan.
    ///
    /// It read 1 600 000 when this test was written -- one honest copy of the chunk out of the
    /// batch arena into an arena that outlives it -- and that copy has since gone too: the chunk is
    /// decoded straight into the arena that retains it. So the number this pins is now the strongest
    /// one available, and the two defects it stands against are visible as two different multiples
    /// of nothing: 7 200 000 bytes when the elements child was re-copied per batch, 1 600 000 when
    /// only the retain copied.
    /// </remarks>
    [Fact]
    public async Task AWindowOntoARetainedListChunkCopiesNoBytes()
    {
        string path = WriteOneListChunk(Rows);
        try
        {
            long before = CanonicalArena.BytesMaterialized;
            long batches = 0;
            long rows = 0;
            await using (VortexFile opened = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                await foreach (RecordBatch batch in opened.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    batches++;
                    rows += batch.RowCount;
                }
            }

            long copied = CanonicalArena.BytesMaterialized - before;

            Console.Out.Write(
                "LIST WINDOW: " + rows.ToString(CultureInfo.InvariantCulture) + " list rows in one chunk, read as " +
                batches.ToString(CultureInfo.InvariantCulture) + " batches, materialized " +
                copied.ToString(CultureInfo.InvariantCulture) + " bytes.\n" +
                "A correct reader copies nothing: it decodes into the arena that retains the chunk, " +
                "and every batch window is a record over the same buffers.\n");

            Assert.Equal(Rows, rows);
            Assert.True(batches > 1, "the chunk must exceed one batch for this test to mean anything");
            Assert.Equal(0, copied);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>A TAKE on one oversized chunk decodes it ONCE, not once per wanted row.</summary>
    /// <remarks>
    /// <para>
    /// THE THIRD FACE OF THE SAME DEFECT, and the last one to be seen. The scan left the quadratic
    /// and the take stayed in it: `FlatLayoutReader` served a selection one batch at a time through
    /// the encoding's own `DecodeSelected`, whose DEFAULT decodes the whole node and gathers. So a
    /// take of n rows spread over a chunk decoded that chunk n times. Counted on the 1M-row
    /// `vortex.zstd` axis: 5 824 calls for 5 824 wanted rows - one row each - a million rows and
    /// ~881 zstd frames every time, 427 ms for 64 rows against 7 ms for a full scan of the same
    /// node. Fifty-nine scans of the file to deliver sixty-four rows.
    /// </para>
    /// <para>
    /// TWO ASSERTIONS, AND THE VALUES ARE THE IMPORTANT ONE. The count pins the shape; the oracle
    /// pins correctness, and it is needed here rather than in <c>TakeSpecializationTests</c> because
    /// no corpus file is bigger than a batch - so the whole corpus, which is that test's oracle,
    /// never reaches this branch at all.
    /// </para>
    /// <para>
    /// `ValuesDecoded` read ZERO on this path before the fix, which is its own small lesson: the
    /// counter that caught the scan quadratic never covered the take, and a defect no instrument
    /// counts is a defect nobody sees.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATakeOnAnOversizedChunkMaterializesItOnce()
    {
        string path = WriteOneChunk(Rows, compressible: false);
        try
        {
            long[] wanted = [0, 1, 1023, 1024, 8191, 8192, 8193, 20_000, 33_333, Rows - 1];

            List<string> all = [];
            await using (VortexFile scanned = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                await foreach (RecordBatch batch in scanned.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Values.DescribeRows(batch, all);
                }
            }

            List<string> expected = [];
            foreach (long index in wanted)
            {
                expected.Add(all[(int)index]);
            }

            FlatLayoutReader.ValuesDecoded = 0;
            List<string> taken = [];
            await using (VortexFile opened = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                await foreach (RecordBatch batch in opened.Scan().Take(wanted).ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Values.DescribeRows(batch, taken);
                }
            }

            long decoded = FlatLayoutReader.ValuesDecoded;
            Console.Out.Write(
                "FLAT TAKE: " + wanted.Length.ToString(CultureInfo.InvariantCulture) + " rows wanted from a " +
                Rows.ToString(CultureInfo.InvariantCulture) + "-row chunk materialized " +
                decoded.ToString(CultureInfo.InvariantCulture) + " values -- " +
                ((double)decoded / Ideal).ToString("F1", CultureInfo.InvariantCulture) + "x the " +
                Ideal.ToString(CultureInfo.InvariantCulture) + " one decode costs.\n" +
                "The defect decoded the chunk once per wanted row and counted none of them.\n");

            Assert.Equal(expected, taken);

            // EQUALITY, like the scan above: one decode of the chunk, whatever the take asks for.
            Assert.Equal(Ideal, decoded);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>Writes one chunk of <paramref name="rows"/> two-element lists.</summary>
    /// <param name="rows">List rows in the single chunk.</param>
    private static string WriteOneListChunk(int rows)
    {
        const int PerRow = 2;
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType list = types.List(i64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [list], Nullability.NonNullable);

        int elements = rows * PerRow;
        VortexBuffer values = arena.Allocate(elements * sizeof(long), sizeof(long), out Span<byte> destination);
        Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
        for (int i = 0; i < elements; i++)
        {
            longs[i] = i;
        }

        VortexBuffer offsets = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> offsetBytes);
        VortexBuffer sizes = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> sizeBytes);
        Span<long> offsetValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(offsetBytes);
        Span<long> sizeValues = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(sizeBytes);
        for (int i = 0; i < rows; i++)
        {
            offsetValues[i] = (long)i * PerRow;
            sizeValues[i] = PerRow;
        }

        int child = arena.AddPrimitive(i64, elements, Validity.NonNullable, PType.I64, values);
        int column = arena.AddListView(
            list, rows, Validity.NonNullable, child, offsets, PType.I64, sizes, PType.I64);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [column]);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-listwindow-{Guid.NewGuid():N}.vortex");
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            WriteAsync(path, schema, batch).GetAwaiter().GetResult();
        }

        return path;
    }

    /// <summary>Writes one chunk of <paramref name="rows"/> rows and returns the file path.</summary>
    /// <param name="rows">Rows in the single chunk.</param>
    /// <param name="compressible">
    /// <see langword="true"/> for the values 0..n, which the writer turns into `vortex.sequence`.
    /// <see langword="false"/> for a xorshift stream, which no encoding here can model.
    /// </param>
    /// <remarks>
    /// Built straight into an arena rather than scanned out of a corpus file, because a scan never
    /// yields a batch above 8192 rows and the whole point is a chunk that does.
    ///
    /// THE TAKE TEST NEEDS THE INCOMPRESSIBLE ONE and it took a red assertion to see why: 0..n is
    /// `vortex.sequence`, which OVERRIDES `DecodeSelected` and therefore takes its rows without
    /// decoding a node at all - so the take never reached the branch it was written to pin, and the
    /// counter read zero. The defect lives on the encodings that fall back, which is what a stream
    /// nothing can model produces.
    /// </remarks>
    private static string WriteOneChunk(int rows, bool compressible = true)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [i64], Nullability.NonNullable);

        VortexBuffer values = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> destination);
        Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
        ulong state = 0x2545F4914F6CDD1DUL;
        for (int i = 0; i < rows; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            longs[i] = compressible ? i : unchecked((long)state);
        }

        int column = arena.AddPrimitive(i64, rows, Validity.NonNullable, PType.I64, values);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [column]);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-flatdecode-{Guid.NewGuid():N}.vortex");
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            WriteAsync(path, schema, batch).GetAwaiter().GetResult();
        }

        return path;
    }

    private static async Task WriteAsync(string path, DType schema, RecordBatch batch)
    {
        // NO ZONE MAP, and that is the whole point. ScanBuilder says the natural batch size is
        // "the file's zone length, or 8192 when it has no zone map" -- so a file WITH a zone map is
        // read one zone at a time, chunk equals batch, and the defect is invisible. Targeting an
        // edition below core2026.08.0 omits the zone map, which is the shape the reference writer
        // produces whenever file statistics are off, and the shape every 1M-row throughput file has.
        VortexWriteOptions options = new VortexWriteOptions
        {
            TargetEdition = VortexEdition.Core20251000,
        };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        await writer.WriteAsync(batch, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
    }
}
