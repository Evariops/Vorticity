// The two passes over a text the filter asks for: the first pass places the rows a rare identifier keeps without decoding
// the identifiers it reads, which their encoding compares on their codes, granule by granule.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Layouts;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetTwoPhaseTests
{
    /// <summary>
    /// 100 000 events in time order, each with an identifier, a long common prefix then 16 hexadecimal digits drawn at
    /// random, compressed by FSST and indexed by granule, a rare one held by thirty of them scattered over every object, and a
    /// body of 400 random bytes: a filter on the rare identifier reads its column by the granules its postings name, and
    /// FSST answers the comparison on their codes, where the first pass decoded every identifier of those granules to
    /// compare them as text. The rows are the thirty.
    /// </summary>
    [Fact]
    public async Task ARareTextIsComparedOnItsCodes()
    {
        const string RareId = "https://example.invalid/vortex/traces/0123456789abcdef";
        Random random = new Random(0xD6E);
        HashSet<int> rare = [];
        while (rare.Count < 30)
        {
            rare.Add(random.Next(100_000));
        }

        Tagged[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Tagged(
            Start.AddSeconds(i),
            rare.Contains(i) ? RareId : string.Create(CultureInfo.InvariantCulture, $"https://example.invalid/vortex/traces/{random.NextInt64():x16}"),
            Convert.ToBase64String(RandomBytes(random, 294))))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Tagged.At))], ObjectBytes = 32 << 20 },
            ContentsObjectBytes = 1,
            Identifiers = [nameof(Tagged.Id)],
            IdentifierLocation = IdentifierLocation.Granule,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions
            {
                BlockRows = 512,
                ChunkTargetBytes = 64 << 10,
                Hints = System.Collections.Immutable.ImmutableDictionary<string, EncodingHint>.Empty.Add(nameof(Tagged.Id), EncodingHint.Fsst),
            },
        };
        await using (VortexDataset writing = await VortexDataset.CreateAsync(store, Tagged.Schema, options, Ct))
        {
            for (int from = 0; from < rows.Length; from += 25_000)
            {
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<Tagged>(rows.AsSpan(from, 25_000).ToArray(), Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct);
        long answered = FlatLayoutReader.RangesAnswered;
        List<string> bodies = [];
        await foreach (Tagged row in dataset.Scan<Tagged>().Where(r => r.Id == RareId).ToRecordsAsync(Ct))
        {
            bodies.Add(row.Body);
        }

        answered = FlatLayoutReader.RangesAnswered - answered;
        Console.WriteLine($"TWO PASSES OVER A TEXT: {bodies.Count} rows, {answered} ranges answered on their codes");
        Assert.Equal([.. rows.Where(r => r.Id == RareId).Select(r => r.Body)], bodies);
        Assert.True(answered > 0, "no range of the identifiers was answered on its codes");
    }

    [VortexRecord]
    public partial record struct Tagged(DateTimeOffset At, string Id, string Body);
}
