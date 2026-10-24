using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Tests.File;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class ObjectContentsTests
{
    /// <summary>
    /// Two sealed hours of calls in chunks of 16 rows, their chunk directories laid in pages of four chunks: the contents say
    /// the pages, each column's page table the region they name; every slice reads the rows a whole open holds, its read
    /// asking for its columns' page tables and the pages of its rows alone, never a directory whole; and a later reader of
    /// the object asks for no table again, its pages read by their checksums.
    /// </summary>
    [Fact]
    public async Task ASliceReadsThePagesOfItsRowsAlone()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Call.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(1_000), BlockRows = 16, ChunkTargetBytes = 1, ChunkPageEntries = 4 },
        }, Ct);
        Random random = new Random(5);
        for (int part = 0; part < 16; part++)
        {
            Call[] rows = [.. Enumerable.Range(0, 400).Select(i => new Call(
                Noon.AddSeconds((part * 400 + i) * 1.25), $"region-{random.Next(3)}", 200 + (random.Next(5) * 100), random.NextInt64(1_000_000)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        ObjectEntry hour = default!;
        await foreach (TreeEntry leaf in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, Ct))
        {
            hour = ObjectEntry.FromBytes(leaf.Value.Span);
            break;
        }

        // Every column paged alike: chunks of 16 rows, four a page.
        ObjectContents contents = hour.Contents!;
        ChunkPaging paging = contents.Paging!;
        Assert.Equal(4, paging.PageEntries);
        Assert.Equal(Enumerable.Range(0, contents.ChunkDirectories.Count), paging.Columns);
        Assert.True(paging.PageRows.Length > 8, $"{paging.PageRows.Length} pages");
        Assert.All(contents.ChunkDirectories, table => Assert.Equal(SliceChunks.TableBytes(paging.PageRows.Length), (int)table.Length));
        ChunkPaging back = ObjectContents.From(contents.ToBytes())!.Paging!;
        Assert.Equal((paging.PageEntries, paging.Count), (back.PageEntries, back.Count));
        Assert.Equal(paging.PageRows, back.PageRows);
        Assert.Equal(paging.Columns, back.Columns);

        byte[] bytes = new byte[hour.Bytes];
        await store.ReadAsync(hour.Key, 0, bytes, null, Ct);
        await using ObjectLease lease = await dataset.RentAsync(hour, Ct);
        List<Call> stored = await lease.File.Scan<Call>().ToRecordsAsync(Ct).ToListAsync(Ct);
        int pageBytes = paging.PageEntries * SliceChunks.EntryBytes;
        for (int s = 0; s < contents.Slices.Slices.Count; s++)
        {
            TimeSlice slice = contents.Slices.Slices[s];
            TestSegmentSource source = new(bytes) { Ranges = [] };
            using (SliceReader reader = SliceReader.Open(source, null, contents)!)
            using (RecordBatch batch = await reader.ReadSliceAsync(s, null, Ct))
            {
                Columns<Call> columns = batch.As<Call>();
                DateTimeOffset[] at = new DateTimeOffset[batch.RowCount];
                long[] durations = new long[batch.RowCount];
                columns.Column<DateTimeOffset>(0).CopyTo(at);
                columns.Column<long>(3).CopyTo(durations);
                Assert.Equal(stored.GetRange((int)slice.FirstRow, (int)slice.Rows).Select(r => (r.At, r.Duration)), at.Zip(durations));
            }

            // Of each column's directory, its page table, the first reader's alone, and the pages of the slice's rows, each whole.
            (int from, int to) = paging.PagesOf(slice.FirstRow, slice.Rows);
            for (int c = 0; c < contents.ChunkDirectories.Count; c++)
            {
                IndexSegment table = contents.ChunkDirectories[c];
                long start = (long)table.Offset;
                long end = start + table.Length + ((long)paging.Count * SliceChunks.EntryBytes);
                List<(long Offset, long Length)> read = [.. source.Ranges!.Where(r => r.Offset >= start && r.Offset < end)];
                Assert.Equal(s == 0 ? 1 : 0, read.Count(r => r.Offset == start && r.Length == table.Length));
                Assert.Equal(
                    Enumerable.Range(from, to - from + 1).Select(page => (start + table.Length + ((long)page * pageBytes), (long)paging.Page(page).Length)),
                    read.Where(r => r.Offset != start).OrderBy(r => r.Offset));
            }
        }
    }
}
