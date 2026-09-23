using System;
using System.Collections.Generic;
using System.Text;
using Vorticity.Editions;

namespace Vorticity.Writing;

/// <summary>Interns component ids into the u16 indices a file's nodes refer to.</summary>
/// <remarks>
/// A file names a couple of dozen distinct ids at most, so the ids are looked up in order rather
/// than hashed, and the list starts at that size.
/// </remarks>
internal sealed class EncodingDictionary
{
    private readonly List<string> _ids = new List<string>(16);
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
    /// distinct ids. On a miss the id is the registry's own string when the registry knows it, and
    /// a new string only when it does not. The ASCII comparison is a fast path rather than an
    /// assumption: an id with a byte above 0x7F falls through to the decoding route, which
    /// compares it properly.
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

        return EditionRegistry.TryGetId(_kind, idUtf8, out string? known)
            ? Intern(known)
            : Intern(Encoding.UTF8.GetString(idUtf8));
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
            _ids.Add(id);
        }
    }

    /// <summary>The index of <paramref name="id"/>, assigning one if it is new.</summary>
    /// <remarks>An id the file names twice, which a seed can bring, is found at its first index.</remarks>
    internal ushort Intern(string id)
    {
        for (int i = 0; i < _ids.Count; i++)
        {
            if (string.Equals(_ids[i], id, StringComparison.Ordinal))
            {
                return (ushort)i;
            }
        }

        if (_ids.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                "A file cannot name more than 65536 distinct component ids.");
        }

        RequireInTarget(id);

        ushort index = (ushort)_ids.Count;
        _ids.Add(id);
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
        throw new VortexUnsupportedException(
            id,
            _kind,
            introduced is null
                ? $"No core edition contains it, so no target can emit it."
                : $"The write targets edition {EditionRegistry.Name(_target)}, which does not contain it; " +
                  $"it was introduced in {EditionRegistry.Name(introduced.Value)} " +
                  $"(minimum library version {EditionRegistry.MinimumLibraryVersion(introduced.Value)}).");
    }
}
