using System;
using System.Collections.Generic;
using Vorticity.Aggregating;

namespace Vorticity.Indexes;

/// <summary>
/// Counts kept by bucket (<see cref="ColumnCounts.Buckets"/>): the counts of each bucket put together, a group a value with
/// an entry for each bucket it has rows in, and a bucket's taken apart again. A rollup keeps its objects' counts so, a
/// bucket a clustering value, so that a reader of one value finds its rows in every bucket in the chunk that holds it.
/// </summary>
internal static class CountsBuckets
{
    /// <summary>
    /// The counts of <paramref name="buckets"/>, each a bucket's in its order or null for a bucket with none, as one kept by
    /// bucket: the measures every bucket carries, sketched when every bucket sketches them, and the wide columns every one
    /// attaches. Null when no bucket has counts. A value one bucket leaves out among its values left out has no entry of
    /// that bucket: its entry of the values left out may hold it.
    /// </summary>
    internal static (ColumnCounts Counts, ColumnSketches? Sketches)? Together(IReadOnlyList<(ColumnCounts Counts, ColumnSketches? Sketches)?> buckets)
    {
        // What every bucket carries: the measures, in the first one's order; whether each sketches; the wide columns.
        int[]? fields = null;
        MeasureKind[] kinds = [];
        bool sketched = true;
        int[]? attached = null;
        foreach ((ColumnCounts counts, ColumnSketches? sketches)? held in buckets)
        {
            if (held is not ({ } counts, var sketches))
            {
                continue;
            }

            if (fields is null)
            {
                (fields, kinds) = ((int[])counts.MeasureFields.Clone(), (MeasureKind[])counts.Kinds.Clone());
            }
            else
            {
                List<int> kept = [];
                List<MeasureKind> keptKinds = [];
                for (int m = 0; m < fields.Length; m++)
                {
                    int place = Array.IndexOf(counts.MeasureFields, fields[m]);
                    if (place >= 0 && counts.Kinds[place] == kinds[m])
                    {
                        kept.Add(fields[m]);
                        keptKinds.Add(kinds[m]);
                    }
                }

                (fields, kinds) = ([.. kept], [.. keptKinds]);
            }

            sketched &= counts.Sketched && sketches is { } held2 && held2.Entries == counts.Entries && held2.Measures == counts.MeasureFields.Length;
            int[] registers = sketches is { } distinct && distinct.Entries == counts.Entries && distinct.Attached == counts.AttachedFields.Length
                ? counts.AttachedFields
                : [];
            attached = attached is null ? registers : Array.FindAll(attached, field => Array.IndexOf(registers, field) >= 0);
        }

        if (fields is null)
        {
            return null;
        }

        // Each group's entries, bucket by bucket: the bucket and its entry there.
        List<(int Bucket, int Entry)> nulls = [];
        List<(int Bucket, int Entry)> others = [];
        SortedDictionary<byte[], List<(int Bucket, int Entry)>> values = new SortedDictionary<byte[], List<(int Bucket, int Entry)>>(Ordinal.Instance);
        for (int b = 0; b < buckets.Count; b++)
        {
            if (buckets[b] is not ({ } counts, _))
            {
                continue;
            }

            foreach (CountGroup group in counts.Groups)
            {
                List<(int Bucket, int Entry)> into = group.Kind switch
                {
                    CountedValue.Null => nulls,
                    CountedValue.Others => others,
                    _ => values.TryGetValue(group.Value!, out List<(int Bucket, int Entry)>? held) ? held : values[group.Value!] = [],
                };
                for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
                {
                    into.Add((b, e));
                }
            }
        }

        int width = StateWords.Count(kinds);
        int entries = nulls.Count + others.Count;
        foreach (List<(int Bucket, int Entry)> list in values.Values)
        {
            entries += list.Count;
        }

        CountGroup[] groups = new CountGroup[(nulls.Count > 0 ? 1 : 0) + (others.Count > 0 ? 1 : 0) + values.Count];
        long[] words = new long[entries * width];
        int[] entryBuckets = new int[entries];
        byte[]?[]? sketchBytes = sketched && fields.Length > 0 ? new byte[]?[entries * fields.Length] : null;
        byte[]?[]? registerBytes = attached is { Length: > 0 } ? new byte[]?[entries * attached.Length] : null;
        int next = 0;
        int g = 0;
        void Place(CountedValue kind, byte[]? value, List<(int Bucket, int Entry)> list)
        {
            groups[g++] = new CountGroup(kind, value, next, list.Count);
            foreach ((int bucket, int entry) in list)
            {
                (ColumnCounts counts, ColumnSketches? sketches) = buckets[bucket]!.Value;
                Project(counts, entry, fields, kinds, words.AsSpan(next * width, width));
                for (int m = 0; sketchBytes is not null && m < fields.Length; m++)
                {
                    sketchBytes[(next * fields.Length) + m] = sketches!.SketchOf(entry, Array.IndexOf(counts.MeasureFields, fields[m]));
                }

                for (int a = 0; registerBytes is not null && a < attached!.Length; a++)
                {
                    registerBytes[(next * attached.Length) + a] = sketches!.DistinctOf(entry, Array.IndexOf(counts.AttachedFields, attached[a]));
                }

                entryBuckets[next++] = bucket;
            }
        }

        if (nulls.Count > 0)
        {
            Place(CountedValue.Null, null, nulls);
        }

        if (others.Count > 0)
        {
            Place(CountedValue.Others, null, others);
        }

        foreach ((byte[] value, List<(int Bucket, int Entry)> list) in values)
        {
            Place(CountedValue.Value, value, list);
        }

        ColumnCounts together = new ColumnCounts(fields, kinds, groups, words)
        {
            Sketched = sketchBytes is not null,
            AttachedFields = registerBytes is null ? [] : attached!,
            Buckets = buckets.Count,
            EntryBuckets = entryBuckets,
        };
        return (together, sketchBytes is null && registerBytes is null
            ? null
            : new ColumnSketches(entries, fields.Length, sketchBytes, attached?.Length ?? 0, registerBytes));
    }

