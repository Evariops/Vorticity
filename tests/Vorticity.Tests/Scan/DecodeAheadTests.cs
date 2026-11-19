// The windows a merge's inputs share past their own (DecodeAhead): a scan its consumer reads takes some, decodes them on
// lanes of the session's, gives each back as the consumer passes it, and delivers the same batches as without; one the
// job's memory refuses takes none.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class DecodeAheadTests
{
    /// <summary>Rows of the file: sixteen splits of <see cref="SplitRows"/>.</summary>
    private const int Rows = 16 * SplitRows;

    /// <summary>The rows of a split, whose three columns weigh more than a scan runs on its consumer's flow.</summary>
    private const int SplitRows = 8_192;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A scan whose consumer reads it, as a merge reads the input it drains, decodes windows of the shared ones past its own
    /// two, gives every one back by its end, and delivers the rows a scan without them delivers, in the same batches.
    /// </summary>
    [Fact]
    public async Task AScanItsConsumerReadsTakesSharedWindowsAndGivesThemBack()
    {
        await using VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 4);
        await using VortexFile file = await session.OpenAsync(new MemorySegmentSource(await WriteAsync()), cancellationToken: Ct);
        List<(int Rows, long First)> plain = await BatchesAsync(file, null);

        DecodeAhead ahead = new DecodeAhead(3, static _ => true);
        Assert.Equal(plain, await BatchesAsync(file, ahead));
        Assert.True(ahead.Taken > 0, "no window was taken past the scan's own");
        Assert.Equal(3, ahead.Free);
        Assert.Equal(Rows / SplitRows, plain.Count);
    }

    /// <summary>A scan whose job's memory admits no window past its own takes none, and delivers the same batches.</summary>
    [Fact]
    public async Task AScanTheJobsMemoryRefusesTakesNoSharedWindow()
    {
        await using VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 4);
        await using VortexFile file = await session.OpenAsync(new MemorySegmentSource(await WriteAsync()), cancellationToken: Ct);
        List<(int Rows, long First)> plain = await BatchesAsync(file, null);

        DecodeAhead refused = new DecodeAhead(3, static _ => false);
        Assert.Equal(plain, await BatchesAsync(file, refused));
        Assert.Equal(0, refused.Taken);
        Assert.Equal(3, refused.Free);
    }

    /// <summary>
    /// A scan holds in flight the splits its consumer's cache and the pool's share allow, one at least. One whose consumer
    /// shares windows ahead, a merge's input, holds its window whatever a split weighs: its own two windows, which its job
    /// prices, and the shared ones, each admitted by its job's memory. Held to the bytes, an input whose split holds 40 MiB
    /// had one split in flight and took no shared window.
    /// </summary>
    [Fact]
    public void AScanThatSharesWindowsAheadHoldsItsWindowWhateverASplitHolds()
    {
        const int Window = 15;
        const double SmallShare = 0.01;

        // Splits of a megabyte: the window either way.
        Assert.Equal(Window, BatchAsyncEnumerator.InFlightSplits(1 << 20, SmallShare, Window, sharesAhead: false));
        Assert.Equal(Window, BatchAsyncEnumerator.InFlightSplits(1 << 20, SmallShare, Window, sharesAhead: true));

        // Splits of 16 MiB, three of which hold what a consumer keeps in flight; of 40 MiB, one.
        Assert.Equal(3, BatchAsyncEnumerator.InFlightSplits(16 << 20, SmallShare, Window, sharesAhead: false));
        Assert.Equal(1, BatchAsyncEnumerator.InFlightSplits(40 << 20, SmallShare, Window, sharesAhead: false));
        Assert.Equal(Window, BatchAsyncEnumerator.InFlightSplits(40 << 20, SmallShare, Window, sharesAhead: true));

        // A split that holds most of what the pool keeps of a size class: one, and the window for a merge's input.
        Assert.Equal(1, BatchAsyncEnumerator.InFlightSplits(1 << 20, 0.9, Window, sharesAhead: false));
        Assert.Equal(Window, BatchAsyncEnumerator.InFlightSplits(1 << 20, 0.9, Window, sharesAhead: true));
    }

    /// <summary>The rows and the first key of each batch a merge's input scan of <paramref name="file"/> delivers, sharing <paramref name="ahead"/>.</summary>
    private static async Task<List<(int Rows, long First)>> BatchesAsync(VortexFile file, DecodeAhead? ahead)
    {
        List<(int Rows, long First)> batches = [];
        ScanBuilder scan = file.ScanBuilder()
            .WithPrefetch(1)
            .WithConsumerLane()
            .WithMaxBatchRows(SplitRows)
            .WithWindowRows(SplitRows)
            .WithHandover(new BatchHandover { Ahead = ahead });
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(Ct))
        {
            batches.Add((batch.RowCount, batch.Column(0).AsPrimitive<long>().Values[0]));
        }

        return batches;
    }

    /// <summary>A file of rising keys and two measures.</summary>
    private static async Task<byte[]> WriteAsync()
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        DType schema = types.Struct(["key", "a", "b"], [i64, f64, f64], Nullability.NonNullable);
        VortexBuffer keyBuffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer aBuffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> aBytes);
        VortexBuffer bBuffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bBytes);
        Span<long> keys = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> a = MemoryMarshal.Cast<byte, double>(aBytes);
        Span<double> b = MemoryMarshal.Cast<byte, double>(bBytes);
        Random random = new Random(0xDEC0DE);
        for (int row = 0; row < Rows; row++)
        {
            keys[row] = row * 3L;
            a[row] = random.NextDouble();
            b[row] = random.NextDouble() * 1_000;
        }

        int key = arena.AddPrimitive(i64, Rows, Validity.NonNullable, PType.I64, keyBuffer);
        int first = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, aBuffer);
        int second = arena.AddPrimitive(f64, Rows, Validity.NonNullable, PType.F64, bBuffer);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [key, first, second]);
        using MemoryStream stream = new MemoryStream();
        await using (VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream, ownsStream: false), schema, new VortexWriteOptions { BlockRows = SplitRows }))
        {
            using RecordBatch batch = new RecordBatch(arena, root, 0);
            await writer.WriteAsync(batch);
            await writer.CompleteAsync();
        }

        return stream.ToArray();
    }
}
