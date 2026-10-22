using System;
using System.Collections.Generic;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>What a dataset handle needs beyond its store: how it writes, reads and commits.</summary>
/// <remarks>
/// The clustering key and the retention are fixed when the dataset is created and carried by every
/// commit; at an open they come from the dataset and these values are ignored.
/// </remarks>
public sealed record DatasetOptions
{
    /// <summary>
    /// The session whose pool, cache, bound on reads in flight and parallelism the dataset's scans
    /// use; <see cref="VortexSession.Default"/> unless given.
    /// </summary>
    public VortexSession Session { get; init; } = VortexSession.Default;

    /// <summary>How the data objects this handle writes are encoded.</summary>
    public VortexWriteOptions Write { get; init; } = VortexWriteOptions.Default;

    /// <summary>
    /// The columns the dataset is ordered by, or null for the order objects arrive in. Declaring one
    /// orders the objects by the smallest key they hold, gives every object this handle writes a
    /// sorted run on it so that a lookup inside one object is a seek, and makes compaction keep the
    /// levels above 0 key-disjoint, so a lookup by key touches a bounded number of objects. The key
    /// is declared on non-nullable columns: a null has no place in the order the levels are disjoint
    /// on, and a run on several columns holds no tuple with one. A row whose key is unknown takes a
    /// value of its own instead, the smallest or the largest its type has.
    /// </summary>
    public IReadOnlyList<string>? ClusteringKey { get; init; }

    /// <summary>How many times a commit re-applies its changes to a newer version before giving up; 8 by default.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// How long a superseded version stays readable before vacuum may delete what only it
    /// references; zero, the default, means seven days.
    /// </summary>
    public TimeSpan RetentionWindow { get; init; }

    /// <summary>How many superseded versions vacuum keeps whatever their age; none by default.</summary>
    public int RetainedVersions { get; init; }

    /// <summary>
    /// The most bytes one data object may take, refused past them as it is written; no bound by default,
    /// where the store refuses what its parts cannot reach. An object is held in memory up to 32 MiB and
    /// sent to the store as it is written past them, so this bounds what the store keeps, not what the
    /// writer holds.
    /// </summary>
    public long MaxObjectBytes { get; init; } = ObjectSegmentSink.DefaultMaxBytes;

    /// <summary>
    /// The bytes past which an object is sent to the store as it is written rather than held whole:
    /// <see cref="ObjectSegmentSink.DefaultStreamedFromBytes"/>, the part threshold, unless a test asks for less.
    /// </summary>
    internal long StreamedObjectBytes { get; init; } = ObjectSegmentSink.DefaultStreamedFromBytes;

    /// <summary>
    /// The bytes of an object's footer from which a scan for an identifier's value finds the object's rows by its index
    /// rather than open it: a mebibyte, unless a test asks for less. A lookup reads a few kilobytes a column, each column
    /// asked for apart, where an open reads the footer whole: under a mebibyte the open asks the store for less.
    /// </summary>
    internal long LookupFooterBytes { get; init; } = 1 << 20;

    /// <summary>
    /// The bytes a rewrite announces to the store for each byte it reads, as the bound of an object it
    /// streams: <see cref="ObjectBound.OutPerIn"/>, unless a test asks for less to run past it.
    /// </summary>
    internal double BoundOutPerIn { get; init; } = ObjectBound.OutPerIn;

    /// <summary>The columns an object's entry summarises, or null for the first <see cref="SummaryColumnLimit"/>.</summary>
    /// <remarks>The summaries are what a scan prunes whole objects by without opening them; the clustering key's columns are always among them.</remarks>
    public IReadOnlyList<string>? SummaryColumns { get; init; }

    /// <summary>How many columns an entry summarises when none are declared; 32 by default.</summary>
    public int SummaryColumnLimit { get; init; } = ColumnSummary.DefaultLimit;

    /// <summary>
    /// How many data objects stay open between scans; 32 by default. A data object is immutable, so
    /// an open handle never goes stale: this only bounds the descriptors and parsed footers held.
    /// </summary>
    /// <remarks>
    /// A scan in key order holds open at once every object whose keys interleave with another's,
    /// whatever this bound, and the next such scan opens again those the bound did not keep: a
    /// bound at the number of objects spares it every open.
    /// </remarks>
    public int MaxOpenObjects { get; init; } = 32;

