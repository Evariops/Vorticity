// What a dataset's job keeps of each column and of each path of a variant, the means a question is answered by without
// its rows: the object's counts, the statistics over its granules, where each value lies, the index directory's entries
// (a Bloom filter, an identifier's postings), the zone maps of a variant's keys, and each path's presence by granule.
//
// THE ORACLE IS THE MATRIX OF THE QUERY PATH PLAN (2026-10-31, section 1.3), as the job writes it today: a column keeps its
// counts and its statistics over the granules where its values allow, where its values lie where they cluster, and the
// Bloom filter the dataset was given; an identifier hands its postings to its partition's index and keeps none; a key of
// a variant keeps its typed column and its statistics over the granules, and its zone maps and where its values lie only
// where they can prune; a nested path keeps its typed column, a rare key its presence. Each lot that gives a path a mean flips
// its line.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSynopsesMatrixTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 40 000 rows, five granules, consolidated into one object: a service, a status, a declared identifier and a tag under
    /// a Bloom filter; attributes holding a route and a code on every row, scattered, a tenant on every row that follows the
    /// service, a nested method on every row, and a debug text on one row in fifty of one service's, which the sort key
    /// holds together in a granule or two.
    /// </summary>
    [Fact]
    public async Task EachColumnAndPathKeepsWhatThePlanSays()
    {
        Random random = new Random(31);
        Row[] rows = [.. Enumerable.Range(0, 40_000).Select(i =>
        {
            int service = random.Next(6);
            List<(string, Variant)> attributes =
            [
                ("route", Variant.From($"/r/{random.Next(12)}")),
                ("tenant", Variant.From($"t-{service / 2}")),
                ("code", Variant.From(200 + (random.Next(10) == 0 ? 300 : random.Next(4)))),
                ("http", Variant.Object([("method", Variant.From(random.Next(3) == 0 ? "POST" : "GET"))])),
            ];
            if (service == 0 && i % 50 == 7)
            {
                attributes.Add(("debug", Variant.From($"trace-{random.Next(50)}")));
            }

            return new Row(Start.AddMilliseconds(i * 53L), $"svc-{service:D2}", 200 + random.Next(5), $"id-{i:D6}", $"tag-{random.Next(400)}", Variant.Object([.. attributes]));
        })];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            Identifiers = [nameof(Row.Id)],
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        await dataset.IndexAsync(IndexPolicy.Auto.Bloom(nameof(Row.Tag)), cancellationToken: Ct);
        foreach (Row[] half in (Row[][])[rows[..20_000], rows[20_000..]])
        {
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Row>(half, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync(Ct);
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        ObjectContents contents = held.Entry.Contents!;
        Vorticity.Types.DType schema = dataset.Snapshot.Schema.DType;
        int attributesField = schema.IndexOfField(nameof(Row.Attributes));
        await using ObjectLease lease = await dataset.RentAsync(held.Entry, Ct);
        IndexDirectory directory = (await lease.File.ReadIndexDirectoryAsync(Ct))!;
        VariantDirectory paths = (await dataset.ReadPathDirectoryAsync(held.Entry, attributesField, Ct))!;
        (string Key, PathClass Class)[] keys = [("route", PathClass.String), ("tenant", PathClass.String), ("code", PathClass.Integer), ("http.method", PathClass.String), ("debug", PathClass.String)];
        (VariantBlockStats? blocks, _, _) = await VariantBlockStats.ReadAsync(lease.File, attributesField, keys, Ct);

        // What each column keeps, then each path: the object's counts, its statistics over the granules, where its values
        // lie, its index kinds, its zone maps by block, and for a path its place and the granules its presence names.
        SortedDictionary<string, string> kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string column in (string[])[nameof(Row.Service), nameof(Row.Status), nameof(Row.Id), nameof(Row.Tag)])
        {
            int field = schema.IndexOfField(column);
            List<string> means = [];
            Add(means, contents.CountsOf(field) is not null, "counts");
            Add(means, contents.GranuleStatsOf(field) is not null, "granule-stats");
            Add(means, contents.LocateOf(field) is not null, "locate");
            means.AddRange(directory.Entries.Where(entry => entry.VariantPath is null && entry.ColumnPath is [uint at] && at == (uint)field).Select(static entry => Short(entry.Kind)));
            kept[column] = string.Join(' ', means.Distinct().Order(StringComparer.Ordinal));
        }

        foreach ((string key, PathClass @class) in keys)
        {
            string[] steps = key.Split('.');
            List<string> means = [];
            PathEntry? entry = paths.Of(steps).ToArray().FirstOrDefault(entry => entry.Class == @class && entry.Values > 0);
            Add(means, entry is not null, entry is null ? string.Empty : entry.Place.ToString().ToLowerInvariant());
            Add(means, entry is { Groups: not null }, "presence");
            Add(means, contents.PathLocateOf(attributesField, key, @class) is not null, "locate");
            Add(means, contents.PathGranuleStatsOf(attributesField, key, @class) is not null, "granule-stats");
            Add(means, blocks is not null && blocks.Keys.Contains((key, @class)), "zones");
            means.AddRange(directory.Entries.Where(index => index.VariantPath is { } path && path.SequenceEqual(steps)).Select(static index => Short(index.Kind)));
            kept[$"{key}:{@class}"] = string.Join(' ', means.Distinct().Order(StringComparer.Ordinal));
        }

        // No column statistics over the granules but of top-level columns: a key keeps its own, beside them.
        Add(kept, contents.GranuleStats.Any(region => region.Field >= schema.FieldCount || region.Field == attributesField), "statistics of a slot past the columns");
        SortedDictionary<string, string> expected = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"] = "column.sheet",
            ["Service"] = "column.counts column.sheet counts granule-stats granule.stats locate",
            ["Status"] = "column.counts column.sheet counts granule-stats granule.stats",
            ["Tag"] = "bloom.sbbf column.counts column.sheet counts",
            ["code:Integer"] = "granule-stats granule.stats typed",
            ["debug:String"] = "presence residual",
            ["http.method:String"] = "typed",
            ["route:String"] = "granule-stats granule.stats typed",
            ["tenant:String"] = "granule-stats granule.stats locate typed zones",
        };
        Assert.True(
            expected.SequenceEqual(kept),
            $"kept:{Environment.NewLine}{string.Join(Environment.NewLine, kept.Select(static pair => $"[\"{pair.Key}\"] = \"{pair.Value}\","))}");
    }

    private static void Add(List<string> means, bool holds, string mean)
    {
        if (holds && mean.Length > 0)
        {
            means.Add(mean);
        }
    }

    private static void Add(SortedDictionary<string, string> kept, bool holds, string what)
    {
        if (holds)
        {
            kept[what] = "yes";
        }
    }

    /// <summary>An index kind's name without its prefix and version: <c>bloom.sbbf</c>.</summary>
    private static string Short(string kind) => kind.Replace("vorticity.", string.Empty, StringComparison.Ordinal).Replace(".v1", string.Empty, StringComparison.Ordinal);

    [VortexRecord]
    public partial record struct Row(DateTime At, string Service, long Status, string Id, string Tag, Variant Attributes);
}
