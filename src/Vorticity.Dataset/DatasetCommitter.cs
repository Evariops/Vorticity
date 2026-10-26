using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>What a commit needs to know beyond its operations.</summary>
internal sealed record CommitOptions
{
    /// <summary>The dataset's chunking seed, fixed at creation and carried by every header.</summary>
    public required ulong Seed { get; init; }

    /// <summary>The header a first commit starts from: schema, clustering key, settings. Ignored
    /// once the dataset exists, the winner's header carrying on instead.</summary>
    public CommitHeader? Template { get; init; }

    /// <summary>How many times to rebase before giving up.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>
    /// Whether the commit may only create the dataset's first version, as a creation does: one that
    /// finds a version there throws rather than rebasing onto it, since a creation that took up another
    /// writer's header would hand its caller a schema and settings it did not ask for.
    /// </summary>
    public bool OnlyFirst { get; init; }

    /// <summary>What the commit is for, which its telemetry names.</summary>
    public CommitKind Kind { get; init; } = CommitKind.Replace;

    /// <summary>Where the dataset lives, as the caller named it, which tags the commit's activity; null when it was given a store.</summary>
    public string? Location { get; init; }

    /// <summary>
    /// A version the writer has seen, from which the latest is found by asking for the ones after
    /// it while <see cref="KnownAt"/> is recent enough; 0 lists the commits instead.
    /// </summary>
    public ulong Known { get; init; }

    /// <summary>When the writer last knew <see cref="Known"/> to be the latest version, by <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset KnownAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>
    /// The commit object of <see cref="Known"/> as the writer holds it, or null. While
    /// <see cref="KnownAt"/> is within <see cref="TrustSpan"/>, the first attempt builds on it without
    /// asking the store anything: the conditional creation says whether another writer went first.
    /// </summary>
    public CommitObject? Held { get; init; }

    /// <summary>
    /// How long after <see cref="KnownAt"/> every version after <see cref="Known"/> is surely still in
    /// the store: half the retention window, since vacuum takes a commit only a window after the next
    /// one superseded it. Past it the commits are listed. Zero never trusts.
    /// </summary>
    public TimeSpan TrustSpan { get; init; }

    /// <summary>The boundary rule, or null for the prolly rule at this dataset's seed.</summary>
    public IBoundaryRule? Rule { get; init; }

    /// <summary>What a node's summary is, or null for the one the object entries carry.</summary>
    public ISummaryFold? Fold { get; init; }

    /// <summary>The clock a header's creation time is read from.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The pages the writer's handle keeps across versions, which its commits read through; null for none.</summary>
    public PageCache? PageCache { get; init; }

    /// <summary>
    /// The most bytes a deletion vector takes inside its entry; a longer one is written into the
    /// commit object and named by reference. 0 keeps every vector in its entry.
    /// </summary>
    public int InlineVectorBytes { get; init; }

    /// <summary>
    /// The source ranges the commit's rows take into account, judged at every attempt against the
    /// offsets of the attempt's parent; null for a commit that declares none.
    /// </summary>
    public IReadOnlyList<SourceRange>? Consumed { get; init; }

    /// <summary>
    /// The sources whose offsets the commit forgets, every other offset kept; null for none. A
    /// writer that took one of them again would count its entries anew.
    /// </summary>
    public IReadOnlyCollection<string>? Forgotten { get; init; }

    /// <summary>A rule in its starting state.</summary>
    public IBoundaryRule NewRule() => Rule?.Fresh() ?? new ProllyBoundaryRule(Seed);

    /// <summary>The fold this commit folds its pages' summaries with.</summary>
    public ISummaryFold NewFold() => Fold ?? ObjectSummaryFold.Instance;
}

/// <summary>What a commit did.</summary>
/// <param name="Version">The version created, or the latest one when no operation applied and nothing was created.</param>
/// <param name="Key">Its commit object's key.</param>
/// <param name="Levels">The trees that version names, one per level.</param>
/// <param name="Outcomes">What each operation decided, in the caller's order.</param>
/// <param name="Attempts">How many times the writer had to rebase, 1 when it won first time.</param>
/// <param name="Pages">A source that can read the new version's pages, new and old.</param>
/// <param name="Commit">
/// The commit object of <paramref name="Version"/> as the writer holds it: the one it placed, or the
/// one it opened when nothing applied.
/// </param>
/// <param name="KnownAt">When the writer knew <paramref name="Version"/> to be the latest, by its clock.</param>
internal sealed record CommitResult(
    ulong Version,
    string Key,
    DatasetLevels Levels,
    IReadOnlyList<OperationOutcome> Outcomes,
    int Attempts,
    IPageSource Pages,
    CommitObject Commit,
    DateTimeOffset KnownAt)
{
    /// <summary>Level 0's tree, where appends land.</summary>
    public DatasetTree Tree => Levels[0];

    /// <summary>Whether the commit created <see cref="Version"/>, rather than finding nothing to apply on it.</summary>
    public bool Created { get; init; }

    /// <summary>Whether the commit's source ranges were counted already, by <see cref="Version"/>, so that it added nothing.</summary>
    public bool Counted { get; init; }
}

/// <summary>
/// Creates versions of a dataset, one conditional creation at a time: put-if-absent linearises
/// commits by itself, so there is no lease, no lock and no external service. A writer that loses
/// re-reads the winner and re-applies its operations rather than merging two trees.
/// </summary>
internal static class DatasetCommitter
{
    /// <summary>Applies the operations to the dataset's latest version, rebasing until it wins.</summary>
    /// <exception cref="ObjectStoreException">
    /// The writer lost <see cref="CommitOptions.MaxAttempts"/> times, or a commit that may only create
    /// the first version (<see cref="CommitOptions.OnlyFirst"/>) found one there.
    /// </exception>
    public static async ValueTask<CommitResult> CommitAsync(
        IObjectStore store,
        IReadOnlyList<DatasetOperation> operations,
        CommitOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxAttempts);

        // One activity over every attempt, and a commit counted however it ends: one left uncounted
        // when it lost or failed would make the share of rebases wrong when contention runs high,
        // which is when it is wanted.
        Activity? activity = DatasetTelemetry.StartCommit(options.Kind, options.Location);
        CommitResult? result;
        try
        {
            result = await AttemptAsync(store, operations, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            DatasetTelemetry.CommitEnded(
                activity, options.Kind, failure is OperationCanceledException ? CommitEnd.Cancelled : CommitEnd.Failed, 0, 0);
            throw;
        }

        if (result is null)
        {
            DatasetTelemetry.CommitEnded(activity, options.Kind, CommitEnd.Lost, options.MaxAttempts, 0);
            throw new ObjectStoreException(
                $"The commit lost {options.MaxAttempts} times; a coordinator that batches commits is the " +
                "answer to contention, not more attempts.");
        }

        DatasetTelemetry.CommitEnded(
            activity, options.Kind, result.Created ? CommitEnd.Applied : CommitEnd.NothingToDo, result.Attempts, result.Version);
        return result;
    }

