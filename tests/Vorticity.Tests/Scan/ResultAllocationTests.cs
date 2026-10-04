using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Scan;

// IN THE SERIALISED COLLECTION, as every allocation axis is: a test running beside it would land in
// the thread's figure only if it shared the thread, but the warm-up passes are what make the figure
// mean anything, and a busy pool stretches them.

/// <summary>
/// The allocation invariant of a query's result: nothing per batch once the query has run, through
/// the record of <c>As</c> as through the values of a selection of one, the batch included.
/// </summary>
[Collection(nameof(AllocationCollection))]
public sealed partial class ResultAllocationTests
{
    private const int Groups = 20_000;

    private const int BatchRows = 1_000;

    [Fact]
    public async Task AResultsBatchesAllocateNothingOnceTheQueryHasRun()
    {
        ReleaseOnlyCeilings.Require();
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            for (int warm = 0; warm < 2; warm++)
            {
                await foreach (Columns<KeyCount> batch in Query(file))
                {
                    Assert.True(batch.RowCount > 0);
                }
            }

            Scan<KeyCount>.AsyncEnumerator batches = Query(file).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            try
            {
                // The first batch runs the query; the next ones are built from its groups.
                Assert.True(await batches.MoveNextAsync());
                for (int i = 0; i < 2; i++)
                {
                    Assert.True(Next(batches.MoveNextAsync()));
                }

                List<long> perBatch = [];
                while (true)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    bool more = Next(batches.MoveNextAsync());
                    long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (!more)
                    {
                        break;
                    }

                    perBatch.Add(delta);
                }

                Assert.True(perBatch.Count >= 10, "the measurement needs several steady-state batches");
                Assert.True(perBatch.TrueForAll(bytes => bytes == 0), string.Create(CultureInfo.InvariantCulture, $"[{string.Join(", ", perBatch)}]"));
            }
            finally
            {
                await batches.DisposeAsync();
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AResultOfOneValueAllocatesNothingPerValue()
    {
        ReleaseOnlyCeilings.Require();
        string path = await WriteAsync();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            for (int warm = 0; warm < 2; warm++)
            {
                await foreach (long count in Counts(file))
                {
                    Assert.True(count > 0);
                }
            }

            IAsyncEnumerator<long> values = Counts(file).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            try
            {
                // Past the first batch, which runs the query: every value after it, batches crossed included.
                for (int i = 0; i < BatchRows + 1; i++)
                {
                    Assert.True(await values.MoveNextAsync());
                }

                // Every value but the last, then the step that ends the stream, apart; around the
                // steps alone, which a loop of asserts over twenty thousand values would blur.
                long allocated = 0;
                for (int i = BatchRows + 1; i < Groups; i++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    ValueTask<bool> step = values.MoveNextAsync();
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.True(Next(step));
                }

                long last = GC.GetAllocatedBytesForCurrentThread();
                ValueTask<bool> end = values.MoveNextAsync();
                long ending = GC.GetAllocatedBytesForCurrentThread() - last;
                Assert.False(Next(end));
                Assert.Equal(0, allocated);
                Assert.Equal(0, ending);
            }
            finally
            {
                await values.DisposeAsync();
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static Scan<KeyCount> Query(VortexFile file) =>
        file.Scan<Keyed>().With(new ScanOptions { BatchRows = BatchRows }).GroupBy(r => r.Key).Select(g => (g.Key, g.Count())).As<KeyCount>();

    private static Aggregation<long> Counts(VortexFile file) =>
        file.Scan<Keyed>().With(new ScanOptions { BatchRows = BatchRows }).GroupBy(r => r.Key).Select(g => g.Count());

    private static bool Next(ValueTask<bool> move)
    {
        Assert.True(move.IsCompletedSuccessfully, "a batch after the query has run is built without an await");
        return move.Result;
    }

    private static async Task<string> WriteAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "result-allocations");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"keyed-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Keyed[] rows = new Keyed[2 * Groups];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = new Keyed((row * 7_919L) % Groups);
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Keyed>(path))
        {
            await writer.WriteAsync<Keyed>(rows, CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Keyed(long Key);
}
