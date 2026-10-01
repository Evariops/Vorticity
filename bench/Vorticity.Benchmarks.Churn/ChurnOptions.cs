using System;
using System.Globalization;
using Vorticity.Dataset;

namespace Vorticity.Bench.Churn;

/// <summary>Where an append's keys land.</summary>
internal enum KeyPlacement
{
    /// <summary>Anywhere in the loaded range, between the keys already there.</summary>
    Random,

    /// <summary>After every key, as a time series grows.</summary>
    Tail,
}

/// <summary>What the caller runs between operations.</summary>
internal enum CompactionCadence
{
    /// <summary>Nothing: level 0 takes every object.</summary>
    None,

    /// <summary>Every due compaction, after every <see cref="ChurnOptions.CompactEvery"/> operations.</summary>
    Drain,

    /// <summary>
    /// Level 0 compacted by the commit that takes it past its ceiling, inside the operation's time,
    /// and the rest drained as <see cref="Drain"/> does.
    /// </summary>
    Inline,
}

/// <summary>One run: the load, the operations, and what the caller does between them.</summary>
internal sealed record ChurnOptions
{
    internal const string Usage =
        "usage: Vorticity.Benchmarks.Churn [--rows N] [--ops N] [--batch N] [--mix A:U:D] [--keys random|tail]\n" +
        "         [--compact none|drain|inline] [--compact-every N] [--vacuum-every N] [--probe-every N] [--report-every N]\n" +
        "         [--store file|memory] [--dir PATH] [--seed N] [--minutes N] [--load-chunk N] [--index-budget PERMILLE]\n" +
        "         [--max-object MiB] [--level-one KiB] [--open-objects N] [--marks on|off] [--mark-bytes KiB] [--purge on|off] [--probe-rounds N]\n" +
        "         [--pick largest|round-robin]";

    /// <summary>The rows loaded before the first operation.</summary>
    public long Rows { get; init; } = 1_000_000;

    /// <summary>The small operations, each its own commit.</summary>
    public int Ops { get; init; } = 10_000;

    /// <summary>The rows one operation appends, or the keys an update or a delete covers.</summary>
    public int Batch { get; init; } = 10;

    /// <summary>The weight of appends in the mix.</summary>
    public int Appends { get; init; } = 1;

    /// <summary>The weight of updates in the mix.</summary>
    public int Updates { get; init; } = 1;

    /// <summary>The weight of deletes in the mix.</summary>
    public int Deletes { get; init; } = 1;

    /// <summary>Where appended keys land.</summary>
    public KeyPlacement Keys { get; init; } = KeyPlacement.Random;

    /// <summary>What the caller runs between operations.</summary>
    public CompactionCadence Compact { get; init; } = CompactionCadence.Drain;

    /// <summary>How many operations go between two drains.</summary>
    public int CompactEvery { get; init; } = 1;

    /// <summary>How many operations go between two vacuums; 0 never vacuums.</summary>
    public int VacuumEvery { get; init; } = 250;

    /// <summary>How many operations go between two checkpoints of reads; 0 for a tenth of them.</summary>
    public int ProbeEvery { get; init; }

    /// <summary>How many operations one line of the write table sums; 0 for a twentieth of them.</summary>
    public int ReportEvery { get; init; }

    /// <summary>Whether the store is in memory, whose bytes then count in the process's heap.</summary>
    public bool Memory { get; init; }

    /// <summary>The directory of the file store; null for a fresh one, removed at the end.</summary>
    public string? Directory { get; init; }

    /// <summary>The seed of every random choice.</summary>
    public int Seed { get; init; } = 42;

    /// <summary>The time after which the run stops taking operations; 0 for none.</summary>
    public double Minutes { get; init; }

    /// <summary>The rows each append of the load writes.</summary>
    public int LoadChunk { get; init; } = 1_000_000;

    /// <summary>
    /// The index budget per thousand bytes of data the objects are written under; 0 for the
    /// library's default.
    /// </summary>
    public int IndexBudget { get; init; }

    /// <summary>The cap on an object compaction writes, in MiB; 0 for the library's default.</summary>
    public int MaxObjectMiB { get; init; }

    /// <summary>The object target of level 1, in KiB; 0 for the library's default.</summary>
    public int LevelOneKiB { get; init; }

    /// <summary>How many objects a handle keeps open; 0 for the library's default.</summary>
    public int OpenObjects { get; init; }

    /// <summary>
    /// Whether a delete or an update marks the rows it takes in the objects that hold them, as the
    /// library does by default, or rewrites every such object, as it does below its thresholds.
    /// </summary>
    public bool Marks { get; init; } = true;

    /// <summary>The most an object's marks take before a delete rewrites it, in KiB; 0 for the library's default.</summary>
    public int MarkKiB { get; init; }

    /// <summary>Whether compaction rewrites an object whose marks reach half a delete's bounds, as the library does by default.</summary>
    public bool Purge { get; init; } = true;

    /// <summary>Which objects a job takes from a level over its size.</summary>
    public CompactionPick Pick { get; init; } = CompactionPick.Largest;

    /// <summary>
    /// How many times each probe's calls are timed at a checkpoint, the fastest round kept: what
    /// other work on the machine adds to a round, the fastest one holds the least of.
    /// </summary>
    public int ProbeRounds { get; init; } = 1;

