using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Vorticity.Editions;

/// <summary>Membership of the frozen <c>core</c> editions.</summary>
/// <remarks>
/// <para>
/// An edition is a frozen set of component ids with a read-forever guarantee: once published it
/// never changes, so these tables are a transcription and not a cache.
/// </para>
/// <para>
/// Each id is stored against the edition that introduced it rather than in one membership set per
/// edition, because editions within a family are cumulative: a component belongs to every edition
/// from the one that added it onward, so membership is a single integer comparison.
/// </para>
/// <para>
/// The four kinds are separate namespaces and must stay separate. <c>vortex.chunked</c> and
/// <c>vortex.dict</c> are each both an array id and a layout id, introduced independently, so a
/// single table would silently answer the wrong question for them.
/// </para>
/// </remarks>
internal static class EditionRegistry
{
    /// <summary>
    /// The newest edition this build knows about. Reading is not limited to it - an unknown id from
    /// a future edition is a read-time question, answered by the encoding registry.
    /// </summary>
    public const VortexEdition Newest = VortexEdition.Core20260803;

    /// <summary>
    /// The oldest edition carrying a read-forever guarantee, and the floor for the read scope.
    /// </summary>
    public const VortexEdition ReadForeverFloor = VortexEdition.Core20250500;

    private static readonly FrozenDictionary<string, VortexEdition> Arrays =
        new Dictionary<string, VortexEdition>(StringComparer.Ordinal)
        {
        ["fastlanes.bitpacked"] = VortexEdition.Core20250500,
        ["fastlanes.for"] = VortexEdition.Core20250500,
        ["fastlanes.rle"] = VortexEdition.Core20251000,
        ["vortex.alp"] = VortexEdition.Core20250500,
        ["vortex.alprd"] = VortexEdition.Core20250500,
        ["vortex.bool"] = VortexEdition.Core20250500,
        ["vortex.bytebool"] = VortexEdition.Core20250500,
        ["vortex.chunked"] = VortexEdition.Core20250500,
        ["vortex.constant"] = VortexEdition.Core20250500,
        ["vortex.datetimeparts"] = VortexEdition.Core20250500,
        ["vortex.decimal"] = VortexEdition.Core20250500,
        ["vortex.decimal_byte_parts"] = VortexEdition.Core20250500,
        ["vortex.dict"] = VortexEdition.Core20250500,
        ["vortex.ext"] = VortexEdition.Core20250500,
        ["vortex.fixed_size_list"] = VortexEdition.Core20251000,
        ["vortex.fsst"] = VortexEdition.Core20250500,
        ["vortex.list"] = VortexEdition.Core20250500,
        ["vortex.listview"] = VortexEdition.Core20251000,
        ["vortex.map"] = VortexEdition.Core20260802,
        ["vortex.masked"] = VortexEdition.Core20251000,
        ["vortex.null"] = VortexEdition.Core20250500,
        ["vortex.onpair"] = VortexEdition.Core20260801,
        ["vortex.parquet.variant"] = VortexEdition.Core20260803,
        ["vortex.pco"] = VortexEdition.Core20250600,
        ["vortex.primitive"] = VortexEdition.Core20250500,
        ["vortex.runend"] = VortexEdition.Core20250500,
        ["vortex.sequence"] = VortexEdition.Core20250600,
        ["vortex.sparse"] = VortexEdition.Core20250500,
        ["vortex.struct"] = VortexEdition.Core20250500,
        ["vortex.varbin"] = VortexEdition.Core20250500,
        ["vortex.varbinview"] = VortexEdition.Core20250500,
        ["vortex.variant"] = VortexEdition.Core20260803,
        ["vortex.zigzag"] = VortexEdition.Core20250500,
        ["vortex.zstd"] = VortexEdition.Core20250600,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, VortexEdition> Layouts =
        new Dictionary<string, VortexEdition>(StringComparer.Ordinal)
        {
        ["vortex.chunked"] = VortexEdition.Core20250500,
        ["vortex.dict"] = VortexEdition.Core20250500,
        ["vortex.flat"] = VortexEdition.Core20250500,
        ["vortex.stats"] = VortexEdition.Core20250500,
        ["vortex.struct"] = VortexEdition.Core20250500,
        ["vortex.zoned"] = VortexEdition.Core20260800,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, VortexEdition> DTypes =
        new Dictionary<string, VortexEdition>(StringComparer.Ordinal)
        {
        ["vortex.date"] = VortexEdition.Core20250500,
        ["vortex.time"] = VortexEdition.Core20250500,
        ["vortex.timestamp"] = VortexEdition.Core20250500,
        ["vortex.uuid"] = VortexEdition.Core20260803,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, VortexEdition> Aggregates =
        new Dictionary<string, VortexEdition>(StringComparer.Ordinal)
        {
        ["vortex.bounded_max"] = VortexEdition.Core20260800,
        ["vortex.bounded_min"] = VortexEdition.Core20260800,
        ["vortex.max"] = VortexEdition.Core20260800,
        ["vortex.min"] = VortexEdition.Core20260800,
        ["vortex.nan_count"] = VortexEdition.Core20260800,
        ["vortex.null_count"] = VortexEdition.Core20260800,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="edition"/> contains <paramref name="id"/> as a
    /// <paramref name="kind"/>.
    /// </summary>
    /// <param name="edition">The edition being targeted.</param>
    /// <param name="kind">Which namespace the id belongs to.</param>
    /// <param name="id">The component id.</param>
    /// <returns>
    /// <see langword="false"/> for an id no core edition contains, which includes both the
    /// genuinely unknown and the real-but-ungated - <c>vortex.patched</c> and
    /// <c>fastlanes.delta</c> have wire ids and belong to no edition.
    /// </returns>
    public static bool Contains(VortexEdition edition, ComponentKind kind, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Table(kind).TryGetValue(id, out VortexEdition introduced) && introduced <= edition;
    }

    /// <summary>
    /// The edition that introduced <paramref name="id"/>, or <see langword="null"/> if no core
    /// edition contains it.
    /// </summary>
    /// <param name="kind">Which namespace the id belongs to.</param>
    /// <param name="id">The component id.</param>
    public static VortexEdition? IntroducedIn(ComponentKind kind, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Table(kind).TryGetValue(id, out VortexEdition introduced) ? introduced : null;
    }

    /// <summary>The edition's own name, as it appears in the spec and in error messages.</summary>
    /// <param name="edition">The edition.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a known edition.</exception>
    public static string Name(VortexEdition edition) => edition switch
    {
        VortexEdition.Core20250500 => "core2025.05.0",
        VortexEdition.Core20250600 => "core2025.06.0",
        VortexEdition.Core20251000 => "core2025.10.0",
        VortexEdition.Core20260800 => "core2026.08.0",
        VortexEdition.Core20260801 => "core2026.08.1",
        VortexEdition.Core20260802 => "core2026.08.2",
        VortexEdition.Core20260803 => "core2026.08.3",
        _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unknown edition."),
    };

    /// <summary>The minimum Vortex library version that can read the edition.</summary>
    /// <param name="edition">The edition.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a known edition.</exception>
    public static string MinimumLibraryVersion(VortexEdition edition) => edition switch
    {
        VortexEdition.Core20250500 => "0.36.0",
        VortexEdition.Core20250600 => "0.40.0",
        VortexEdition.Core20251000 => "0.54.0",
        VortexEdition.Core20260800 => "0.84.0",
        VortexEdition.Core20260801 => "0.84.0",
        VortexEdition.Core20260802 => "0.85.0",
        VortexEdition.Core20260803 => "0.85.0",
        _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unknown edition."),
    };

    /// <summary>
    /// The registry's own string for the id <paramref name="idUtf8"/> spells, when it knows the id:
    /// a caller holding an id as bytes then needs no string of its own for it.
    /// </summary>
    /// <param name="kind">Which namespace the id belongs to.</param>
    /// <param name="idUtf8">The component id, as UTF-8.</param>
    /// <param name="known">The registry's string, when it has one.</param>
    /// <remarks>A walk over a few dozen ids, for a caller that asks once an id.</remarks>
    internal static bool TryGetId(ComponentKind kind, ReadOnlySpan<byte> idUtf8, [NotNullWhen(true)] out string? known)
    {
        foreach (string id in Table(kind).Keys)
        {
            if (System.Text.Ascii.Equals(idUtf8, id))
            {
                known = id;
                return true;
            }
        }

        known = null;
        return false;
    }

    private static FrozenDictionary<string, VortexEdition> Table(ComponentKind kind) => kind switch
    {
        ComponentKind.Array => Arrays,
        ComponentKind.Layout => Layouts,
        ComponentKind.DType => DTypes,
        ComponentKind.Aggregate => Aggregates,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown component kind."),
    };
}
