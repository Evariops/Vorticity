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
