// The footer's array_specs / layout_specs dictionaries, built as the writer goes.
//
// A component id appears once in the file and every node refers to it by a u16 index
// (docs/02-format.md §3). Interning them here is what makes a fifty-column file carry
// "vortex.primitive" once rather than fifty times -- and the reason the reader's own encoding table
// is an index lookup rather than a string compare.
//
// Insertion order IS the index order, and nothing re-sorts: a node's u16 is handed out the first
// time its id is seen and stays valid for the rest of the write.
//
// It is also where THE TARGET EDITION IS ENFORCED, because it is the one place every array and
// every layout id in the file passes through exactly once. docs/90-registry.md asks for a per-kind
// allowlist that "fails the write when a serializer produces an ID outside it"; putting it here
// rather than at each call site is what makes that an invariant instead of a habit - a new
// serializer cannot forget to ask.
using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Editions;

namespace Vorticity.Writing;

/// <summary>Interns component ids into the u16 indices a file's nodes refer to.</summary>
internal sealed class EncodingDictionary
{
    private readonly Dictionary<string, ushort> _indices = new Dictionary<string, ushort>(StringComparer.Ordinal);
    private readonly List<string> _ids = [];
    private readonly ComponentKind _kind;
    private readonly VortexEdition _target;

    /// <summary>A dictionary for one namespace, checked against one edition.</summary>
    /// <param name="kind">Which namespace these ids belong to.</param>
    /// <param name="target">The edition every id must belong to.</param>
    internal EncodingDictionary(ComponentKind kind, VortexEdition target)
    {
        _kind = kind;
        _target = target;
    }

    /// <summary>The edition every id in this dictionary must belong to.</summary>
    internal VortexEdition Target => _target;


    /// <summary>The ids, in index order, for the footer's spec vector.</summary>
    internal IReadOnlyList<string> Ids => _ids;

    /// <summary>How many distinct ids the file uses.</summary>
    internal int Count => _ids.Count;

    /// <summary>The index of <paramref name="idUtf8"/>, assigning one if it is new.</summary>
    /// <param name="idUtf8">The component id, in its wire form.</param>
    /// <returns>The index this file refers to the id by.</returns>
    /// <remarks>
    /// <para>
    /// PERF-AUDIT-v2.md W-1. Every array node is written from a `u8` literal, so this is called
    /// once per NODE while the set of distinct ids in a file is a dozen or two. Decoding the span to
    /// a `string` in order to look it up allocated one string per node and threw it away on the very
    /// next line, every time but the first. Here the string is built ONLY on a genuine miss.
    /// </para>
    /// <para>
    /// AGAINST THE STRINGS WE ALREADY KEEP, and not against a parallel table of their UTF-8 forms.
    /// The parallel table was written first and measured WORSE on nine files of ten: it trades one
    /// string per node for one `byte[]` per distinct id plus a growing `List`, which on a file of a
    /// few nodes costs more than it saves (`encodings/zstd` +648 B). Comparing straight against the
    /// `string` costs nothing at all because <see cref="Ascii.Equals(ReadOnlySpan{byte},
    /// ReadOnlySpan{char})"/> reads both sides in place.
    /// </para>
    /// <para>
    /// ASCII IS NOT AN ASSUMPTION ABOUT THE DATA, it is a fast path: an id with a byte above 0x7F on
    /// either side simply does not match here and falls through to the `GetString` route, which
    /// compares it properly. Every component id in the registry is ASCII, so the slow route is
    /// reached once per distinct id and never in a loop.
    /// </para>
    /// <para>
    /// A LINEAR SCAN AND NOT A HASH, because the table is the size it is: a file names a couple of
    /// dozen distinct ids, and a hash of the span would have to read every byte of it before doing
    /// the same comparison anyway. The string dictionary stays for the `string` overload, which the
    /// layout ids use and which is called a handful of times per file.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The file uses more ids than a u16 can index.</exception>
    internal ushort Intern(ReadOnlySpan<byte> idUtf8)
    {
        for (int i = 0; i < _ids.Count; i++)
        {
            if (Ascii.Equals(idUtf8, _ids[i]))
            {
                return (ushort)i;
            }
        }

        return Intern(Encoding.UTF8.GetString(idUtf8));
    }

    /// <summary>
    /// Takes over an existing file's ids at their indices, for an append (docs/11 §3.8): its
    /// segments name encodings by index, and the file they stay in must keep the indices.
    /// </summary>
    /// <param name="ids">The file's ids, in index order.</param>
    /// <remarks>Not checked against the target: the file already carries them.</remarks>
    internal void Seed(IReadOnlyList<string> ids)
    {
        if (_ids.Count > 0)
        {
            throw new InvalidOperationException("A dictionary is seeded before its first id.");
        }

        foreach (string id in ids)
        {
            _indices.TryAdd(id, (ushort)_ids.Count);
            _ids.Add(id);
        }
    }

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

        RequireInTarget(id);

        index = (ushort)_ids.Count;
        _ids.Add(id);
        _indices.Add(id, index);
        return index;
    }

    /// <summary>
    /// Fails the write rather than producing a file the target's readers cannot open.
    /// </summary>
    /// <remarks>
    /// The message carries the edition that DOES introduce the id, which is the single most useful
    /// thing to know: it turns "this does not work" into "raise your target to this".
    /// </remarks>
    private void RequireInTarget(string id)
    {
        if (EditionRegistry.Contains(_target, _kind, id))
        {
            return;
        }

        VortexEdition? introduced = EditionRegistry.IntroducedIn(_kind, id);
        string kind = _kind switch
        {
            ComponentKind.Array => VortexComponentKind.Array,
            ComponentKind.Layout => VortexComponentKind.Layout,
            ComponentKind.DType => VortexComponentKind.DType,
            _ => VortexComponentKind.Aggregate,
        };

        throw new VortexUnsupportedException(
            id,
            kind,
            introduced is null
                ? $"No core edition contains it, so no target can emit it."
                : $"The write targets edition {EditionRegistry.Name(_target)}, which does not contain it; " +
                  $"it was introduced in {EditionRegistry.Name(introduced.Value)} " +
                  $"(minimum library version {EditionRegistry.MinimumLibraryVersion(introduced.Value)}).");
    }
}