    /// <summary>
    /// The commit's attempts, rebasing until one creates its version or the batch has nothing to
    /// apply; null when every attempt lost.
    /// </summary>
    private static async ValueTask<CommitResult?> AttemptAsync(
        IObjectStore store,
        IReadOnlyList<DatasetOperation> operations,
        CommitOptions options,
        CancellationToken cancellationToken)
    {
        ulong known = options.Known;
        DateTimeOffset knownAt = options.KnownAt;
        CommitObject? held = options.Held is { } given && given.Header.Version == known ? given : null;
        bool askFirst = false;
        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A creation that lost its put to another one knows a first version is there, without
            // asking: it is told so rather than handed the other's dataset.
            if (options.OnlyFirst && known > 0)
            {
                throw AlreadyThere(known);
            }

            // 1. The latest version: the one the writer holds, taken as it is while every version
            // after it is surely in the store, since the creation tells a writer that another went
            // first; otherwise asked for from the one the writer last saw, which a lost attempt moves
            // past the one it built on, and which was the latest when it was asked. Held, its commit
            // object is not read again when it turns out to be the latest still.
            DateTimeOffset asked = options.TimeProvider.GetUtcNow();
            bool trusted = known > 0 && asked - knownAt < options.TrustSpan;
            bool outright = trusted && held is not null && !askFirst;
            (ulong parent, CommitObject? commit) = outright
                ? (known, held)
                : await LatestAsync(store, known, trusted, held, cancellationToken).ConfigureAwait(false);
            if (!outright)
            {
                knownAt = asked;
            }

            if (options.OnlyFirst && parent != 0)
            {
                throw AlreadyThere(parent);
            }

            known = parent;
            CommitPageSource pages = new CommitPageSource(store, options.PageCache) { KeepsReads = true };
            DatasetLevels levels = DatasetLevels.Empty;
            CommitHeader template = options.Template ?? new CommitHeader { Version = 1 };
            if (commit is not null)
            {
                template = commit.Header;
                pages.Open(parent, commit);
                levels = DatasetLevels.Of(commit.Header);
            }

            // The source ranges, judged against the offsets this attempt builds on: the commit applies
            // only where each range starts at its source's offset. A verdict that adds nothing is final
            // only on a version the store said was the latest, since one taken as held may have been
            // passed: a gap there may be filled since, and a range counted there is counted since too.
            IReadOnlyList<SourceOffset> sources = options.Forgotten is { Count: > 0 } forgotten
                ? SourceTable.Without(template.Sources, forgotten)
                : template.Sources;
            if (options.Consumed is { Count: > 0 } consumed)
            {
                SourceVerdict verdict = SourceTable.Judge(template.Sources, consumed);
                if (verdict != SourceVerdict.Applied && outright)
                {
                    askFirst = true;
                    attempt--;
                    continue;
                }

                if (verdict == SourceVerdict.Conflict)
                {
                    throw SourceTable.Conflict(template.Sources, consumed, parent, commit, knownAt);
                }

                if (verdict == SourceVerdict.AlreadyThere)
                {
                    OperationOutcome[] counted = new OperationOutcome[operations.Count];
                    Array.Fill(counted, OperationOutcome.AlreadyThere);
                    return new CommitResult(
                        parent, CommitKey.For(parent), levels, counted, attempt, pages,
                        commit ?? throw new InvalidOperationException("Ranges counted on a dataset with no version."), knownAt)
                    {
                        Counted = true,
                    };
                }

                sources = SourceTable.Advanced(sources, consumed, options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            }

            // 2. The operations, re-applied to whatever is there now, a batch per level. The commit
            // object exists before they are applied, because an indexer's fragment is written into
            // it and its entry names it there; a lost attempt throws both away.
            ulong version = parent + 1;
            CommitObjectBuilder builder = new CommitObjectBuilder(version);
            SchemaEdit schema = new SchemaEdit(template.Schema, template.Retired)
            {
                WritePolicy = template.WritePolicy,
                Identifiers = template.Identifiers,
                ShreddedPaths = template.ShreddedPaths,
            };
            (Dictionary<int, List<TreeChange>> changes, List<OperationOutcome> outcomes, Relocation repack) =
                await ApplyAsync(levels, operations, pages, builder, schema, options.InlineVectorBytes, cancellationToken).ConfigureAwait(false);

            pages.Writing(builder, version);
            DatasetLevels next = levels;
            foreach ((int level, List<TreeChange> batch) in changes)
            {
                if (batch.Count == 0)
                {
                    continue;
                }

                // The tree of rollups, whose entries carry no bounds a fold reads.
                next = level == RollupTree
                    ? next.WithRollups(await next.Rollups
                        .CommitAsync(batch, options.NewRule(), NoSummary.Instance, pages, builder, cancellationToken)
                        .ConfigureAwait(false))
                    : next.With(
                        level,
                        await next[level]
                            .CommitAsync(batch, options.NewRule(), options.NewFold(), pages, builder, cancellationToken)
                            .ConfigureAwait(false));
            }

            // Each level a replacement took its sources from records where it stopped, once the
            // replacement applied; the latest in the batch wins, as it would in two commits.
            for (int i = 0; i < operations.Count; i++)
            {
                if (operations[i] is DatasetOperation.ReplaceObjects { Pointer: { } pointer } && outcomes[i] == OperationOutcome.Applied)
                {
                    next = next.WithPointer(pointer.Level, pointer.Key);
                }
            }

            // A repack moves the pages last, over the trees the changes produced: a leaf the batch
            // rewrote is already in this commit, and what is left in the named objects moves now.
            if (repack.Versions.Count > 0)
            {
                foreach ((int level, DatasetTree tree) in next.Occupied())
                {
                    (DatasetTree relocated, int moved) = await tree
                        .RelocateAsync(reference => repack.Versions.Contains(reference.Version), pages, builder, cancellationToken)
                        .ConfigureAwait(false);
                    next = next.With(level, relocated);
                    repack.Moved += moved;
                }

                if (!next.Rollups.IsEmpty)
                {
                    (DatasetTree relocated, int moved) = await next.Rollups
                        .RelocateAsync(reference => repack.Versions.Contains(reference.Version), pages, builder, cancellationToken)
                        .ConfigureAwait(false);
                    next = next.WithRollups(relocated);
                    repack.Moved += moved;
                }

                foreach (int at in repack.At)
                {
                    outcomes[at] = repack.Moved > 0 ? OperationOutcome.Applied : OperationOutcome.AlreadyThere;
                }
            }

            // A batch in which nothing applied would publish a copy of its parent under a new
            // number; it publishes nothing, and names the version it was decided against, which has
            // to be the latest: one taken as held is asked for first, and the batch decided again. A
            // batch that consumes source ranges publishes even with no operation, since its offsets
            // move, and nothing when one of its operations did not apply, since they would move
            // without its rows.
            bool consumes = options.Consumed is { Count: > 0 };
            bool idle = !consumes && operations.Count > 0 && !outcomes.Contains(OperationOutcome.Applied);
            bool partial = consumes && outcomes.Exists(outcome => outcome is not (OperationOutcome.Applied or OperationOutcome.AlreadyThere));
            if ((idle || partial) && commit is not null)
            {
                if (outright)
                {
                    // The same attempt again, on the version the store says is the latest.
                    askFirst = true;
                    attempt--;
                    continue;
                }

                return new CommitResult(parent, CommitKey.For(parent), levels, outcomes, attempt, pages, commit, knownAt);
            }

            (IReadOnlyList<CommitLevel> recorded, CommitLevel? rollups, IReadOnlyList<PagesStart> starts) = LevelsOf(next, builder, pages);
            CommitHeader header = template with
            {
                Version = version,
                Parent = parent,
                Seed = options.Seed,
                CreatedAtUnixMilliseconds = options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Levels = recorded,
                Rollups = rollups,
                Schema = schema.Schema,
                Retired = schema.Retired,
                WritePolicy = schema.WritePolicy,
                Identifiers = schema.Identifiers,
                ShreddedPaths = schema.ShreddedPaths,
                LastDataChange = ChangesRows(options.Kind, outcomes) ? version : LastDataChangeOf(template, parent),
                Sources = sources,
                Starts = starts,
                Features = template.Features | FeaturesOf(operations) | schema.Declared,
            };
            header = Fitted(WithoutHeld(header));
            // 3. One conditional creation. Once it is in, nothing after it can exist yet.
            string key = CommitKey.For(version);
            byte[] bytes = builder.Build(header);
            DateTimeOffset put = options.TimeProvider.GetUtcNow();
            DatasetTelemetry.Attempted(options.Kind);
            if (await store.PutIfAbsentAsync(key, bytes, cancellationToken).ConfigureAwait(false)
                == PutOutcome.Created)
            {
                pages.Placed(bytes);
                return new CommitResult(version, key, next, outcomes, attempt, pages, CommitObject.Placed(header, bytes), put)
                {
                    Created = true,
                };
            }

            // Another writer created this number: the search for the latest starts past it.
            known = version;
            held = null;
        }

        return null;
    }

