// The layout's interleaving fuzzer (spec A, 12; A10): seeded schedules of appends, late rows among them, deletes, merges
// and consolidations over a dataset under a layout drawn at random, beside the same changes to a dataset without one.
//
// THE ORACLE IS THE DATASET WITHOUT A LAYOUT AND THE INVARIANTS: after every third step the layout's invariants hold
// (VerifyAsync: a partition an object, its rows in the key's order, its sparse index, its entry's keys, the levels
// disjoint, its chunks' geometry, its dictionaries' order); at the end, filters drawn at random keep the rows, and the sums,
// of the dataset that holds the same rows with no layout.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLayoutFuzzTests
{
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = [.. Enumerable.Range(0, 9).Select(i => $"svc-{i}")];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A layout drawn of the seed: a partition by the hour, the day or the service, or none; a key of one to three components
    /// among the service, the instant, its minute and a bucket of the size; an indexed prefix of it; levels or tiers; objects
    /// of 32 KiB and merges of 2 to 4 objects under tiers, so that merges write several a partition. Thirty steps: appends
    /// of a row to 6 000, one in five of them late by hours; deletes of a service's status, or of small sizes; merges until
    /// none is due; consolidations.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task ASeededScheduleKeepsTheInvariantsAndTheRowsAnswers(int seed)
    {
        Random random = new Random(seed);
        DatasetLayout layout = Layout(random);
        await using MemoryObjectStore laid = new MemoryObjectStore();
        await using MemoryObjectStore plain = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(laid, Call.Schema, new DatasetOptions { Layout = layout, ContentsObjectBytes = 1 }, Ct);
        await using VortexDataset oracle = await VortexDataset.CreateAsync(plain, Call.Schema, new DatasetOptions(), Ct);
        List<string> schedule = [];
        long sequence = 0;
        DateTimeOffset now = Noon;
        for (int step = 0; step < 30; step++)
        {
            int draw = random.Next(10);
            if (draw < 4)
            {
                bool late = random.Next(5) == 0;
                int count = random.Next(1, 6_000);
                Call[] rows = [.. Enumerable.Range(0, count).Select(_ => Draw(random, now, late, sequence++))];
                now = now.AddMinutes(20);
                schedule.Add($"append {count}{(late ? " late" : "")}");
                foreach (VortexDataset target in (VortexDataset[])[dataset, oracle])
                {
                    await using ObjectDraft draft = target.StartObject();
                    await draft.Writer.WriteAsync<Call>(rows, Ct);
                    await target.AppendAsync(draft, Ct);
                }
            }
            else if (draw < 6)
            {
                string service = Services[random.Next(Services.Length)];
                short status = Statuses[random.Next(Statuses.Length)];
                long size = random.Next(50_000);
                bool byService = random.Next(2) == 0;
                schedule.Add(byService ? $"delete {service} {status}" : $"delete size < {size}");
                foreach (VortexDataset target in (VortexDataset[])[dataset, oracle])
                {
                    _ = byService
                        ? await target.DeleteAsync<Call>(c => c.Service == service & c.Status == status, Ct)
                        : await target.DeleteAsync<Call>(c => c.Size < size, Ct);
                }
            }
            else if (draw < 9)
            {
                CompactionOptions options = new CompactionOptions { LevelZeroCeiling = random.Next(1, 3), TargetBytesAtLevelOne = 64 << 10 };
                int jobs = 0;
                while (await dataset.CompactAsync(options, Ct) is not null && ++jobs < 50)
                {
                }

                schedule.Add($"merge {jobs}");
            }
            else
            {
                schedule.Add($"consolidate {(await dataset.ConsolidateAsync(Ct)).Count}");
            }

            if (step % 3 == 2 || step == 29)
            {
                DatasetVerification verified = await dataset.VerifyAsync(cancellationToken: Ct);
                Assert.True(verified.Holds, $"seed {seed}, {Describe(layout)}, after {string.Join(", ", schedule)}:\n{string.Join('\n', verified.Problems.Take(10))}");
            }
        }

        for (int probe = 0; probe < 20; probe++)
        {
            (string name, Func<Probe<Call>, Predicate> filter) = Filter(random, now);
            (long Rows, long Size) want = await AnswerAsync(oracle, filter);
            (long Rows, long Size) got = await AnswerAsync(dataset, filter);
            Assert.True(want == got, $"seed {seed}, {Describe(layout)}, {name}: {got} where the rows say {want}, after {string.Join(", ", schedule)}");
        }
    }

    private static readonly short[] Statuses = [200, 201, 404, 500];

    private static DatasetLayout Layout(Random random)
    {
        LayoutValue? partition = random.Next(4) switch
        {
            0 => LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Hour),
            1 => LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
            2 => LayoutValue.Column(nameof(Call.Service)),
            _ => null,
        };
        LayoutValue[][] keys =
        [
            [LayoutValue.Column(nameof(Call.Service))],
            [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
            [LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Minute), LayoutValue.Column(nameof(Call.Service))],
            [LayoutValue.Column(nameof(Call.Size)).Bucket(1_000L), LayoutValue.Column(nameof(Call.At))],
            [LayoutValue.Column(nameof(Call.Status)), LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
        ];
        LayoutValue[] key = keys[random.Next(keys.Length)];
        return new DatasetLayout
        {
            Partition = partition,
            SortKey = key,
            IndexedPrefix = random.Next(1, key.Length + 1),
            Organization = random.Next(2) == 0 ? LayoutOrganization.Leveled : LayoutOrganization.Tiered,
            ObjectBytes = 32 << 10,
            MergeFanout = random.Next(2, 5),
        };
    }

    private static string Describe(DatasetLayout layout) =>
        $"{layout.Organization} by {(layout.Partition is null ? "nothing" : "a partition")}, {layout.SortKey.Count} components, {layout.IndexedPrefix} indexed";

    private static Call Draw(Random random, DateTimeOffset now, bool late, long sequence) => new Call(
        Services[(int)(Math.Pow(random.NextDouble(), 2) * Services.Length)],
        now.AddSeconds(-random.Next(late ? 3 * 3_600 : 1_200)),
        Statuses[random.Next(Statuses.Length)],
        random.Next(100_000),
        sequence);

    private static (string Name, Func<Probe<Call>, Predicate> Filter) Filter(Random random, DateTimeOffset now)
    {
        string service = Services[random.Next(Services.Length)];
        DateTimeOffset from = Noon.AddMinutes(random.Next(-180, (int)(now - Noon).TotalMinutes + 1));
        DateTimeOffset to = from.AddMinutes(random.Next(1, 240));
        long size = random.Next(100_000);
        short status = Statuses[random.Next(Statuses.Length)];
        return random.Next(5) switch
        {
            0 => ($"service {service}", c => c.Service == service),
            1 => ($"from {from:HH:mm} to {to:HH:mm}", c => c.At >= from & c.At < to),
            2 => ($"service {service} past {from:HH:mm}", c => c.Service == service & c.At >= from),
            3 => ($"size below {size}", c => c.Size < size),
            _ => ($"status {status}, service at least {service}", c => c.Status == status & c.Service >= service),
        };
    }

    private static async Task<(long Rows, long Size)> AnswerAsync(VortexDataset dataset, Func<Probe<Call>, Predicate> filter)
    {
        (long Rows, long Size) answer = default;
        await foreach (Call call in dataset.Scan<Call>().Where(filter).ToRecordsAsync(Ct))
        {
            answer = (answer.Rows + 1, answer.Size + call.Size);
        }

        return answer;
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, short Status, long Size, long Sequence);
}
