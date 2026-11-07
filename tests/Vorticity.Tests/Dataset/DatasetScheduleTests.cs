// Every order of a few writers' commits, run on the real code: the reach of the model of the commit
// (docs/design/models/dataset-commit) brought to the C#, at a small scale.
//
// A STORE HOLDS EACH COMMIT'S PUT, and a scheduler releases one at a time. Every writer runs until it
// reaches its next put, or its next plan, and waits there: a deleter and a compactor refresh and plan
// only when released, as the model's plans read the latest version. Once every writer waits or is
// done, the scheduler picks which goes on. A run is replayed from scratch for each sequence of picks,
// the next sequence found by backtracking the last pick that had another option, until none has.
//
// THE SCENARIOS ARE THE MODEL'S: rows of keys 1 and 2, one row an append, a seeded object in level 1,
// three levels, deletes that mark rows in their objects, compactions that merge level 0 into a level
// above. Each created version is abstracted to the model's vocabulary, objects named after the writer
// that wrote them and rows after the appender, read from the objects themselves, and the model's
// properties are checked on every version: no row read twice, no acknowledged row lost, no deleted
// row brought back, no two objects of a level above 0 sharing a key.
//
// THE HISTORIES ARE TLC'S TO JUDGE TOO. With VORTICITY_MODEL_TRACES set to a directory, each distinct
// history is written there as a trace of the model, with a configuration TLC accepts only if the model
// can create those versions in that order: docs/design/models/dataset-commit-trace says how to run it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>A row of the scenarios: its key, and the writer that appended it.</summary>
[VortexRecord]
public partial record struct ScheduleRow(long Key, string Name);

public sealed class DatasetScheduleTests
{
    /// <summary>The attempts a commit makes, in the code and in the model: more than any scenario here loses.</summary>
    private const int MaxAttempts = 4;

    /// <summary>The key of the seeded row, the larger of the model's two.</summary>
    private const long SeedKey = 2;

    private static readonly TimeSpan Waiting = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task EveryOrderOfTwoAppendsAndACompactorKeepsTheModelsProperties()
    {
        Exploration explored = await ExploreAsync(
            "appends", [Actor.Append("a1", 1), Actor.Append("a2", 2), Actor.Compact("c1", 2)], TestContext.Current.CancellationToken);
        Assert.True(explored.Runs >= 20, $"{explored.Runs} runs");
        Assert.True(explored.Lost > 0, "no run lost a race");
    }

    [Fact]
    public async Task EveryOrderOfAnAppendADeleteAndACompactorKeepsTheModelsProperties()
    {
        Exploration explored = await ExploreAsync(
            "delete", [Actor.Append("a1", 2), Actor.Delete("d1", 2), Actor.Compact("c1", 1)], TestContext.Current.CancellationToken);
        Assert.True(explored.Runs >= 20, $"{explored.Runs} runs");
        Assert.True(explored.Abandoned > 0, "no run abandoned a replacement");
    }

    [Fact]
    public async Task EveryOrderOfADeleteBesideAMergeKeepsTheModelsProperties()
    {
        // Two appends of two keys, which a job merges with the seed into one object of both, so that a delete after
        // it marks rows in that object rather than taking it out whole, and one before it takes objects out whole.
        Exploration explored = await ExploreAsync(
            "marks", [Actor.Append("a1", 1), Actor.Append("a2", 2), Actor.Delete("d1", 2), Actor.Compact("c1", 1)], TestContext.Current.CancellationToken);
        Assert.True(explored.Runs >= 100, $"{explored.Runs} runs");
        Assert.True(explored.Marked > 0, "no run marked rows");
    }

    // ------------------------------ the exploration ------------------------------