    /// <summary>Whether the usage was asked for.</summary>
    public bool Help { get; init; }

    /// <summary>The checkpoint interval once defaults are applied.</summary>
    public int ProbeInterval => ProbeEvery > 0 ? ProbeEvery : Math.Max(1, Ops / 10);

    /// <summary>The report interval once defaults are applied.</summary>
    public int ReportInterval => ReportEvery > 0 ? ReportEvery : Math.Max(1, Ops / 20);

    /// <summary>Reads the command line.</summary>
    /// <exception cref="ArgumentException">An argument is unknown or malformed.</exception>
    public static ChurnOptions Parse(string[] args)
    {
        ChurnOptions options = new ChurnOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            if (flag is "--help" or "-h")
            {
                return options with { Help = true };
            }

            string value = i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{flag} takes a value.");
            options = flag switch
            {
                "--rows" => options with { Rows = Long(flag, value) },
                "--ops" => options with { Ops = Int(flag, value) },
                "--batch" => options with { Batch = Int(flag, value) },
                "--mix" => Mix(options, value),
                "--keys" => options with { Keys = value == "tail" ? KeyPlacement.Tail : value == "random" ? KeyPlacement.Random : throw new ArgumentException($"--keys takes random or tail, not '{value}'.") },
                "--compact" => options with
                {
                    Compact = value switch
                    {
                        "none" => CompactionCadence.None,
                        "drain" => CompactionCadence.Drain,
                        "inline" => CompactionCadence.Inline,
                        _ => throw new ArgumentException($"--compact takes none, drain or inline, not '{value}'."),
                    },
                },
                "--compact-every" => options with { CompactEvery = Int(flag, value) },
                "--vacuum-every" => options with { VacuumEvery = Int(flag, value) },
                "--probe-every" => options with { ProbeEvery = Int(flag, value) },
                "--report-every" => options with { ReportEvery = Int(flag, value) },
                "--store" => options with { Memory = value == "memory" ? true : value == "file" ? false : throw new ArgumentException($"--store takes file or memory, not '{value}'.") },
                "--dir" => options with { Directory = value },
                "--seed" => options with { Seed = Int(flag, value) },
                "--minutes" => options with { Minutes = double.Parse(value, CultureInfo.InvariantCulture) },
                "--load-chunk" => options with { LoadChunk = Int(flag, value) },
                "--index-budget" => options with { IndexBudget = Int(flag, value) },
                "--max-object" => options with { MaxObjectMiB = Int(flag, value) },
                "--level-one" => options with { LevelOneKiB = Int(flag, value) },
                "--open-objects" => options with { OpenObjects = Int(flag, value) },
                "--marks" => options with { Marks = value == "on" ? true : value == "off" ? false : throw new ArgumentException($"--marks takes on or off, not '{value}'.") },
                "--mark-bytes" => options with { MarkKiB = Int(flag, value) },
                "--purge" => options with { Purge = value == "on" ? true : value == "off" ? false : throw new ArgumentException($"--purge takes on or off, not '{value}'.") },
                "--probe-rounds" => options with { ProbeRounds = Math.Max(1, Int(flag, value)) },
                "--pick" => options with
                {
                    Pick = value switch
                    {
                        "largest" => CompactionPick.Largest,
                        "round-robin" => CompactionPick.RoundRobin,
                        _ => throw new ArgumentException($"--pick takes largest or round-robin, not '{value}'."),
                    },
                },
                _ => throw new ArgumentException($"Unknown argument '{flag}'."),
            };
        }

        return options;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        string cadence = Compact switch
        {
            CompactionCadence.Drain => string.Create(CultureInfo.InvariantCulture, $"drain every {CompactEvery}"),
            CompactionCadence.Inline => string.Create(CultureInfo.InvariantCulture, $"level 0 inline, the rest drained every {CompactEvery}"),
            _ => "none",
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Rows} rows, {Ops} ops of {Batch} rows, mix {Appends}:{Updates}:{Deletes} (append:update:delete), " +
            $"keys {Keys.ToString().ToLowerInvariant()}, compaction {cadence}, vacuum every {VacuumEvery}, " +
            $"{(Memory ? "memory" : "file")} store, index budget {(IndexBudget > 0 ? IndexBudget.ToString(CultureInfo.InvariantCulture) + "‰" : "default")}, " +
            $"deletes by {(Marks ? "marks" : "rewrites")}{(Marks && MarkKiB > 0 ? string.Create(CultureInfo.InvariantCulture, $" of at most {MarkKiB} KiB") : "")}" +
            $"{(Marks && !Purge ? ", never purged" : "")}, {(Pick == CompactionPick.RoundRobin ? "round robin" : "largest first")}, seed {Seed}");
    }

    private static ChurnOptions Mix(ChurnOptions options, string value)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 3)
        {
            throw new ArgumentException($"--mix takes three weights, appends:updates:deletes, not '{value}'.");
        }

        return options with { Appends = Int("--mix", parts[0]), Updates = Int("--mix", parts[1]), Deletes = Int("--mix", parts[2]) };
    }

    private static int Int(string flag, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{flag} takes a count, not '{value}'.");

    private static long Long(string flag, string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{flag} takes a count, not '{value}'.");
}
