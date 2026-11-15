using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// A consolidation too large for one commit, run a piece at a time: each job takes the keys of its partition below a cut,
/// writes them to level 1, and commits, the rows it took marked in the inputs of level 0 it leaves there, as a delete
/// marks rows. The next job's plan finds the inputs' rows past the cut, and consolidates the next piece: a failure loses a
/// piece at most, and a piece's objects go into level 1 as soon as they are made.
/// </summary>
/// <remarks>
/// <para>
/// A piece reads about the layout's object size (<see cref="CompactionJob.TargetBytes"/>), its cut the first key of a
/// granule of its inputs' sparse indexes past that share of their bytes, moved up to the next object of level 1 that
/// starts at or past it, so that no object of level 1 lies across the cut: its objects there are inputs whole, and the
/// level stays disjoint by key. An input of level 0 reads its rows from past the run its earlier pieces marked, up to the
/// granule that reaches the cut; the merge stops each at the cut exactly, and counts what it took of each.
/// </para>
/// <para>
/// A consolidation runs in one piece when it reads no more than an object's size, when an input keeps no sparse index of
/// its keys, or when one carries the counts of rows a delete marked, which the marks of a piece would leave behind.
/// </para>
/// </remarks>
internal static partial class DatasetCompactor
{
    /// <summary>
    /// A piece of a consolidation: its job, the inputs it reads and the rows of each, below its cut, and what the merge
    /// took of each.
    /// </summary>
    private sealed class Piece(CompactionJob job, KeyRanges ranges)
    {
        /// <summary>The piece's job: the inputs of level 0 it reads rows of, and those of level 1 it takes whole.</summary>
        internal CompactionJob Job => job;

        /// <summary>The rows of each input the piece reads, its first range, below the cut; its second, past it, unread.</summary>
        internal KeyRanges Ranges => ranges;

        /// <summary>The rows the merge took of each input, by its place in the piece's job.</summary>
        internal long[] Emitted { get; } = new long[job.Inputs.Count];
    }

