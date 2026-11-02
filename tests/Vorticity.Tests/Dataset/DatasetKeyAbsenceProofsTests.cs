// The absence of a value of a variant's key proven granule by granule (the query path plan, B4 and V4): a filter that leaves
// one value of a shredded key out, or a set of them, is answered by the granules the key's indexes do not place the values
// in, judged under the rest of the filter, the key's statistics proving each row holds a value of it: a Bloom filter
// declared on the key of the dataset, Attributes['user.id'].AsString(), which every job that writes the key builds, or the
// postings of the key declared an identifier.
//
// THE ORACLE IS LINQ AND THE ROWS' PATH: every answer is LINQ's; the granules' path reads less than the rows'.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyAbsenceProofsTests
{
    private const string UserId = "Attributes['user.id'].AsString()";

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The calls of <see cref="WrittenAsync"/> under a Bloom filter declared on the user's key before the first write. Every
    /// call but one user's, and every call but two users', by service, are answered by the granules whose filters do not hold
    /// the users, the key's statistics proving every row holds a user; the four granules the filters place them in read as
    /// rows. The filter of a key whose values are nearly all distinct weighs as much as the key, 787 KB here, and a granule
    /// read as rows reads the key's dictionary whole: the granules' path reads a tenth less than the rows';
    /// an identifier's postings are the index for such a key. An index of another kind on a key is refused.
    /// </summary>
    [Fact]
    public async Task AValueOfAKeyLeftOutIsPlacedByTheKeysBloomFilter()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Call[] calls = await WrittenAsync(store, identifier: false);
        await AskedAsync(store, calls, 2, least: 1);
    }

    /// <summary>
    /// The calls of <see cref="WrittenAsync"/>, the user's key declared an identifier: every call but one user's, and every
    /// call but two users', by service, and every call but one user's by tenant, a key too, answered by the granules the
    /// postings of the key's partition do not place the users in, as every request but one is.
    /// </summary>
    [Fact]
    public async Task AValueOfAnIdentifierKeyLeftOutIsPlacedByItsPostings()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Call[] calls = await WrittenAsync(store, identifier: true);
        await AskedAsync(store, calls, 3, least: 1);
    }

    /// <summary>
    /// Asks the first <paramref name="questions"/> questions of <paramref name="store"/>, through the granules and through the
    /// rows: the answers LINQ's, every unit answered by its granules, which read <paramref name="least"/> times fewer bytes.
    /// Without a segment cache: the rows' second pass reads again the segments of the filter's key its first read, which a
    /// cache serves (question 0 of the Bloom filter's: 1.17 MB through the rows, 1.44 through the granules, where without
    /// one they read 2.28 and 2.04).
    /// </summary>
    private static async Task AskedAsync(CountingObjectStore store, Call[] calls, int questions, int least)
    {
        string user = calls[77_777].User;
        string other = calls[200_001].User;
        for (int q = 0; q < questions; q++)
        {
            string[] expected = [.. calls.Where(c => q == 1 ? c.User != user && c.User != other : c.User != user)
                .GroupBy(c => q == 2 ? c.Tenant : c.Service).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)];
            long[] bytes = new long[2];
            foreach (bool synopses in (bool[])[false, true])
            {
                await using VortexDataset fresh = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = Uncached.Session, ContentsObjectBytes = 0, SheetObjectBytes = 0, CostProfile = CostProfile.Memory }, Ct);
                store.Reset();
                Vorticity.Aggregation question = Asked(fresh.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }), q, user, other);
                string[] answers = [.. (await question.As<GroupCount>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Key}:{a.Count}")];
                bytes[synopses ? 1 : 0] = store.BytesRead;
                Assert.Equal(expected, answers.Order(StringComparer.Ordinal));
                if (synopses)
                {
                    ScanPlan plan = await Asked(fresh.Scan<Call>(), q, user, other).ExplainAsync(Ct);
                    Assert.All(plan.Units, static unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
                }
            }

            Console.WriteLine($"KEY ABSENCE: question {q}, {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
            Assert.True(bytes[1] * least < bytes[0], $"question {q}: {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        }

        static Vorticity.Aggregation Asked(Scan<Call> scan, int question, string user, string other) => question switch
        {
            0 => scan.Where(c => c.Attributes["user.id"].AsString() != user).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            1 => scan.Where(c => !c.Attributes["user.id"].AsString().In(user, other)).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            _ => scan.Where(c => c.Attributes["user.id"].AsString() != user).GroupBy(c => c.Attributes["tenant"].AsString()).Select(g => (g.Key, g.Count())),
        };
    }

    /// <summary>
    /// 262 144 calls of four services, 65 536 each, sorted by the service, then the instant, 32 granules of one service each,
    /// whose attributes hold a user among 200 000 and a tenant that follows the service, written to <paramref name="store"/>
    /// and consolidated into one object of a day: under a Bloom filter on the user's key, or the key an identifier.
    /// </summary>
    private static async Task<Call[]> WrittenAsync(CountingObjectStore store, bool identifier)
    {
        Random random = new Random(73);
        Call[] calls = [.. Enumerable.Range(0, 4 * 65_536).Select(i => new Call(
            Start.AddMilliseconds(i * 29L), $"svc-{i % 4}",
            Variant.Object(("user.id", Variant.From($"user-{random.Next(200_000):D6}")), ("tenant", Variant.From($"t-{i % 4 / 2}")))))];
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            Identifiers = identifier ? [UserId] : [],
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        if (!identifier)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.IndexAsync(IndexPolicy.None.Postings(UserId), cancellationToken: Ct));
            await dataset.IndexAsync(IndexPolicy.None.Bloom(UserId), cancellationToken: Ct);
        }

        foreach (Call[] half in (Call[][])[calls[..131_072], calls[131_072..]])
        {
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Call>(half, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync(Ct);
        int attributes = dataset.Snapshot.Schema.DType.IndexOfField(nameof(Call.Attributes));
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.NotNull(held.Entry.Contents!.PathGranuleStatsOf(attributes, "user.id", PathClass.String));

            // A read of a part of the column reads the user's dictionary values whole, 517 KB of its 1.1 MB, and says so.
            long columnBytes = held.Entry.Contents.ColumnBytes[attributes];
            Assert.InRange(held.Entry.Contents.ColumnWholeBytes[attributes], columnBytes * 2 / 5, columnBytes / 2);
            if (!identifier)
            {
                Assert.Contains(held.Entry.Contents.Locating ?? [], entry => entry.Column == (uint)attributes && entry.Kind == IndexKinds.BloomSbbf && entry.Key == "user.id" && entry.Bytes > 0);
            }
        }

        return calls;
    }

    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, Variant Attributes)
    {
        internal readonly string User => Attributes["user.id"]!.Value.AsString()!;

        internal readonly string Tenant => Attributes["tenant"]!.Value.AsString()!;
    }

    [VortexRecord]
    public partial record struct GroupCount(string? Key, long Count);
}
