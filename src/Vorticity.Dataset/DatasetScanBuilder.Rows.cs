using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// Has the scan of one lane of an object read ahead, over a store whose read is a round trip, as many of its splits as
    /// a pass's lanes keep in flight together (<see cref="AggregationEngine.TotalAhead"/>), reserved in the session's
    /// budget as a pass reserves them: half as many at a time as the budget grants fewer, and none below two, the scan
    /// then decoding a batch ahead as before. The reservation, held while the scan reads; null when it reads none ahead:
    /// an aggregation's lane, granted its own, a scan of several lanes, of a take, or of a file that costs no round trip.
    /// </summary>
    private QueryMemory? ReadsAhead(ScanBuilder scan, VortexFile file)
    {
        int degree = _options.DegreeOfParallelism > 0 ? _options.DegreeOfParallelism : _dataset.Session.Options.MaxDegreeOfParallelism;
        if (_readAheadGranted || degree > 1 || _options.Prefetch > 1 || _take is not null || _orderPaths is not null
            || file.Source.CostsNoRoundTrip || scan.LargestSplitBytes() is not (> 0 and var split))
        {
            return null;
        }

        QueryMemory memory = new QueryMemory(_dataset.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process);
        int ahead = AggregationEngine.TotalAhead(split);
        while (ahead >= BatchAsyncEnumerator.ReadAheadSplits && !memory.TryGrow(ahead * split))
        {
            ahead /= 2;
        }

        if (ahead < BatchAsyncEnumerator.ReadAheadSplits)
        {
            memory.Dispose();
            return null;
        }

        scan.WithReadAhead(ahead);
        return memory;
    }

    /// <summary>
    /// A lease of <paramref name="held"/>: for a scan that reads keys of variant columns, an object of level 0
    /// through the node's memo of it shredded in memory, which parses its keys once; the object itself otherwise.
    /// </summary>
    private async ValueTask<ObjectLease> RentAsync(PositionedObject held, CancellationToken cancellationToken)
    {
        // Once a scan, the memo lets go of the objects that have left level 0 since it last looked.
        if (!_memoChecked)
        {
            _memoChecked = true;
            await _dataset.LevelZero.ForgetMovedAsync(_version, cancellationToken).ConfigureAwait(false);
        }

        return held.Level == 0 && _variantKeys is { Count: > 0 } keys
            ? await _dataset.LevelZero.RentAsync(_dataset, _version, held.Entry, keys, cancellationToken).ConfigureAwait(false)
            : await _version.RentAsync(held.Entry, cancellationToken).ConfigureAwait(false);
    }

    private bool _memoChecked;

    /// <summary>
    /// The conjunct of <paramref name="filter"/> that looks one value of a declared identifier up, or a few,
    /// <c>id == v</c> or <c>id IN (…)</c> on a column of its own, never null: what the identifier's index answers object
    /// by object with no open of the object (<see cref="VortexDataset.LookupAsync"/>). Null for any other filter.
    /// </summary>
    internal static VortexExpr? LookupOf(VortexExpr? filter, IReadOnlyList<string> identifiers)
    {
        if (filter is null || identifiers.Count == 0)
        {
            return null;
        }

        List<VortexExpr> conjuncts = [];
        GroupPredicates.Conjuncts(filter, conjuncts);
        foreach (VortexExpr conjunct in conjuncts)
        {
            FieldExpr? field = conjunct switch
            {
                ComparisonExpr { Op: ComparisonOp.Equal } equal when equal.Value.Kind != FilterLiteralKind.Null => equal.Field,
                InExpr inList when inList.Values.Count > 0 && !HoldsNull(inList.Literals) => inList.Field,
                _ => null,
            };
            if (field is ColumnFieldExpr)
            {
                foreach (string identifier in identifiers)
                {
                    if (string.Equals(identifier, field.Path, StringComparison.Ordinal))
                    {
                        return conjunct;
                    }
                }
            }
        }

        return null;

        static bool HoldsNull(FilterLiteral[] values)
        {
            foreach (FilterLiteral value in values)
            {
                if (value.Kind == FilterLiteralKind.Null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The filter's lookup of an identifier (<see cref="LookupOf"/>), for a scan of rows that reads them in the tree's order
    /// with its indexes; null otherwise.
    /// </summary>
    private VortexExpr? Lookup
    {
        get
        {
            if (!_lookupKnown)
            {
                _lookup = _indexes && _orderPaths is null && _take is null && LookupColumns(out _)
                    ? LookupOf(_filter, _version.Header.Identifiers)
                    : null;
                _lookupKnown = true;
            }

            return _lookup;
        }
    }

    /// <summary>
    /// The columns a lookup reads its rows for: the projected ones, whole, which the filter's must be among; null in
    /// <paramref name="columns"/> for every one. False when the projection keeps part of a column, or leaves out a column
    /// the filter reads.
    /// </summary>
    private bool LookupColumns(out int[]? columns)
    {
        columns = null;
        if (_mask is not { IsAll: false } mask)
        {
            return true;
        }

        DType schema = _version.Schema.DType;
        int count = mask.SelectedCount(schema.FieldCount);
        int[] fields = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (!mask.SelectedMask(i).IsAll)
            {
                return false;
            }

            fields[i] = mask.SelectedField(i);
        }

        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        _filter?.CollectFields(named);
        foreach (string path in named)
        {
            int field = schema.IndexOfField(path);
            if (field < 0 || Array.IndexOf(fields, field) < 0)
            {
                return false;
            }
        }

        columns = fields;
        return true;
    }

    /// <summary>
    /// Whether the rows of the object <paramref name="held"/> are found by its identifier's index rather than by its open, when
    /// the scan looks a value up (spec B, 5): an object with no row marked whose entry carries a table of contents that names
    /// its index directory, where a lookup, which asks for each column it reads apart, a request more for each past the
    /// first, weighs less than the footer an open reads whole before the same reads.
    /// </summary>
    internal bool LooksUp(in PositionedObject held)
    {
        ObjectEntry entry = held.Entry;
        if (entry.HasDeletions || entry.Contents is not { DirectoryRegion: not null })
        {
            return false;
        }

        int columns = LookupColumns(out int[]? read) && read is not null ? read.Length : FieldsRead().Length;
        return Math.Max(columns - 1, 0) * (double)_version.Costs.Profile.RequestBytes < PathCosts.OpenBytes(entry);
    }

    /// <summary>Whether <paramref name="held"/> holds the version's own columns, whose rows a read of its chunks gives as they are.</summary>
    private bool OwnColumns(in PositionedObject held)
    {
        try
        {
            return held.Entry.Contents is { } contents && _version.Schema.ColumnsOf(contents.SchemaType, held.Entry.Key) is null;
        }
        catch (VortexFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The filter's lookup when <paramref name="held"/> takes it: an object the scan covers whole, of the version's own
    /// columns, whose rows a lookup finds for less than an open (<see cref="LooksUp"/>); null otherwise.
    /// </summary>
    private VortexExpr? LookupFor(in PositionedObject held)
    {
        if (Lookup is not { } lookup || Covered(held) != held.Entry.Rows || !LooksUp(held)
            || held.Entry.Contents is not { } contents)
        {
            return null;
        }

        try
        {
            return _version.Schema.ColumnsOf(contents.SchemaType, held.Entry.Key) is null ? lookup : null;
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The scan of a lookup of an identifier: every object the walk keeps that takes the lookup (<see cref="LookupFor"/>)
    /// looked up at once, as many reads in flight as the session lets be, and the objects' rows given in the tree's order;
    /// an object that does not take it, a small one whose footer an open reads in a request, read as the scan reads any,
    /// <see cref="LookupReads"/> of them at once, the rows they keep copied. A lookup takes four round trips whatever the
    /// object holds, and an open and a read of a small object two or three: thirty objects one after the other would wait
    /// for 120, or for 60 to 90.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> LookedUpAsync(VortexExpr lookup, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LookupColumns(out int[]? columns);
        List<(PositionedObject Held, Task<List<RecordBatch>?>? Found, bool Looked)> objects = [];
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SemaphoreSlim? reading = null;

        // The objects the index of a consolidated partition covers are found by it, a page a partition; an object whose blocks
        // hold no value sought is passed over unread.
        PartitionLookup? partitions = PartitionLookup.Of(_dataset, _version, lookup);
        try
        {
            int read = 0;
            await foreach (PositionedObject held in WalkAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Covered(held) > 0)
                {
                    if (partitions is not null && Covered(held) == held.Entry.Rows && !held.Entry.HasDeletions && OwnColumns(held)
                        && await partitions.BlocksAsync(held, cancellationToken).ConfigureAwait(false) is { } located)
                    {
                        if (located.Blocks.Length == 0)
                        {
                            if (_metrics is { } metrics)
                            {
                                metrics.ObjectsPassedByPartitionIndex++;
                            }

                            continue;
                        }

                        objects.Add((held, _dataset.ReadBlocksAsync(held.Entry, located.Blocks, located.BlockRows, lookup, columns, stopped.Token).AsTask(), true));
                        continue;
                    }

                    bool looked = LookupFor(held) is not null;
                    objects.Add((held, looked ? _dataset.LookupAsync(held.Entry, lookup, columns, stopped.Token).AsTask() : null, looked));
                    read += looked ? 0 : 1;
                }
            }

            // Objects read as rows, two or more, read ahead together; one alone is read when its turn comes, as a scan reads it.
            for (int i = 0; i < objects.Count && read > 1; i++)
            {
                if (!objects[i].Looked)
                {
                    reading ??= new SemaphoreSlim(LookupReads);
                    objects[i] = (objects[i].Held, ReadAheadAsync(objects[i].Held, reading, stopped.Token), false);
                }
            }

            foreach ((PositionedObject held, Task<List<RecordBatch>?>? found, bool looked) in objects)
            {
                if (looked)
                {
                    await foreach (RecordBatch batch in FoundAsync(held, await found!.ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
                    {
                        yield return batch;
                    }

                    continue;
                }

                if (found is not null)
                {
                    foreach (RecordBatch batch in await found.ConfigureAwait(false) ?? [])
                    {
                        yield return batch;
                    }

                    continue;
                }

                await foreach (RecordBatch batch in RowsAsync(held, 1, cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            // A scan stopped or failed: the lookups and the reads still on their way are called off and end, and what they
            // found is let go.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach ((_, Task<List<RecordBatch>?>? found, _) in objects)
            {
                if (found is null)
                {
                    continue;
                }

                try
                {
                    foreach (RecordBatch batch in await found.ConfigureAwait(false) ?? [])
                    {
                        batch.Dispose();
                    }
                }
                catch (Exception) when (found.IsFaulted || found.IsCanceled)
                {
                }
            }

            reading?.Dispose();
        }
    }

    /// <summary>The objects a lookup's scan reads as rows at once, those that do not take the lookup.</summary>
    private const int LookupReads = 16;

    /// <summary>
    /// The rows of <paramref name="held"/> the scan keeps, read as the scan reads any object once <paramref name="reading"/>
    /// lets one more be read, each batch that keeps a row copied, the scan's own batches being let go as it moves on.
    /// </summary>
    private async Task<List<RecordBatch>?> ReadAheadAsync(PositionedObject held, SemaphoreSlim reading, CancellationToken cancellationToken)
    {
        await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<RecordBatch> kept = [];
        try
        {
            await foreach (RecordBatch batch in RowsAsync(held, 1, cancellationToken).ConfigureAwait(false))
            {
                if (batch.SelectedRows > 0)
                {
                    kept.Add(batch.View.ToOwned());
                }
            }

            return kept;
        }
        catch
        {
            foreach (RecordBatch batch in kept)
            {
                batch.Dispose();
            }

            throw;
        }
        finally
        {
            reading.Release();
        }
    }

    /// <summary>
    /// The rows an object's lookup found, the scan's filter kept, as the dataset's; the object's rows as any other's when its
    /// index could not say (<paramref name="found"/> null). The batches stay the lookup's, which the scan lets go.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> FoundAsync(
        PositionedObject held, List<RecordBatch>? found, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (found is null)
        {
            await foreach (RecordBatch batch in RowsAsync(held, 1, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }

            yield break;
        }

        if (_metrics is { } metrics)
        {
            metrics.ObjectsLookedUp++;
        }

        RecordBatch? view = null;
        try
        {
            foreach (RecordBatch batch in found)
            {
                if (_filter is { } filter)
                {
                    SelectBy(batch, filter);
                }

                if (batch.SelectedRows > 0)
                {
                    yield return InDataset(batch, held.FirstRow, ref view);
                }
            }
        }
        finally
        {
            view?.Dispose();
        }
    }

    /// <summary>
    /// The geometry of the chunks of the columns the scan reads of the object <paramref name="held"/>, open as
    /// <paramref name="file"/> (spec C, 2.1): what its scans of a part of its rows read the granules of that part by, its marks
    /// read once per object and scan, before the first such scan: those <paramref name="marks"/> brings, read beside the
    /// open, or through the file, whose tail serves them. Null when the object keeps none, or when it is so small that
    /// a request reads it whole for what one costs at the store's profile (spec C, 6): its granules could spare no request.
    /// A count reads the filter's columns alone, and their marks alone (<paramref name="judged"/>).
    /// </summary>
    private async ValueTask GeometryAsync(
        VortexFile file, PositionedObject held, Task<Indexes.ColumnMarks?[]>? marks, CancellationToken cancellationToken, bool judged = false)
    {
        if (!WantsGeometry(held))
        {
            return;
        }

        int[] fields = judged ? FieldsJudged() : FieldsRead();
        Indexes.ColumnMarks?[] read = await (marks ?? _dataset.ReadMarksAsync(held.Entry, fields, cancellationToken, file).AsTask()).ConfigureAwait(false);
        Indexes.ColumnMarks?[] byField = new Indexes.ColumnMarks?[_version.Schema.DType.IsDefault ? 0 : _version.Schema.DType.FieldCount];
        for (int i = 0; i < fields.Length; i++)
        {
            byField[fields[i]] = read[i];
        }

        LazyInitializer.EnsureInitialized(ref _geometries)[held.Entry.Uid] =
            ColumnsOfFile(file, held.Entry) ? Indexes.GranuleGeometry.Of(file.LayoutTree, byField, _version.Costs.Profile.RequestBytes) : null;
    }

    /// <summary>
    /// Whether the marks of the columns the scan reads of the object <paramref name="held"/> lie in the tail an open of it
    /// reads (<see cref="PathCosts.InOpen"/>): the open's tail serves them.
    /// </summary>
    private bool MarksInOpenTail(in PositionedObject held)
    {
        IReadOnlyList<Indexes.IndexSegment> marks = held.Entry.Contents!.Marks;
        foreach (int field in FieldsRead())
        {
            if (field < marks.Count && !PathCosts.InOpen(held.Entry, marks[field]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a read of the object <paramref name="held"/> may read parts of its chunks by their granules, whose geometry
    /// it has not read yet: one that keeps its marks and is larger than what a request is worth at the store's profile.
    /// </summary>
    private bool WantsGeometry(in PositionedObject held) =>
        held.Entry.Contents is { Marks.Count: > 0 } && !(_geometries?.ContainsKey(held.Entry.Uid) ?? false)
        && held.Entry.Bytes > _version.Costs.Profile.RequestBytes;

    /// <summary>
    /// The blocks of <paramref name="file"/>'s pruning that <paramref name="granules"/> keeps, a bit a granule: null when the
    /// granules are not its blocks, which the scan then prunes alone.
    /// </summary>
    private static Compute.BlockMask? Masked(VortexFile file, ulong[] granules)
    {
        LayoutTree tree = file.LayoutTree;
        Compute.BlockMask mask = new Compute.BlockMask(tree.Root.RowCount, Scanning.SplitPlan.NaturalBatchRows(tree));
        if ((mask.BlockCount + 63) / 64 != granules.Length)
        {
            return null;
        }

        mask.Keep(granules);
        return mask;
    }

    /// <summary>Whether <paramref name="file"/> holds the version's own columns in their places, which the marks name by place.</summary>
    private bool ColumnsOfFile(VortexFile file, ObjectEntry entry) => _version.Schema.ColumnsOf(file.DType, entry.Key) is null;

    /// <summary>The geometry <see cref="GeometryAsync"/> read for the object <paramref name="held"/>; null before, or for none.</summary>
    private Indexes.GranuleGeometry? GeometryOf(in PositionedObject held) =>
        _geometries is { } known && known.TryGetValue(held.Entry.Uid, out Indexes.GranuleGeometry? geometry) ? geometry : null;

    // The geometry of each object a scan of a part of read, by its identity: the lanes that read the parts of one object,
    // and the parts of the scan, share it. Made at the first such object.
    private System.Collections.Concurrent.ConcurrentDictionary<UInt128, Indexes.GranuleGeometry?>? _geometries;

    /// <summary>One object's rows as the dataset's, its lease held until the last is read.</summary>
    private async IAsyncEnumerable<RecordBatch> RowsAsync(
        PositionedObject held, int open, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The marks of the columns read, beside the object's open, when a read of a part of it may ask them (under a filter,
        // a take, the granules of the key's index or a range of the scan) and the tail the open reads does not hold them all;
        // that tail serves them otherwise, once the object is open.
        Task<Indexes.ColumnMarks?[]>? marks = WantsGeometry(held)
            && (_filter is not null || _take is not null || held.Granules is not null || Covered(held) < held.Entry.Rows)
            && !MarksInOpenTail(held)
            ? _dataset.ReadMarksAsync(held.Entry, FieldsRead(), cancellationToken).AsTask()
            : null;
        try
        {
            await foreach (RecordBatch batch in OpenedRowsAsync(held, open, marks, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }
        finally
        {
            if (marks is not null)
            {
                await ((Task)marks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>One object's rows as the dataset's, its lease held until the last is read, the marks of its columns on their way.</summary>
    private async IAsyncEnumerable<RecordBatch> OpenedRowsAsync(
        PositionedObject held, int open, Task<Indexes.ColumnMarks?[]>? marks, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            RecordOpen(lease, open);
            ObjectColumns? columns = ColumnsOf(lease, held.Entry);
            if (_settles is { } plan && columns is null && _take is null && MaySettle(held)
                && await SettlingOfAsync(lease.File, held, plan, cancellationToken).ConfigureAwait(false) is { } settling)
            {
                // The rows between the settled blocks, each run of them a part of the object: the granules of the run alone.
                await GeometryAsync(lease.File, held, marks, cancellationToken).ConfigureAwait(false);
                await foreach (RecordBatch batch in SettledAsync(lease.File, held, settling, cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }

                yield break;
            }

            if (columns is null && !held.Entry.HasDeletions && _take is null && _filter is { } asked)
            {
                // Under a filter, an object of the version's columns with no row marked: its rows from the granules a kept row
                // may lie in, in one pass or two (spec D, 4 and 5).
                await foreach (RecordBatch batch in FilteredAsync(lease.File, held, asked, marks, cancellationToken).ConfigureAwait(false))
                {
                    yield return batch;
                }

                yield break;
            }

            if (columns is null && (_take is not null || held.Granules is not null || Covered(held) < held.Entry.Rows))
            {
                // A part of the object, by the places of its rows, the granules its key's index keeps, or the scan's range:
                // the bytes of those granules alone.
                await GeometryAsync(lease.File, held, marks, cancellationToken).ConfigureAwait(false);
            }

            if (columns is null && !held.Entry.HasDeletions)
            {
                // The object's own scan, which every object of the version's schema with no row marked is read by, rebased
                // into one view for the whole object.
                RecordBatch? view = null;
                ScanBuilder scan = Of(lease.File, held).WithGranules(GeometryOf(held));
                using QueryMemory? ahead = ReadsAhead(scan, lease.File);
                await foreach (RecordBatch batch in scan.ExecuteAsync()
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return InDataset(batch, held.FirstRow, ref view);
                }

                yield break;
            }

            if (BatchesOfAsync(lease.File, held, PartOf(columns, _mask), held.FirstRow, _options.Compact, cancellationToken) is { } batches)
            {
                await foreach (RecordBatch batch in batches.ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
    }

    /// <summary>
    /// Whether the blocks of the object <paramref name="held"/> may settle under the scan's filter: there is none, or it reads
    /// the key's first column alone and the object's summaries prove it true over every row. Under a filter on another column
    /// a block is rarely kept whole, and the proof of each, the zone maps of the columns it reads, is not worth asking.
    /// </summary>
    private bool MaySettle(in PositionedObject held)
    {
        if (_filter is not { } filter)
        {
            return true;
        }

        if (!_wholeKnown)
        {
            // The key's first column as it is, which a partition may lead, under the version this scan reads.
            SummaryPruner pruner = new SummaryPruner(filter);
            string? leading = _version.Schema.KeyColumns is [{ } first, ..] ? first : null;
            bool alone = leading is not null && pruner.ReadsColumns;
            foreach (string path in pruner.Paths)
            {
                alone &= string.Equals(path, leading, StringComparison.Ordinal);
            }

            _whole = alone ? pruner : null;
            _wholeKnown = true;
        }

        return _whole is { } whole && whole.MustMatch(held.Entry.Summaries.Columns, held.Entry.PhysicalRows);
    }

    /// <summary>
    /// The blocks of the object <paramref name="held"/>, within the rows the scan reads of it, that its zone maps settle for
    /// <paramref name="plan"/>, those no delete marked a row of; null when none does. The filter, which the object's summaries
    /// prove true over its rows (<see cref="MaySettle"/>), keeps every block whole.
    /// </summary>
    private ValueTask<ZoneSettling?> SettlingOfAsync(VortexFile file, PositionedObject held, AggregationPlan plan, CancellationToken cancellationToken)
    {
        (long lower, long upper) = PhysicalRange(held);
        return ZoneSettling.PlanObjectAsync(
            file, new RowRange(lower, upper), filter: null, _indexes, plan, held.Entry.HasDeletions ? held.Entry.Deletions : null, _counters, cancellationToken);
    }

    /// <summary>
    /// The rows of the object <paramref name="held"/>, the blocks <paramref name="settling"/> settles in place of theirs: one
    /// read of the others as the object's own, its marked rows left out, its pruning starting from them (spec D, 4.3), and
    /// each settled block, which holds none, folded as a block of as many rows of its zones' bounds at its place among the
    /// dataset's rows.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> SettledAsync(
        VortexFile file, PositionedObject held, ZoneSettling settling, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ZoneSettling.Scratch scratch = settling.NewScratch();
        DeletionVector deletions = held.Entry.Deletions;
        ScanBuilder? scan = held.Entry.HasDeletions
            ? null
            : Of(file, held).WithPruned(settling.Live).WithMaxBatchRows(BlockBatch(settling.Live)).WithGranules(GeometryOf(held));
        using QueryMemory? ahead = scan is null ? null : ReadsAhead(scan, file);
        IAsyncEnumerable<RecordBatch> rows = scan?.ExecuteAsync()
            ?? LiveAsync(file, held, _filter, _mask, held.FirstRow, _options.Compact, reshaped: false, cancellationToken, pruned: settling.Live);
        RecordBatch? view = null;
        int block = 0;
        await foreach (RecordBatch read in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            RecordBatch batch = scan is null ? read : InDataset(read, held.FirstRow, ref view);
            for (; block < settling.Count && held.FirstRow + deletions.Logical(settling.Range(block).Start) < batch.StartRow; block++)
            {
                yield return SettledBlock(settling, block, scratch, held.FirstRow, deletions);
            }

            yield return batch;
        }

        for (; block < settling.Count; block++)
        {
            yield return SettledBlock(settling, block, scratch, held.FirstRow, deletions);
        }
    }

    /// <summary>
    /// The rows a batch of a read pruned by <paramref name="live"/> holds at most: a block, so that a split reads no row of a
    /// dead one, or fewer when the scan asks it.
    /// </summary>
    private int BlockBatch(Compute.BlockMask live) =>
        _options.BatchRows > 0 && _options.BatchRows < live.BlockRows ? _options.BatchRows : (int)live.BlockRows;

    /// <summary>The settled block <paramref name="block"/>, folded at its place among the dataset's rows, its object's from <paramref name="firstRow"/>.</summary>
    private RecordBatch SettledBlock(ZoneSettling settling, int block, ZoneSettling.Scratch scratch, long firstRow, DeletionVector deletions)
    {
        if (_metrics is { } metrics)
        {
            metrics.GranulesSettled++;
        }

        return settling.Batch(block, scratch, firstRow + deletions.Logical(settling.Range(block).Start));
    }

    /// <summary>Selects the rows of <paramref name="batch"/> <paramref name="filter"/> holds for.</summary>
    private static void SelectBy(RecordBatch batch, VortexExpr filter) => FilterEvaluator.Select(batch, filter);

    /// <summary>
    /// A file's batch seen as the dataset's rows: row 0 is the object's row <c>StartRow</c>, after
    /// the rows before the object. One view serves the whole stream, bound again per batch, since a
    /// batch is valid until the next one is asked for and a view per batch would be an allocation
    /// per batch.
    /// </summary>
    /// <param name="batch">The file's batch.</param>
    /// <param name="firstRow">The dataset row the object's row 0 is.</param>
    /// <param name="view">The stream's view, made at the first batch that needs one.</param>
    private static RecordBatch InDataset(RecordBatch batch, long firstRow, ref RecordBatch? view)
    {
        if (firstRow == 0)
        {
            return batch;
        }

        if (view is null)
        {
            view = batch.Rebased(firstRow + batch.StartRow);
            return view;
        }

        view.Dispose();
        view.RebindRebased(batch, firstRow + batch.StartRow);
        return view;
    }

    /// <summary>
    /// The rows of the object <paramref name="held"/> that <paramref name="filter"/> keeps, an object of the version's columns
    /// with no row marked, as the dataset's (spec D, 4 and 5). Its scan's pruning starts from the granules a kept row may lie
    /// in: those where the filter's values lie, as the walk found them, and those where it kept one before, its pages. When the
    /// filter's columns are a part of those read, they are read first (<see cref="TwoPassesAsync"/>), then, as the rows they
    /// kept say, the rest for those rows by their places, or the granules that hold them, whole (<see cref="KeptAsync"/>);
    /// otherwise every column at once, from those granules, read alone where the store's profile says it pays (spec D, 4.4).
    /// A filter evaluated over every row of the object notes its pages.
    /// </summary>
    private async IAsyncEnumerable<RecordBatch> FilteredAsync(
        VortexFile file, PositionedObject held, VortexExpr filter, Task<Indexes.ColumnMarks?[]>? marks, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SynopsisCache? cache = _dataset.Session.Options.SynopsisCache;
        long rows = held.Entry.PhysicalRows;
        long granuleRows = Scanning.SplitPlan.NaturalBatchRows(file.LayoutTree);
        int granules = granuleRows <= 0 ? 0 : (int)((rows + granuleRows - 1) / granuleRows);
        long blocks = (rows + TwoPhaseReads.BlockRows - 1) / TwoPhaseReads.BlockRows;
        bool whole = Covered(held) == held.Entry.Rows;
        UInt128 predicate = granules > 0 ? PredicateKey : default;

        // The granules a kept row may lie in: where the filter's values lie, and where it kept one before.
        ulong[]? may = Volatile.Read(ref _narrowed) is { } narrowed && narrowed.TryGetValue(held.Entry.Uid, out ulong[]? located) ? located : null;
        if (predicate != default && _dataset.Predicates.Of(held.Entry.Uid, predicate, granules, cache) is { } pages)
        {
            may = may is null || may.Length != pages.Length ? pages : Both(may, pages);
        }

        Compute.BlockMask? granted = may is null ? null : Masked(file, may);
        await GeometryAsync(file, held, marks, cancellationToken).ConfigureAwait(false);
        RecordBatch? view = null;
        if (granules >= TwoPhaseReads.LeastGranules && await TwoPassesAsync(held, filter, predicate, cancellationToken).ConfigureAwait(false) is { } filtered
            && await KeptAsync(file, held, filter, filtered, granted, granuleRows, cancellationToken).ConfigureAwait(false) is { } first)
        {
            if (whole)
            {
                _dataset.Predicates.Keep(held.Entry.Uid, predicate, first.Granules, granules, first.ByPlaces, cache);
            }

            if (first.Kept == 0)
            {
                yield break;
            }

            // The rest for the rows kept by their places, which reads the granules that hold them alone and decodes only their
            // blocks of 1 024 that do (spec C, 3); or the granules that hold them, whole, the filter judged again.
            ScanBuilder second = first.ByPlaces
                ? Configure(file.ScanBuilder(), null, _mask, canonical: false).Take(first.Rows).WithGranules(GeometryOf(held))
                : Of(file, held).WithGranuleMask(Masked(file, first.Granules)).WithGranules(GeometryOf(held));
            using QueryMemory? reads = ReadsAhead(second, file);
            await foreach (RecordBatch batch in second.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return InDataset(batch, held.FirstRow, ref view);
            }

            yield break;
        }

        // Every column at once, from the granules a kept row may lie in.
        ScanBuilder scan = Of(file, held).WithGranuleMask(granted).WithGranules(GeometryOf(held));
        bool notes = whole && predicate != default && !_options.Compact && !_dataset.Predicates.Wide(held.Entry.Uid, predicate);
        ulong[]? noted = notes ? new ulong[(granules + 63) / 64] : null;
        ulong[]? blocked = notes ? new ulong[(blocks + 63) / 64] : null;
        long keptBlocks = 0;
        long selected = 0;
        using QueryMemory? ahead = ReadsAhead(scan, file);
        await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (noted is not null)
            {
                PredicatePages.Note(batch.SelectionWords, batch.StartRow, batch.RowCount, granuleRows, noted);
                keptBlocks += PredicatePages.Note(batch.SelectionWords, batch.StartRow, batch.RowCount, TwoPhaseReads.BlockRows, blocked!);
            }

            selected += batch.SelectedRows;
            yield return InDataset(batch, held.FirstRow, ref view);
        }

        if (noted is not null)
        {
            _dataset.Predicates.Keep(held.Entry.Uid, predicate, noted, granules, TwoPhaseReads.ByPlaces(keptBlocks, blocks), cache);
        }

        // What a sort key led by each column of the filter would have spared the object, read whole as rows.
        if (whole && granted is null && FilterColumns() is { Count: > 0 } credited)
        {
            _dataset.Advisor.Read(credited, _version.Costs.Rows(held.Entry, FieldsRead()).Bytes, selected, held.Entry.Rows);
        }
    }

    /// <summary>The granules both <paramref name="left"/> and <paramref name="right"/> keep, a bit a granule.</summary>
    private static ulong[] Both(ulong[] left, ulong[] right)
    {
        ulong[] both = new ulong[left.Length];
        for (int i = 0; i < both.Length; i++)
        {
            both[i] = left[i] & right[i];
        }

        return both;
    }

    /// <summary>
    /// The columns of <paramref name="filter"/> when the object <paramref name="held"/> is read in two passes (spec D, 5): a
    /// filter on top-level columns alone, which reads a part of the columns the scan reads, over an object that keeps the
    /// geometry of its granules; null to read it in one. What its first pass keeps decides the second (<see cref="KeptAsync"/>).
    /// The sheets of its columns, those the walk read, which the handle keeps, rule the object out when they say it keeps rows
    /// in half its blocks or more (<see cref="TwoPhaseReads"/>), and so does the filter noted wide there before
    /// (<see cref="PredicatePages"/>).
    /// </summary>
    private async ValueTask<FieldMask?> TwoPassesAsync(PositionedObject held, VortexExpr filter, UInt128 predicate, CancellationToken cancellationToken)
    {
        DType schema = _version.Schema.DType;
        if (!_summaries || held.Entry.Contents is not { Marks.Count: > 0 } contents || schema.IsDefault
            || (predicate != default && _dataset.Predicates.Wide(held.Entry.Uid, predicate)))
        {
            return null;
        }

        List<int> fields = [];
        foreach (string path in new SummaryPruner(filter).Paths)
        {
            if (path.Contains('.', StringComparison.Ordinal) || schema.IndexOfField(path) is not (int field and >= 0))
            {
                return null;
            }

            if (!fields.Contains(field))
            {
                fields.Add(field);
            }
        }

        if (fields.Count == 0 || !ReadsBesides(fields))
        {
            return null;
        }

        // The rows the filter is expected to keep, by the sheets of the columns it asks values of and by the object's summaries
        // for a window on a number: in half its blocks or more, or so many that the reads of the second pass join into the
        // columns whole at the store's profile, the object is read in one pass, its filter's columns once.
        List<int> asked = [];
        Indexes.ColumnSheet?[] sheets = [];
        if (contents.Sheets.Count > 0 && SheetPruning.Asks(filter, schema))
        {
            asked = SheetPruning.Columns(filter, schema);
            sheets = await _dataset.ReadSheetsAsync(held.Entry, asked, cancellationToken).ConfigureAwait(false);
        }

        long blocks = (held.Entry.PhysicalRows + TwoPhaseReads.BlockRows - 1) / TwoPhaseReads.BlockRows;
        if (TwoPhaseReads.ExpectedRows(
                filter, schema, field => asked.IndexOf(field) is int at and >= 0 ? sheets[at] : null, held.Summaries(_version.Schema).Columns,
                held.Entry.PhysicalRows) is long rows
            && !TwoPhaseReads.Spares(rows, blocks, WidestBesides(held, fields), _version.Costs.Profile.JoinedGap))
        {
            return null;
        }

        return FieldMasks.Of(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fields));
    }

    /// <summary>Whether the scan reads a column besides <paramref name="fields"/>.</summary>
    private bool ReadsBesides(List<int> fields)
    {
        DType schema = _version.Schema.DType;
        int read = _mask is { } mask ? mask.SelectedCount(schema.FieldCount) : schema.FieldCount;
        for (int i = 0; i < read; i++)
        {
            if (!fields.Contains(_mask is { } selected ? selected.SelectedField(i) : i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the first of two passes kept (<see cref="KeptAsync"/>): its rows, the granules that hold one, a bit a granule,
    /// whether the second reads them by their places, and then their places in the file.
    /// </summary>
    private readonly record struct FirstPass(long Kept, ulong[] Granules, bool ByPlaces, long[] Rows);

    /// <summary>
    /// The first of two passes over the object <paramref name="held"/> (spec D, 5): its columns <paramref name="filtered"/>
    /// alone, from the granules <paramref name="granted"/> leaves, what <paramref name="filter"/> keeps: the granules of
    /// <paramref name="granuleRows"/> rows that hold a row it kept, and, while the rows kept are no more than the object's
    /// blocks of 1 024, their places. The second pass reads them by their places when the blocks that hold one are fewer than
    /// half the object's (<see cref="TwoPhaseReads.ByPlaces"/>); otherwise the granules that hold one. Null, the first pass
    /// given up, once they are half its blocks or more and more than half its granules: the second pass would read most of
    /// the object again, which one pass reads once. With no granule granted, it reads a first part of the object, its first
    /// thirty-second and 64 blocks at least, to where the chunks of the filter's columns next start, then the rest: a part
    /// that says so gives it up, its reads that part's alone, and no chunk is read by both.
    /// </summary>
    private async ValueTask<FirstPass?> KeptAsync(
        VortexFile file, PositionedObject held, VortexExpr filter, FieldMask filtered, Compute.BlockMask? granted, long granuleRows,
        CancellationToken cancellationToken)
    {
        long rows = held.Entry.PhysicalRows;
        int granules = (int)((rows + granuleRows - 1) / granuleRows);
        long blocks = (rows + TwoPhaseReads.BlockRows - 1) / TwoPhaseReads.BlockRows;
        ulong[] granuleBits = new ulong[(granules + 63) / 64];
        ulong[] blockBits = new ulong[(blocks + 63) / 64];
        long[] places = new long[(int)Math.Min(blocks, 1_024)];
        int count = 0;
        bool placed = true;
        long kept = 0;
        int keptGranules = 0;
        long keptBlocks = 0;

        // The rows the scan reads of the object, and the first part, whole granules, read before the rest.
        (long lower, long upper) = PhysicalRange(held);
        if (held.Granules is { } part)
        {
            (lower, upper) = (Math.Max(lower, part.Start), Math.Min(upper, part.End));
        }

        // The widest column the second pass reads, which the gap between its kept rows is judged in at the store's profile.
        int joined = _version.Costs.Profile.JoinedGap;
        long widest = WidestBesides(held, Fields(filtered));

        // The first part ends where a chunk of each of the filter's columns starts, the first past the sample, so that no
        // chunk is read by both parts; with none before half the rows, the object is read in one part.
        long sample = Math.Max(64, blocks / 32) * TwoPhaseReads.BlockRows;
        sample = (sample + granuleRows - 1) / granuleRows * granuleRows;
        long cut = granted is null && upper - lower > 2 * sample ? FirstStart(file, new RowRange(lower, upper), lower + sample, filtered) : -1;
        RowRange[] parts = cut > 0 && cut - lower <= (upper - lower) / 2
            ? [new RowRange(lower, cut), new RowRange(cut, upper)]
            : [new RowRange(lower, upper)];
        for (int p = 0; p < parts.Length; p++)
        {
            ScanBuilder scan = Of(file, held, filter, filtered, canonical: false, within: parts[p])
                .WithCompaction(false).WithGranuleMask(granted).WithGranules(GeometryOf(held));
            await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                ReadOnlySpan<ulong> words = batch.SelectionWords;
                keptGranules += PredicatePages.Note(words, batch.StartRow, batch.RowCount, granuleRows, granuleBits);
                keptBlocks += PredicatePages.Note(words, batch.StartRow, batch.RowCount, TwoPhaseReads.BlockRows, blockBits);
                kept += batch.SelectedRows;
                placed &= kept <= blocks;
                if (placed && batch.SelectedRows > 0)
                {
                    if (kept > places.Length)
                    {
                        Array.Resize(ref places, (int)Math.Min(blocks, Math.Max(places.Length * 2L, kept)));
                    }

                    count = Append(words, batch.StartRow, batch.RowCount, places, count);
                }

                if (!TwoPhaseReads.Spares(keptBlocks, blocks, widest, joined) && (joined > 0 || keptGranules * 2L > granules))
                {
                    return null;
                }
            }

            // The first part, read whole, says what the rest would.
            if (p == 0 && parts.Length > 1
                && !TwoPhaseReads.ByPlaces(keptBlocks, parts[0].Length / TwoPhaseReads.BlockRows) && keptGranules * 2L > parts[0].Length / granuleRows)
            {
                return null;
            }
        }

        bool byPlaces = placed && TwoPhaseReads.ByPlaces(keptBlocks, blocks);
        return new FirstPass(kept, granuleBits, byPlaces, byPlaces ? places[..count] : []);
    }

    /// <summary>The bytes of the widest column the scan reads of the object <paramref name="held"/> besides <paramref name="fields"/>, as its table of contents says.</summary>
    private long WidestBesides(in PositionedObject held, List<int> fields)
    {
        IReadOnlyList<long> bytes = held.Entry.Contents!.ColumnBytes;
        long widest = 0;
        foreach (int field in FieldsRead())
        {
            widest = !fields.Contains(field) && field < bytes.Count ? Math.Max(widest, bytes[field]) : widest;
        }

        return widest;
    }

    /// <summary>The top-level columns <paramref name="mask"/> names.</summary>
    private List<int> Fields(in FieldMask mask)
    {
        int count = mask.SelectedCount(_version.Schema.DType.FieldCount);
        List<int> fields = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            fields.Add(mask.SelectedField(i));
        }

        return fields;
    }

    /// <summary>The first row at or past <paramref name="at"/> inside <paramref name="rows"/> where a chunk of each column of <paramref name="mask"/> starts; -1 for none.</summary>
    private static long FirstStart(VortexFile file, RowRange rows, long at, FieldMask mask)
    {
        foreach (long start in Scanning.SplitPlan.CommonStarts(file.LayoutTree, rows, in mask))
        {
            if (start >= at)
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>
    /// Appends to <paramref name="into"/> from <paramref name="at"/> the places of the rows <paramref name="selection"/> keeps of
    /// the batch of <paramref name="rows"/> rows from row <paramref name="start"/>, every row when it is empty; where it ends.
    /// </summary>
    private static int Append(ReadOnlySpan<ulong> selection, long start, int rows, long[] into, int at)
    {
        if (selection.IsEmpty)
        {
            for (int row = 0; row < rows; row++)
            {
                into[at++] = start + row;
            }

            return at;
        }

        for (int w = 0; w < selection.Length; w++)
        {
            ulong bits = selection[w];
            while (bits != 0)
            {
                into[at++] = start + (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
            }
        }

        return at;
    }

    private void RecordOpen(ObjectLease lease, int open = 1)
    {
        if (_metrics is { } metrics)
        {
            // A lookup's scan opens the objects it reads as rows several at once.
            lock (metrics)
            {
                metrics.ObjectsOpened++;
                metrics.Cursors = Math.Max(metrics.Cursors, open);
                if (lease.WasCached)
                {
                    metrics.CacheHits++;
                }
            }
        }
    }

    private void RecordCounted()
    {
        if (_metrics is { } metrics)
        {
            metrics.ObjectsCounted++;
        }
    }

    /// <summary>
    /// How an object answers for the version's columns: null when it holds exactly them, the case of
    /// every object written since the schema last changed, whose own scan then serves unchanged.
    /// </summary>
    private ObjectColumns? ColumnsOf(ObjectLease lease, ObjectEntry entry) => _version.Schema.ColumnsOf(lease.File.DType, entry.Key);

    /// <summary>
    /// An object's part of this read of the columns <paramref name="mask"/> names: what the filter is
    /// for it, from <paramref name="columns"/> for an object of an earlier schema, and for one of the
    /// version's own the filter whole, which its scan takes as it is. What an object was written under
    /// is settled here, once, and every terminal goes by the filter's fate alone.
    /// </summary>
    private ObjectPart PartOf(ObjectColumns? columns, FieldMask? mask)
    {
        if (columns is null)
        {
            return new ObjectPart(_filter is null ? FilterFate.Every : FilterFate.Pushed, _filter, mask, null);
        }

        ObjectReadPlan read = columns.Read(_filter, mask ?? FieldMask.All);
        return new ObjectPart(read.Fate, read.Pushed, read.Shape.Source, read);
    }

    /// <summary>The object's own scan for its part of the read: the filter it takes and the fields it reads, decoded whole when its batches are to be reshaped.</summary>
    private ScanBuilder ScanOf(VortexFile file, PositionedObject held, in ObjectPart part) =>
        Of(file, held, part.Pushed, part.Source, canonical: part.Mapped is not null);

    /// <summary>
    /// The batches of an object that its own scan alone does not read as the version's rows, one of an
    /// earlier schema or one with rows marked, counted from <paramref name="baseRow"/>; null when the
    /// filter can keep no row of it. The one place where the steps compose, in this order whatever
    /// reads the batches: the object's scan, its marked rows taken out or deselected, the batches
    /// reshaped to the version's columns, and the filter evaluated on them where it could not go to
    /// the scan.
    /// </summary>
    private IAsyncEnumerable<RecordBatch>? BatchesOfAsync(
        VortexFile file, PositionedObject held, in ObjectPart part, long baseRow, bool compact, CancellationToken cancellationToken)
    {
        if (part.Fate == FilterFate.None)
        {
            return null;
        }

        return part.Mapped is { } read
            ? ReshapedAsync(file, held, read, baseRow, compact, cancellationToken)
            : LiveAsync(file, held, part.Pushed, part.Source, baseRow, compact, reshaped: false, cancellationToken);
    }

    /// <summary>An object of another schema read as the version's: its own scan, each batch reshaped and counted from <paramref name="baseRow"/>.</summary>
    private IAsyncEnumerable<RecordBatch> ReshapedAsync(
        VortexFile file, PositionedObject held, ObjectReadPlan read, long baseRow, bool compact, CancellationToken cancellationToken) =>
        ObjectColumns.ReshapeAsync(
            held.Entry.HasDeletions
                ? LiveAsync(file, held, read.Pushed, read.Shape.Source, 0, compact && read.Evaluated is null, reshaped: true, cancellationToken)
                : Of(file, held, read.Pushed, read.Shape.Source, canonical: true).ExecuteAsync(),
            read.Shape,
            read.Evaluated,
            baseRow,
            compact,
            cancellationToken);

    /// <summary>The file's own scan, carrying this builder's filter, projection, options and the rows that fall in this object.</summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held) => Of(file, held, _filter, _mask, canonical: false);

    /// <summary>
    /// An object's part of a read, whatever schema it was written under: what the filter is for it,
    /// the filter its own scan takes, the fields that scan reads, and how its batches become the
    /// version's rows; none for an object of the version's own columns, whose batches already are.
    /// </summary>
    private readonly record struct ObjectPart(FilterFate Fate, VortexExpr? Pushed, FieldMask? Source, ObjectReadPlan? Mapped);

    /// <summary>
    /// The file's own scan under <paramref name="filter"/> and <paramref name="mask"/>, with this
    /// builder's options and the rows that fall in this object, within <paramref name="within"/> of
    /// the file's when it is given; with <paramref name="canonical"/>, every column decoded, which the
    /// reshaping of another schema's batches takes.
    /// </summary>
    private ScanBuilder Of(VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, bool canonical, RowRange? within = null)
    {
        ScanBuilder scan = Configure(file.ScanBuilder(), filter, mask, canonical);
        DeletionVector deletions = held.Entry.Deletions;
        if (_take is { } take)
        {
            // A row taken by its place among the dataset's live rows is at that place among the
            // object's live rows, which the deleted rows before it push further on.
            (int first, int count) = TakenBy(held);
            long[] local = new long[count];
            for (int i = 0; i < count; i++)
            {
                local[i] = deletions.Physical(take[first + i] - held.FirstRow);
            }

            return scan.Take(local).WithGranules(GeometryOf(held));
        }

        // The range is the dataset's; the file's is the part of it that falls inside this object, and inside its part.
        (long lower, long upper) = PhysicalRange(held);
        if (held.Granules is { } part)
        {
            lower = Math.Max(lower, part.Start);
            upper = Math.Min(upper, part.End);
        }

        if (within is { } inside)
        {
            lower = Math.Max(lower, inside.Start);
            upper = Math.Min(upper, inside.End);
        }

        if (lower > 0 || upper < held.Entry.PhysicalRows)
        {
            // A part of the object: the granules of the part alone, where its geometry is known (spec C, 2).
            scan.Rows(new RowRange(lower, Math.Max(upper, lower))).WithGranules(GeometryOf(held));
        }

        return scan;
    }

    /// <summary>
    /// The part of the scan's range inside an object, at the object's own positions: the range
    /// counts live rows, and the deleted rows before and inside it push it further on.
    /// </summary>
    private (long Lower, long Upper) PhysicalRange(in PositionedObject held)
    {
        long lower = Math.Max(_from - held.FirstRow, 0);
        long upper = _to == long.MaxValue ? held.Entry.Rows : Math.Min(_to - held.FirstRow, held.Entry.Rows);
        DeletionVector deletions = held.Entry.Deletions;
        if (deletions.IsEmpty)
        {
            return (lower, upper);
        }

        long first = deletions.Physical(lower);
        return (first, upper <= lower ? first : deletions.Physical(upper - 1) + 1);
    }

    /// <summary>
    /// An object's rows under <paramref name="filter"/> and <paramref name="mask"/> without the
    /// ones it deleted: its own scan run without compaction, so that the deleted rows can be found
    /// at their places, then taken out, which gathers decoded columns, and the rest numbered from
    /// <paramref name="baseRow"/>. Batches not compacted that nobody reads a place of keep their rows
    /// instead, the deleted ones deselected, and their columns encoded as an object without deletions
    /// delivers them; <paramref name="reshaped"/> decodes every column all the same, for the
    /// reshaping of another schema's batches. With <paramref name="pruned"/>, the blocks it keeps alone, its pruning done.
    /// </summary>
    private IAsyncEnumerable<RecordBatch> LiveAsync(
        VortexFile file, PositionedObject held, VortexExpr? filter, FieldMask? mask, long baseRow, bool compact, bool reshaped, CancellationToken cancellationToken,
        RowRange? within = null, Compute.BlockMask? pruned = null)
    {
        bool deselect = !compact && _positionsUnread;
        ScanBuilder scan = Of(file, held, filter, mask, canonical: reshaped || !deselect, within).WithCompaction(false);
        IAsyncEnumerable<RecordBatch> rows =
            (pruned is null ? scan : scan.WithPruned(pruned).WithMaxBatchRows(BlockBatch(pruned)).WithGranules(GeometryOf(held))).ExecuteAsync();
        return deselect
            ? LiveRows.DeselectedAsync(rows, held.Entry.Deletions, baseRow, cancellationToken)
            : LiveRows.WithoutDeletedAsync(rows, held.Entry.Deletions, compact, baseRow, cancellationToken);
    }

    /// <summary>A file's scan under a filter, a projection and this builder's options.</summary>
    private ScanBuilder Configure(ScanBuilder scan, VortexExpr? filter, FieldMask? projection, bool canonical)
    {
        if (filter is not null)
        {
            scan.Where(filter);
        }

        // An object read in the dataset's own columns reads its variants by keys where its path map
        // allows; one reshaped from an earlier schema reads them whole.
        if (!canonical)
        {
            scan.WithVariantKeys(_variantKeys);
        }

        if (projection is { } mask)
        {
            scan.ProjectMask(in mask);
        }

        if (_options.BatchRows > 0)
        {
            scan.WithMaxBatchRows(_options.BatchRows);
        }

        int degree = _options.DegreeOfParallelism > 0
            ? _options.DegreeOfParallelism
            : _dataset.Session.Options.MaxDegreeOfParallelism;
        scan.WithPruning(_options.UseStatistics)
            .WithIndexes(_indexes)
            .WithDegreeOfParallelism(Math.Max(degree, 1))
            .WithPrefetch(_options.Prefetch)
            .WithCompaction(_options.Compact)
            .WithEncodings(!canonical && _keepEncodings, !canonical && _sinkDecodes);
        if (_readAhead > 0)
        {
            scan.WithReadAhead(_readAhead);
        }

        return _counters is { } counters ? scan.WithMetrics(counters) : scan;
    }
}
