// The two codings of a sparse index's keys (spec 10, 4.4): as the row encoding writes them, and with their texts unpadded,
// which the writer keeps where it takes fewer bytes; every reader compares in the coding of the index it reads.
//
// THE ORACLE IS LINQ, THE ROWS AND THE VERIFIER: a dataset whose objects hold both codings answers as LINQ does, each
// object's index keeps the granules its kept rows lie in, a seek lands on the first tuple at or past it, and a consolidation
// in ranges of the key over inputs of both codings holds every row and verifies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyCodingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Four appends compacted under a writer that keeps the row encoding's keys, then four more under the default writer,
    /// rows of two days, a partition each, which the indexes leave out: the objects hold both codings, and answer filters, seeks and the verifier as one; consolidated in ranges of the key,
    /// their objects hold the unpadded coding alone and every row.
    /// </summary>
    [Fact]
    public async Task ObjectsOfEitherCodingAnswerSeekAndConsolidateAsOne()
    {
        Random random = new Random(61);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 4);
        List<Row> all = [];
        await using (VortexDataset padded = await VortexDataset.CreateAsync(store, Row.Schema, Options(session, unpadded: false, Layout([nameof(Row.Service), nameof(Row.Status)], partitioned: true)), Ct))
        {
            await AppendAsync(padded, all, random, Service);
            while (await padded.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(session, unpadded: true, layout: null), Ct);
        await AppendAsync(dataset, all, random, Service);
        (int rows, int unpadded) = await CodingsAsync(dataset);
        Assert.True(rows > 0 && unpadded > 0, $"{rows} object(s) of keys row-encoded, {unpadded} unpadded");
        await AnswersAsync(dataset, all, random);

        IReadOnlyList<CompactionResult> consolidated = await dataset.ConsolidateAsync(Ct);
        Assert.True(consolidated.Any(static job => job.RangesAtOnce > 1), string.Join("; ", consolidated.Select(static job => $"{job.ObjectsIn} in, {job.BytesIn} B, {job.RangesAtOnce} at once")));
        Assert.Equal(0, (await CodingsAsync(dataset)).Rows);
        await AnswersAsync(dataset, all, random);
        Assert.Equal(all, (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence));
    }

    /// <summary>
    /// The writer keeps the row encoding's keys where they take no more bytes: keys of integers alone, the same either way,
    /// and texts of zeros and ones, which unpadded escape every byte. Filters on them answer as LINQ does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheWriterKeepsTheRowEncodingWhereItTakesNoMoreBytes(bool zeros)
    {
        Random random = new Random(zeros ? 67 : 71);
        await using MemoryObjectStore store = new MemoryObjectStore();
        string[] key = zeros ? [nameof(Row.Service), nameof(Row.Status)] : [nameof(Row.Status), nameof(Row.Sequence)];
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(session: null, unpadded: true, Layout(key, partitioned: false)), Ct);
        List<Row> all = [];
        await AppendAsync(dataset, all, random, zeros ? ZerosAndOnes : Service);
        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        (int rows, int unpadded) = await CodingsAsync(dataset);
        Assert.True(rows > 0 && unpadded == 0, $"{rows} object(s) of keys row-encoded, {unpadded} unpadded");
        string service = all[random.Next(all.Count)].Service;
        int status = random.Next(0, 100);
        await CountsAsync(dataset, all, Expr.Eq(Expr.Field(key[0]), Expr.Literal(zeros ? FilterLiteral.From(service) : FilterLiteral.From(status))),
            row => zeros ? row.Service == service : row.Status == status, "the leading component");
    }

    private static DatasetLayout Layout(string[] key, bool partitioned) => new DatasetLayout
    {
        Partition = partitioned ? LayoutValue.Column(nameof(Row.Day)) : null,
        SortKey = [.. key.Select(LayoutValue.Column)],
        ObjectBytes = 1 << 18,
        MergeFanout = 4,
        CalmAfter = null,
    };

    private static DatasetOptions Options(VortexSession? session, bool unpadded, DatasetLayout? layout) => new DatasetOptions
    {
        Session = session ?? VortexSession.Default,
        Layout = layout,
        ContentsObjectBytes = 0,
        Write = new VortexWriteOptions { BlockRows = 512, UnpaddedKeyIndex = unpadded },
    };

    private static string Service(Random random) => $"service-{random.Next(0, 300):D3}";

    /// <summary>A text of 40 to 60 zeros and ones, which unpadded takes twice its bytes.</summary>
    private static string ZerosAndOnes(Random random) =>
        new string([.. Enumerable.Range(0, random.Next(40, 61)).Select(_ => (char)random.Next(0, 2))]);

    private static async Task AppendAsync(VortexDataset dataset, List<Row> all, Random random, Func<Random, string> service)
    {
        for (int a = 0; a < 4; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 15_000).Select(i => new Row(random.Next(0, 8) == 0 ? 1 : 0, service(random), random.Next(0, 100), all.Count + i, random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }
    }

    /// <summary>The objects whose index codes its keys as the row encoding does, and those that code them unpadded; each index read.</summary>
    private static async Task<(int Rows, int Unpadded)> CodingsAsync(VortexDataset dataset)
    {
        int rows = 0;
        int unpadded = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.True(held.Entry.Contents is { KeyIndex: not null }, $"'{held.Entry.Key}' keeps no sparse index");
            using GranuleKeys keys = await dataset.KeyIndexAsync(held.Entry, held.Entry.Contents!, Ct)
                ?? throw new InvalidOperationException($"'{held.Entry.Key}': its sparse index does not read");
            rows += keys.Coding == KeyCoding.Rows ? 1 : 0;
            unpadded += keys.Coding == KeyCoding.UnpaddedTexts ? 1 : 0;
        }

        return (rows, unpadded);
    }

    /// <summary>
    /// Filters on the key's components against LINQ, each object's index keeping the granules its kept rows lie in; tuples;
    /// seeks of the key's cursor against the first tuple at or past them; and the verifier.
    /// </summary>
    private static async Task AnswersAsync(VortexDataset dataset, List<Row> all, Random random)
    {
        for (int probe = 0; probe < 6; probe++)
        {
            string service = probe == 0 ? "service-zzz" : all[random.Next(all.Count)].Service;
            string other = all[random.Next(all.Count)].Service;
            (string low, string high) = string.CompareOrdinal(service, other) <= 0 ? (service, other) : (other, service);
            int status = random.Next(0, 100);
            await CountsAsync(dataset, all, Expr.Eq(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From(service))), row => row.Service == service, $"Service == {service}");
            await CountsAsync(
                dataset, all,
                Expr.And(Expr.Ge(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From(low))), Expr.Lt(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From(high)))),
                row => string.CompareOrdinal(row.Service, low) >= 0 && string.CompareOrdinal(row.Service, high) < 0, $"{low} <= Service < {high}");
            await CountsAsync(
                dataset, all,
                Expr.And(Expr.Eq(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From(service))), Expr.Ge(Expr.Field(nameof(Row.Status)), Expr.Literal(FilterLiteral.From(status)))),
                row => row.Service == service && row.Status >= status, $"Service == {service} && Status >= {status}");
            await CountsAsync(dataset, all, Expr.Eq(Expr.Field(nameof(Row.Status)), Expr.Literal(FilterLiteral.From(status))), row => row.Status == status, $"Status == {status}", tight: false);
        }

        (string, int)[] pairs = [.. Enumerable.Range(0, 20).Select(_ => all[random.Next(all.Count)]).Select(static row => (row.Service, row.Status)), ("service-zzz", 1)];
        Assert.Equal(
            all.Count(row => pairs.Contains((row.Service, row.Status))),
            await dataset.Scan<Row>().Where(r => (r.Service, r.Status).In(pairs)).ToRecordsAsync(Ct).CountAsync(Ct));

        // The key's cursor bisects each object's granules by its index, in the index's coding, to the first tuple at or past
        // the one sought.
        SortKey tuple = SortKey.OfColumns(dataset.Snapshot.Schema.DType, [nameof(Row.Service), nameof(Row.Status)], descending: false);
        List<(string Service, int Status)> sorted = [.. all.Select(static row => (row.Service, row.Status)).Order(Ordinal.Instance)];
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, Ct);
        for (int probe = 0; probe < 12; probe++)
        {
            (string Service, int Status) sought = probe == 0 ? ("service-zzz", 0) : (all[random.Next(all.Count)].Service, random.Next(0, 101));
            int at = sorted.FindIndex(pair => Ordinal.Instance.Compare(pair, sought) >= 0);
            bool found = await cursor.SeekAsync(FilterLiteral.From(tuple.Encode([FilterLiteral.From(sought.Service), FilterLiteral.From(sought.Status)])), cancellationToken: Ct);
            Assert.Equal(at >= 0, found);
            if (found)
            {
                Assert.Equal(tuple.Encode([FilterLiteral.From(sorted[at].Service), FilterLiteral.From(sorted[at].Status)]), cursor.Key.BytesValue.ToArray());
            }
        }

        DatasetVerification verified = await dataset.VerifyAsync(cancellationToken: Ct);
        Assert.True(verified.Holds, string.Join('\n', verified.Problems));
    }

    /// <summary>
    /// The rows <paramref name="filter"/> keeps against LINQ's; and in each object, the granules the key's pruning keeps
    /// against those its kept rows lie in: every one of those, and, <paramref name="tight"/> for a filter on the leading
    /// component, at most one more at each end of each range kept. An index of keys unpadded keeps the granules the same
    /// index of keys row-encoded keeps.
    /// </summary>
    private static async Task CountsAsync(VortexDataset dataset, List<Row> all, VortexExpr filter, Func<Row, bool> keep, string label, bool tight = true)
    {
        Assert.Equal(all.Count(keep), await dataset.ScanBuilder().Where(filter).CountAsync(Ct));
        KeyPruning pruning = KeyPruning.Of(dataset.Snapshot.Schema, filter) ?? throw new InvalidOperationException($"{label}: no pruning");
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            using GranuleKeys index = await dataset.KeyIndexAsync(held.Entry, held.Entry.Contents!, Ct)
                ?? throw new InvalidOperationException($"'{held.Entry.Key}': its sparse index does not read");
            ReadOnlySpan<byte> order = VortexDataset.OrderOf(held.TreeKey).Span;
            byte[] partition = dataset.Snapshot.Schema.Partitioned ? order[..dataset.Snapshot.Schema.Tree!.PrefixLength(order, 1)].ToArray() : [];
            List<(int First, int End)> kept = pruning.Granules(index, partition);
            if (index.Coding == KeyCoding.UnpaddedTexts)
            {
                using GranuleKeys rows = Rows(dataset.Snapshot.Schema, index);
                Assert.Equal(KeyPruning.Of(dataset.Snapshot.Schema, filter)!.Granules(rows, partition), kept);
            }

            List<Row> read;
            await using (ObjectLease lease = await dataset.RentAsync(held.Entry, Ct))
            {
                read = await lease.File.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
            }

            HashSet<int> needed = [.. Enumerable.Range(0, read.Count).Where(row => keep(read[row])).Select(row => row / index.GranuleRows)];
            HashSet<int> granules = [.. kept.SelectMany(static range => Enumerable.Range(range.First, range.End - range.First))];
            Assert.True(needed.IsSubsetOf(granules), $"{label}, '{held.Entry.Key}' ({index.Coding}): a granule that holds a kept row is passed over");
            Assert.True(!tight || granules.Count <= needed.Count + (2 * kept.Count), $"{label}, '{held.Entry.Key}' ({index.Coding}): {granules.Count} granules kept in {kept.Count} ranges for {needed.Count} holding a kept row");
        }
    }

    /// <summary><paramref name="index"/>, of keys unpadded, written again with its keys as the row encoding has them.</summary>
    private static GranuleKeys Rows(DatasetSchema schema, GranuleKeys index)
    {
        ReadOnlySpan<int> widths = schema.Tree!.Widths.Slice(schema.Partitioned ? 1 : 0, index.Components);
        List<byte[]> keys = [];
        GranuleKeys.KeyReader reader = default;
        for (int granule = 0; granule < index.Granules; granule++)
        {
            keys.Add(Vorticity.RowEncoding.UnpaddedKeys.Widen(reader.Key(index, granule), widths));
        }

        reader.Dispose();
        (byte[] table, byte[] pages) = GranuleKeys.Build(
            index.GranuleRows, index.Rows, index.Components, keys, Vorticity.RowEncoding.UnpaddedKeys.Widen(index.LastKey, widths), index.PageEntries);
        GranuleKeys rows = GranuleKeys.From(table)!;
        Assert.True(!rows.WantsPages || rows.TakePages(pages));
        Assert.Equal(KeyCoding.Rows, rows.Coding);
        return rows;
    }

    /// <summary>Tuples of a text and an integer in the row encoding's order: the text's bytes, then the integer.</summary>
    private sealed class Ordinal : IComparer<(string Service, int Status)>
    {
        internal static readonly Ordinal Instance = new Ordinal();

        public int Compare((string Service, int Status) x, (string Service, int Status) y)
        {
            int order = string.CompareOrdinal(x.Service, y.Service);
            return order != 0 ? order : x.Status.CompareTo(y.Status);
        }
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, int Status, long Sequence, double Value);
}
