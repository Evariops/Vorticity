using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// Tallies the paths of <paramref name="question"/>'s variant columns among the rows the scan keeps, object by object as a
    /// count walks them, so that the objects and granules read are a count's. An object whose entry settles the count
    /// answers from its path directories, read without opening it, every group of its rows kept; any other is opened, and
    /// answers as its own file does: from its directories for the groups of rows its count's plan keeps, its deleted rows
    /// left out, or from a census of the rows it keeps where no directory answers. An object of an earlier schema has its
    /// kept rows counted as the version reads them. Under a degree of parallelism above one, as many objects as the degree
    /// are answered at once, each into tallies of its own added to the question's in the order of the walk, so that the
    /// answer is the one a walk object by object gives.
    /// </summary>
    /// <remarks>
    /// The work of an object is its count's plan, a count of the groups its window cuts and a read of the few of them its
    /// edges are, each a lane's work or less: the five objects of a source's hour of logs, one after the other, left 32
    /// lanes as long as one.
    /// </remarks>
    internal async ValueTask TallyPathsAsync(PathsQuestion question, CancellationToken cancellationToken)
    {
        KeyRange? range = CountRange();
        int degree = ScanDegree;
        if (degree <= 1)
        {
            await foreach (PositionedObject held in WalkAsync(cancellationToken, granules: true).ConfigureAwait(false))
            {
                if (Covered(held) > 0)
                {
                    await ObjectPathsAsync(held, range, question, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        using SemaphoreSlim slots = new SemaphoreSlim(degree);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        InOrder answered = new InOrder(question);
        List<Task> running = [];
        try
        {
            await foreach (PositionedObject held in WalkAsync(stop.Token, granules: true).ConfigureAwait(false))
            {
                if (Covered(held) == 0)
                {
                    continue;
                }

                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                int place = answered.Place();
                running.Add(Task.Run(
                    async () =>
                    {
                        try
                        {
                            PathsQuestion own = question.Alone();
                            await ObjectPathsAsync(held, range, own, stop.Token).ConfigureAwait(false);
                            answered.Answer(place, own);
                        }
                        catch
                        {
                            await stop.CancelAsync().ConfigureAwait(false);
                            throw;
                        }
                        finally
                        {
                            slots.Release();
                        }
                    },
                    stop.Token));
            }
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>The objects' own questions added to the question in the order of the walk, each as soon as those before it are.</summary>
    private sealed class InOrder(PathsQuestion question)
    {
        private readonly List<PathsQuestion?> _answers = [];
        private readonly Lock _gate = new Lock();
        private int _next;

        /// <summary>The place in the walk of the next object.</summary>
        internal int Place()
        {
            lock (_gate)
            {
                _answers.Add(null);
                return _answers.Count - 1;
            }
        }

        /// <summary>Adds <paramref name="own"/>, the object at <paramref name="place"/>'s, once every object before it is added.</summary>
        internal void Answer(int place, PathsQuestion own)
        {
            lock (_gate)
            {
                _answers[place] = own;
                while (_next < _answers.Count && _answers[_next] is { } ready)
                {
                    question.Take(ready);
                    _answers[_next++] = null;
                }
            }
        }
    }

    /// <summary>The paths of <paramref name="question"/>'s variant columns among the rows the scan keeps of the object <paramref name="held"/>.</summary>
    private async ValueTask ObjectPathsAsync(PositionedObject held, KeyRange? range, PathsQuestion question, CancellationToken cancellationToken)
    {
        List<PathColumn> left = [.. question.Columns];
        if (Settled(held, range) && await SettledAsync(held, question, left, cancellationToken).ConfigureAwait(false))
        {
            RecordCounted();
            return;
        }

        ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            RecordOpen(lease);
            PathsQuestion rest = question with { Columns = [.. left] };
            ObjectPart part = PartOf(ColumnsOf(lease, held.Entry), _mask);
            if (part.Fate == FilterFate.None)
            {
                return;
            }

            // The version's own columns: the object's directories answer, its deleted rows left out, a take naming live rows
            // alone; the columns they leave, and every column of an earlier schema, counted from the rows it keeps.
            IReadOnlyList<PathColumn>? counted = rest.Columns;
            if (part.Mapped is null)
            {
                IRowExclusion? deleted = _take is null && held.Entry.HasDeletions ? held.Entry.Deletions : null;
                counted = await Of(lease.File, held, part.Pushed, part.Source, canonical: false)
                    .TallyDirectoriesAsync(rest, deleted, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (counted is not null)
            {
                await PathQuestions.CountAsync(BatchesOfAsync(lease.File, held, part, 0, compact: true, cancellationToken)!, rest, counted, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tallies from their path directories, read without opening the object, the columns of <paramref name="left"/> an object
    /// the count settles answers for: every row of its part kept, its deleted rows aside. A directory whose groups the part
    /// cuts, or holds deleted rows of, few enough to read again (<see cref="Types.Variants.GroupVerdicts.EdgesWorthCounting"/>),
    /// answers when the object is opened, its rows there counted, so that it lists no path no kept row holds. Those it answers
    /// for leave <paramref name="left"/>; whether none is left.
    /// </summary>
    private async ValueTask<bool> SettledAsync(PositionedObject held, PathsQuestion question, List<PathColumn> left, CancellationToken cancellationToken)
    {
        ObjectEntry entry = held.Entry;
        if (_take is not null || entry.Contents is not { } contents || _version.Schema.ColumnsOf(contents.SchemaType, entry.Key) is not null)
        {
            return false;
        }

        (long lower, long upper) = PhysicalRange(held);
        if (held.Granules is { } granules)
        {
            lower = System.Math.Max(lower, granules.Start);
            upper = System.Math.Min(upper, granules.End);
        }

        KeptRows judge = new KeptRows(new RowRange(lower, System.Math.Max(lower, upper)), null, null, entry.HasDeletions ? entry.Deletions : null, filtered: false);
        DType schema = _version.Schema.DType;
        for (int c = left.Count - 1; c >= 0; c--)
        {
            Expressions.FieldExpr column = left[c].Field;
            int field = column.SegmentsUtf8.Length == 1 ? schema.IndexOfField(column.SegmentsUtf8[0]) : -1;
            if (field < 0 || schema.GetField(field).Kind != DTypeKind.Variant)
            {
                continue;
            }

            using Indexes.VariantDirectory.DirectoryBody? body = await _dataset.OpenPathDirectoryAsync(entry, field, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                continue;
            }

            PathTally tally = left[c].Tally;
            tally.Verdicts.Judge(body, judge);
            if (tally.Verdicts.PartGroups > 0 && tally.Verdicts.Grouped && tally.Verdicts.EdgesWorthCounting)
            {
                continue;
            }

            tally.Verdicts.Share = judge.Share(tally.Verdicts.WholeRows, tally.Verdicts.PartRows);
            if (tally.AddDirectory(body, edges: null, question.Exact))
            {
                question.Reads.Directories++;
                question.Reads.Unopened++;
                left.RemoveAt(c);
            }
        }

        return left.Count == 0;
    }
}
