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
}
