// The search of one object's sparse index, over an index built in memory: what it judges, what it holds, which granules it
// keeps.
//
// THE ORACLE IS THE GRANULES' KEYS: granule i holds keys from its key to the next one's, both included, the last to the
// object's last key; a filter whose conjuncts on the indexed components a box judges exactly keeps a granule exactly when
// that span meets them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyPruningTests
{
    /// <summary>
    /// A window on the hour under <c>(Service, Hour)</c> bounds the second component alone: the search judges boxes, as many
    /// as it counts, and the index it searched holds fewer bytes than its keys, coded forward but a key of each run of 16.
    /// </summary>
    [Fact]
    public async Task ASearchCountsTheBoxesItJudgesAndTheIndexTheBytesItHolds()
    {
        DatasetSchema schema = await SchemaAsync(new DatasetLayout
        {
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour)],
        });
        List<FilterLiteral[]> keys = [];
        for (int service = 0; service < 6; service++)
        {
            for (int hour = 0; hour < 24; hour += 2)
            {
                keys.Add([FilterLiteral.From($"svc-{service:D2}"), Micros(Start.AddHours(hour))]);
            }
        }

        GranuleKeys index = Index(schema, keys, [FilterLiteral.From("svc-05"), Micros(Start.AddHours(23))]);
        DateTime ten = Start.AddHours(10);
        KeyPruning pruning = KeyPruning.Of(schema, Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(ten.AddHours(1)))))!;
        List<(int First, int End)> kept = pruning.Granules(index, default);

        Assert.NotEmpty(kept);
        Assert.True(pruning.Judgments > kept.Count, $"{pruning.Judgments} boxes judged for {kept.Count} ranges kept");
        long keyBytes = keys.Sum(key => (long)Encode(schema, key).Length);
        Assert.True(index.ResidentBytes > 0 && index.ResidentBytes < keyBytes, $"{index.ResidentBytes} bytes held for {keyBytes} bytes of keys");
    }

    /// <summary>
    /// An equality on the leading component, a set of it, a range of it, or an equality then a window on the hour, is found
    /// by bisection: no box judged, a few comparisons an interval, and the granules its rows lie in kept, at most one more at
    /// each end. Before the bisection each judged dozens of boxes.
    /// </summary>
    [Fact]
    public async Task AFilterOnTheLeadingComponentsIsFoundByBisectionWithNoBoxJudged()
    {
        DatasetSchema schema = await SchemaAsync(KeyLayout);
        List<KeyRow> rows = KeyRows(new Random(64), 4_000);
        GranuleKeys index = Index(schema, rows, granuleRows: 8);
        DateTime ten = Start.AddHours(10);
        (string Label, VortexExpr Filter, Func<KeyRow, bool> Keep)[] questions =
        [
            ("Service == svc-07", Eq(nameof(Row.Service), FilterLiteral.From("svc-07")), static row => row.Service == "svc-07"),
            ("Service in (svc-03, a long one, svc-11)", Expr.In(Expr.Field(nameof(Row.Service)), [FilterLiteral.From("svc-03"), FilterLiteral.From(LongService(2)), FilterLiteral.From("svc-11")]),
                static row => row.Service is "svc-03" or "svc-11" || row.Service == LongService(2)),
            ("Service in [svc-04, svc-06)", Expr.And(Ge(nameof(Row.Service), FilterLiteral.From("svc-04")), Lt(nameof(Row.Service), FilterLiteral.From("svc-06"))),
                static row => string.CompareOrdinal(row.Service, "svc-04") >= 0 && string.CompareOrdinal(row.Service, "svc-06") < 0),
            ("Service == svc-05 and At in [10h, 12h)", Expr.And(Eq(nameof(Row.Service), FilterLiteral.From("svc-05")), Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(ten.AddHours(2))))),
                row => row.Service == "svc-05" && row.At >= ten && row.At < ten.AddHours(2)),
        ];

        // Each bound bisects the runs of 16 by their whole keys, then reads the keys of one run.
        int log = (int)Math.Ceiling(Math.Log2(((index.Granules + GranuleKeys.RestartEvery - 1) / GranuleKeys.RestartEvery) + 1));
        foreach ((string label, VortexExpr filter, Func<KeyRow, bool> keep) in questions)
        {
            KeyPruning pruning = KeyPruning.Of(schema, filter)!;
            List<(int First, int End)> kept = pruning.Granules(index, default);
            Assert.Equal(0, pruning.Judgments);
            Assert.True(pruning.Intervals.Exact, $"{label}: the intervals are not exact");
            int intervals = pruning.Intervals.Count;
            Assert.True(pruning.Comparisons <= intervals * 2 * ((2 * log) + 2 + GranuleKeys.RestartEvery), $"{label}: {pruning.Comparisons} keys compared for {intervals} interval(s) over {index.Granules} granules");
            AssertKept(rows, index, kept, keep, label);
        }
    }

    /// <summary>
    /// Filters drawn at random over the key <c>(Service, Hour(At), Sequence)</c>, texts past a block of 32 bytes among the
    /// services: equalities, sets, ranges strict or not, through the hour's function, contradictions, conjuncts past the
    /// bounded components and conjuncts the intervals cannot hold. Every granule a kept row lies in is kept, at most one
    /// more at each end of a range; exact intervals judge no box.
    /// </summary>
    [Fact]
    public async Task RandomFiltersKeepTheGranulesTheirRowsLieIn()
    {
        DatasetSchema schema = await SchemaAsync(KeyLayout);
        Random random = new Random(65);
        List<KeyRow> rows = KeyRows(random, 3_000);
        GranuleKeys index = Index(schema, rows, granuleRows: 4, pageEntries: 8);
        for (int question = 0; question < 400; question++)
        {
            (string label, VortexExpr filter, Func<KeyRow, bool> keep) = RandomFilter(random, rows);
            KeyPruning pruning = KeyPruning.Of(schema, filter)!;
            List<(int First, int End)> kept = pruning.Granules(index, default);
            Assert.True(!pruning.Intervals.Exact || pruning.Judgments == 0, $"{label}: exact intervals judged {pruning.Judgments} boxes");

            // Within what the boxes alone keep: an interval knows a granule's rows start at its key, which its box forgets
            // past the first component the two keys part on. Boxes keep what their spans say, many more than their rows
            // where those vary, and an hour more than a window within it.
            HashSet<int> boxed = [.. pruning.Excluded(index, default).SelectMany(static range => Enumerable.Range(range.First, range.End - range.First))];
            Assert.True(kept.TrueForAll(range => Enumerable.Range(range.First, range.End - range.First).All(boxed.Contains)), $"{label}: a granule kept that the boxes refute");
            AssertKept(rows, index, kept, keep, label, edges: false);
        }
    }

    /// <summary>The key of <see cref="KeyRow"/>s: the service, the hour of the instant, the sequence.</summary>
    private static DatasetLayout KeyLayout => new DatasetLayout
    {
        SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour), LayoutValue.Column(nameof(Row.Sequence))],
    };

    /// <summary>A row's key values.</summary>
    private readonly record struct KeyRow(string Service, DateTime At, long Sequence)
    {
        internal DateTime Hour => new DateTime(At.Ticks - (At.Ticks % TimeSpan.TicksPerHour), DateTimeKind.Utc);

        internal FilterLiteral[] Key => [FilterLiteral.From(Service), Micros(Hour), FilterLiteral.From(Sequence)];
    }

    /// <summary>A service whose name passes a block of the encoding of texts, 32 bytes.</summary>
    private static string LongService(int i) => $"svc-{new string('x', 40)}-{i:D2}";

    /// <summary><paramref name="count"/> rows over 16 services, two of them long, a day of instants, ordered by the key.</summary>
    private static List<KeyRow> KeyRows(Random random, int count) =>
        [.. Enumerable.Range(0, count)
            .Select(i => new KeyRow(
                random.Next(0, 8) == 0 ? LongService(random.Next(0, 3)) : $"svc-{random.Next(0, 14):D2}",
                Start.AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay) / 10 * 10),
                random.Next(-1_000, 1_000)))
            .OrderBy(static row => row.Service, StringComparer.Ordinal).ThenBy(static row => row.Hour).ThenBy(static row => row.Sequence)];

    /// <summary>The sparse index of <paramref name="rows"/>, ordered by the key, in granules of <paramref name="granuleRows"/>.</summary>
    private static GranuleKeys Index(DatasetSchema schema, List<KeyRow> rows, int granuleRows, int pageEntries = 16)
    {
        List<FilterLiteral[]> keys = [];
        for (int row = 0; row < rows.Count; row += granuleRows)
        {
            keys.Add(rows[row].Key);
        }

        return Index(schema, keys, rows[^1].Key, granuleRows, pageEntries);
    }

    /// <summary>
    /// The granules kept hold every granule a kept row lies in, and with <paramref name="edges"/> at most one more at each end
    /// of each range.
    /// </summary>
    private static void AssertKept(List<KeyRow> rows, GranuleKeys index, List<(int First, int End)> kept, Func<KeyRow, bool> keep, string label, bool edges = true)
    {
        HashSet<int> needed = [];
        for (int row = 0; row < rows.Count; row++)
        {
            if (keep(rows[row]))
            {
                needed.Add(row / index.GranuleRows);
            }
        }

        for (int i = 1; i < kept.Count; i++)
        {
            Assert.True(kept[i - 1].End < kept[i].First, $"{label}: ranges {kept[i - 1]} and {kept[i]} touch or go back");
        }

        HashSet<int> granules = [.. kept.SelectMany(static range => Enumerable.Range(range.First, range.End - range.First))];
        if (!needed.IsSubsetOf(granules))
        {
            Assert.Fail($"{label}: granule {needed.Except(granules).First()}, which holds a kept row, is passed over");
        }

        Assert.True(!edges || granules.Count <= needed.Count + (2 * kept.Count), $"{label}: {granules.Count} granules kept in {kept.Count} ranges for {needed.Count} holding a kept row");
    }

    /// <summary>A filter drawn over the key's columns, with the rows it keeps.</summary>
    private static (string Label, VortexExpr Filter, Func<KeyRow, bool> Keep) RandomFilter(Random random, List<KeyRow> rows)
    {
        KeyRow at = rows[random.Next(rows.Count)];
        KeyRow other = rows[random.Next(rows.Count)];
        string service = random.Next(0, 6) == 0 ? "svc-99" : at.Service;
        DateTime from = at.At.AddMinutes(random.Next(-90, 30));
        DateTime to = from.AddMinutes(random.Next(0, 240));
        long sequence = at.Sequence;
        (string, VortexExpr, Func<KeyRow, bool>) head = random.Next(0, 4) switch
        {
            0 => ($"Service == {service}", Eq(nameof(Row.Service), FilterLiteral.From(service)), row => row.Service == service),
            1 => ($"Service in ({service}, {other.Service})", Expr.In(Expr.Field(nameof(Row.Service)), [FilterLiteral.From(service), FilterLiteral.From(other.Service)]),
                row => row.Service == service || row.Service == other.Service),
            2 => ($"Service > {service}", Expr.Gt(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From(service))), row => string.CompareOrdinal(row.Service, service) > 0),
            _ => ($"Service <= {service}", Expr.Le(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From(service))), row => string.CompareOrdinal(row.Service, service) <= 0),
        };
        (string, VortexExpr, Func<KeyRow, bool>)? next = random.Next(0, 9) switch
        {
            0 => null,
            1 => ($"At in [{from:HH:mm}, {to:HH:mm})", Expr.And(Ge(nameof(Row.At), Micros(from)), Lt(nameof(Row.At), Micros(to))), row => row.At >= from && row.At < to),
            2 => ($"At == {at.At:HH:mm:ss.fffffff}", Eq(nameof(Row.At), Micros(at.At)), row => row.At == at.At),
            3 => ($"At > {from:HH:mm}", Expr.Gt(Expr.Field(nameof(Row.At)), Expr.Literal(Micros(from))), row => row.At > from),
            4 => ($"Sequence >= {sequence}", Expr.Ge(Expr.Field(nameof(Row.Sequence)), Expr.Literal(FilterLiteral.From(sequence))), row => row.Sequence >= sequence),
            5 => ($"Sequence in ({sequence}, {other.Sequence})", Expr.In(Expr.Field(nameof(Row.Sequence)), [FilterLiteral.From(sequence), FilterLiteral.From(other.Sequence)]),
                row => row.Sequence == sequence || row.Sequence == other.Sequence),
            6 => ($"Service == {other.Service}", Eq(nameof(Row.Service), FilterLiteral.From(other.Service)), row => row.Service == other.Service),
            7 => ($"Service != {other.Service}", Expr.Not(Eq(nameof(Row.Service), FilterLiteral.From(other.Service))), row => row.Service != other.Service),
            _ => ($"At hour {at.Hour:HH} and Sequence < {sequence}", Expr.And(Eq(nameof(Row.At), Micros(at.At)), Expr.Lt(Expr.Field(nameof(Row.Sequence)), Expr.Literal(FilterLiteral.From(sequence)))),
                row => row.At == at.At && row.Sequence < sequence),
        };

        if (random.Next(0, 5) == 0)
        {
            // No head: the exclusion search alone.
            head = next ?? head;
            next = null;
        }

        return next is { } then
            ? ($"{head.Item1} and {then.Item1}", Expr.And(head.Item2, then.Item2), row => head.Item3(row) && then.Item3(row))
            : head;
    }

    /// <summary>The schema of a dataset of <see cref="Row"/>s under <paramref name="layout"/>, which holds no row.</summary>
    private static async Task<DatasetSchema> SchemaAsync(DatasetLayout layout)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions { Layout = layout }, Ct);
        return dataset.Snapshot.Schema;
    }

    /// <summary>A key of the layout's indexed components, row-encoded as an object's sparse index holds it.</summary>
    private static byte[] Encode(DatasetSchema schema, FilterLiteral[] values) =>
        SortKey.Of(schema.Layout!, partition: false, values.Length).Encode(values);

    /// <summary>
    /// The sparse index of an object of granules of <paramref name="granuleRows"/> rows whose keys are <paramref name="keys"/>,
    /// ascending, and whose last row's key is <paramref name="last"/>, every page in hand.
    /// </summary>
    private static GranuleKeys Index(DatasetSchema schema, List<FilterLiteral[]> keys, FilterLiteral[] last, int granuleRows = 8, int pageEntries = 16)
    {
        List<byte[]> encoded = [.. keys.Select(key => Encode(schema, key))];
        (byte[] table, byte[] pages) = GranuleKeys.Build(granuleRows, (long)keys.Count * granuleRows, last.Length, encoded, Encode(schema, last), pageEntries);
        GranuleKeys index = GranuleKeys.From(table) ?? throw new InvalidOperationException("The table does not decode.");
        for (int page = 0; page < index.Pages; page++)
        {
            (long offset, int length, ulong _) = index.PageAt(page);
            Assert.True(index.HasPage(page) || index.TakePage(page, pages.AsSpan((int)offset, length)));
        }

        return index;
    }
}