    private static async Task<Exploration> ExploreAsync(string name, IReadOnlyList<Actor> actors, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        List<int> prefix = [];
        Exploration explored = new Exploration();
        Dictionary<string, List<int>> histories = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        while (true)
        {
            Run run = await RunAsync(actors, prefix, ct);
            explored.Runs++;
            explored.Lost += run.Lost;
            explored.Abandoned += run.Abandoned;
            explored.Marked += run.History.Any(version => version.Levels.Any(level => level.Any(entry => entry.Marked.Count > 0))) ? 1 : 0;
            string picks = string.Join(",", run.Picks.Select(pick => pick.Chosen));
            Check(run, picks);
            string history = Tla(run.History);
            if (!histories.ContainsKey(history))
            {
                histories[history] = [.. run.Picks.Select(pick => pick.Chosen)];
            }

            int last = run.Picks.Count - 1;
            while (last >= 0 && run.Picks[last].Chosen + 1 >= run.Picks[last].Options)
            {
                last--;
            }

            if (last < 0)
            {
                break;
            }

            prefix = [.. run.Picks.Take(last).Select(pick => pick.Chosen), run.Picks[last].Chosen + 1];
        }

        Console.WriteLine(
            $"SCHEDULE {name}: {explored.Runs} orders, {histories.Count} distinct histories, {explored.Lost} races lost, " +
            $"{explored.Abandoned} replacements abandoned, {explored.Marked} runs that marked rows");
        if (Environment.GetEnvironmentVariable("VORTICITY_MODEL_TRACES") is { Length: > 0 } directory)
        {
            WriteTraces(Path.Combine(directory, name), name, actors, histories);
        }

        return explored;
    }

    /// <summary>One run of the scenario, the picks of <paramref name="prefix"/> first and the first option after.</summary>
    private static async Task<Run> RunAsync(IReadOnlyList<Actor> actors, List<int> prefix, CancellationToken ct)
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore inner = new MemoryObjectStore { TimeProvider = clock };
        ScheduledStore store = new ScheduledStore(inner);
        DatasetOptions options = Options(clock);