    /// <summary>
    /// The first piece of <paramref name="job"/>, a consolidation that reads more than an object's size; null when it runs in
    /// one piece.
    /// </summary>
    private static async ValueTask<Piece?> PieceAsync(VortexDataset dataset, DatasetSchema schema, CompactionJob job, CancellationToken cancellationToken)
    {
        // An object's size, or less, so that a piece commits within its share of the window and its copies fit the scratch.
        long pieceBytes = Math.Max(1, Math.Min(job.TargetBytes, JobBytesBound(dataset)));
        if (job.Trigger != CompactionTrigger.Consolidation || job.Moves || !schema.Keyed || job.Bytes <= pieceBytes
            || dataset.ConsolidatesWhole(CompactionPolicy.PartitionOf(dataset, job)))
        {
            return null;
        }

        byte[] partition = schema.Partitioned ? PartitionOf(schema, job.Inputs[0]) : [];
        GranuleKeys?[] indexes = new GranuleKeys?[job.Inputs.Count];
        try
        {
            for (int i = 0; i < indexes.Length; i++)
            {
                CompactionInput input = job.Inputs[i];
                if (input.Entry.HasNegatives
                    || (schema.Partitioned && !VortexDataset.OrderOf(input.Key).Span.StartsWith(partition))
                    || input.Entry.Contents is not { KeyIndex: not null } contents
                    || await dataset.KeyIndexAsync(input.Entry, contents, cancellationToken).ConfigureAwait(false) is not { } keys)
                {
                    return null;
                }

                indexes[i] = keys;
            }

            int[] widths = schema.Tree is { } tree ? tree.Widths[(schema.Partitioned ? 1 : 0)..].ToArray() : [];
            GranuleKeys.DecodedKeys[] decoded = new GranuleKeys.DecodedKeys[indexes.Length];
            for (int i = 0; i < indexes.Length; i++)
            {
                decoded[i] = RowsOf(indexes[i]!, Widths(widths, indexes[i]!));
            }

            // Where the rows level 0 still holds start, about: the first key of the granule each input's earlier pieces stopped
            // in. The objects of level 1 the earlier pieces wrote end below it, and stay out of this one, which would rewrite
            // them every piece.
            byte[]? low = null;
            bool marked = false;
            for (int i = 0; i < indexes.Length; i++)
            {
                CompactionInput input = job.Inputs[i];
                GranuleKeys keys = indexes[i]!;
                long from = input.Entry.Deletions.LeadingRun;
                marked |= input.Level == 0 && from > 0;
                if (input.Level >= 1 || from >= keys.Rows)
                {
                    continue;
                }

                byte[] key = Led(partition, decoded[i][(int)(from / keys.GranuleRows)]);
                low = low is null || key.AsSpan().SequenceCompareTo(low) < 0 ? key : low;
            }

            // The cut: the first key past the live granules of an object's size, moved up so that no object of level 1 lies
            // across it; none when what is left fits a piece, which then runs to the partition's end. A consolidation that no
            // piece began, whose keys hold no such cut, runs in one piece.
            byte[]? cut = FirstCut(job, indexes, decoded, partition, low, pieceBytes);
            cut = cut is null ? null : PastLevelOne(job, cut);
            if (cut is null && !marked)
            {
                return null;
            }

            List<CompactionInput> inputs = [];
            List<RowRange[]> rows = [];
            for (int i = 0; i < indexes.Length; i++)
            {
                CompactionInput input = job.Inputs[i];
                if (input.Level >= 1)
                {
                    // An object of level 1 lies below the cut whole, or past it whole; one that ends below what level 0 still
                    // holds is left as it is.
                    bool below = low is not null && !input.Entry.LastKey.IsEmpty && input.Entry.LastKey.Span.SequenceCompareTo(low) < 0;
                    if ((cut is null || FirstKeyOf(input).SequenceCompareTo(cut) < 0) && !below)
                    {
                        inputs.Add(input);
                        rows.Add(Ranges(new RowRange(0, input.Entry.PhysicalRows), cut));
                    }

                    continue;
                }

                // Past the run the earlier pieces marked, to the granule that reaches the cut, or to its end.
                GranuleKeys keys = indexes[i]!;
                long start = input.Entry.Deletions.LeadingRun;
                long end = keys.Rows;
                if (cut is not null)
                {
                    ReadOnlySpan<byte> past = cut.AsSpan(partition.Length);
                    end = EndBefore(keys, keys.Coding == KeyCoding.Rows ? past : UnpaddedKeys.Narrow(past, Widths(widths, keys)));
                }

                if (end > start)
                {
                    inputs.Add(input);
                    rows.Add(Ranges(new RowRange(start, end), cut));
                }
            }

            if (inputs.Count == 0)
            {
                return null;
            }

            return new Piece(
                new CompactionJob(job.FromLevel, job.ToLevel, job.Style, job.Trigger, inputs, job.TargetBytes, job.FirstRow),
                new KeyRanges(cut is null ? [] : [cut], [.. rows]));
        }
        catch (VortexFormatException)
        {
            // A page of an input's index that does not hold the keys its table names: the consolidation runs in one piece.
            return null;
        }
        finally
        {
            foreach (GranuleKeys? keys in indexes)
            {
                keys?.Dispose();
            }
        }
    }