    /// <summary>
    /// The bytes of tree pages read from the store that the handle keeps from one version it reads to
    /// the next, least recently used first out; 32 MiB by default, and 0 keeps none past the version
    /// that read them. A version names every page its commit did not rewrite by the reference an
    /// earlier version gave it, so a refresh or a commit asks the store again only for the pages that
    /// changed, as long as the pages it walks fit here.
    /// </summary>
    /// <remarks>
    /// Beside them the handle keeps, whatever this says, what the reads opening the last sixteen
    /// versions it read or wrote brought back of their pages: the pages those versions wrote, which a
    /// later version names until a commit rewrites them.
    /// </remarks>
    public long PageCacheBytes { get; init; } = 32L << 20;

    /// <summary>
    /// The most bytes a commit that takes level 0 past its ceiling compacts before it returns; 0, the
    /// default, compacts nothing inline. Past the ceiling, level 0 is merged into the level above when
    /// the job reads no more than this, so a writer with no process to spare holds the read bound
    /// itself, at the price of the merge's latency in the commit that crosses it; a larger job is left
    /// to another driver, and the commit returns at once.
    /// </summary>
    /// <remarks>
    /// The commit stands whatever befalls the compaction after it, and its outcome is what the caller
    /// is told: a compaction that fails, or that the caller cancels, leaves level 0 as it was for the
    /// next commit or another driver, and <see cref="VortexDataset.LastInlineCompactionFailure"/>
    /// says what a failed one raised. The compaction commits a version of its own, after the
    /// caller's. Commits running at once on one handle run one such compaction between them.
    /// </remarks>
    public long InlineCompactionBytes { get; init; }

    /// <summary>
    /// The clock a commit's creation time is read from, and that dates when the handle last knew its
    /// version to be the latest; the system's by default.
    /// </summary>
    /// <remarks>
    /// It must agree with the store's to within half the retention window, as a vacuum's clock must:
    /// for that half window after it learned its version was the latest, a handle takes every version
    /// after it for still being in the store, and past it lists the commits instead.
    /// </remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Whether the dataset lives on a store that keeps every object under a retention lock, S3 Object
    /// Lock, Azure immutable blob storage, a GCS bucket lock, so that nothing it writes can be deleted
    /// before the lock's date; fixed when the dataset is created and carried by every commit.
    /// </summary>
    /// <remarks>
    /// Such a store keeps every byte written for the lock's term, so the dataset writes as few as it
    /// can: a delete marks rows whatever the object's size or the share they take, its vectors up to a
    /// page's bound, since a rewrite would add bytes and free none; compaction holds the read bounds
    /// only, purging no marks and merging the levels at a fan-out of 100 unless the dataset states one;
    /// and vacuum deletes nothing a lock or a legal hold still keeps, reporting it instead.
    /// </remarks>
    public bool LockedStore { get; init; }

    /// <summary>
    /// Whether the dataset is encrypted. Set at creation, every object it writes is sealed, data
    /// objects and commit objects, under a data key drawn for it and a dataset id minted with it, both
    /// from <see cref="Session"/>'s keyring. Given to a handle of an existing dataset, it refuses one
    /// that is plain.
    /// </summary>
    /// <remarks>
    /// A dataset is also created encrypted when its session seals its files
    /// (<see cref="VortexSessionOptions.EncryptFiles"/>) or refuses plaintext. It is encrypted when its
    /// commit objects are sealed, and nothing in a commit header says so: a handle of a session with
    /// the keyring opens it whatever this says, and refuses a dataset that mixes sealed and plain
    /// objects. A plain dataset becomes encrypted only by being copied into a new one.
    /// </remarks>
    public bool Encrypted { get; init; }

    /// <summary>
    /// The smallest object whose deleted rows are marked in its entry rather than rewritten out of
    /// it; 1 MiB by default, below which a rewrite costs a few milliseconds and keeps reads plain.
    /// </summary>
    internal long MarkedObjectBytes { get; init; } = 1L << 20;

    /// <summary>
    /// The rows a lane's range of an object takes at least, when a pass on several lanes cuts the object: 2^20 by
    /// default. Each range's scan reads the structures its filter asks of the object, its path directories and its
    /// keys' statistics among them, whatever its rows: an hour of 36 000 log records cut into four ranges of about
    /// 9 000, at 16 lanes, read 3.18 MB where the whole object read 1.06 MB, and allocated 17.9 MB where it
    /// allocated 4.7 MB, no faster. A test cuts its small objects with less.
    /// </summary>
    internal long LeastPieceRows { get; init; } = 1L << 20;

