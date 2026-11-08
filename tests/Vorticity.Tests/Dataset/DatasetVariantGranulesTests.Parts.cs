// A part of a variant's chunk read through a shredded key: the key's rows of the part decoded alone, not the chunk's.
//
// THE ORACLE IS LINQ AND THE VALUES DECODED: the answer is LINQ's, and the scan decodes fewer values than the chunk holds.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetVariantGranulesTests
{
    /// <summary>
    /// The calls of <see cref="AKeysStatisticsAreThoseOfItsValuesAsAColumn"/>, sorted by the service, asked for one service's
    /// calls under a status code of 500 or more through the rows: the scan reads a sixth of each chunk, and decodes the
    /// code's rows of that sixth alone, from the key's range, where the chunk's whole decode, retained for parts no question
    /// read, decoded every row of each chunk; the answer is LINQ's.
    /// </summary>
    [Fact]
    public async Task APartOfAChunkDecodesTheKeysOfItsRowsAlone()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Call[] calls = await WrittenAsync(store);
        string[] expected =
        [
            .. calls.Where(static c => c.Service == "svc-3" && c.Code >= 500).GroupBy(static c => c.Service).Select(static g => $"{g.Key}:{g.Count()}"),
        ];
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        Scan<Call> scan = dataset.Scan<Call>().With(new ScanOptions { UseSynopses = false });
        List<ServiceCount> answers = await scan.Where(c => c.Service == "svc-3" && c.Attributes["code"].AsInt64() >= 500)
            .GroupBy(c => c.Service).Select(g => (g.Key, g.Count())).As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(expected, answers.Select(static a => $"{a.Service}:{a.Count}"));
        long decoded = scan.Counters.ValuesDecoded;
        Console.WriteLine($"PARTS: {decoded} values decoded for {calls.Length / 6} calls of one service, {calls.Length} in all");
        Assert.True(decoded < calls.Length / 2, $"{decoded} values decoded for a sixth of {calls.Length} rows");
    }

    /// <summary>
    /// An object's variant directory asked again by the same question for the same paths: the one the question kept, nothing
    /// read; asked for other paths, read and kept apart; asked by another question, read again, nothing kept past the question
    /// that read it.
    /// </summary>
    [Fact]
    public async Task AQuestionKeepsTheVariantDirectoriesItReadForItsOtherScans()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        _ = await WrittenAsync(store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        int attributes = dataset.Snapshot.Schema.DType.IndexOfField(nameof(Call.Attributes));
        byte[][][] code = [[System.Text.Encoding.UTF8.GetBytes("code")]];
        byte[][][] route = [[System.Text.Encoding.UTF8.GetBytes("route")]];
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
            QuestionReads question = new QuestionReads();
            (VariantDirectory? first, int read, _) = await VariantDirectory.ReadAsync(lease.File, attributes, code, Ct, question);
            if (first is null)
            {
                continue;
            }

            objects++;
            Assert.Equal(1, read);
            (VariantDirectory? again, int readAgain, long bytesAgain) = await VariantDirectory.ReadAsync(lease.File, attributes, [[.. code[0]]], Ct, question);
            Assert.Same(first, again);
            Assert.Equal((0, 0L), (readAgain, bytesAgain));
            (VariantDirectory? other, int readOther, _) = await VariantDirectory.ReadAsync(lease.File, attributes, route, Ct, question);
            Assert.NotNull(other);
            Assert.NotSame(first, other);
            Assert.Equal(1, readOther);
            (VariantDirectory? next, int readNext, _) = await VariantDirectory.ReadAsync(lease.File, attributes, code, Ct, new QuestionReads());
            Assert.NotSame(first, next);
            Assert.Equal(1, readNext);
        }

        Assert.True(objects > 0);
    }

    /// <summary>
    /// An object's path map asked again by the same question for the same keys, in any order: the one the question kept;
    /// for other keys, or by another question, read again (the block statistics: <c>VariantBlockStatsTests</c>).
    /// </summary>
    [Fact]
    public async Task AQuestionKeepsThePathMapsItReadForItsOtherScans()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        _ = await WrittenAsync(store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        int attributes = dataset.Snapshot.Schema.DType.IndexOfField(nameof(Call.Attributes));
        int maps = 0;
        int keyed = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
            QuestionReads question = new QuestionReads();
            if (await VariantPathMap.ReadAsync(lease.File, attributes, ["code", "route"], Ct, question) is { } map)
            {
                maps++;
                Assert.Same(map, await VariantPathMap.ReadAsync(lease.File, attributes, ["route", "code"], Ct, question));
                Assert.NotSame(map, await VariantPathMap.ReadAsync(lease.File, attributes, ["code"], Ct, question));
                Assert.NotSame(map, await VariantPathMap.ReadAsync(lease.File, attributes, ["code", "route"], Ct, new QuestionReads()));
            }

            // The reads by keys made of them, the same for the same columns and keys in another order.
            Dictionary<string, string[]> columns = new() { [nameof(Call.Attributes)] = ["code", "route"] };
            if (await VariantKeyReads.LoadAsync(lease.File, columns, Ct, question) is { } reads)
            {
                keyed++;
                Dictionary<string, string[]> again = new() { [nameof(Call.Attributes)] = ["route", "code"] };
                Assert.Same(reads, await VariantKeyReads.LoadAsync(lease.File, again, Ct, question));
                Assert.NotSame(reads, await VariantKeyReads.LoadAsync(lease.File, new Dictionary<string, string[]> { [nameof(Call.Attributes)] = ["code"] }, Ct, question));
                Assert.NotSame(reads, await VariantKeyReads.LoadAsync(lease.File, columns, Ct, new QuestionReads()));
            }
        }

        Assert.True(maps > 0);
        Assert.True(keyed > 0);
    }

    /// <summary>
    /// What a question read of an object stays while a lease holds the object and goes with the last one: held twice, a map
    /// is the one kept after the first release, read again after the second; read with no hold, it stays.
    /// </summary>
    [Fact]
    public async Task AnObjectsReadsGoWithTheLastLeaseOfIt()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        _ = await WrittenAsync(store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        int attributes = dataset.Snapshot.Schema.DType.IndexOfField(nameof(Call.Attributes));
        int maps = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
            QuestionReads question = new QuestionReads();
            question.Hold(lease.File);
            question.Hold(lease.File);
            if (await VariantPathMap.ReadAsync(lease.File, attributes, ["code"], Ct, question) is not { } map)
            {
                continue;
            }

            maps++;
            question.KeepGeometry(lease.File, null);
            question.Release(lease.File);
            Assert.Same(map, await VariantPathMap.ReadAsync(lease.File, attributes, ["code"], Ct, question));
            Assert.True(question.TryGetGeometry(lease.File, out _));
            question.Release(lease.File);
            Assert.Equal(0, question.ReadsKept);
            Assert.False(question.TryGetGeometry(lease.File, out _));
            VariantPathMap? again = await VariantPathMap.ReadAsync(lease.File, attributes, ["code"], Ct, question);
            Assert.NotSame(map, again);
            Assert.Same(again, await VariantPathMap.ReadAsync(lease.File, attributes, ["code"], Ct, question));
            Assert.Equal(1, question.ReadsKept);
        }

        Assert.True(maps > 0);
    }

    /// <summary>
    /// A question on a variant's key, its rows, then their count by service: LINQ's answers, the objects the rows' question
    /// reads keeping their reads while they are read, and neither question keeping anything of them once it has ended.
    /// </summary>
    [Fact]
    public async Task AQuestionKeepsNothingOfAnObjectPastItsLastLease()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Call[] calls = await WrittenAsync(store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        int expected = calls.Count(static c => c.Code >= 500);
        Scan<Call> scan = Asked(dataset);
        QuestionReads reads = ((DatasetScanSource)scan.Source).Reads;
        int rows = 0;
        int kept = 0;
        await foreach (Call call in scan.ToRecordsAsync(Ct))
        {
            rows++;
            kept = Math.Max(kept, reads.ReadsKept);
        }

        Assert.Equal(expected, rows);
        Assert.True(kept > 0, "the objects read kept their reads while they were read");
        Assert.Equal(0, reads.ReadsKept);

        Scan<Call> grouped = Asked(dataset);
        List<ServiceCount> answers = await grouped.GroupBy(c => c.Service).Select(g => (g.Key, g.Count())).As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(expected, answers.Sum(static a => (long)a.Count));
        Assert.Equal(0, ((DatasetScanSource)grouped.Source).Reads.ReadsKept);

        static Scan<Call> Asked(VortexDataset dataset) =>
            dataset.Scan<Call>().With(new ScanOptions { UseSynopses = false }).Where(c => c.Attributes["code"].AsInt64() >= 500);
    }
}
