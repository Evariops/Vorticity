// The footer's array_specs / layout_specs dictionaries, built as the writer goes.
//
// A component id appears once in the file and every node refers to it by a u16 index
// (docs/02-format.md §3). Interning them here is what makes a fifty-column file carry
// "vortex.primitive" once rather than fifty times -- and the reason the reader's own encoding table
// is an index lookup rather than a string compare.
//
// Insertion order IS the index order, and nothing re-sorts: a node's u16 is handed out the first
// time its id is seen and stays valid for the rest of the write.
using System;
using System.Collections.Generic;
using System.Text;

namespace Vorticity.Writing;

/// <summary>Interns component ids into the u16 indices a file's nodes refer to.</summary>
internal sealed class EncodingDictionary
{
    private readonly Dictionary<string, ushort> _indices = new Dictionary<string, ushort>(StringComparer.Ordinal);
    private readonly List<string> _ids = [];

    /// <summary>The ids, in index order, for the footer's spec vector.</summary>
    internal IReadOnlyList<string> Ids => _ids;

    /// <summary>How many distinct ids the file uses.</summary>
    internal int Count => _ids.Count;

    /// <summary>The index of <paramref name="idUtf8"/>, assigning one if it is new.</summary>
    /// <param name="idUtf8">The component id.</param>
    /// <exception cref="InvalidOperationException">The file uses more ids than a u16 can index.</exception>
    internal ushort Intern(ReadOnlySpan<byte> idUtf8) => Intern(Encoding.UTF8.GetString(idUtf8));

    /// <summary>The index of <paramref name="id"/>, assigning one if it is new.</summary>
    /// <param name="id">The component id.</param>
    /// <exception cref="InvalidOperationException">The file uses more ids than a u16 can index.</exception>
    internal ushort Intern(string id)
    {
        if (_indices.TryGetValue(id, out ushort index))
        {
            return index;
        }

        if (_ids.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                "A file cannot name more than 65536 distinct component ids.");
        }

        index = (ushort)_ids.Count;
        _ids.Add(id);
        _indices.Add(id, index);
        return index;
    }
}