        // The seeded version: one row in level 1, as the model's first version holds.
        ulong first;
        await using (VortexDataset setup = await VortexDataset.CreateAsync(store, ScheduleRow.Schema, options, ct))
        {
            await AppendAsync(setup, "seed", SeedKey, ct);
            Assert.NotNull(await setup.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0, MaxLevels = 3 }, ct));
            first = setup.Version;
        }

        Schedule schedule = new Schedule();
        store.Scheduler = schedule;
        List<VortexDataset> handles = [];
        try
        {
            List<ActorState> states = [];
            foreach (Actor actor in actors)
            {
                handles.Add(await VortexDataset.OpenAsync(store, options, ct));
                states.Add(new ActorState(actor));
            }

            List<Task> running = [];
            for (int i = 0; i < actors.Count; i++)
            {
                (ActorState state, VortexDataset handle) = (states[i], handles[i]);
                running.Add(schedule.Start(state, () => ActAsync(state, handle, schedule, ct)));
            }

            List<Pick> picks = [];
            while (true)
            {
                IReadOnlyList<ActorState> waiting = await schedule.QuiesceAsync();
                if (waiting.Count == 0)
                {
                    break;
                }

                int chosen = picks.Count < prefix.Count ? prefix[picks.Count] : 0;
                Assert.True(chosen < waiting.Count, $"pick {picks.Count} has {waiting.Count} options, not {chosen + 1}: the run is not deterministic");
                picks.Add(new Pick(chosen, waiting.Count));
                schedule.Release(waiting[chosen]);
            }

            await Task.WhenAll(running).WaitAsync(Waiting, ct);
            ulong latest;
            await using (VortexDataset final = await VortexDataset.OpenAsync(inner, options, ct))
            {
                latest = final.Version;
            }

            List<AbstractVersion> history = await HistoryAsync(inner, options, first, latest, store.Creators, ct);
            return new Run(picks, history, states, states.Sum(state => state.Lost), states.Sum(state => state.Abandoned));
        }
        finally
        {
            foreach (VortexDataset handle in handles)
            {
                await handle.DisposeAsync();
            }
        }
    }

    /// <summary>What a writer does: the model's actors, through the public API.</summary>
    private static async Task ActAsync(ActorState state, VortexDataset handle, Schedule schedule, CancellationToken ct)
    {
        Actor actor = state.Actor;
        switch (actor.Kind)
        {
            case ActorKind.Append:
                await AppendAsync(handle, actor.Name, actor.Key, ct);
                state.Acked = true;
                break;

            case ActorKind.Delete:
                await schedule.StepAsync();
                await handle.RefreshAsync(ct);
                try
                {
                    long key = actor.Key;
                    RowChangeResult deleted = await handle.DeleteAsync<ScheduleRow>(row => row.Key == key, ct);
                    state.Outcomes.Add(deleted.Outcome);
                }
                catch (ObjectStoreException)
                {
                    state.Outcomes.Add(OperationOutcome.Dropped);
                }

                break;

            case ActorKind.Compact:
                for (int job = 1; job <= actor.Jobs; job++)
                {
                    await schedule.StepAsync();
                    await handle.RefreshAsync(ct);
                    state.Job = job;
                    try
                    {
                        CompactionResult? result = await handle.CompactAsync(Compaction, ct);
                        if (result is null)
                        {
                            break;
                        }

                        state.Outcomes.Add(result.Outcome);
                        state.Abandoned += result.Outcome == OperationOutcome.Abandoned ? 1 : 0;
                    }
                    catch (ObjectStoreException)
                    {
                        // Lost on every attempt: the job is given up, as the model's compactor does.
                        state.Outcomes.Add(OperationOutcome.Dropped);
                    }
                }

                break;
        }
    }

    private static async Task AppendAsync(VortexDataset dataset, string name, long key, CancellationToken ct)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ScheduleRow>([new ScheduleRow(key, name)], ct);
        await dataset.AppendAsync(draft, ct);
    }

    /// <summary>
    /// The model's compactor: level 0 merged into a level above, never moved whole from level 0, and no
    /// job the model does not have, neither a purge of marks nor a merge of neighbours.
    /// </summary>
    private static readonly CompactionOptions Compaction = new CompactionOptions
    {
        LevelZeroCeiling = 0,
        MaxLevels = 3,
        PurgeMarks = false,
        TrivialMoves = false,
        MergeNeighbours = false,
    };

    private static DatasetOptions Options(ManualClock clock) => new DatasetOptions
    {
        Seed = 0x5C4E_D01E,
        Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(ScheduleRow.Key))] },
        MaxAttempts = MaxAttempts,
        TimeProvider = clock,

        // A delete marks the rows it takes in their object, as the model's deleter does, whatever its size and
        // whatever share of its rows the marks reach, and takes it out whole when it takes every row left.
        MarkedObjectBytes = 0,
        MarkedShare = 1,
    };

    // ------------------------------ the abstraction ------------------------------

    /// <summary>
    /// Every version from <paramref name="first"/> to <paramref name="latest"/>, in the model's terms: per level, each
    /// object named after who wrote it, its rows after the appenders, the rows its deletion vector marks, and the
    /// smallest and largest key of its rows.
    /// </summary>
    private static async Task<List<AbstractVersion>> HistoryAsync(
        MemoryObjectStore store, DatasetOptions options, ulong first, ulong latest, IReadOnlyDictionary<ulong, (ActorState Actor, int Job)> creators, CancellationToken ct)
    {
        Dictionary<UInt128, string> names = [];
        List<AbstractVersion> history = [];
        for (ulong version = first; version <= latest; version++)
        {
            await using VortexDataset at = await VortexDataset.OpenAsync(store, version, options, ct);
            (ActorState Actor, int Job)? creator = creators.TryGetValue(version, out (ActorState, int) created) ? created : null;
            List<AbstractEntry>[] levels = [[], [], []];
            for (int level = 0; level < at.Levels.Count; level++)
            {
                Assert.True(level < levels.Length, $"version {version} has a level {level}");
                await foreach (TreeEntry leaf in at.Levels[level].EnumerateAsync(at.Pages, ct))
                {
                    ObjectEntry entry = ObjectEntry.FromBytes(leaf.Value.Span);
                    if (!names.TryGetValue(entry.Uid, out string? named))
                    {
                        named = creator switch
                        {
                            null => "<<\"seed\">>",
                            { Actor.Actor.Kind: ActorKind.Append } made => $"<<\"append\", \"{made.Actor.Actor.Name}\">>",
                            { Actor.Actor.Kind: ActorKind.Compact } made => $"<<\"job\", \"{made.Actor.Actor.Name}\", {made.Job}>>",
                            _ => throw new InvalidOperationException($"version {version} has an object no compactor or appender wrote"),
                        };
                        names[entry.Uid] = named;
                    }

                    Assert.True(entry.IsResolved, "a deletion vector out of line");
                    await using ObjectLease lease = await at.RentAsync(entry, ct);
                    List<ScheduleRow> rows = await lease.File.Scan<ScheduleRow>().ToRecordsAsync(ct).ToListAsync(ct);
                    SortedSet<string> marked = new SortedSet<string>(StringComparer.Ordinal);
                    for (int position = 0; position < rows.Count; position++)
                    {
                        if (entry.Deletions.Contains(position))
                        {
                            marked.Add(rows[position].Name);
                        }
                    }

                    levels[level].Add(new AbstractEntry(
                        named, new SortedSet<string>(rows.Select(row => row.Name), StringComparer.Ordinal), marked, rows.Min(row => row.Key), rows.Max(row => row.Key)));
                }
            }

            history.Add(new AbstractVersion(version, creator?.Actor, levels));
        }

        return history;
    }

    // ------------------------------ the model's properties ------------------------------

    private static void Check(Run run, string picks)
    {
        HashSet<string> erased = new HashSet<string>(StringComparer.Ordinal);
        for (int at = 0; at < run.History.Count; at++)
        {
            AbstractVersion version = run.History[at];
            string where = $"order {picks}, version {version.Number}";

            // NoDuplicate: no row is read twice.
            List<string> live = [.. version.Levels.SelectMany(level => level).SelectMany(entry => entry.Rows.Except(entry.Marked))];
            Assert.True(live.Count == live.Distinct().Count(), $"{where}: a row read twice in {Describe(version)}");

            // KeyDisjoint: above level 0, no two objects of a level share a key.
            for (int level = 1; level < version.Levels.Length; level++)
            {
                List<AbstractEntry> objects = [.. version.Levels[level].OrderBy(entry => entry.Lo)];
                for (int i = 1; i < objects.Count; i++)
                {
                    Assert.True(objects[i - 1].Hi < objects[i].Lo, $"{where}: level {level} overlaps in {Describe(version)}");
                }
            }

            // NoResurrection: the rows a delete took come back in no later version.
            if (at > 0 && version.Creator?.Actor.Kind == ActorKind.Delete)
            {
                HashSet<string> before = [.. run.History[at - 1].Levels.SelectMany(level => level).SelectMany(entry => entry.Rows.Except(entry.Marked))];
                erased.UnionWith(before.Except(live));
            }

            Assert.True(!live.Intersect(erased).Any(), $"{where}: a deleted row came back in {Describe(version)}");
        }

        // NoLostRow: an append its writer was told landed, and no delete took, is in the latest version.
        AbstractVersion latest = run.History[^1];
        HashSet<string> rows = [.. latest.Levels.SelectMany(level => level).SelectMany(entry => entry.Rows.Except(entry.Marked))];
        foreach (ActorState state in run.States.Where(state => state.Acked && !erased.Contains(state.Actor.Name)))
        {
            Assert.True(rows.Contains(state.Actor.Name), $"order {picks}: {state.Actor.Name}'s row is lost from {Describe(latest)}");
        }
    }

    private static string Describe(AbstractVersion version) => Tla(version);

    // ------------------------------ the traces ------------------------------

    private static string Tla(IReadOnlyList<AbstractVersion> history) =>
        "<<" + string.Join(",\n  ", history.Select(Tla)) + ">>";

    private static string Tla(AbstractVersion version) =>
        "(" + string.Join(" @@ ", version.Levels.Select((objects, level) =>
            $"{level} :> {{{string.Join(", ", objects.Select(Tla).Order(StringComparer.Ordinal))}}}")) + ")";

    private static string Tla(AbstractEntry entry) =>
        $"[uid |-> {entry.Uid}, rows |-> {Set(entry.Rows)}, marked |-> {Set(entry.Marked)}, lo |-> {entry.Lo}, hi |-> {entry.Hi}]";

    private static string Set(IEnumerable<string> names) => "{" + string.Join(", ", names.Select(name => $"\"{name}\"")) + "}";

    /// <summary>
    /// Each distinct history as a definition of a TLA+ module the trace model extends, with a configuration each, and
    /// the exit code each must give: 12, TLC having followed the model through every version of the run.
    /// </summary>
    private static void WriteTraces(string directory, string name, IReadOnlyList<Actor> actors, Dictionary<string, List<int>> histories)
    {
        Directory.CreateDirectory(directory);
        string prefix = string.Concat(name.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        StringBuilder module = new StringBuilder();
        module.Append(CultureInfo.InvariantCulture, $"---- MODULE {prefix}Runs ----\n");
        module.Append("\\* Generated by DatasetScheduleTests: the versions each order of the scenario created.\n");
        module.Append("EXTENDS TLC\n\n");
        StringBuilder expected = new StringBuilder();
        int index = 0;
        foreach ((string history, List<int> picks) in histories)
        {
            index++;
            module.Append(CultureInfo.InvariantCulture, $"\\* picks {string.Join(",", picks)}\n{prefix}{index} ==\n  {history}\n\n");
            System.IO.File.WriteAllText(
                Path.Combine(directory, $"{prefix}{index}.cfg"),
                Configuration(actors, $"{prefix}{index}"));
            expected.Append(CultureInfo.InvariantCulture, $"{prefix}{index}.cfg=12\n");
        }

        module.Append("====\n");
        System.IO.File.WriteAllText(Path.Combine(directory, $"{prefix}Runs.tla"), module.ToString());
        System.IO.File.WriteAllText(Path.Combine(directory, "expected-exit-codes.txt"), expected.ToString());
        System.IO.File.WriteAllText(
            Path.Combine(directory, "DatasetCommitTrace.tla"),
            System.IO.File.ReadAllText(Path.Combine(Root(), "docs", "design", "models", "dataset-commit-trace", "DatasetCommitTrace.tla"))
                .Replace("EXTENDS DatasetCommit, TLC, DatasetCommitRuns", $"EXTENDS DatasetCommit, TLC, {prefix}Runs", StringComparison.Ordinal));
    }

    /// <summary>The model's constants for the scenario, and the run the configuration checks.</summary>
    internal static string Configuration(IReadOnlyList<Actor> actors, string run)
    {
        static string Names(IEnumerable<Actor> chosen) => "{" + string.Join(", ", chosen.Select(actor => $"\"{actor.Name}\"")) + "}";
        return $"""
            \* module: DatasetCommitTrace
            SPECIFICATION TraceSpec
            CONSTANTS
                Appenders = {Names(actors.Where(actor => actor.Kind == ActorKind.Append))}
                Deleters = {Names(actors.Where(actor => actor.Kind == ActorKind.Delete))}
                Compactors = {Names(actors.Where(actor => actor.Kind == ActorKind.Compact))}
                Keys = {"{"}1, 2{"}"}
                MaxLevel = 2
                Jobs = {actors.Where(actor => actor.Kind == ActorKind.Compact).Select(actor => actor.Jobs).DefaultIfEmpty(0).Max()}
                MaxAttempts = {MaxAttempts}
                Crashes = FALSE
                PhantomExists = FALSE
                Seed = TRUE
                UidInKey = TRUE
                RewalkDestination = TRUE
                CheckExpected = TRUE
                TraceHistory <- {run}
            INVARIANT NotFollowed
            CHECK_DEADLOCK FALSE

            """;
    }

    private static string Root()
    {
        string? at = AppContext.BaseDirectory;
        while (at is not null && !System.IO.File.Exists(Path.Combine(at, "Vorticity.slnx")))
        {
            at = Path.GetDirectoryName(at);
        }

        return at ?? throw new InvalidOperationException("no repository root above the test's directory");
    }

    // ------------------------------ the scheduler ------------------------------

    /// <summary>
    /// The writers of a run, each running until it waits at a put or a plan, or ends; one is released at a time once
    /// none runs.
    /// </summary>
    private sealed class Schedule
    {
        private static readonly AsyncLocal<ActorState?> Current = new AsyncLocal<ActorState?>();
        private readonly object _lock = new object();
        private readonly List<ActorState> _running = [];
        private readonly List<(ActorState Actor, TaskCompletionSource Go)> _waiting = [];
        private TaskCompletionSource _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static ActorState? Actor => Current.Value;

        internal Task Start(ActorState actor, Func<Task> body)
        {
            lock (_lock)
            {
                _running.Add(actor);
            }

            return Task.Run(async () =>
            {
                Current.Value = actor;
                try
                {
                    await body();
                }
                finally
                {
                    lock (_lock)
                    {
                        _running.Remove(actor);
                        Pulse();
                    }
                }
            });
        }

        /// <summary>Waits to be released, when called from a writer of the run.</summary>
        internal Task StepAsync()
        {
            if (Current.Value is not { } actor)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _running.Remove(actor);
                _waiting.Add((actor, go));
                Pulse();
            }

            return go.Task;
        }

        /// <summary>The writers waiting once none runs, in the order of the scenario; none when all are done.</summary>
        internal async Task<IReadOnlyList<ActorState>> QuiesceAsync()
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (_running.Count == 0)
                    {
                        return [.. _waiting.Select(waiting => waiting.Actor).OrderBy(actor => actor.Actor.Name, StringComparer.Ordinal)];
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(Waiting);
            }
        }

        internal void Release(ActorState actor)
        {
            TaskCompletionSource go;
            lock (_lock)
            {
                int at = _waiting.FindIndex(waiting => waiting.Actor == actor);
                go = _waiting[at].Go;
                _waiting.RemoveAt(at);
                _running.Add(actor);
            }

            go.SetResult();
        }

        private void Pulse()
        {
            TaskCompletionSource changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.SetResult();
        }
    }

    /// <summary>A store that holds each conditional put of a commit until the scheduler releases its writer, and records who created each version.</summary>
    private sealed class ScheduledStore(IObjectStore inner) : ForwardingStore(inner)
    {
        private readonly Dictionary<ulong, (ActorState, int)> _creators = [];

        internal Schedule? Scheduler { get; set; }

        internal IReadOnlyDictionary<ulong, (ActorState Actor, int Job)> Creators
        {
            get
            {
                lock (_creators)
                {
                    return new Dictionary<ulong, (ActorState, int)>(_creators);
                }
            }
        }

        public override async ValueTask<PutOutcome> PutAsync(
            string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            bool commit = condition.Kind == PutConditionKind.IfAbsent && CommitKey.IsCommit(key);
            ActorState? actor = Schedule.Actor;
            if (commit && Scheduler is { } schedule && actor is not null)
            {
                await schedule.StepAsync();
            }

            PutOutcome outcome = await Inner.PutAsync(key, content, length, condition, cancellationToken);
            if (commit && actor is not null)
            {
                if (outcome == PutOutcome.Created && CommitKey.TryParse(key, out ulong version))
                {
                    lock (_creators)
                    {
                        _creators[version] = (actor, actor.Job);
                    }
                }
                else if (outcome == PutOutcome.Exists)
                {
                    actor.Lost++;
                }
            }

            return outcome;
        }
    }

    // ------------------------------ the vocabulary ------------------------------

    internal enum ActorKind
    {
        Append,
        Delete,
        Compact,
    }

    /// <summary>A writer of the scenario: an appender of one row of a key, a deleter of a key's rows, a compactor of some jobs.</summary>
    internal sealed record Actor(string Name, ActorKind Kind, long Key, int Jobs)
    {
        internal static Actor Append(string name, long key) => new Actor(name, ActorKind.Append, key, 0);

        internal static Actor Delete(string name, long key) => new Actor(name, ActorKind.Delete, key, 0);

        internal static Actor Compact(string name, int jobs) => new Actor(name, ActorKind.Compact, 0, jobs);
    }

    private sealed class ActorState(Actor actor)
    {
        internal Actor Actor { get; } = actor;

        internal bool Acked { get; set; }

        internal int Job { get; set; }

        internal int Lost { get; set; }

        internal int Abandoned { get; set; }

        internal List<OperationOutcome> Outcomes { get; } = [];
    }

    private sealed record AbstractEntry(string Uid, SortedSet<string> Rows, SortedSet<string> Marked, long Lo, long Hi);

    private sealed record AbstractVersion(ulong Number, ActorState? Creator, List<AbstractEntry>[] Levels);

    private readonly record struct Pick(int Chosen, int Options);

    private sealed record Run(List<Pick> Picks, List<AbstractVersion> History, List<ActorState> States, int Lost, int Abandoned);

    private sealed class Exploration
    {
        internal int Runs { get; set; }

        internal int Lost { get; set; }

        internal int Abandoned { get; set; }

        /// <summary>The runs in which a delete marked rows in an object it left in the version.</summary>
        internal int Marked { get; set; }
    }
}
