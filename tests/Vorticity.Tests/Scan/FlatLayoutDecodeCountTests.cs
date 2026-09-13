// A chunk larger than a batch is decoded once PER BATCH, which is quadratic in the row count.
//
// FlatLayoutReader.Execute decodes `total` -- the whole node -- and then slices out the batch's
// rows. Upstream calls the layout indivisible and so does the comment there, and for READING the
// segment that is right: a flat layout's bytes cannot be fetched in parts. Decoding them once per
// batch is a different claim, and it is the one that costs.
//
// WHY NOTHING SAW IT. Every conformance corpus file is 8193 rows or fewer, and the benchmark file is
// 64 splits of 1024 rows, so a chunk is never bigger than the 8192-row batch and the factor is
// exactly 1. It took generating 1M-row single-encoding files to make it visible, and then it was
// 90x on ten times the rows -- `vortex.fsst` at 3.4 SECONDS for a million values, against 26.5 ms
// with the subdivision removed.
//
// THIS TEST NEEDS NO SUCH FILE. The quantity is exact and machine-independent -- values materialized
// per scan -- so a few tens of thousands of rows show the shape that a timing would need millions to
// show through the noise. It is a RATCHET ON A DEFECT, in the style of the allocation ceilings: it
// pins today's number so the fix is visible as a drop and a regression as a rise. The target is in
// `Ideal`, and it is not what the assertion uses, because a red suite is not a plan.
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Types;
using Vorticity.Editions;
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

    /// <summary>Writes one chunk of <paramref name="rows"/> rows and returns the file path.</summary>
    /// <param name="rows">Rows in the single chunk.</param>
    /// <remarks>
    /// Built straight into an arena rather than scanned out of a corpus file, because a scan never
    /// yields a batch above 8192 rows and the whole point is a chunk that does.
    /// </remarks>
    private static string WriteOneChunk(int rows)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [i64], Nullability.NonNullable);

        VortexBuffer values = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> destination);
        Span<long> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(destination);
        for (int i = 0; i < rows; i++)
        {
            longs[i] = i;
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
