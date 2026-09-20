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

    internal EncodingDictionary(ComponentKind kind, VortexEdition target)
    {
        _kind = kind;
        _target = target;
    }

    /// <summary>The edition every id in this dictionary must belong to.</summary>
    internal VortexEdition Target => _target;


    /// <summary>The ids, in index order, for the footer's spec vector.</summary>
    internal IReadOnlyList<string> Ids => _ids;

    internal int Count => _ids.Count;

    /// <summary>
    /// The index of <paramref name="idUtf8"/>, assigning one if it is new. The scan is linear and
    /// compares against the strings already kept, because a file names only a couple of dozen
    /// distinct ids and a string is then built only on a genuine miss. The ASCII comparison is a
    /// fast path rather than an assumption: an id with a byte above 0x7F falls through to the
    /// decoding route, which compares it properly.
    /// </summary>
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
    /// Takes over an existing file's ids at their indices, for an append: its segments name
    /// encodings by index, so the file has to keep them. The ids are not checked against the
    /// target, since the file already carries them.
    /// </summary>
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

    /// <summary>Fails the write rather than producing a file the target's readers cannot open.</summary>
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
