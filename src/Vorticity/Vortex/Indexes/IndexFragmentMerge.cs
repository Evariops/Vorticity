using System;
using System.Collections.Generic;

namespace Vorticity.Indexes;

/// <summary>
/// Adds index fragments to the directory a file names, so that pruners and key sources read one
/// directory however it was assembled. Where an entry goes is decided by its identity -- its kind,
/// its column, its block length and its options byte for byte. The file's own entry of an identity
/// already covers everything the file holds, so a fragment repeating it adds nothing; the same
/// identity across fragments is one index over several block ranges and its runs join when their
/// blocks are disjoint; an overlap is left out, since a key walk over both would meet the same keys
/// twice. Nothing here fails the open: a fragment or an entry that does not fit is left out with
/// its reason, and the scan runs without it.
/// </summary>
internal static class IndexFragmentMerge
{
    /// <summary>The directory the file names, with every fragment's entries added to it.</summary>
    /// <param name="named">The file's own directory; null when it has none.</param>
    /// <param name="fragments">
    /// Each fragment's directory, already bound to the file; null for one refused whole. Fragment
    /// <c>i</c> is origin <c>i + 1</c>.
    /// </param>
    /// <param name="refusals">Why each fragment was refused, extended here with the entries left out.</param>
    /// <param name="rowCount">The file's rows.</param>
    /// <param name="describe">How to name an entry in a refusal.</param>
    /// <returns>The directory, or null when neither the file nor a fragment brought one.</returns>
    internal static IndexDirectory? Merge(
        IndexDirectory? named,
        IReadOnlyList<IndexDirectory?> fragments,
        string?[] refusals,
        ulong rowCount,
        Func<IndexEntry, string> describe)
    {
        List<IndexEntry> entries = named is null ? [] : [.. named.Entries];
        int own = entries.Count;
        IndexDirectory? first = null;
        for (int i = 0; i < fragments.Count; i++)
        {
            if (fragments[i] is not { } fragment)
            {
                continue;
            }

            first ??= fragment;
            foreach (IndexEntry entry in fragment.Entries)
            {
                IndexEntry tagged = entry with { Runs = Tagged(entry.Runs, i + 1) };
                int same = IndexOf(entries, tagged);
                if (same < 0)
                {
                    entries.Add(tagged);
                }
                else if (same < own)
                {
                    Refuse(refusals, i, $"its {describe(entry)} entry is the file's own already");
                }
                else if (Joined(entries[same], tagged) is { } joined)
                {
                    entries[same] = joined;
                }
                else
                {
                    Refuse(refusals, i, $"its {describe(entry)} entry covers blocks another fragment covers");
                }
            }
        }

        if (named is not null)
        {
            return named with { Entries = entries };
        }

        // No directory of the file's own: the fragments' are the whole of it, and the first one's
        // policy stands for the file's, which nothing but an append reads.
        return first is null ? null : new IndexDirectory(rowCount, 0, first.Policy, entries);
    }

    /// <summary>The runs, marked as read from <paramref name="origin"/>.</summary>
    private static IndexRun[] Tagged(IReadOnlyList<IndexRun> runs, int origin)
    {
        IndexRun[] tagged = new IndexRun[runs.Count];
        for (int i = 0; i < tagged.Length; i++)
        {
            tagged[i] = runs[i] with { Origin = origin };
        }

        return tagged;
    }

    /// <summary>Where an entry of the same identity already is, or -1.</summary>
    private static int IndexOf(List<IndexEntry> entries, IndexEntry wanted)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            IndexEntry entry = entries[i];
            if (string.Equals(entry.Kind, wanted.Kind, StringComparison.Ordinal)
                && entry.BlockLength == wanted.BlockLength
                && Same(entry.ColumnPath, wanted.ColumnPath)
                && entry.Options.AsSpan().SequenceEqual(wanted.Options))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Same(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One entry holding both entries' runs in block order, or null when their blocks meet.</summary>
    private static IndexEntry? Joined(IndexEntry held, IndexEntry added)
    {
        List<IndexRun> runs = [.. held.Runs, .. added.Runs];
        runs.Sort(static (left, right) => left.FirstBlock.CompareTo(right.FirstBlock));
        for (int i = 1; i < runs.Count; i++)
        {
            if (runs[i].FirstBlock < runs[i - 1].EndBlock)
            {
                return null;
            }
        }

        return held with { Runs = runs };
    }

    private static void Refuse(string?[] refusals, int fragment, string reason) =>
        refusals[fragment] = refusals[fragment] is { } earlier ? earlier + "; " + reason : reason;
}
