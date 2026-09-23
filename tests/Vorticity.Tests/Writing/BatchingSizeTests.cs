// How much the CALLER's batching decides the file's size.
//
// WrittenSizeTests measures one batching: whatever the source file's own chunking produces. This
// rewrites `types/binary_nonnull_r8193` twice, once in the source's own batches and once in
// 1024-row batches. The writer cuts its own blocks -- `RowBlockSize` rows, coalesced up to a byte
// target -- so both writes emit the same chunks and the same bytes, and this test holds that. A
// writer that let the caller's batching reach the file would pay a whole dictionary per extra
// chunk: the multi-chunk axis of WrittenSizeTests, which forces the chunks, shows what that costs.
//
// 1024 divides the row block. Batches that do not can still move bytes, because the blocks follow
// the rows the writer has been handed; the write specification states that limit.
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>What splitting the same rows across more batches costs on disk.</summary>
public sealed class BatchingSizeTests
{
    /// <summary>A column the writer dictionary-encodes, at 8193 rows so it splits.</summary>
    private const string Entry = "types/binary_nonnull_r8193";

    /// <summary>Rows per batch for the fragmented write; the file holds 8193.</summary>
    private const int SmallBatch = 1024;

    /// <summary>
    /// What the fragmented write may cost against the source's own batching, as a multiple.
    /// </summary>
    /// <remarks>
    /// Measured at 1.00×: the caller's batching does not reach the file. Anything above is the
    /// writer starting to follow the batches it is handed.
    /// </remarks>
    private const double FragmentationCeiling = 1.00;

    [Fact]
    public async Task SplittingTheSameRowsAcrossMoreBatchesStaysWithinItsRatchet()
    {
        Decoders.EnsureRegistered();

        (long whole, int wholeBatches) = await Rewrite(cap: 0);
        (long fragmented, int smallBatches) = await Rewrite(SmallBatch);

        Assert.True(smallBatches > wholeBatches, "the small-batch write must hand the writer more batches");

        double ratio = (double)fragmented / whole;
        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"BATCHING SIZE: {Entry} written from {wholeBatches} batches is {whole} bytes, from " +
            $"{smallBatches} batches is {fragmented} -- {ratio:F2}x (ceiling {FragmentationCeiling:F2})");
        Console.Out.WriteLine(report);

        Assert.True(ratio <= FragmentationCeiling, report);
    }

    [Fact]
    public async Task AWindowOfALargerBatchIsSizedByTheRowsItNames()
    {
        // A window keeps its batch's whole text heap, of which its views name a part; sized by the
        // heap, it would cut the file's chunks at a block. The same rows handed over as windows and
        // as batches of their own write the same file.
        Decoders.EnsureRegistered();
        const int rows = 60_000;
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["text"], [utf8], Nullability.NonNullable);
        CanonicalArena whole = new CanonicalArena();
        VortexBuffer views = whole.Allocate(rows * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = whole.Allocate(rows * 24, 8, out Span<byte> heapBytes);
        for (int row = 0; row < rows; row++)
        {
            Span<byte> value = heapBytes.Slice(row * 24, 24);
            Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"text-{row * 7_919 % 100_003:D19}"), value);
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            MemoryMarshal.Write(view, 24);
            value[..4].CopyTo(view[4..]);
            MemoryMarshal.Write(view[8..], 0);
            MemoryMarshal.Write(view[12..], row * 24);
        }

        int column = whole.AddVarBinView(utf8, rows, Validity.NonNullable, views, [heap]);
        int root = whole.AddStruct(schema, rows, Validity.NonNullable, [column]);

        byte[] windows = await WriteAsync(schema, whole, root, rows, own: false);
        byte[] owned = await WriteAsync(schema, whole, root, rows, own: true);

        Assert.Equal(owned, windows);
    }

    private static async Task<byte[]> WriteAsync(DType schema, CanonicalArena whole, int root, int rows, bool own)
    {
        using MemoryStream stream = new MemoryStream();
        VortexWriteOptions options = new VortexWriteOptions { Identity = new Guid("29292929-2929-4929-8929-292929292929") };
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, options))
        {
            CanonicalArena apart = new CanonicalArena();
            for (int start = 0; start < rows; start += 5_000)
            {
                int window = CanonicalSlice.SliceAcross(whole, whole, root, start, 5_000);
                using (RecordBatch batch = own
                    ? new RecordBatch(apart, apart.CopyFrom(whole, window), start)
                    : new RecordBatch(whole, window, start))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                apart.Reset();
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        return stream.ToArray();
    }

    /// <summary>Reads the entry and writes it back, optionally forcing a batch size.</summary>
    /// <param name="cap">Rows per batch, or 0 to take the source's own chunking.</param>
    private static async Task<(long Bytes, int Batches)> Rewrite(int cap)
    {
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-batching-{Guid.NewGuid():N}.vortex");
        try
        {
            int batches = 0;
            await using (VortexFile source = await VortexFile.OpenAsync(Corpus.Path(Entry), CancellationToken.None))
            await using (VortexFileWriter writer = VortexFileWriter.Create(written, source.DType))
            {
                ScanBuilder scan = source.ScanBuilder();
                if (cap > 0)
                {
                    scan = scan.WithMaxBatchRows(cap);
                }

                await foreach (RecordBatch batch in scan.ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    batches++;
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return (new FileInfo(written).Length, batches);
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }
}