    /// <summary>
    /// The rows of an edge of a synopsis answer, a slice its window cuts, that a piece of a pass on several lanes reads at
    /// least: 2^17 by default, four chunks of the design's requests. An edge of twice as many or more is cut between its
    /// chunks into pieces of its rows alone, as many as the lanes but none smaller, where one lane read the whole edge
    /// while the others waited. A test cuts its small edges with less.
    /// </summary>
    internal long LeastEdgePieceRows { get; init; } = 1L << 17;

    /// <summary>
    /// The objects in a row their synopses answer that one lane's piece of a pass on several lanes takes at most: 8 by
    /// default. An answer reads a few tables and folds their rows, a tenth of a millisecond warm, where a piece costs a
    /// walk of the tree to its rows and a lane's turn: a piece an object made a warm question over 48 hourly objects at
    /// 16 lanes spend more of its lanes' time waiting than answering.
    /// </summary>
    internal int AnswersPerPiece { get; init; } = 8;

    /// <summary>
    /// The rows of synopsis tables, the states, a lane's piece of answers folds at least, when a pass on several lanes
    /// cuts the objects their synopses answer: 2^16 by default, about a millisecond of folding, against the tenth a piece
    /// costs. Answers in a row share a piece while they fold less together, and an answer that folds twice as many or more
    /// is cut between the chunks of its tables into pieces of about a share each, a lane's part of the slices of its
    /// window, where every answer was a lane's whole: a window that cuts three hours folds 3.47 M rows of runs. A test
    /// cuts its small tables with less.
    /// </summary>
    internal long LeastPieceStates { get; init; } = 1L << 16;

    /// <summary>
    /// The rows of a chunk of a synopsis table and the bytes past which an hour's rows are chunks of their own, as the
    /// compaction writes them; null for the defaults (<see cref="SynopsisTables.ChunkRows"/>). A test cuts tables of a few
    /// rows into chunks, which an answer is cut between.
    /// </summary>
    internal (int Rows, long HourBytes)? SynopsisChunking { get; init; }

    /// <summary>
    /// The share of an object's rows its marks may reach, as a divisor: past a
    /// <c>1/MarkedShare</c>-th of its rows deleted, a delete rewrites the object instead; 8 by default.
    /// </summary>
    internal int MarkedShare { get; init; } = 8;

    /// <summary>
    /// The fewest bytes an object's marks may take in its entry, past which a delete rewrites the
    /// object instead; 1 KiB by default, a few hundred runs. The entry lives in a leaf page, which
    /// every commit that marks a row of any object on it writes again: the bound trades the rewrites
    /// it saves against the bytes it adds to each of those commits. A larger object's marks may take
    /// a part of its bytes (<see cref="MarkedVectorShare"/>).
    /// </summary>
    internal int MarkedVectorBytes { get; init; } = 1 << 10;

    /// <summary>
    /// The part of an object's bytes its marks may take, as a divisor: a vector past
    /// <see cref="MarkedVectorBytes"/> up to <c>1/MarkedVectorShare</c>-th of its object, 1,024 by
    /// default. A rewrite costs the object's bytes read and written; a vector out of line costs its
    /// own bytes at each open of the object and each delete that grows it, so marks of a thousandth of
    /// the object stay the cheaper until the object has been opened and marked some two thousand times
    /// since. An object of 4 MiB keeps its marks to 4 KiB, one of 64 MiB to 64 KiB.
    /// </summary>
    internal int MarkedVectorShare { get; init; } = 1 << 10;

    /// <summary>
    /// The most time a deleted row may stay in the store's bytes after its delete: within it, the
    /// planner rewrites every object whose marks are as old as half of it, so that the rewrite has the
    /// other half to run, with every delete those marks gathered since. Null, the default, erases a
    /// row when its marks pass their bounds of share and bytes. Fixed when the dataset is created and
    /// carried by every commit, which a library that does not know the delay refuses.
    /// </summary>
    /// <remarks>
    /// The rewritten object leaves the old one behind for the versions the retention keeps, and a
    /// vacuum deletes it once they pass: the bytes are gone from the store when both are, so the delay
    /// the application owes is this one plus the retention. A dataset on a store that locks what it
    /// keeps cannot erase before the lock's term, and refuses a delay.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The delay is under a second.</exception>
    public TimeSpan? ErasureDelay
    {
        get => _erasureDelay;
        init
        {
            if (value is { } delay && delay < TimeSpan.FromSeconds(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "An erasure delay is a second or more.");
            }

            _erasureDelay = value;
        }
    }

    private readonly TimeSpan? _erasureDelay;

