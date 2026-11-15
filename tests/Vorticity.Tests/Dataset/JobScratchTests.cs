// A job of large inputs reads them through the local scratch: each copied once, read from the disk, the job's directory
// gone with it; one the scratch budget cannot hold is refused before it reads anything.
//
// THE ORACLE IS THE ROWS AND THE SCRATCH: every row read back, the bytes the job put on the scratch, the scratch empty after.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class JobScratchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Eight appends of some 3 MB each, merged: the job copies them to its scratch, reads them there, and leaves nothing.</summary>
    [Fact]
    public async Task ALargeJobReadsItsInputsThroughTheScratch()
    {
        string scratch = Scratch();
        try
        {
            await using VortexSession session = VortexSession.Create(o => o.ScratchDirectory = scratch);
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
            List<Row> all = await AppendAsync(dataset, appends: 8);

            CompactionResult? job = await dataset.CompactAsync(cancellationToken: Ct);

            Assert.NotNull(job);
            Assert.True(job.BytesIn >= JobInputs.FromBytes, $"the job read {job.BytesIn} bytes");
            Assert.True(job.ScratchBytes >= job.BytesIn, $"the job put {job.ScratchBytes} bytes on the scratch for {job.BytesIn} read");
            Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The same job under a scratch budget of a mebibyte: refused before it reads, with nothing committed.</summary>
    [Fact]
    public async Task AJobTheScratchCannotHoldIsRefused()
    {
        string scratch = Scratch();
        try
        {
            await using VortexSession session = VortexSession.Create(o =>
            {
                o.ScratchDirectory = scratch;
                o.ScratchBudget = new ScratchBudget(1 << 20);
            });
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session), Ct);
            await AppendAsync(dataset, appends: 8);
            ulong before = dataset.Version;

            await Assert.ThrowsAsync<VortexMemoryException>(async () => await dataset.CompactAsync(cancellationToken: Ct));
            Assert.Equal(before, dataset.Version);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>A directory of the test's own on the disk the tests run from, never one in memory.</summary>
    private static string Scratch()
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static DatasetOptions Options(VortexSession session) => new DatasetOptions
    {
        Session = session,
        Layout = new DatasetLayout
        {
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Sequence))],
            MergeFanout = 8,
        },
    };

    /// <summary><paramref name="appends"/> appends of 16 384 rows each, every row with 192 characters of random text, which compress little.</summary>
    private static async Task<List<Row>> AppendAsync(VortexDataset dataset, int appends)
    {
        List<Row> all = [];
        byte[] noise = new byte[144];
        Random random = new Random(53);
        for (int a = 0; a < appends; a++)
        {
            Row[] rows = new Row[16_384];
            for (int i = 0; i < rows.Length; i++)
            {
                random.NextBytes(noise);
                rows[i] = new Row($"svc-{i % 7}", all.Count + i, Convert.ToBase64String(noise));
            }

            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return all;
    }

    [VortexRecord]
    public partial record struct Row(string Service, long Sequence, string Payload);
}
