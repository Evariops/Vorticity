// The dataset's telemetry: an activity per commit, per compaction and per vacuum, and the counters a
// dashboard sums over a process. A listener hears the whole process, every neighbouring test's commits
// included, so these tests run alone: what they count is then their own.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class DatasetTelemetryTests
{
    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Fact]
    public async Task EachCommitCompactionAndVacuumIsCountedAndTracedWithHowItEnded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "vortex-telemetry-" + Guid.NewGuid().ToString("N"));
        using Recorder recorder = new Recorder();
        try
        {
            Decoders.EnsureRegistered();
            await using VortexDataset first = await VortexDataset.CreateAsync(directory, ChangeRow.Schema, Options(), ct);
            await using VortexDataset second = await VortexDataset.OpenAsync(directory, Options(), ct);

            // The second handle still holds version 1 when it appends: its first creation finds the
            // number taken, and its second lands.
            await AppendAsync(first, 0, ct);
            await AppendAsync(second, 1, ct);
            Activity rebased = recorder.Stopped("vortex.dataset.commit").Single(activity => Equals(activity.GetTagItem("vortex.attempts"), 2));
            Assert.Equal(("append", "applied", 3L, directory), Tags(rebased));
            Assert.Equal(("create", "applied", 1L, directory), Tags(recorder.Stopped("vortex.dataset.commit").Single(IsOperation("create"))));
            Assert.Equal(2, recorder.Sum("vortex.dataset.commits", ("vortex.operation", "append"), ("vortex.outcome", "applied")));
            Assert.Equal(3, recorder.Sum("vortex.dataset.commit_attempts", ("vortex.operation", "append")));

            // Level 0 past its ceiling, planned on one handle and run on both: the job that commits
            // second finds its inputs gone and is abandoned, with what it read and wrote counted all
            // the same.
            await AppendAsync(first, 2, ct);
            await second.RefreshAsync(ct);
            CompactionOptions compaction = new CompactionOptions { LevelZeroCeiling = 2 };
            CompactionJob late = Assert.IsType<CompactionJob>((await second.PlanCompactionAsync(compaction, ct)).Job);
            CompactionResult applied = Assert.IsType<CompactionResult>(await first.CompactAsync(compaction, ct));
            CompactionResult abandoned = await DatasetCompactor.RunAsync(second, late, ct);
            Assert.Equal((OperationOutcome.Applied, OperationOutcome.Abandoned), (applied.Outcome, abandoned.Outcome));

            Activity[] compactions = [.. recorder.Stopped("vortex.dataset.compaction")];
            Assert.Equal(["abandoned", "applied"], compactions.Select(activity => (string)activity.GetTagItem("vortex.outcome")!).Order());
            Activity ran = compactions.Single(activity => (string)activity.GetTagItem("vortex.outcome")! == "applied");
            Assert.Equal(
                ("level_zero_ceiling", 0, 1, applied.BytesIn, applied.BytesOut, directory),
                ((string)ran.GetTagItem("vortex.trigger")!, (int)ran.GetTagItem("vortex.level.from")!, (int)ran.GetTagItem("vortex.level.to")!,
                    (long)ran.GetTagItem("vortex.bytes_in")!, (long)ran.GetTagItem("vortex.bytes_out")!, (string)ran.GetTagItem("vortex.location")!));

            // Each compaction's commit is a child of its activity.
            foreach (Activity job in compactions)
            {
                Assert.Single(recorder.Stopped("vortex.dataset.commit"), activity => ReferenceEquals(activity.Parent, job));
            }

            Assert.Equal(1, recorder.Sum("vortex.dataset.compactions", ("vortex.trigger", "level_zero_ceiling"), ("vortex.outcome", "applied")));
            Assert.Equal(1, recorder.Sum("vortex.dataset.compactions", ("vortex.trigger", "level_zero_ceiling"), ("vortex.outcome", "abandoned")));
            Assert.Equal(applied.BytesIn + abandoned.BytesIn, recorder.Sum("vortex.dataset.compaction.bytes_read", ("vortex.trigger", "level_zero_ceiling")));
            Assert.Equal(applied.BytesOut + abandoned.BytesOut, recorder.Sum("vortex.dataset.compaction.bytes_written", ("vortex.trigger", "level_zero_ceiling")));
            Assert.Equal(1, recorder.Sum("vortex.dataset.commits", ("vortex.operation", "compaction"), ("vortex.outcome", "applied")));
            Assert.Equal(1, recorder.Sum("vortex.dataset.commits", ("vortex.operation", "compaction"), ("vortex.outcome", "nothing_to_do")));

            // A commit cancelled before it lands is counted, with how it ended.
            using CancellationTokenSource cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first.EvolveSchemaAsync(Widened(), cancellationToken: cancelled.Token));
            Assert.Equal(1, recorder.Sum("vortex.dataset.commits", ("vortex.operation", "schema"), ("vortex.outcome", "cancelled")));
            Assert.Equal(ActivityStatusCode.Error, recorder.Stopped("vortex.dataset.commit").Single(IsOperation("schema")).Status);

            // A vacuum eight days on deletes what compaction replaced, and counts it; a dry run counts nothing.
            VacuumOptions later = new VacuumOptions { TimeProvider = new Later(TimeSpan.FromDays(8)) };
            VacuumResult dry = await first.VacuumAsync(later with { DryRun = true }, ct);
            VacuumResult vacuumed = await first.VacuumAsync(later, ct);
            Assert.NotEmpty(vacuumed.Deleted);
            Assert.Equal(dry.Deleted.Length, vacuumed.Deleted.Length);
            Assert.Equal(vacuumed.Deleted.Length, recorder.Sum("vortex.dataset.vacuum.objects_deleted"));
            Assert.Equal([true, false], recorder.Stopped("vortex.dataset.vacuum").Select(activity => (bool)activity.GetTagItem("vortex.dry_run")!));
            Assert.All(recorder.Stopped("vortex.dataset.vacuum"), activity => Assert.Equal(vacuumed.Deleted.Length, (int)activity.GetTagItem("vortex.deleted")!));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void WithNothingListeningTheTelemetryAllocatesNothing()
    {
        // What a commit, a compaction and a vacuum add when no listener is there: a check per
        // instrument, and no tag built.
        CompactionJob job = new CompactionJob(
            0, 1, CompactionStyle.Leveled, CompactionTrigger.LevelZeroCeiling,
            [new CompactionInput(0, new byte[] { 1 }, new ObjectEntry(CommitKey.ForData("00000001"), 1, 10, 100, UInt128.Zero))],
            1 << 20,
            0);
        CompactionResult result = new CompactionResult { Trigger = CompactionTrigger.LevelZeroCeiling, Outcome = OperationOutcome.Applied, BytesIn = 100, BytesOut = 90 };
        VacuumResult vacuumed = new VacuumResult();

        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Run();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            Activity? commit = DatasetTelemetry.StartCommit(CommitKind.Append, "s3://lake/readings/");
            DatasetTelemetry.Attempted(CommitKind.Append);
            DatasetTelemetry.CommitEnded(commit, CommitKind.Append, CommitEnd.Applied, 1, 2);
            Activity? compaction = DatasetTelemetry.StartCompaction(job, "s3://lake/readings/");
            DatasetTelemetry.CompactionEnded(compaction, result);
            Activity? vacuum = DatasetTelemetry.StartVacuum("s3://lake/readings/", dryRun: false);
            DatasetTelemetry.VacuumEnded(vacuum, vacuumed, dryRun: false);
        }
    }

    private static (string Operation, string Outcome, long Version, string Location) Tags(Activity commit) => (
        (string)commit.GetTagItem("vortex.operation")!,
        (string)commit.GetTagItem("vortex.outcome")!,
        (long)commit.GetTagItem("vortex.version")!,
        (string)commit.GetTagItem("vortex.location")!);

    private static Func<Activity, bool> IsOperation(string operation) =>
        activity => (string?)activity.GetTagItem("vortex.operation") == operation;

    private static DatasetOptions Options() => new DatasetOptions
    {
        ClusteringKey = ["Key"],
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
    };

    /// <summary>The rows' schema with one more column, nullable, under a name the dataset never had.</summary>
    private static VortexSchema Widened() =>
        VortexSchema.Create([.. ChangeRow.Schema.FieldArray, new VortexField("Note", VortexType.Utf8.Nullable)]);

    private static async Task AppendAsync(VortexDataset dataset, int append, CancellationToken ct)
    {
        ChangeRow[] rows = [.. Enumerable.Range(0, 50).Select(i => new ChangeRow((append * 1_000L) + i, i * 0.5, Cities[i % Cities.Length]))];
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
        await dataset.AppendAsync(draft, ct);
    }

    /// <summary>A clock a fixed span ahead of the system's.</summary>
    private sealed class Later(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    /// <summary>What the dataset's instruments said while it listened: each measurement with its tags, and each activity stopped.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _meters = new MeterListener();
        private readonly ActivityListener _activities;
        private readonly ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measured = new();
        private readonly ConcurrentQueue<Activity> _stopped = new();

        internal Recorder()
        {
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == VortexDiagnostics.MeterName && instrument.Name.StartsWith("vortex.dataset.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                Dictionary<string, object?> held = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    held[tag.Key] = tag.Value;
                }

                _measured.Enqueue((instrument.Name, value, held));
            });
            _meters.Start();
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == VortexDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName.StartsWith("vortex.dataset.", StringComparison.Ordinal))
                    {
                        _stopped.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_activities);
        }

        internal IEnumerable<Activity> Stopped(string name) => _stopped.Where(activity => activity.OperationName == name);

        internal long Sum(string instrument, params (string Key, string Value)[] tags) =>
            _measured
                .Where(measured => measured.Instrument == instrument && tags.All(tag => Equals(measured.Tags.GetValueOrDefault(tag.Key), tag.Value)))
                .Sum(measured => measured.Value);

        public void Dispose()
        {
            _meters.Dispose();
            _activities.Dispose();
        }
    }
}