    /// <summary>
    /// Compacts the dataset by periods of its instant, the first column of its clustering key past its
    /// partition columns, a timestamp in UTC: the open period's rows in fresh levels, rows later than the lateness horizon
    /// beside them, and each period, once closed, sealed into one object. Null, the default, compacts
    /// by levels. Fixed when the dataset is created and carried by every commit, which a library that
    /// does not know periods refuses.
    /// </summary>
    public PeriodOptions? Periods { get; init; }

    /// <summary>
    /// How many leading columns of the clustering key are partition columns, such as a retention class
    /// before the instant in a key <c>(Retention, At)</c>: 0, the default, for none. Every object holds one
    /// partition value, an append split into an object per value, and each partition grows its own periods,
    /// so that what dies together is written together and expires without a read. Partition columns have
    /// few values by declaration: an append that holds more than <see cref="MaxPartitionValues"/> is refused.
    /// The instant of a dataset by periods is the key's column after them. Fixed when the dataset is created
    /// and carried by every commit, which a library that does not know partitions refuses.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public int PartitionColumns
    {
        get => _partitionColumns;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _partitionColumns = value;
        }
    }

    private readonly int _partitionColumns;

    /// <summary>
    /// The partition values one append may hold, at most: past it the append is refused before anything is
    /// committed, since each value is an object of its own. 64 by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The bound is not positive.</exception>
    public int MaxPartitionValues
    {
        get => _maxPartitionValues;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxPartitionValues = value;
        }
    }

    private readonly int _maxPartitionValues = 64;

    /// <summary>
    /// The columns whose values questions look up one at a time, such as a request's id: those an
    /// object indexes by value so that a lookup reads the objects that hold the value and no other.
    /// An identifier may also be a key of a variant column read as a string or an integer, written as a
    /// filter writes it, <c>Attributes['user'].AsString()</c>: a job then shreds that key wherever a
    /// row holds it in that class, and indexes it. Null, the default, names none. Fixed when the
    /// dataset is created and carried by every commit.
    /// </summary>
    public IReadOnlyList<string>? Identifiers { get; init; }

    /// <summary>
    /// Keys of variant columns that every object a job writes keeps in a column of its own, in one class, written as a
    /// filter reads them, <c>Attributes['http.status_code'].AsInt64()</c>: shredded whatever the rows a job counts, so
    /// that a question on the key reads that column rather than parse the variant, and the object's synopsis counts its
    /// values. A key read as a string, an integer, a double or a boolean (<c>AsString</c>, <c>AsInt64</c>,
    /// <c>AsDouble</c>, <c>AsBool</c>). Null, the default, names none. Fixed when the dataset is created and carried by
    /// every commit.
    /// </summary>
    public IReadOnlyList<string>? ShreddedPaths { get; init; }

    /// <summary>
    /// Whether an append shreds the <see cref="ShreddedPaths"/> as a job does, so that a question on them over the rows
    /// no job has rewritten yet, the last minutes', reads their columns rather than parse the variant, at the price of
    /// the shredding in each append. False, the default, leaves an append's variants whole until a job rewrites them.
    /// A setting of the handle, which each writer chooses.
    /// </summary>
    public bool ShredOnAppend { get; init; }

    /// <summary>
    /// Columns that questions filter and group by together, beyond the prefixes of an object's synopsis order: each
    /// grouping one to four synopsis columns, or keys of variant columns written as a filter reads them, the first the
    /// one questions pick values of, <c>["Service", "HttpStatus", "Route"]</c>. Every job writes a table of each grouping
    /// of two columns or more by hour and by object beside the prefix tables, and tables of each grouping's measures'
    /// sketches by slice, by hour and by object, weighed by the same choice under the same budget, their chunks bounded
    /// by the first column's values, so that a question over those columns alone, whatever columns the order places
    /// between them, reads those tables rather than the rows or the deepest prefix: a quantile per service over the last
    /// minutes, declared <c>["Service"]</c>, reads the sketches of the slices its window keeps whole. A grouping that
    /// names a column an object's order does not hold writes no table in it. Null, the default, declares none. A setting
    /// of the handle that runs the jobs.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>>? SynopsisGroupings { get; init; }

    /// <summary>
    /// The bytes an object's marks may take: <see cref="MarkedVectorBytes"/>, or a part of the
    /// object's own (<see cref="MarkedVectorShare"/>), whichever is more.
    /// </summary>
    internal long MarkedVectorBytesOf(long objectBytes) =>
        Math.Max(MarkedVectorBytes, objectBytes / Math.Max(MarkedVectorShare, 1));

    /// <summary>
    /// The most bytes a vector takes inside its entry; past them a commit writes it once into its own
    /// commit object, as an index fragment is, and the entry names it by reference: 256 by default. In
    /// the entry it rides in every page that holds the entry; out of line it costs a read beside the
    /// object's open, once per handle. A run a delete on the key takes is a few bytes; rows scattered
    /// over an object make vectors past this.
    /// </summary>
    internal int InlineVectorBytes { get; init; } = 256;

    /// <summary>
    /// The chunking seed every header carries; 0, the default, for one drawn at creation (<see cref="SeedOf"/>), so that
    /// options built alike are equal.
    /// </summary>
    internal ulong Seed { get; init; }

    /// <summary>The seed a creation under <paramref name="options"/> writes: theirs, or one drawn now, never 0.</summary>
    internal static ulong SeedOf(DatasetOptions options)
    {
        ulong seed = options.Seed;
        while (seed == 0)
        {
            seed = (ulong)Random.Shared.NextInt64();
        }

        return seed;
    }

    /// <summary>
    /// The compaction settings every header carries: the level cap, level 1's object size and the
    /// fan-out. <see langword="default"/> states none, and a planner uses its own options.
    /// </summary>
    internal CompactionSettings Compaction { get; init; }

    /// <summary>
    /// The boundary rule, or null for the prolly rule at the header's chunker settings, which is how
    /// every writer of a dataset agrees on where pages end. A rule given here overrides them, and it
    /// is then the caller's business to give the same rule to every writer: two writers under two
    /// rules still build a correct tree, but not the same pages for the same keys.
    /// </summary>
    internal IBoundaryRule? Rule { get; init; }

    /// <summary>
    /// Whether a creation accepts a key of one column that may hold a null, as the library did before
    /// it refused one. Only the tests of the datasets created then set it: such a dataset still
    /// opens, reads and writes, and its nulls are read after every other key.
    /// </summary>
    internal bool AcceptsNullableKey { get; init; }

    /// <summary>The periods a header records, or null for a dataset compacted by levels.</summary>
    /// <remarks>The slice target is the handle's own, from <paramref name="asked"/>, as no header records it.</remarks>
    private static PeriodOptions? PeriodsOf(CommitHeader header, PeriodOptions? asked) =>
        header.Lateness is { } lateness
            ? new PeriodOptions
            {
                Lateness = lateness,
                Length = header.PeriodLength ?? throw new InvalidOperationException("A header of a dataset of periods names no period length."),
                SliceTargetRows = asked?.SliceTargetRows ?? TimeSlicer.DefaultTargetRows,
                RefuseLateRows = asked?.RefuseLateRows ?? false,
            }
            : null;

    /// <summary>What vacuum keeps, as a header records it.</summary>
    internal RetentionSettings Retention => new RetentionSettings(RetainedVersions, (long)RetentionWindow.TotalSeconds);

    /// <summary>The fan-out a dataset on a locked store merges its levels at, unless it states one: each merge's bytes are kept for the lock's term.</summary>
    internal const int LockedFanout = 100;

    /// <summary>
    /// These options as a handle on a dataset of <paramref name="header"/> applies them: on a locked
    /// store, every delete marks, whatever the object's size or the share of its rows, its vector up to
    /// the bound of a page.
    /// </summary>
    internal DatasetOptions For(CommitHeader header) => !header.LockedStore
        ? this with
        {
            Seed = header.Seed,
            LockedStore = false,
            ErasureDelay = header.ErasureDelay,
            Periods = PeriodsOf(header, Periods),
            Identifiers = header.Identifiers.Count > 0 ? header.Identifiers : null,
            ShreddedPaths = header.ShreddedPaths.Count > 0 ? header.ShreddedPaths : null,
        }
        : this with
        {
            Seed = header.Seed,
            LockedStore = true,
            ErasureDelay = null,
            Periods = PeriodsOf(header, Periods),
            Identifiers = header.Identifiers.Count > 0 ? header.Identifiers : null,
            ShreddedPaths = header.ShreddedPaths.Count > 0 ? header.ShreddedPaths : null,
            MarkedObjectBytes = 0,
            MarkedShare = 1,
            MarkedVectorBytes = header.Chunker.MaxBytes > 0 ? header.Chunker.MaxBytes : ProllyBoundaryRule.DefaultMaxBytes,
            MarkedVectorShare = int.MaxValue,
        };
}
