// Searches by tuples of columns: `(a, b) IN ((1, 'x'), (2, 'y'))` over a dataset, its text, its rows and its pruning.
//
// THE ORACLE IS LINQ UNDER THREE-VALUED LOGIC: a tuple holding a null matches no row; a row whose values are all set is
// true when a tuple holds them, false otherwise; a row with a null is unknown when a tuple holds its other values, false
// otherwise. A filter keeps the true rows, its negation the false ones.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetTupleInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The columns of the tuples asked, as the record names them.</summary>
    private static readonly string[][] Shapes =
    [
        [nameof(Call.Service), nameof(Call.Code)],
        [nameof(Call.Code), nameof(Call.Service)],
        [nameof(Call.Service), nameof(Call.Sequence)],
        [nameof(Call.Code), nameof(Call.Score)],
        [nameof(Call.Service), nameof(Call.Flag)],
        [nameof(Call.Service), nameof(Call.Code), nameof(Call.Sequence)],
    ];

    /// <summary>
    /// A tuple's text reads back as written, negated or not, its columns' sets left out of it though the filter holds them;
    /// a tuple of no tuples reads too.
    /// </summary>
    [Fact]
    public void TheTextOfATupleReadsBackAsWritten()
    {
        foreach (string text in new[]
        {
            "(Service, Code) in (('svc-01', 200), ('svc-02', 503))",
            "not ((Service, Code) in (('svc-01', 200)))",
            "(Service, Code) in (('svc-01', 200)) and Score > 1.5",
            "(Code, Service, Sequence) in ((200, 'svc-01', 7), (404, 'svc-02', 9))",
            "(Service, Code) in ()",
        })
        {
            VortexExpr filter = ExprText.Parse(text);
            Assert.Equal(text, ExprText.Format(filter));
            Assert.Equal(text, ExprText.Format(ExprText.Parse(ExprText.Format(filter))));
        }

        List<string> read = [];
        ExprText.Parse("(Service, Code) in (('svc-01', 200), ('svc-01', 404))").CollectFields(read);
        Assert.Equal([nameof(Call.Service), nameof(Call.Code), nameof(Call.Service), nameof(Call.Code)], read);
    }

    /// <summary>
    /// 24 000 calls over six appends, services, codes and flags drawn with nulls among them: 240 questions over tuples of two
    /// or three columns of five types, a third of their tuples absent and some holding a null, half negated, some with
    /// another conjunct; each counts the rows LINQ keeps.
    /// </summary>
    [Fact]
    public async Task ATupleKeepsTheRowsLinqKeeps()
    {
        Random random = new Random(41);
        (VortexDataset dataset, List<Call> all) = await DatasetAsync(random, keyed: false);
        await using (dataset)
        {
            List<string> failures = [];
            for (int question = 0; question < 240; question++)
            {
                (string text, Func<Call, bool> keep) = RandomQuestion(random, all);
                long got = await dataset.Scan().Where(VortexExpr.Parse(text)).CountAsync(Ct);
                long want = all.Count(keep);
                if (got != want)
                {
                    failures.Add($"{text}: {got} rows, LINQ {want}");
                }
            }

            Assert.True(failures.Count == 0, $"{failures.Count} wrong:\n{string.Join('\n', failures.Take(10))}");
        }
    }

    /// <summary>
    /// The typed tuple, <c>(r.Service, r.Code).In(pairs)</c>, keeps the rows LINQ keeps, nulls and absent pairs among the
    /// pairs; a semi-join on two and three columns, its first question the calls of the highest scores, keeps the rows
    /// whose tuple that question gives; more tuples than asked are refused, and a first question of another width too.
    /// </summary>
    [Fact]
    public async Task ATypedTupleAndASemiJoinKeepTheRowsLinqKeeps()
    {
        Random random = new Random(43);
        (VortexDataset dataset, List<Call> all) = await DatasetAsync(random, keyed: false);
        await using (dataset)
        {
            (string?, int?)[] pairs = [.. all.Take(40).Select(static call => (call.Service, call.Code)), ("svc-zz", 200), (null, 404), ("svc-01", null)];
            HashSet<(string, int)> held = [.. pairs.Where(static pair => pair.Item1 is not null && pair.Item2 is not null).Select(static pair => (pair.Item1!, pair.Item2!.Value))];
            Assert.Equal(
                all.Count(call => call.Service is { } service && call.Code is { } code && held.Contains((service, code))),
                await dataset.Scan<Call>().Where(r => (r.Service, r.Code).In(pairs)).ToRecordsAsync(Ct).CountAsync(Ct));
            Assert.Equal(0, await dataset.Scan<Call>().Where(r => (r.Service, r.Code).In(("svc-zz", 1))).ToRecordsAsync(Ct).CountAsync(Ct));

            Scan<Call> pairsOfTheBest = await dataset.Scan<Call>().WhereInAsync(
                r => (r.Service, r.Code), dataset.Scan<Call>().Where(r => r.Score >= 9.5).Select(r => (r.Service, r.Code)), cancellationToken: Ct);
            HashSet<(string, int)> best = [.. all.Where(static call => call.Score >= 9.5 && call.Service is not null && call.Code is not null).Select(static call => (call.Service!, call.Code!.Value))];
            Assert.Equal(
                all.Count(call => call.Service is { } service && call.Code is { } code && best.Contains((service, code))),
                await pairsOfTheBest.ToRecordsAsync(Ct).CountAsync(Ct));

            Scan<Call> triples = await dataset.Scan<Call>().WhereInAsync(
                r => (r.Service, r.Code, r.Sequence), dataset.Scan<Call>().Where(r => r.Score >= 9.5).Select(r => (r.Service, r.Code, r.Sequence)), cancellationToken: Ct);
            Assert.Equal(
                all.Count(static call => call.Score >= 9.5 && call.Service is not null && call.Code is not null),
                await triples.ToRecordsAsync(Ct).CountAsync(Ct));

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await dataset.Scan<Call>().WhereInAsync(
                r => (r.Service, r.Code), dataset.Scan<Call>().Select(r => (r.Service, r.Code)), maxValues: 10, cancellationToken: Ct));
            await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.Scan<Call>().WhereInAsync(
                r => (r.Service, r.Code), dataset.Scan<Call>().Select(r => (r.Service, r.Code, r.Sequence)), cancellationToken: Ct));
        }
    }

    /// <summary>The calls of six appends, each sorted by the service and the code when <paramref name="keyed"/>.</summary>
    private static async Task<(VortexDataset Dataset, List<Call> All)> DatasetAsync(Random random, bool keyed)
    {
        MemoryObjectStore store = new MemoryObjectStore();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = keyed
                ? new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Code)), LayoutValue.Column(nameof(Call.Sequence))] }
                : null,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
        }, Ct);
        List<Call> all = [];
        for (int append = 0; append < 6; append++)
        {
            Call[] calls = [.. Enumerable.Range(0, 4_000).Select(i => new Call(
                random.Next(12) == 0 ? null : $"svc-{random.Next(40):D2}",
                random.Next(10) == 0 ? null : new[] { 200, 201, 404, 500, 503 }[random.Next(5)],
                all.Count + i,
                random.Next(40) * 0.25,
                random.Next(2) == 0))];
            all.AddRange(calls);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Call>(calls, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return (dataset, all);
    }

    /// <summary>A filter of a tuple of random columns, its text and the rows LINQ keeps of it.</summary>
    private static (string Text, Func<Call, bool> Keep) RandomQuestion(Random random, List<Call> all)
    {
        string[] columns = Shapes[random.Next(Shapes.Length)];
        int count = random.Next(1, 30);
        List<object?[]> tuples = [];
        for (int t = 0; t < count; t++)
        {
            Call drawn = all[random.Next(all.Count)];
            object?[] tuple = [.. columns.Select(column => ValueOf(drawn, column))];
            int roll = random.Next(10);
            if (roll < 3)
            {
                // Absent: a service no row holds, or a code none does.
                tuple[0] = columns[0] == nameof(Call.Service) ? "svc-zz" : columns[0] == nameof(Call.Code) ? 299 : tuple[0];
            }
            else if (roll == 3)
            {
                tuple[random.Next(tuple.Length)] = null;
            }

            tuples.Add(tuple);
        }

        StringBuilder text = new StringBuilder();
        text.Append('(').Append(string.Join(", ", columns)).Append(") in (");
        text.Append(string.Join(", ", tuples.Select(tuple => "(" + string.Join(", ", tuple.Select(Literal)) + ")")));
        text.Append(')');
        bool negated = random.Next(2) == 0;
        string filter = negated ? $"not ({text})" : text.ToString();
        bool scored = random.Next(4) == 0;
        if (scored)
        {
            filter += " and Score >= 5";
        }

        return (filter, row => (!scored || row.Score >= 5) && Truth(row, columns, tuples) == (negated ? False : True));
    }

    private const int False = 0;
    private const int True = 1;
    private const int Unknown = 2;

    /// <summary>The truth of <paramref name="row"/> against <paramref name="tuples"/> of <paramref name="columns"/>, three-valued.</summary>
    private static int Truth(Call row, string[] columns, List<object?[]> tuples)
    {
        object?[] values = [.. columns.Select(column => ValueOf(row, column))];
        bool someNull = values.Any(static value => value is null);
        foreach (object?[] tuple in tuples)
        {
            if (tuple.Any(static value => value is null))
            {
                continue;
            }

            bool holds = true;
            for (int i = 0; i < values.Length && holds; i++)
            {
                holds = values[i] is null || values[i]!.Equals(tuple[i]);
            }

            if (holds)
            {
                return someNull ? Unknown : True;
            }
        }

        return False;
    }

    private static object? ValueOf(Call row, string column) => column switch
    {
        nameof(Call.Service) => row.Service,
        nameof(Call.Code) => row.Code,
        nameof(Call.Sequence) => row.Sequence,
        nameof(Call.Score) => row.Score,
        _ => row.Flag,
    };

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string text => $"'{text}'",
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.0###", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    [VortexRecord]
    public partial record struct Call(string? Service, int? Code, long Sequence, double Score, bool Flag);
}
