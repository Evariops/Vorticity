using System;
using Vorticity.Editions;

namespace Vorticity;

/// <summary>A frozen edition of the <c>core</c> family, in publication order.</summary>
/// <remarks>
/// The numeric values are ordinals, so <c>&lt;=</c> is "is contained by": editions are cumulative
/// within a family.
/// </remarks>
public enum VortexEdition
{
    /// <summary><c>core2025.05.0</c>, readable by the Rust library from 0.36.0.</summary>
    Core20250500 = 0,

    /// <summary><c>core2025.06.0</c>, readable by the Rust library from 0.40.0.</summary>
    Core20250600 = 1,

    /// <summary><c>core2025.10.0</c>, readable by the Rust library from 0.54.0.</summary>
    Core20251000 = 2,

    /// <summary><c>core2026.08.0</c>, readable by the Rust library from 0.84.0.</summary>
    Core20260800 = 3,

    /// <summary><c>core2026.08.1</c>, readable by the Rust library from 0.84.0.</summary>
    Core20260801 = 4,

    /// <summary><c>core2026.08.2</c>, readable by the Rust library from 0.85.0.</summary>
    Core20260802 = 5,

    /// <summary><c>core2026.08.3</c>, readable by the Rust library from 0.85.0.</summary>
    Core20260803 = 6,
}

/// <summary>Which kind of component an id names.</summary>
public enum ComponentKind : byte
{
    /// <summary>An array encoding id, such as <c>fastlanes.bitpacked</c>.</summary>
    Array = 0,

    /// <summary>A layout id, such as <c>vortex.zoned</c>.</summary>
    Layout = 1,

    /// <summary>An extension dtype id, such as <c>vortex.timestamp</c>.</summary>
    DType = 2,

    /// <summary>A zone-map aggregate id, such as <c>vortex.min</c>.</summary>
    Aggregate = 3,

    /// <summary>A segment compression scheme, such as <c>lz4</c>.</summary>
    Compression = 4,

    /// <summary>A segment encryption scheme.</summary>
    Encryption = 5,

    /// <summary>An index kind, or the index a request needs and the file does not carry.</summary>
    Index = 6,

    /// <summary>A capability the library does not offer for this input, such as an append over a layout it cannot resume.</summary>
    Feature = 7,
}

/// <summary>The editions a file can be written to, and what each contains.</summary>
public static class VortexEditions
{
    /// <summary>
    /// The edition a write targets unless its options say otherwise: the one the most deployed Rust
    /// reader accepts, chosen per release and named in the release notes.
    /// </summary>
    /// <remarks>
    /// It is the first edition that carries <c>vortex.uuid</c>, without which a <see cref="Guid"/>
    /// column cannot be written.
    /// </remarks>
    public const VortexEdition Default = VortexEdition.Core20260803;

    /// <summary>The newest frozen edition this library knows.</summary>
    public const VortexEdition Newest = EditionRegistry.Newest;

    /// <summary>The oldest edition this library reads; every file of it or later opens.</summary>
    public const VortexEdition ReadFloor = EditionRegistry.ReadForeverFloor;

    /// <summary>Whether <paramref name="edition"/> contains the component <paramref name="id"/> of <paramref name="kind"/>.</summary>
    /// <param name="edition">The edition.</param>
    /// <param name="kind">Which dictionary the id belongs to; <c>vortex.dict</c> is both an array and a layout.</param>
    /// <param name="id">The component id, such as <c>vortex.alp</c>.</param>
    /// <returns>Whether a writer targeting the edition may emit it.</returns>
    public static bool Contains(VortexEdition edition, ComponentKind kind, string id) =>
        EditionRegistry.Contains(edition, kind, id);

    /// <summary>The edition that introduced a component, or null when no core edition contains it.</summary>
    /// <param name="kind">Which dictionary the id belongs to.</param>
    /// <param name="id">The component id.</param>
    /// <returns>The introducing edition.</returns>
    public static VortexEdition? IntroducedIn(ComponentKind kind, string id) =>
        EditionRegistry.IntroducedIn(kind, id);

    /// <summary>The oldest Rust library version that reads every file of <paramref name="edition"/>.</summary>
    /// <param name="edition">The edition.</param>
    /// <returns>A version string such as <c>0.85.0</c>.</returns>
    public static string MinimumRustVersion(VortexEdition edition) =>
        EditionRegistry.MinimumLibraryVersion(edition);

    /// <summary>The edition's name as the format writes it, such as <c>core2026.08.3</c>.</summary>
    /// <param name="edition">The edition.</param>
    /// <returns>The name.</returns>
    public static string Name(VortexEdition edition) => EditionRegistry.Name(edition);
}