    /// <summary>
    /// <paramref name="cut"/>, or the first key of the first object of level 1 among <paramref name="job"/>'s inputs that starts
    /// past an object of level 1 the cut falls inside; null when that object ends the level, and the piece would be the job.
    /// </summary>
    private static byte[]? PastLevelOne(CompactionJob job, byte[] cut)
    {
        List<CompactionInput> above = [];
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Level >= 1)
            {
                above.Add(input);
            }
        }

        above.Sort(static (left, right) => FirstKeyOf(left).SequenceCompareTo(FirstKeyOf(right)));
        for (int i = 0; i < above.Count; i++)
        {
            CompactionInput input = above[i];
            bool across = FirstKeyOf(input).SequenceCompareTo(cut) < 0
                && (input.Entry.LastKey.IsEmpty || input.Entry.LastKey.Span.SequenceCompareTo(cut) >= 0);
            if (across)
            {
                return i + 1 < above.Count ? FirstKeyOf(above[i + 1]).ToArray() : null;
            }
        }

        return cut;
    }

    /// <summary>The ranges of an input of a piece: <paramref name="read"/> below the cut, and nothing past it; the one range to the end without a cut.</summary>
    private static RowRange[] Ranges(RowRange read, byte[]? cut) => cut is null ? [read] : [read, RowRange.Empty];

    /// <summary><paramref name="key"/>, a key past the partition, led by <paramref name="partition"/>: a tree key's order.</summary>
    private static byte[] Led(byte[] partition, ReadOnlySpan<byte> key)
    {
        byte[] led = new byte[partition.Length + key.Length];
        partition.CopyTo(led, 0);
        key.CopyTo(led.AsSpan(partition.Length));
        return led;
    }

    /// <summary>
    /// The first key of a granule of <paramref name="job"/>'s inputs past those that hold <paramref name="pieceBytes"/> of
    /// what is left: of level 0, the granules from the one each input's earlier pieces stopped in; of level 1, those of the
    /// objects that do not end below <paramref name="low"/>. A cut goes where the key changes, so that no key is cut. Null
    /// when what is left holds less, or no key changes past it.
    /// </summary>
    private static byte[]? FirstCut(
        CompactionJob job, GranuleKeys?[] indexes, GranuleKeys.DecodedKeys[] decoded, byte[] partition, byte[]? low, long pieceBytes)
    {
        List<long> live = [];
        for (int i = 0; i < indexes.Length; i++)
        {
            CompactionInput input = job.Inputs[i];
            GranuleKeys keys = indexes[i]!;
            int from = 0;
            if (input.Level == 0)
            {
                from = (int)Math.Min(keys.Granules, input.Entry.Deletions.LeadingRun / Math.Max(keys.GranuleRows, 1));
            }
            else if (low is not null && !input.Entry.LastKey.IsEmpty && input.Entry.LastKey.Span.SequenceCompareTo(low) < 0)
            {
                continue;
            }

            for (int granule = from; granule < keys.Granules; granule++)
            {
                live.Add(((long)i << 32) | (uint)granule);
            }
        }

        live.Sort(new ByGranuleKey(decoded));
        long before = 0;
        for (int k = 0; k < live.Count; k++)
        {
            int input = (int)(live[k] >> 32);
            ReadOnlySpan<byte> key = decoded[input][(int)live[k]];
            if (before >= pieceBytes && k > 0 && key.SequenceCompareTo(decoded[(int)(live[k - 1] >> 32)][(int)live[k - 1]]) > 0)
            {
                return Led(partition, key);
            }

            before += WeightOf(job, indexes, input);
        }

        return null;
    }

    /// <summary>The rows <paramref name="emitted"/> counts over its inputs.</summary>
    private static long Sum(long[] emitted)
    {
        long sum = 0;
        foreach (long rows in emitted)
        {
            sum += rows;
        }

        return sum;
    }

    /// <summary>The first key of <paramref name="input"/>, its tree key without the uid, the partition leading it.</summary>
    private static ReadOnlySpan<byte> FirstKeyOf(CompactionInput input) => input.Key.Span[..^VortexDataset.UidBytes];

    /// <summary>
    /// What the commit of <paramref name="piece"/> takes out of the dataset and puts back: the objects of level 1 it read,
    /// whole, and those of level 0 it took every row of; the inputs of level 0 it took some rows of, put back at level 0
    /// with those rows marked, the marks they held kept. The inputs it took nothing of stay out of the commit.
    /// </summary>
    /// <exception cref="DatasetIntegrityException">A piece took part of an object of level 1, which it takes whole or not at all.</exception>
    private static (List<(int Level, ReadOnlyMemory<byte> Key)> Consumed, List<(int Level, ReadOnlyMemory<byte> Key, ObjectEntry Entry)> Marked, List<ObjectEntry> Read, List<string> Gone)
        PieceCommit(Piece piece)
    {
        List<(int, ReadOnlyMemory<byte>)> consumed = [];
        List<(int, ReadOnlyMemory<byte>, ObjectEntry)> marked = [];
        List<ObjectEntry> read = [];
        List<string> gone = [];
        for (int i = 0; i < piece.Job.Inputs.Count; i++)
        {
            CompactionInput input = piece.Job.Inputs[i];
            long took = piece.Emitted[i];
            if (input.Level >= 1)
            {
                if (took != input.Entry.Rows)
                {
                    throw new DatasetIntegrityException(
                        $"A piece of a consolidation took {took} of the {input.Entry.Rows} rows of '{input.Entry.Key}' in level 1, which it takes whole: a piece that loses rows is the one failure it must not have.");
                }

                consumed.Add((input.Level, input.Key));
                read.Add(input.Entry);
                gone.Add(input.Entry.Key);
                continue;
            }

            if (took == 0)
            {
                continue;
            }

            // The rows it took are its first live rows from where it was read, the key's order its own: marked through the
            // physical row past the last of them.
            DeletionVector deletions = input.Entry.Deletions;
            long first = piece.Ranges.RowsOf(i, 0).Start;
            long logical = first - deletions.DeletedBefore(first);
            DeletionVector through = deletions.Through(deletions.Physical(logical + took - 1) + 1);
            consumed.Add((input.Level, input.Key));
            read.Add(input.Entry);
            if (through.Count >= input.Entry.PhysicalRows)
            {
                gone.Add(input.Entry.Key);
            }
            else
            {
                marked.Add((input.Level, input.Key, input.Entry.WithDeletions(through)));
            }
        }

        return (consumed, marked, read, gone);
    }
}
