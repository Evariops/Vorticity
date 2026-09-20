// PHASE1-CONTRACTS.md §2.3 and §11.2. This is ONE of exactly three places in the library that may
// throw VortexUnsupportedException, and the only one that may throw it with kind "layout".
//
// Registration is AOT- and trim-safe: the static constructor news up every reader explicitly, by
// name, in one place. No reflection, no assembly scanning, no module initializer.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Vorticity.Arrays;

namespace Vorticity.Layouts;

/// <summary>Maps a resolved <see cref="LayoutEncodingId"/> to its reader.</summary>
public static class LayoutReaderTable
{
    private static readonly LayoutReader?[] Readers =
        new LayoutReader?[EncodingRegistry.MaxLayoutEncodingId + 1];

    static LayoutReaderTable()
    {
        Register(FlatLayoutReader.Instance);
        Register(ChunkedLayoutReader.Instance);
        Register(StructLayoutReader.Instance);
        Register(DictLayoutReader.Instance);
        Register(ZonedLayoutReader.Instance);
        Register(StatsLayoutReader.Instance);
        Register(ListLayoutReader.Instance);
    }

    /// <summary>
    /// The reader for <paramref name="id"/>. <b>The only place a
    /// <see cref="VortexUnsupportedException"/> with kind <c>"layout"</c> is thrown</b> (contract §2.3).
    /// </summary>
    /// <param name="id">The resolved id; <see cref="LayoutEncodingId.Unknown"/> always throws.</param>
    /// <param name="idText">The id exactly as the file spells it, for the exception message.</param>
    /// <returns>The shared, stateless reader.</returns>
    /// <exception cref="VortexUnsupportedException">This build does not read that layout.</exception>
    public static LayoutReader Get(LayoutEncodingId id, string idText)
    {
        LayoutReader? reader = Lookup(id);
        return reader ?? ThrowUnsupported(idText);
    }

    /// <summary><see langword="true"/> when this build has a reader for <paramref name="id"/>.</summary>
    /// <param name="id">The resolved id.</param>
    public static bool IsImplemented(LayoutEncodingId id) => Lookup(id) is not null;

    /// <summary>
    /// The reader for <paramref name="node"/>, without materializing its id text on the success
    /// path.
    /// </summary>
    /// <remarks>
    /// <see cref="LayoutNode.EncodingIdText"/> allocates a string, and the contract's
    /// <see cref="Get(LayoutEncodingId, string)"/> takes one by value — so calling it per node per
    /// batch would allocate on a decode path, which §1.3 forbids. The failing case still goes
    /// through <c>Get</c>, so the throw site is unchanged.
    /// </remarks>
    internal static LayoutReader Require(in LayoutNode node)
    {
        LayoutReader? reader = Lookup(node.Encoding);
        return reader ?? Get(node.Encoding, node.EncodingIdText);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LayoutReader? Lookup(LayoutEncodingId id)
    {
        LayoutReader?[] readers = Readers;
        return (uint)id < (uint)readers.Length ? readers[(int)id] : null;
    }

    /// <summary>
    /// Installs <paramref name="reader"/> in its declared slot. Called only from this type's static
    /// constructor, so it needs no synchronization and none is provided.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The reader's slot is <see cref="LayoutEncodingId.Unknown"/> or out of range, its
    /// <see cref="LayoutReader.IdUtf8"/> does not resolve back to that slot, or the slot is taken.
    /// All three are build-time mistakes, not file problems.
    /// </exception>
    private static void Register(LayoutReader reader)
    {
        LayoutEncodingId id = reader.EncodingId;
        if (id == LayoutEncodingId.Unknown || (uint)id >= (uint)Readers.Length)
        {
            throw new ArgumentException(
                $"Reader {reader.GetType().Name} declares layout id {(ushort)id}, which is not a " +
                "registrable slot.",
                nameof(reader));
        }

        // Catches the transposition that would otherwise be silent: a reader whose IdUtf8 and
        // EncodingId disagree would read the wrong layout with a plausible-looking error.
        if (EncodingRegistry.ResolveLayout(reader.IdUtf8) != id)
        {
            throw new ArgumentException(
                $"Reader {reader.GetType().Name} declares EncodingId {id} but an IdUtf8 that " +
                "resolves elsewhere.",
                nameof(reader));
        }

        if (Readers[(int)id] is not null)
        {
            throw new ArgumentException($"A reader for {id} is already registered.", nameof(reader));
        }

        Readers[(int)id] = reader;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static LayoutReader ThrowUnsupported(string idText)
    {
        throw new VortexUnsupportedException(idText ?? "<unnamed>", VortexComponentKind.Layout);
    }
}