    /// <summary>What a creation is told when the store holds a dataset already.</summary>
    private static ObjectStoreException AlreadyThere(ulong version) =>
        new ObjectStoreException($"This store already holds a dataset at version {version}; open it rather than creating it.");

    /// <summary>
    /// The latest version and its commit object; (0, null) when the dataset has none. From
    /// <paramref name="known"/>, a version the caller knew to be the latest recently enough that
    /// every version after it is still in the store (<paramref name="trusted"/>), those versions are
    /// asked for. Otherwise, or when the answer is gone by the time it is read, the commits are listed:
    /// vacuum keeps an old commit object whose pages a retained version still names and takes the
    /// ones after it, so above a version learned too long ago a missing one does not end the run.
    /// <paramref name="held"/>, the caller's commit object of <paramref name="known"/> or null, is
    /// handed back when nothing follows it rather than read again.
    /// </summary>
    public static async ValueTask<(ulong Version, CommitObject? Commit)> LatestAsync(
        IObjectStore store, ulong known, bool trusted, CommitObject? held, CancellationToken cancellationToken)
    {
        held = held is not null && held.Header.Version == known ? held : null;
        if (known > 0 && trusted)
        {
            try
            {
                return await AfterAsync(store, known, held, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectNotFoundException)
            {
                // Vacuumed since it was seen: the listing finds what is there now.
            }
        }

        ulong listed = await NewestVersionAsync(store, cancellationToken).ConfigureAwait(false);
        if (listed == known && held is not null)
        {
            return (known, held);
        }

        return listed == 0 ? (0, null) : (listed, await OpenAsync(store, listed, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The latest version from <paramref name="known"/>, a version every one after which is still in
    /// the store, and its commit object. The first version not in hand is read while a head asks for the
    /// one after it, both in one round trip: the version after <paramref name="known"/> when
    /// <paramref name="held"/> is its commit object, <paramref name="known"/> itself otherwise. Nothing
    /// after the one read, and it is the latest, read already, or <paramref name="known"/> when it is
    /// absent. Something after it, and versions came in whatever the read found, since the two answers
    /// may come back in either order: the search goes on from there.
    /// </summary>
    /// <remarks>
    /// A refresh then sees no commit or one in a round trip, where heads alone took one and four, and
    /// a rebase onto the version it lost to reads that version in the same round trip. The price is a
    /// request more when nothing is new, a read of an absent key, which stores bill as a head.
    /// </remarks>
    /// <exception cref="ObjectNotFoundException">The version read is gone, which vacuum may do to one it superseded long ago.</exception>
    private static async ValueTask<(ulong Version, CommitObject? Commit)> AfterAsync(
        IObjectStore store, ulong known, CommitObject? held, CancellationToken cancellationToken)
    {
        ulong first = held is null ? known : known + 1;
        Task<CommitObject?> read = ReadIfThereAsync(store, first, cancellationToken);
        Task<ObjectHead?> next = store.HeadAsync(CommitKey.For(first + 1), cancellationToken).AsTask();
        await Task.WhenAll(read, next).ConfigureAwait(false);
        if (await next.ConfigureAwait(false) is not null)
        {
            ulong newest = await NewestFromAsync(store, first + 1, cancellationToken).ConfigureAwait(false);
            return (newest, await OpenAsync(store, newest, cancellationToken).ConfigureAwait(false));
        }

        if (await read.ConfigureAwait(false) is { } commit)
        {
            return (first, commit);
        }

        return held is not null ? (known, held) : throw ObjectNotFoundException.For(CommitKey.For(known));
    }

    /// <summary>The commit object of <paramref name="version"/>, or null when the store holds none; a torn one reported against it.</summary>
    private static async Task<CommitObject?> ReadIfThereAsync(IObjectStore store, ulong version, CancellationToken cancellationToken)
    {
        try
        {
            return await OpenAsync(store, version, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest version from <paramref name="known"/> on: the versions after it asked for in
    /// doubling steps, then bisected. Every version after one the caller knew to be the latest less
    /// than half a retention window ago is still in the store, since each was created after that and
    /// vacuum takes a commit only a window after the next one superseded it: a version that is
    /// missing ends the run, and a single request answers when nothing is newer.
    /// </summary>
    private static async ValueTask<ulong> NewestFromAsync(IObjectStore store, ulong known, CancellationToken cancellationToken)
    {
        ulong present = known;
        ulong step = 1;
        ulong absent;
        while (true)
        {
            ulong probe = present + step;
            if (await store.HeadAsync(CommitKey.For(probe), cancellationToken).ConfigureAwait(false) is null)
            {
                absent = probe;
                break;
            }

            present = probe;
            step <<= 1;
        }

        while (absent - present > 1)
        {
            ulong middle = present + ((absent - present) >> 1);
            if (await store.HeadAsync(CommitKey.For(middle), cancellationToken).ConfigureAwait(false) is null)
            {
                absent = middle;
            }
            else
            {
                present = middle;
            }
        }

        return present;
    }

    /// <summary>The latest version and its commit object, in one listing and one read; (0, null)
    /// when the dataset has none.</summary>
    public static async ValueTask<(ulong Version, CommitObject? Commit)> LatestAsync(
        IObjectStore store, CancellationToken cancellationToken)
    {
        ulong version = await NewestVersionAsync(store, cancellationToken).ConfigureAwait(false);
        return version == 0 ? (0, null) : (version, await OpenAsync(store, version, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The commit object of <paramref name="version"/>, a torn one reported against it.</summary>
    /// <exception cref="ObjectNotFoundException">The version has no commit object.</exception>
    internal static async ValueTask<CommitObject> OpenAsync(IObjectStore store, ulong version, CancellationToken cancellationToken)
    {
        try
        {
            return await CommitObject.OpenAsync(store, CommitKey.For(version), cancellationToken).ConfigureAwait(false);
        }
        catch (CommitFormatException torn)
        {
            throw TornCommitException.Of(version, torn);
        }
    }

    /// <summary>The version of the newest commit object, without opening it; 0 when there is none.</summary>
    public static async ValueTask<ulong> NewestVersionAsync(IObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<string> newest = await store
            .ListAsync(CommitKey.Prefix, null, 1, cancellationToken).ConfigureAwait(false);
        return newest.Count == 1 && CommitKey.TryParse(newest[0], out ulong version) ? version : 0;
    }

    /// <summary>
    /// Whether the commit object of <paramref name="version"/> is whole: read in full and checked from
    /// its preamble to its trailer, which a writer that stopped early never wrote. A sealed one read
    /// without the dataset's keys is checked by its envelope's header and trailer instead.
    /// </summary>
    public static async ValueTask<bool> IsWholeAsync(IObjectStore store, ulong version, CancellationToken cancellationToken)
    {
        string key = CommitKey.For(version);
        long objectLength;
        if (store is SealedObjectStore { Seals: true } sealedStore)
        {
            // A sealed commit's plaintext length comes from its head; one torn short of its trailer has none.
            try
            {
                objectLength = await sealedStore.CommitLengthAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (CommitFormatException)
            {
                return false;
            }
        }
        else
        {
            ObjectHead head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw ObjectNotFoundException.For(key);
            objectLength = head.Length;
        }

        if (objectLength > int.MaxValue)
        {
            return false;
        }

        const int Chunk = 1 << 20;
        byte[] bytes = new byte[objectLength];
        for (int at = 0; at < bytes.Length; at += Chunk)
        {
            int length = Math.Min(Chunk, bytes.Length - at);
            using ObjectRange range = await store.GetRangeAsync(key, at, length, cancellationToken).ConfigureAwait(false);
            if (range.Length != length)
            {
                return false;
            }

            range.Bytes.CopyTo(bytes.AsSpan(at));
        }

        // A sealed commit read without the dataset's keys is judged by its envelope, which is whole
        // exactly when its writer finished it.
        if (bytes.AsSpan().StartsWith(Sealing.SealedFormat.HeaderMagic))
        {
            Vorticity.IO.MemorySegmentSource envelope = new Vorticity.IO.MemorySegmentSource(bytes);
            await using (envelope.ConfigureAwait(false))
            {
                return await Sealing.SealedFiles.IsWholeAsync(envelope, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            CommitObject.Open(bytes, bytes.Length);
            return true;
        }
        catch (CommitFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The pages a header carries, top first, spending what is left of its inline room, and into
    /// <paramref name="named"/> those it names without carrying them: its top, or a child of a page
    /// it carries. Only pages already in hand are inlined: reading one just to inline it would spend
    /// a request to save bytes, and the rewrite already holds the levels above the leaves.
    /// </summary>
    /// <remarks>
    /// Nor is a leaf that holds an object with rows marked in it, unless this commit wrote it. Its
    /// bytes grow with every mark, and carried in every header they would cost each commit that does
    /// not touch its level what only the commits that mark or purge an object of it have to pay; a
    /// reader reads it once, where the header says its version's pages start, and a handle keeps it.
    /// </remarks>
    private static IReadOnlyList<InlinedPage> Inline(
        CommitObjectBuilder builder, CommitPageSource pages, DatasetTree tree, ref long budget, List<PageReference> named)
    {
        List<InlinedPage> inlined = [];
        Queue<(PageReference Reference, int Level)> queue = new Queue<(PageReference, int)>();
        queue.Enqueue((tree.Root, tree.Depth));
        while (queue.Count > 0)
        {
            (PageReference reference, int level) = queue.Dequeue();
            if ((!builder.TryGetPage(reference, out ReadOnlyMemory<byte> page) && !pages.TryGetKnown(reference, out page))
                || (level == 1 && reference.Version != builder.Version && Marks(page)))
            {
                named.Add(reference);
                continue;
            }

            if (page.Length > budget)
            {
                named.Add(reference);
                foreach ((PageReference left, int _) in queue)
                {
                    named.Add(left);
                }

                break;
            }

            inlined.Add(new InlinedPage(reference, page));
            budget -= page.Length;
            if (level > 1)
            {
                foreach (InternalEntry entry in TreePage.ReadInternal(page))
                {
                    queue.Enqueue((entry.Child, level - 1));
                }
            }
        }

        return inlined;
    }

    /// <summary>Whether a leaf holds an object with rows marked in it, read off its entries in place.</summary>
    private static bool Marks(ReadOnlyMemory<byte> leaf)
    {
        foreach (TreeEntry entry in TreePage.ReadLeaf(leaf))
        {
            if (ObjectEntry.TallyOf(entry.Value.Span).DeletedRows > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where the pages of the earlier versions a header names without carrying start, as far as this
    /// commit learned it: from its parent's header, or by reading one of their pages.
    /// </summary>
    private static IReadOnlyList<PagesStart> StartsOf(List<PageReference> named, ulong version, CommitPageSource pages)
    {
        SortedDictionary<ulong, long> starts = [];
        foreach (PageReference reference in named)
        {
            if (reference.Version != version && pages.TryGetStart(reference.Version, out long start))
            {
                starts[reference.Version] = start;
            }
        }

        List<PagesStart> ordered = new List<PagesStart>(starts.Count);
        foreach ((ulong earlier, long start) in starts)
        {
            ordered.Add(new PagesStart(earlier, start));
        }

        return ordered;
    }

    /// <summary>What the inlined pages may take, leaving room under the header's size cap for
    /// everything else it carries.</summary>
    private const int InlineBudget = 192 << 10;

    /// <summary>
    /// The header without the pages this commit wrote that the object's open read brings back
    /// anyway: its pages region follows the header, and a page of it that ends within the first
    /// <see cref="CommitFormat.OpenBytes"/> costs a reader nothing more, where inlined as well it would
    /// be written twice. Every such page is left out, then those the header left over would push past
    /// the read are taken back, the last written first, until the header that holds the rest leaves
    /// every page it omits inside the read: a header only grows as pages go back into it, so the pages
    /// it omits only get fewer, and the last header tried is the one written.
    /// </summary>
    private static CommitHeader WithoutHeld(CommitHeader header)
    {
        HashSet<PageReference> omitted = [];
        foreach (CommitLevel level in header.Levels)
        {
            foreach (InlinedPage page in level.Inlined)
            {
                if (page.Reference.Version == header.Version)
                {
                    omitted.Add(page.Reference);
                }
            }
        }

        while (omitted.Count > 0)
        {
            CommitHeader trial = Without(header, omitted);
            long pagesStart = CommitFormat.PreambleBytes + CommitObjectBuilder.HeaderLength(trial);
            if (omitted.RemoveWhere(reference => pagesStart + reference.Offset + reference.Length > CommitFormat.OpenBytes) == 0)
            {
                return trial;
            }
        }

        return header;
    }

    /// <summary>
    /// The features of format 4 the operations bring into the trees: an entry they add with fields of
    /// format 4. An entry rebuilt from one in the trees keeps its fields, and its feature is named already.
    /// </summary>
    private static DatasetFeatures FeaturesOf(IReadOnlyList<DatasetOperation> operations)
    {
        DatasetFeatures features = DatasetFeatures.None;
        foreach (DatasetOperation operation in operations)
        {
            switch (operation)
            {
                case DatasetOperation.AddObject { Entry.HasFields: true }:
                    features |= DatasetFeatures.EntryFields;
                    break;
                case DatasetOperation.PutRollup:
                    features |= DatasetFeatures.Rollups;
                    break;
                case DatasetOperation.ReplaceObjects replace:
                    foreach ((int _, ReadOnlyMemory<byte> _, ObjectEntry entry) in replace.Outputs)
                    {
                        if (entry.HasFields || entry.HasNegatives)
                        {
                            features |= DatasetFeatures.EntryFields;
                            break;
                        }
                    }

                    break;
            }
        }

        return features;
    }

    /// <summary>The header with the inlined pages of <paramref name="omitted"/> left out.</summary>
    private static CommitHeader Without(CommitHeader header, HashSet<PageReference> omitted)
    {
        List<CommitLevel> levels = new List<CommitLevel>(header.Levels.Count);
        foreach (CommitLevel level in header.Levels)
        {
            List<InlinedPage> kept = new List<InlinedPage>(level.Inlined.Count);
            foreach (InlinedPage page in level.Inlined)
            {
                if (!omitted.Contains(page.Reference))
                {
                    kept.Add(page);
                }
            }

            levels.Add(level with { Inlined = kept });
        }

        return header with { Levels = levels };
    }

    /// <summary>
    /// The header with as many of its inlined pages as leave it inside the read that opens its
    /// object, where a reader must find it whole: past it, every open of the version would fail and
    /// the object would read as torn, though whole. The pages go from the last inlined, the deepest
    /// of the highest level, whose readers read them where they lie instead; a header too large
    /// with none of them is refused before anything is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The header does not fit the open read even with no page inlined.</exception>
    private static CommitHeader Fitted(CommitHeader header)
    {
        const int Room = CommitFormat.OpenBytes - CommitFormat.PreambleBytes;
        int length = CommitObjectBuilder.HeaderLength(header);
        while (length > Room)
        {
            // Pages go until their bytes make up what is over; their framing makes up the rest, and
            // the header is measured again in case it did not.
            long over = length - Room;
            List<CommitLevel> levels = [.. header.Levels];
            bool dropped = false;
            for (int i = levels.Count - 1; i >= 0 && over > 0; i--)
            {
                List<InlinedPage> kept = [.. levels[i].Inlined];
                while (kept.Count > 0 && over > 0)
                {
                    over -= kept[^1].Bytes.Length;
                    kept.RemoveAt(kept.Count - 1);
                    dropped = true;
                }

                levels[i] = levels[i] with { Inlined = kept };
            }

            if (!dropped)
            {
                throw new InvalidOperationException(
                    $"The commit's header would take {length} bytes, and a reader opens a commit object by reading its first " +
                    $"{CommitFormat.OpenBytes}: the version would never open. What fills it is the schema, the names retired " +
                    "from it and the levels' records, none of which a commit can leave out.");
            }

            header = header with { Levels = levels };
            length = CommitObjectBuilder.HeaderLength(header);
        }

        return header;
    }

    /// <summary>The tree a header names at level 0, where appends land; empty when it names
    /// none.</summary>
    public static DatasetTree TreeOf(CommitHeader header) => DatasetLevels.Of(header)[0];

    /// <summary>
    /// What a header records for each occupied level, with the pages it can inline, and where the
    /// pages it names without carrying start. The levels share one inline budget and spend it from
    /// level 0 up, since every lookup descends level 0; a level whose top does not fit costs one
    /// request to read instead.
    /// </summary>
    private static (IReadOnlyList<CommitLevel> Levels, CommitLevel? Rollups, IReadOnlyList<PagesStart> Starts) LevelsOf(
        DatasetLevels levels, CommitObjectBuilder builder, CommitPageSource pages)
    {
        List<CommitLevel> recorded = [];
        List<PageReference> named = [];
        long budget = InlineBudget;
        foreach ((int level, DatasetTree tree) in levels.Occupied())
        {
            IReadOnlyList<InlinedPage> inlined = Inline(builder, pages, tree, ref budget, named);
            recorded.Add(new CommitLevel(level, tree.Entries, tree.Root, inlined)
            {
                Depth = tree.Depth,
                Rows = tree.Rows,
                Pointer = levels.PointerOf(level),
            });
        }

        // The tree of rollups is named, never inlined: its leaves are no objects' entries, which an inlining reads.
        CommitLevel? rollups = null;
        if (levels.Rollups is { IsEmpty: false } held)
        {
            named.Add(held.Root);
            rollups = new CommitLevel(0, held.Entries, held.Root) { Depth = held.Depth, Rows = held.Rows };
        }

        return (recorded, rollups, StartsOf(named, builder.Version, pages));
    }

    /// <summary>Re-applies the operations to the levels as they are now.</summary>
    private static async ValueTask<(Dictionary<int, List<TreeChange>> Changes, List<OperationOutcome> Outcomes, Relocation Repack)>
        ApplyAsync(
            DatasetLevels levels,
            IReadOnlyList<DatasetOperation> operations,
            CommitPageSource pages,
            CommitObjectBuilder builder,
            SchemaEdit schema,
            int inlineVectorBytes,
            CancellationToken cancellationToken)
    {
        Relocation repack = new Relocation();
        // Sorted and unique by key within a level, which is what a batch has to be; an operation
        // that touches a key another already touched sees the pending value, so two fragments on
        // one object both land. One batch per level, because one tree per level.
        Dictionary<int, SortedDictionary<byte[], TreeChange>> changes = [];
        // A key is unique inside a level and a compaction moves one object from one level to
        // another, so the entries pending are kept per level: the same key at two levels is two
        // objects. Looked up by the key's bytes, which needs no copy of them.
        Dictionary<int, Dictionary<byte[], ObjectEntry?>> pending = [];

        // The rollups the batch puts, by their tree keys; and whether it changes the schema, which takes every rollup away.
        Dictionary<byte[], RollupEntry> rollups = new Dictionary<byte[], RollupEntry>(Buffers.ByteKeys.Instance);
        bool reschemed = false;
        List<OperationOutcome> outcomes = new List<OperationOutcome>(operations.Count);

        foreach (DatasetOperation operation in operations)
        {
            switch (operation)
            {
                case DatasetOperation.AddObject add:
                {
                    ObjectEntry? current = await CurrentAsync(add.Level, add.Key).ConfigureAwait(false);
                    if (current is { } held && held.Uid == add.Entry.Uid)
                    {
                        // The same uid is the same bytes, so the winner already added this object.
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Put(add.Level, add.Key, add.Entry);
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.AddFragment fragment:
                {
                    ObjectEntry? current = await CurrentAsync(fragment.Level, fragment.Key).ConfigureAwait(false);
                    if (current is not { } entry || entry.Uid != fragment.Uid)
                    {
                        // The object is gone, or is not the one the fragment was built against.
                        outcomes.Add(OperationOutcome.Dropped);
                        break;
                    }

                    if (entry.Holds(fragment.Fragment.Span))
                    {
                        // Another indexer got there first with the same fragment; its bytes are not
                        // written a second time.
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    PageReference written = builder.AddFragment(fragment.Fragment.Span);
                    Put(fragment.Level, fragment.Key, entry.With(written));
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.DropFragment drop:
                {
                    ObjectEntry? current = await CurrentAsync(drop.Level, drop.Key).ConfigureAwait(false);
                    if (current is not { } entry)
                    {
                        outcomes.Add(OperationOutcome.Dropped);
                        break;
                    }

                    if (!entry.Holds(drop.Fragment))
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Put(drop.Level, drop.Key, entry.Without(drop.Fragment));
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.ReplaceObjects replace:
                {
                    ObjectEntry?[] currents = new ObjectEntry?[replace.Inputs.Count];
                    bool complete = true;
                    for (int i = 0; i < replace.Inputs.Count; i++)
                    {
                        (int level, ReadOnlyMemory<byte> input) = replace.Inputs[i];
                        ObjectEntry? current = await CurrentAsync(level, input).ConfigureAwait(false);
                        currents[i] = current;
                        complete &= current is not null
                            && (replace.Expected is not { } expected || SameRows(current, expected[i]));
                    }

                    // Another compaction took an input, so this one's outputs are garbage; or moved an
                    // object into the part of the level the outputs fill, which they would overlap.
                    if (!complete || (replace.Range is { } range && await ForeignAsync(replace, range).ConfigureAwait(false)))
                    {
                        outcomes.Add(OperationOutcome.Abandoned);
                        break;
                    }

                    foreach ((int level, ReadOnlyMemory<byte> input) in replace.Inputs)
                    {
                        Remove(level, input);
                    }

                    foreach ((int level, ReadOnlyMemory<byte> key, ObjectEntry entry) in replace.Outputs)
                    {
                        Put(level, key, WithCurrentFragments(entry, key, replace.Inputs, currents));
                    }

                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                case DatasetOperation.Repack move:
                {
                    // The fragments move here, as leaf changes: an entry names its fragments, so a
                    // moved fragment is a changed entry. The pages move after the batches.
                    repack.Versions.UnionWith(move.Versions);
                    repack.At.Add(outcomes.Count);
                    outcomes.Add(OperationOutcome.AlreadyThere);
                    foreach ((int level, DatasetTree tree) in levels.Occupied())
                    {
                        await foreach (TreeEntry leaf in tree.EnumerateAsync(pages, cancellationToken).ConfigureAwait(false))
                        {
                            // The entry as this commit leaves it: its own change when an earlier
                            // operation made one, else the leaf the walk holds, which is the tree's
                            // and is read in place until it names a fragment to move.
                            ObjectEntry? held = pending.Count > 0 && TryPending(level, leaf.Key.Span, out ObjectEntry? change)
                                ? change
                                : null;
                            if (held is null ? !ObjectEntry.NamesAny(leaf.Value.Span, repack.Versions) : !held.NamesAny(repack.Versions))
                            {
                                continue;
                            }

                            ObjectEntry entry = held ?? ObjectEntry.FromBytes(leaf.Value);
                            PageReference[] fragments = [.. entry.Fragments];
                            for (int i = 0; i < fragments.Length; i++)
                            {
                                if (repack.Versions.Contains(fragments[i].Version))
                                {
                                    ReadOnlyMemory<byte> bytes = await pages
                                        .ReadFragmentAsync(fragments[i], cancellationToken).ConfigureAwait(false);
                                    fragments[i] = builder.AddFragment(bytes.Span);
                                    repack.Moved++;
                                }
                            }

                            // A vector out of line moves as a fragment does, its bytes unchanged.
                            entry = entry.WithFragments(fragments);
                            if (entry.VectorAt.Exists && repack.Versions.Contains(entry.VectorAt.Version))
                            {
                                await entry.ResolveAsync(pages, cancellationToken).ConfigureAwait(false);
                                entry = entry.WithVectorAt(builder.AddFragment(entry.EncodedDeletions.Span));
                                repack.Moved++;
                            }

                            // So do the counts of its deleted rows.
                            if (entry.NegativesAt.Exists && repack.Versions.Contains(entry.NegativesAt.Version))
                            {
                                await entry.ResolveNegativesAsync(pages, cancellationToken).ConfigureAwait(false);
                                entry = entry.WithNegativesAt(builder.AddFragment(entry.EncodedNegatives.Span));
                                repack.Moved++;
                            }

                            Put(level, leaf.Key, entry);
                        }
                    }

                    break;
                }

                case DatasetOperation.ChangeSchema change:
                {
                    if (Same(schema.Schema, schema.Retired, change.To, change.Retired))
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                    }
                    else if (change.From.Length > 0 && schema.Schema.Span.SequenceEqual(change.From.Span))
                    {
                        // The schema the change was checked against is still the dataset's: its
                        // retired names change only with it, so the bytes say it all.
                        schema.Schema = change.To;
                        schema.Retired = change.Retired;
                        reschemed = true;
                        outcomes.Add(OperationOutcome.Applied);
                    }
                    else
                    {
                        // Another writer changed the schema first: what this change checked of the
                        // columns was checked against a schema the dataset no longer has.
                        outcomes.Add(OperationOutcome.Dropped);
                    }

                    break;
                }

                case DatasetOperation.Rekey:
                    // The tree as it is, under the data key the store now seals with.
                    outcomes.Add(OperationOutcome.Applied);
                    break;

                case DatasetOperation.AddIndexPolicy added:
                {
                    // Joined to the policy of the version it lands on, never put in its place: two
                    // indexings of two columns committed at once both keep their column.
                    // A default that indexes, such as every column's automatic index, takes the place of none.
                    Indexes.WritePolicy current = Indexes.IndexDirectory.PolicyOf(schema.WritePolicy.Span);
                    Indexes.WritePolicy adding = Indexes.IndexDirectory.PolicyOf(added.Policy.Span);
                    Indexes.WritePolicy merged = current.With(adding);
                    merged = adding.Default.Kind != Indexes.IndexPolicyKind.None ? merged.WithDefault(adding.Default) : merged;
                    byte[] joined = Indexes.IndexDirectory.PolicyBytes(merged);
                    if (joined.AsSpan().SequenceEqual(Indexes.IndexDirectory.PolicyBytes(current)))
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                    }
                    else
                    {
                        schema.WritePolicy = joined;
                        outcomes.Add(OperationOutcome.Applied);
                    }

                    break;
                }

                case DatasetOperation.AddIdentifiers add:
                {
                    // Joined to those of the version it lands on, as two policies are, so that two writers adding one each
                    // keep both; dropped under another schema, where a column they name may be gone.
                    List<string>? joined = null;
                    foreach (string identifier in add.Identifiers)
                    {
                        if (IndexOf(joined ?? schema.Identifiers, identifier) < 0)
                        {
                            (joined ??= [.. schema.Identifiers]).Add(identifier);
                        }
                    }

                    if (joined is null)
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                    }
                    else if (!schema.Schema.Span.SequenceEqual(add.Schema.Span))
                    {
                        outcomes.Add(OperationOutcome.Dropped);
                    }
                    else
                    {
                        schema.Identifiers = joined;
                        outcomes.Add(OperationOutcome.Applied);
                    }

                    break;
                }

                case DatasetOperation.ChangeShreddedPaths change:
                {
                    // A list of paths takes the place of another, so a change worked out against paths another writer
                    // changed first would undo theirs: dropped, as under another schema.
                    if (SameNames(schema.ShreddedPaths, change.To))
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                    }
                    else if (!SameNames(schema.ShreddedPaths, change.From) || !schema.Schema.Span.SequenceEqual(change.Schema.Span))
                    {
                        outcomes.Add(OperationOutcome.Dropped);
                    }
                    else
                    {
                        schema.ShreddedPaths = change.To;
                        outcomes.Add(OperationOutcome.Applied);
                    }

                    break;
                }

                case DatasetOperation.PutRollup put:
                {
                    // A rollup is put over what the version holds as it summed it, or not at all: its level's entries over
                    // its range, or the rollups of the rank below, hashed as its builder hashed them.
                    (UInt128 fingerprint, int children) = await SummedAsync(levels, put.Entry, pages, cancellationToken).ConfigureAwait(false);
                    if (fingerprint != put.Entry.Fingerprint || children != put.Entry.Children)
                    {
                        outcomes.Add(OperationOutcome.Dropped);
                        break;
                    }

                    byte[] key = put.Entry.TreeKey;
                    if (await levels.Rollups.FindAsync(key, pages, cancellationToken).ConfigureAwait(false) is { } held
                        && RollupEntry.FromBytes(held.Key.Span, held.Value.Span).Fingerprint == put.Entry.Fingerprint)
                    {
                        outcomes.Add(OperationOutcome.AlreadyThere);
                        break;
                    }

                    Batch(RollupTree)[key] = TreeChange.Put(key, put.Entry.ToBytes(), put.Entry.Rows);
                    rollups[key] = put.Entry;
                    outcomes.Add(OperationOutcome.Applied);
                    break;
                }

                default:
                    throw new ArgumentException(
                        $"An operation of type {operation.GetType().Name} is not one a commit knows.", nameof(operations));
            }
        }

        // A rollup covers every object of its level between its first and its last tree keys: an entry the batch puts or
        // removes there takes it away, of every rank, with the rollups the batch put itself.
        await InvalidateAsync().ConfigureAwait(false);

        Dictionary<int, List<TreeChange>> batches = [];
        foreach ((int level, SortedDictionary<byte[], TreeChange> sorted) in changes)
        {
            batches[level] = [.. sorted.Values];
        }

        return (batches, outcomes, repack);

        async ValueTask InvalidateAsync()
        {
            // A rollup names its columns by their places in the schema it was summed under, as an object's counts do: a
            // schema changed takes them all away, and a later job sums them again under the new one.
            if (reschemed)
            {
                foreach (byte[] put in rollups.Keys)
                {
                    Batch(RollupTree).Remove(put);
                }

                if (!levels.Rollups.IsEmpty)
                {
                    await foreach (TreeEntry entry in levels.Rollups.EnumerateAsync(pages, cancellationToken).ConfigureAwait(false))
                    {
                        byte[] treeKey = entry.Key.ToArray();
                        Batch(RollupTree)[treeKey] = TreeChange.Remove(treeKey);
                    }
                }

                return;
            }

            // The levels' batches as they stand, since taking a rollup away adds the batch of the tree of rollups.
            List<(int Level, SortedDictionary<byte[], TreeChange> Batch)> levelBatches = [];
            foreach ((int level, SortedDictionary<byte[], TreeChange> batch) in changes)
            {
                if (level != RollupTree && batch.Count > 0)
                {
                    levelBatches.Add((level, batch));
                }
            }

            foreach ((int level, SortedDictionary<byte[], TreeChange> batch) in levelBatches)
            {

                // The level's rollups, every rank: those the tree holds, read only when it holds any, and the batch's.
                List<(RollupEntry Rollup, bool Held)> covering = [];
                byte[] low = RollupEntry.PrefixOf(level, 0);
                byte[] high = RollupEntry.PrefixOf(level + 1, 0);
                if (await levels.Rollups.NextAsync(low, wrap: false, pages, cancellationToken).ConfigureAwait(false) is { } first
                    && TreePage.Compare(first.Key.Span, high) < 0)
                {
                    await foreach (TreeEntry entry in RangeAsync(levels.Rollups, low, high, pages, cancellationToken).ConfigureAwait(false))
                    {
                        covering.Add((RollupEntry.FromBytes(entry.Key.Span, entry.Value.Span), true));
                    }
                }

                foreach (RollupEntry put in rollups.Values)
                {
                    if (put.Level == level)
                    {
                        covering.Add((put, false));
                    }
                }

                foreach ((RollupEntry rollup, bool held) in covering)
                {
                    foreach (byte[] key in batch.Keys)
                    {
                        if (rollup.Covers(key))
                        {
                            byte[] treeKey = rollup.TreeKey;
                            if (held)
                            {
                                Batch(RollupTree)[treeKey] = TreeChange.Remove(treeKey);
                            }
                            else
                            {
                                Batch(RollupTree).Remove(treeKey);
                            }

                            break;
                        }
                    }
                }
            }
        }

        async ValueTask<ObjectEntry?> CurrentAsync(int level, ReadOnlyMemory<byte> key)
        {
            if (TryPending(level, key.Span, out ObjectEntry? held))
            {
                return held;
            }

            TreeEntry? entry = await levels[level].FindAsync(key, pages, cancellationToken).ConfigureAwait(false);
            return entry is { } found ? ObjectEntry.FromBytes(found.Value) : null;
        }

        // Whether the part of the level a replacement's outputs fill holds an object that is none of
        // its inputs, as this commit leaves the level so far: one the walk of the version finds and
        // no earlier operation removed, or one an earlier operation put there. The walk enters only
        // the subtrees whose summaries may meet the range, the ones the plan walked.
        async ValueTask<bool> ForeignAsync(DatasetOperation.ReplaceObjects replace, LevelRange range)
        {
            await foreach (PositionedEntry held in levels[range.Level]
                .WalkAsync(pages, 0, long.MaxValue, range.MayOverlap, cancellationToken).ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> key = held.Entry.Key;
                if (TryPending(range.Level, key.Span, out ObjectEntry? changed) && changed is null)
                {
                    continue;
                }

                if (!Takes(replace, range.Level, key.Span) && range.Overlaps(key.Span, held.Entry.Value))
                {
                    return true;
                }
            }

            if (pending.TryGetValue(range.Level, out Dictionary<byte[], ObjectEntry?>? entries))
            {
                foreach ((byte[] key, ObjectEntry? entry) in entries)
                {
                    if (entry is not null && !Takes(replace, range.Level, key) && range.Overlaps(key, entry))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        void Put(int level, ReadOnlyMemory<byte> key, ObjectEntry entry)
        {
            // A vector too long for its entry goes into this commit object, once, and the entry, in
            // every page that will hold it, carries its reference instead.
            if (inlineVectorBytes > 0 && entry.InlineVectorBytes > inlineVectorBytes)
            {
                // Kept by the writer, whose next reads of the object then ask the store for nothing.
                ReadOnlyMemory<byte> vector = entry.EncodedDeletions;
                entry = entry.WithVectorAt(builder.AddFragment(vector.Span));
                pages.Keep(entry.VectorAt, vector);
            }

            // The counts of the rows a delete took go there always, as a fragment does, and are kept as the vector is.
            if (entry.NegativesPending)
            {
                ReadOnlyMemory<byte> negatives = entry.EncodedNegatives;
                entry = entry.WithNegativesAt(builder.AddFragment(negatives.Span));
                pages.Keep(entry.NegativesAt, negatives);
            }

            byte[] stored = Stored(level, key.Span);
            Batch(level)[stored] = TreeChange.Put(stored, entry.ToBytes(), entry.Rows);
            Pending(level)[stored] = entry;
        }

        void Remove(int level, ReadOnlyMemory<byte> key)
        {
            byte[] stored = Stored(level, key.Span);
            Batch(level)[stored] = TreeChange.Remove(stored);
            Pending(level)[stored] = null;
        }

        SortedDictionary<byte[], TreeChange> Batch(int level)
        {
            if (!changes.TryGetValue(level, out SortedDictionary<byte[], TreeChange>? batch))
            {
                batch = new SortedDictionary<byte[], TreeChange>(KeyOrder.Instance);
                changes[level] = batch;
            }

            return batch;
        }

        Dictionary<byte[], ObjectEntry?> Pending(int level)
        {
            if (!pending.TryGetValue(level, out Dictionary<byte[], ObjectEntry?>? entries))
            {
                entries = new Dictionary<byte[], ObjectEntry?>(Buffers.ByteKeys.Instance);
                pending[level] = entries;
            }

            return entries;
        }

        bool TryPending(int level, ReadOnlySpan<byte> key, out ObjectEntry? entry)
        {
            entry = null;
            return pending.TryGetValue(level, out Dictionary<byte[], ObjectEntry?>? entries)
                && entries.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(key, out entry);
        }

        // The array a key already changed in this commit is held under, or a copy of it: one per key
        // whatever the operations that touch it.
        byte[] Stored(int level, ReadOnlySpan<byte> key) =>
            Pending(level).GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(key, out byte[]? held, out _) ? held : key.ToArray();
    }

    /// <summary>Where a batch's changes to the tree of rollups lie among its changes by level: past every level.</summary>
    private const int RollupTree = -1;

    /// <summary>
    /// What a rollup of <paramref name="rollup"/>'s place sums in <paramref name="levels"/>, hashed as its builder hashes it,
    /// and how many children: at rank 1, its level's entries between its first and its last tree keys, each its tree key and
    /// its entry; past it, the rollups of the rank below over that range, each its first tree key and its fingerprint.
    /// </summary>
    internal static async ValueTask<(UInt128 Fingerprint, int Children)> SummedAsync(
        DatasetLevels levels, RollupEntry rollup, IPageSource pages, CancellationToken cancellationToken)
    {
        System.IO.Hashing.XxHash128 hash = new System.IO.Hashing.XxHash128();
        int children = 0;
        if (rollup.Rank == 1)
        {
            await foreach (TreeEntry entry in RangeAsync(levels[rollup.Level], rollup.FirstKey, rollup.LastKey, pages, cancellationToken).ConfigureAwait(false))
            {
                hash.Append(entry.Key.Span);
                hash.Append(ObjectEntry.FromBytes(entry.Value).ToBytes());
                children++;
            }
        }
        else
        {
            byte[] low = RollupEntry.TreeKeyOf(rollup.Level, rollup.Rank - 1, rollup.FirstKey.Span);
            byte[] high = RollupEntry.TreeKeyOf(rollup.Level, rollup.Rank - 1, rollup.LastKey.Span);
            byte[] fingerprint = new byte[16];
            await foreach (TreeEntry entry in RangeAsync(levels.Rollups, low, high, pages, cancellationToken).ConfigureAwait(false))
            {
                RollupEntry child = RollupEntry.FromBytes(entry.Key.Span, entry.Value.Span);
                hash.Append(child.FirstKey.Span);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(fingerprint, child.Fingerprint);
                hash.Append(fingerprint);
                children++;
            }
        }

        return (hash.GetCurrentHashAsUInt128(), children);
    }

    /// <summary>
    /// The entries of <paramref name="tree"/> whose keys lie from <paramref name="low"/> to <paramref name="high"/>, both in,
    /// in key order: a page whose bounds miss the range is not read.
    /// </summary>
    internal static async IAsyncEnumerable<TreeEntry> RangeAsync(
        DatasetTree tree, ReadOnlyMemory<byte> low, ReadOnlyMemory<byte> high, IPageSource pages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (PositionedEntry held in tree.WalkAsync(
            pages,
            0,
            long.MaxValue,
            node => TreePage.Compare(node.MaxKey.Span, low.Span) >= 0 && TreePage.Compare(node.MinKey.Span, high.Span) <= 0,
            cancellationToken).ConfigureAwait(false))
        {
            if (TreePage.Compare(held.Entry.Key.Span, low.Span) < 0)
            {
                continue;
            }

            if (TreePage.Compare(held.Entry.Key.Span, high.Span) > 0)
            {
                yield break;
            }

            yield return held.Entry;
        }
    }

    /// <summary>Whether a replacement takes the object under <paramref name="key"/> in <paramref name="level"/> as an input.</summary>
    private static bool Takes(DatasetOperation.ReplaceObjects replace, int level, ReadOnlySpan<byte> key)
    {
        foreach ((int at, ReadOnlyMemory<byte> input) in replace.Inputs)
        {
            if (at == level && input.Span.SequenceEqual(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An output that is one of the inputs under its own key, the same file with more rows marked, as
    /// a delete leaves it, or moved into the level above whole, as a trivial move does, with the
    /// fragments the input carries now: the output was made from the entry the job read, and a
    /// fragment attached to the object or dropped from it since changes no row, so the replacement
    /// goes ahead, and must neither drop the one nor bring back the other.
    /// </summary>
    private static ObjectEntry WithCurrentFragments(
        ObjectEntry output,
        ReadOnlyMemory<byte> key,
        IReadOnlyList<(int Level, ReadOnlyMemory<byte> Key)> inputs,
        ObjectEntry?[] currents)
    {
        for (int i = 0; i < inputs.Count; i++)
        {
            if (currents[i] is { } current
                && current.Uid == output.Uid
                && inputs[i].Key.Span.SequenceEqual(key.Span))
            {
                return SameFragments(current, output) ? output : output.WithFragments(current.Fragments);
            }
        }

        return output;
    }

    private static bool SameFragments(ObjectEntry left, ObjectEntry right)
    {
        if (left.Fragments.Count != right.Fragments.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Fragments.Count; i++)
        {
            if (left.Fragments[i] != right.Fragments[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an entry still names the rows another did: the same file and the same deleted rows.
    /// A fragment attached meanwhile changes no row.
    /// </summary>
    private static bool SameRows(ObjectEntry current, ObjectEntry read) =>
        current.Uid == read.Uid
        && string.Equals(current.Key, read.Key, StringComparison.Ordinal)
        && current.SameDeletions(read);

    /// <summary>Whether two schemas and their retired names are the same.</summary>
    private static bool Same(ReadOnlyMemory<byte> schema, IReadOnlyList<RetiredColumn> retired, ReadOnlyMemory<byte> other, IReadOnlyList<RetiredColumn> otherRetired)
    {
        if (!schema.Span.SequenceEqual(other.Span) || retired.Count != otherRetired.Count)
        {
            return false;
        }

        for (int i = 0; i < retired.Count; i++)
        {
            if (retired[i] != otherRetired[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The schema a commit leaves, as its operations change it.</summary>
    /// <summary>
    /// Whether an attempt changes rows: the creation, or a commit of rows or of the schema one of whose
    /// operations applied. A compaction, an index fragment, a repack and a rekey only move rows.
    /// </summary>
    private static bool ChangesRows(CommitKind kind, List<OperationOutcome> outcomes) =>
        kind == CommitKind.Create
        || (kind is CommitKind.Append or CommitKind.Import or CommitKind.Replace or CommitKind.Delete or CommitKind.Update or CommitKind.Schema
            && outcomes.Contains(OperationOutcome.Applied));

    /// <summary>
    /// The last change of rows a version records, or, when a writer that does not know the field wrote
    /// it, the version itself: no later one, which is all that can be said.
    /// </summary>
    internal static ulong LastDataChangeOf(CommitHeader header, ulong version) =>
        header.LastDataChange != 0 ? Math.Min(header.LastDataChange, version) : version;

    private sealed class SchemaEdit(ReadOnlyMemory<byte> schema, IReadOnlyList<RetiredColumn> retired)
    {
        internal ReadOnlyMemory<byte> Schema { get; set; } = schema;

        internal IReadOnlyList<RetiredColumn> Retired { get; set; } = retired;

        /// <summary>The indexes every writer builds, as the header records them.</summary>
        internal ReadOnlyMemory<byte> WritePolicy { get; set; }

        /// <summary>The identifiers, as the header records them.</summary>
        internal IReadOnlyList<string> Identifiers { get; set; } = [];

        /// <summary>The shredded paths, as the header records them.</summary>
        internal IReadOnlyList<string> ShreddedPaths { get; set; } = [];

        /// <summary>The features of format 4 the identifiers and the shredded paths name, once there are any.</summary>
        internal DatasetFeatures Declared =>
            (Identifiers.Count > 0 ? DatasetFeatures.Identifiers : DatasetFeatures.None)
            | (ShreddedPaths.Count > 0 ? DatasetFeatures.ShreddedPaths : DatasetFeatures.None);
    }

    /// <summary>Where <paramref name="name"/> stands in <paramref name="names"/>; -1 when it does not.</summary>
    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether two lists name the same names in the same order.</summary>
    internal static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Relocation
    {
        internal HashSet<ulong> Versions { get; } = [];

        internal List<int> At { get; } = [];

        internal int Moved { get; set; }
    }

    /// <summary>The tree's own byte order over keys.</summary>
    private sealed class KeyOrder : IComparer<byte[]>
    {
        internal static KeyOrder Instance { get; } = new KeyOrder();

        public int Compare(byte[]? x, byte[]? y) => TreePage.Compare(x, y);
    }
}
