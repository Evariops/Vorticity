// The same bits by every path: every question of the matrix, and questions drawn at random, give the bits
// their rows give, with the synopses on and off, at a degree of one, of three and of the machine, on objects
// of one slice, of many slices and of a minute each, before compaction, after one step of it and after all.
//
// WHAT IS HELD: an answer does not depend on how it was read. The reference is the rows read at a degree of
// one; every other path, the tables folded included, gives its bits, float sums and means among them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    public static TheoryData<string, string> Shapes()
    {
        TheoryData<string, string> shapes = [];
        foreach (string shape in new[] { "one slice an object", "slices of a hundred rows", "periods of a minute" })
        {
            foreach (string stage in new[] { "appended", "one compaction", "compacted" })
            {
                shapes.Add(shape, stage);
            }
        }

        return shapes;
    }

    private static IEnumerable<string> MatrixQuestions() => Questions().Select(row => row.Data).Concat(
        ["a set of services", "a range of statuses", "a negation", "either of two keys", "a measure's bound"]);

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task EveryQuestionGivesTheSameBitsByEveryPath(string shape, string stage)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await ShapedAsync(store, shape, stage);
        long folded = 0;
        foreach (string question in MatrixQuestions())
        {
            (List<string> rows, _) = await AskAsync(dataset, question, synopses: false, degree: 1);
            Assert.NotEmpty(rows);
            foreach (int degree in new[] { 1, 3, Environment.ProcessorCount })
            {
                foreach (bool synopses in new[] { true, false })
                {
                    (List<string> answer, ScanMetrics metrics) = await AskAsync(dataset, question, synopses, degree);
                    Assert.True(rows.SequenceEqual(answer), $"{shape}, {stage}, '{question}' at degree {degree}, synopses {(synopses ? "on" : "off")}");
                    folded += metrics.SynopsisRows;
                }
            }
        }

        Console.Out.Write($"MATRIX {shape}, {stage}: {folded} table rows folded.\n");

        // A minute's 48 rows weigh less than any table over them, which the writer's choice then drops.
        if (stage == "compacted" && shape != "periods of a minute")
        {
            Assert.True(folded > 0, "no table answered");
        }
    }

    /// <summary>
    /// Questions drawn at random over the dataset of <see cref="FilledAsync"/>: a filter of up to three levels
    /// of conjunctions, disjunctions and negations over the keys of the tables, a measure and the instant, a
    /// window or none, a grouping and the aggregates of one of four shapes. Each gives the bits of its rows,
    /// and some of them come from the tables.
    /// </summary>
    [Fact]
    public async Task RandomQuestionsGiveTheSameBits()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        Random random = new Random(59);
        int answered = 0;
        const int Questions = 150;
        for (int question = 0; question < Questions; question++)
        {
            int seed = random.Next();
            List<string>[] answers = new List<string>[2];
            long folded = 0;
            foreach (bool synopses in new[] { true, false })
            {
                (answers[synopses ? 0 : 1], ScanMetrics metrics) = await GroupsAsync(Drawn(dataset, new Random(seed), synopses));
                folded += metrics.SynopsisRows;
            }

            Assert.True(answers[1].SequenceEqual(answers[0]), $"question {question}, seed {seed}");
            answered += folded > 0 ? 1 : 0;
        }

        Console.Out.Write($"RANDOM: {answered} of {Questions} questions folded tables.\n");
        Assert.True(answered > Questions / 10, $"{answered} of {Questions} questions folded tables");
    }

    /// <summary>A question drawn from <paramref name="random"/>, the same one for the same seed.</summary>
    private static Vorticity.Aggregation Drawn(VortexDataset dataset, Random random, bool synopses)
    {
        Scan<Request> scan = dataset.Scan<Request>().With(new ScanOptions { UseSynopses = synopses });
        Atom[] atoms = [.. Enumerable.Range(0, random.Next(4)).Select(_ => Atom.Drawn(random))];
        int[] joins = [.. Enumerable.Range(0, atoms.Length).Select(_ => random.Next(3))];
        if (atoms.Length > 0)
        {
            scan = scan.Where(r => Filter(r, atoms, joins));
        }

        // A window: none, on the hours, or cutting slices.
        DateTimeOffset from = Noon.AddMinutes(random.Next(4) * 30 + (random.Next(2) == 0 ? 0 : random.Next(1, 29) + 0.25));
        DateTimeOffset to = from.AddMinutes(random.Next(1, 100));
        scan = random.Next(3) switch
        {
            0 => scan,
            _ => scan.Where(r => r.At >= from && r.At < to),
        };

        int shape = random.Next(4);
        return random.Next(5) switch
        {
            0 => Selected(scan.GroupBy(r => r.Service), shape),
            1 => Selected(scan.GroupBy(r => r.Method), shape),
            2 => Selected(scan.GroupBy(r => r.Status), shape),
            3 => scan.GroupBy(r => (r.Service, r.Method)).Select(g => (g.Key.Service, g.Key.Method, g.Count(), g.Sum(r => r.Bytes), g.Max(r => r.Duration))),
            _ => scan.GroupBy(r => (r.At.Truncate(CalendarUnit.Hour), r.Method)).Select(g => (g.Key.Item1, g.Key.Method, g.Count(), g.Sum(r => r.Duration))),
        };
    }

    /// <summary>One of four shapes of aggregates over a grouping by one key.</summary>
    private static Vorticity.Aggregation Selected<TKey>(GroupedScan<Request, Sym<TKey>> groups, int shape) => shape switch
    {
        0 => groups.Select(g => (g.Key, g.Count())),
        1 => groups.Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes), g.Sum(r => r.Duration))),
        2 => groups.Select(g => (g.Key, g.Min(r => r.Bytes), g.Max(r => r.Bytes), g.Min(r => r.Duration))),
        _ => groups.Select(g => (g.Key, g.Average(r => r.Duration), g.Average(r => r.Bytes))),
    };

    /// <summary>The atoms folded left to right, each by a conjunction, a disjunction or a conjunction with the negation of the atom.</summary>
    private static Predicate Filter(Probe<Request> r, Atom[] atoms, int[] joins)
    {
        Predicate filter = atoms[0].Of(r);
        for (int i = 1; i < atoms.Length; i++)
        {
            Predicate atom = atoms[i].Of(r);
            filter = joins[i] switch
            {
                0 => filter & atom,
                1 => filter | atom,
                _ => filter & !atom,
            };
        }

        return joins[0] == 2 ? !filter : filter;
    }

    /// <summary>A comparison on one column.</summary>
    private readonly record struct Atom(int Kind, string Text, long Number)
    {
        internal static Atom Drawn(Random random) => random.Next(6) switch
        {
            0 => new Atom(0, Services[random.Next(Services.Length)], 0),
            1 => new Atom(1, Methods[random.Next(Methods.Length)], 0),
            2 => new Atom(2, "", random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L }),
            3 => new Atom(3, "", random.Next(2) == 0 ? 300L : 450L),
            4 => new Atom(4, "", random.NextInt64(1, 1L << 40)),
            _ => new Atom(5, "", random.Next(5, 995)),
        };

        internal Predicate Of(Probe<Request> r) => Kind switch
        {
            0 => r.Service == Text,
            1 => r.Method == Text,
            2 => r.Status == Number,
            3 => r.Status >= Number,
            4 => r.Bytes < Number,
            _ => r.Duration > Number,
        };
    }

    /// <summary>
    /// The requests of <see cref="FilledAsync"/>, in objects of one shape, compacted to one stage: every
    /// object in level 0, one step of compaction, or every period sealed.
    /// </summary>
    private static async Task<VortexDataset> ShapedAsync(Vorticity.IO.IObjectStore store, string shape, string stage)
    {
        PeriodOptions periods = shape switch
        {
            "one slice an object" => new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 1_000_000 },
            "periods of a minute" => new PeriodOptions { Lateness = TimeSpan.FromSeconds(30), Length = SliceWidth.Minute, SliceTargetRows = 100 },
            _ => new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
        };
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Request.At)],
            Periods = periods,
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(41);
        for (int part = 0; part < 16; part++)
        {
            Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                Methods[random.Next(Methods.Length)],
                random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L },
                random.NextDouble() * 1_000,
                random.NextInt64(1, 1L << 40)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        if (stage == "one compaction")
        {
            await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct);
        }
        else if (stage == "compacted")
        {
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
            {
            }
        }

        return dataset;
    }
}