    /// <summary>
    /// The counts of bucket <paramref name="bucket"/> of <paramref name="counts"/>, kept by bucket, with
    /// <paramref name="sketches"/> their sketches, as an object's: a group a value that has rows in the bucket. Null when
    /// no value has.
    /// </summary>
    internal static (ColumnCounts Counts, ColumnSketches? Sketches)? Apart(ColumnCounts counts, ColumnSketches? sketches, int bucket)
    {
        int width = counts.WordsPerEntry;
        List<CountGroup> groups = [];
        List<int> taken = [];
        foreach (CountGroup group in counts.Groups)
        {
            for (int e = group.FirstEntry; e < group.FirstEntry + group.Entries; e++)
            {
                if (counts.EntryBuckets![e] == bucket)
                {
                    groups.Add(new CountGroup(group.Kind, group.Value, taken.Count, 1));
                    taken.Add(e);
                    break;
                }
            }
        }

        if (taken.Count == 0)
        {
            return null;
        }

        long[] words = new long[taken.Count * width];
        int measures = sketches?.Measures ?? 0;
        int attached = sketches?.Attached ?? 0;
        byte[]?[]? sketchBytes = measures > 0 ? new byte[]?[taken.Count * measures] : null;
        byte[]?[]? registerBytes = attached > 0 ? new byte[]?[taken.Count * attached] : null;
        for (int i = 0; i < taken.Count; i++)
        {
            counts.WordsOf(taken[i]).CopyTo(words.AsSpan(i * width, width));
            for (int m = 0; sketchBytes is not null && m < measures; m++)
            {
                sketchBytes[(i * measures) + m] = sketches!.SketchOf(taken[i], m);
            }

            for (int a = 0; registerBytes is not null && a < attached; a++)
            {
                registerBytes[(i * attached) + a] = sketches!.DistinctOf(taken[i], a);
            }
        }

        ColumnCounts apart = new ColumnCounts(counts.MeasureFields, counts.Kinds, [.. groups], words)
        {
            Sketched = counts.Sketched && sketchBytes is not null,
            AttachedFields = registerBytes is null ? [] : counts.AttachedFields,
        };
        return (apart, sketchBytes is null && registerBytes is null ? null : new ColumnSketches(taken.Count, measures, sketchBytes, attached, registerBytes));
    }

    /// <summary>Writes into <paramref name="into"/> the words of entry <paramref name="entry"/> of <paramref name="counts"/> over the measures <paramref name="fields"/>.</summary>
    private static void Project(ColumnCounts counts, int entry, int[] fields, MeasureKind[] kinds, Span<long> into)
    {
        ReadOnlySpan<long> words = counts.WordsOf(entry);
        into[0] = words[0];
        for (int m = 0, at = 1; m < fields.Length; m++)
        {
            int place = Array.IndexOf(counts.MeasureFields, fields[m]);
            int offset = 1;
            for (int p = 0; p < place; p++)
            {
                offset += MeasureLayout.Words(counts.Kinds[p] == MeasureKind.Float);
            }

            int length = MeasureLayout.Words(kinds[m] == MeasureKind.Float);
            words.Slice(offset, length).CopyTo(into.Slice(at, length));
            at += length;
        }
    }

    /// <summary>The order of values' bytes, a group's.</summary>
    private sealed class Ordinal : IComparer<byte[]>
    {
        internal static readonly Ordinal Instance = new Ordinal();

        public int Compare(byte[]? left, byte[]? right) => left.AsSpan().SequenceCompareTo(right);
    }
}
