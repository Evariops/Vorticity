using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Indexes;
using Vorticity.Layouts;

namespace Vorticity.Types.Variants;

/// <summary>
/// The top-level keys of a variant column that a census of a part of its rows reads, as its path directory names them in the
/// groups of rows the part lies in: a shredded key it does not name is not decoded (<see cref="Arrays.ScanContext.ApartKeys"/>),
/// since no row of those groups holds a value of it.
/// </summary>
/// <remarks>
/// The edge granules of a window of one source's logs, read again for their paths, decoded each of the some hundred and
/// sixty keys their chunks shred, a run-end node and its dictionary each, where the source sends some sixty: the keys'
/// decodes were two thirds of the question.
/// </remarks>
internal sealed class ApartKeys
{
    private const int StackChars = 128;

    private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    internal ApartKeys()
    {
        _lookup = _keys.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The keys named.</summary>
    internal int Count => _keys.Count;

    /// <summary>
    /// Adds the top-level keys of the paths <paramref name="body"/> names in a group <paramref name="verdicts"/> judged kept in
    /// part, or in every group, for a path that names none.
    /// </summary>
    internal void AddEdges(VariantDirectory.DirectoryBody body, GroupVerdicts verdicts)
    {
        int[] parts = new int[verdicts.PartGroups];
        int found = 0;
        for (int g = 0; g < verdicts.Groups && found < parts.Length; g++)
        {
            if (verdicts.IsPart(g))
            {
                parts[found++] = g;
            }
        }

        Edges edges = new Edges(this, parts.AsMemory(0, found));
        body.Visit(ref edges);
    }

    /// <summary>Names <paramref name="key"/>, in UTF-8.</summary>
    internal void Add(ReadOnlySpan<byte> key)
    {
        Span<char> chars = key.Length <= StackChars ? stackalloc char[StackChars] : new char[key.Length];
        chars = chars[..Encoding.UTF8.GetChars(key, chars)];
        if (!_lookup.Contains(chars))
        {
            _keys.Add(new string(chars));
        }
    }

    /// <summary>Whether <paramref name="key"/>, in UTF-8, is named.</summary>
    internal bool Holds(ReadOnlySpan<byte> key)
    {
        Span<char> chars = key.Length <= StackChars ? stackalloc char[StackChars] : new char[key.Length];
        return _lookup.Contains(chars[..Encoding.UTF8.GetChars(key, chars)]);
    }

    /// <summary>The fields of the struct <paramref name="shredded"/>, its keys, that are named; <see cref="FieldMask.All"/> when every one is.</summary>
    internal FieldMask MaskOf(DType shredded)
    {
        int fieldCount = shredded.FieldCount;
        int[] fields = new int[fieldCount];
        int named = 0;
        for (int f = 0; f < fieldCount; f++)
        {
            if (Holds(shredded.GetFieldNameUtf8(f)))
            {
                fields[named++] = f;
            }
        }

        return named == fieldCount ? FieldMask.All
            : named == 0 ? FieldMask.Empty
            : FieldMask.Subset(new FieldMaskNode(fields[..named], null));
    }

    /// <summary>The visit that names the keys of the paths a directory places in the groups kept in part.</summary>
    private readonly struct Edges(ApartKeys keys, ReadOnlyMemory<int> parts) : IDirectoryVisitor
    {
        public void Head(long rows, long groupRows, bool partial)
        {
        }

        public void Entry(in DirectoryEntry entry)
        {
            if (entry.Depth == 0 || !InPart(in entry))
            {
                return;
            }

            keys.Add(entry.Key(0));
        }

        private bool InPart(in DirectoryEntry entry)
        {
            if (!entry.HasGroups)
            {
                return true;
            }

            ReadOnlySpan<byte> groups = entry.Groups;
            foreach (int g in parts.Span)
            {
                if ((g >> 3) < groups.Length && (groups[g >> 3] & (1 << (g & 7))) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
