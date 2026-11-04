using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

internal sealed partial class DatasetScanBuilder
{
    /// <summary>
    /// Tallies the paths of <paramref name="question"/>'s variant columns among the rows the scan keeps, object by object as a
    /// count walks them, so that the objects and granules read are a count's. An object whose entry settles the count
    /// answers from its path directories, read without opening it, every group of its rows kept; any other is opened, and
    /// answers as its own file does: from its directories for the groups of rows its count's plan keeps, its deleted rows
    /// left out, or from a census of the rows it keeps where no directory answers. An object of an earlier schema has its
    /// kept rows counted as the version reads them.
    /// </summary>
    internal async ValueTask TallyPathsAsync(PathsQuestion question, CancellationToken cancellationToken)
    {
        KeyRange? range = CountRange();
        List<PathColumn> left = [];
        await foreach (PositionedObject held in WalkAsync(cancellationToken, granules: true).ConfigureAwait(false))
        {
            if (Covered(held) == 0)
            {
                continue;
            }

            left.Clear();
            left.AddRange(question.Columns);
            if (Settled(held, range) && await SettledAsync(held, question, left, cancellationToken).ConfigureAwait(false))
            {
                RecordCounted();
                continue;
            }

            ObjectLease lease = await RentAsync(held, cancellationToken).ConfigureAwait(false);
            await using (lease.ConfigureAwait(false))
            {
                RecordOpen(lease);
                PathsQuestion rest = question with { Columns = [.. left] };
                ObjectPart part = PartOf(ColumnsOf(lease, held.Entry), _mask);
                if (part.Fate == FilterFate.None)
                {
                    continue;
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
    }

    /// <summary>
    /// Tallies from their path directories, read without opening the object, the columns of <paramref name="left"/> an object
    /// the count settles answers for: every row of its part kept, its deleted rows aside. Those it answers for leave
    /// <paramref name="left"/>; whether none is left.
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
            if (field >= 0 && schema.GetField(field).Kind == DTypeKind.Variant
                && await _dataset.TallyPathDirectoryAsync(entry, field, left[c].Tally, judge, question.Exact, cancellationToken).ConfigureAwait(false))
            {
                left.RemoveAt(c);
            }
        }

        return left.Count == 0;
    }
}
