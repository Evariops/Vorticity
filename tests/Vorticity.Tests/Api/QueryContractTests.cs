using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Tests.Aggregation;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>The promises of a query's surface that hold for every query, from docs/design/14-public-api.md.</summary>
[Trait("Category", "ApiContract")]
public sealed class QueryContractTests
{
    // Rule 6: a builder never reads. Everything it checks is in the schema the open read.
    [Fact]
    public async Task ABuilderReadsNothingAndItsRunReads()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "query-contracts");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"builders-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        try
        {
            // Larger than the tail an open reads, so that the run has segments left to ask for.
            Grouped[] rows = new Grouped[200_000];
            for (int row = 0; row < rows.Length; row++)
            {
                rows[row] = new Grouped(row % 3 == 0 ? "Paris" : "Lyon", row / 100, row % 2 == 0, row % 7, row * 7_919L, (row * 37 % 10_007) / 10.0);
            }

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Grouped>(path))
            {
                await writer.WriteAsync<Grouped>(rows, TestContext.Current.CancellationToken);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            CountingSource source = new CountingSource(new MemorySegmentSource(await System.IO.File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
            await using VortexFile file = await VortexSession.Default.OpenAsync(source, cancellationToken: TestContext.Current.CancellationToken);
            int opened = source.Reads;

            Scan<CityDay> query = file.Scan<Grouped>()
                .Where(r => r.Day >= 10)
                .OrderBy(r => r.Day)
                .GroupBy(r => (r.City, r.Day))
                .Select(g => (g.Key.City, g.Key.Day, g.Count(), g.Average(r => r.Score)))
                .As<CityDay>();
            Assert.Equal(opened, source.Reads);

            long groups = 0;
            await foreach (CityDay _ in query.ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                groups++;
            }

            Assert.True(groups > 0);
            Assert.True(source.Reads > opened, "the run read nothing");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A source that counts the reads asked of it, single or batched.</summary>
    private sealed class CountingSource(ISegmentSource inner) : ISegmentSource
    {
        private int _reads;

        internal int Reads => Volatile.Read(ref _reads);

        public long Length => inner.Length;

        public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAsync(range, cancellationToken);
        }

        public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAsync(ranges, leases, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
