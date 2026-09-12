// Layout vortex.chunked — spec/flatbuffers/layout.fbs' own comment, which contradicts
// spec/METADATA.md's "empty metadata" entry and wins (Phase 1 contract §6.4):
//
//   the `ChunkedLayout` uses the first byte of the `metadata` array as a boolean to indicate
//   whether the first child Layout represents the statistics table for the other chunks
using System;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.chunked</c> <b>layout</b> metadata: not a Protobuf message at all, but a single
/// optional flag byte.
/// </summary>
/// <remarks>
/// <para>
/// Treating a non-empty chunked metadata as an error would reject legal older files; treating the
/// flag as absent would misread the first chunk as data. Trailing bytes are ignored rather than
/// rejected, because the field has never carried anything else and a future writer may extend it.
/// </para>
/// <para>
/// <b>Vortex 0.86.1 no longer implements the flag, and rejects a file that sets it.</b>
/// <c>impl VTable for Chunked</c> declares <c>type Metadata = EmptyMetadata</c> and its
/// <c>deserialize</c> names the parameter <c>_metadata</c>
/// (vortex-layout-0.86.1/src/layouts/chunked/mod.rs), while
/// <c>EmptyMetadata::deserialize</c> raises "EmptyMetadata should not have metadata bytes" for a
/// non-empty payload (vortex-array-0.86.1/src/metadata.rs). The <c>layout.fbs</c> comment this
/// codec follows therefore describes a reader that no longer exists, every corpus file reports
/// <c>metadata_bytes = 0</c> for its chunked layouts, and no fixture can validate the
/// <see cref="HasStatsTable"/> branch. Reading the flag keeps us able to open a pre-0.86 file
/// upstream would now refuse; <b>acting</b> on it — skipping child 0 as a statistics table — is a
/// path with no ground truth behind it, so a layout reader should treat a true result as a reason
/// to be careful, not as a verified shape.
/// </para>
/// </remarks>
public readonly struct ChunkedLayoutMetadata : IEquatable<ChunkedLayoutMetadata>
{
    /// <summary>Creates chunked layout metadata.</summary>
    /// <param name="hasStatsTable">Whether child 0 is the statistics table for the other chunks.</param>
    public ChunkedLayoutMetadata(bool hasStatsTable) => HasStatsTable = hasStatsTable;

    /// <summary>True when the first child layout is the statistics table for the other chunks.</summary>
    public bool HasStatsTable { get; }

    /// <summary>Reads a <c>vortex.chunked</c> layout metadata payload.</summary>
    /// <param name="layoutMetadata">
    /// The raw layout metadata bytes. Empty means <see cref="HasStatsTable"/> is false; otherwise
    /// the first byte is the flag (0 = false, anything else = true).
    /// </param>
    public static ChunkedLayoutMetadata Read(ReadOnlySpan<byte> layoutMetadata) =>
        new ChunkedLayoutMetadata(!layoutMetadata.IsEmpty && layoutMetadata[0] != 0);

    /// <summary>Serializes the flag: one byte when set, and nothing at all when clear.</summary>
    /// <param name="value">The metadata.</param>
    /// <param name="destination">Receives the bytes; must hold at least one.</param>
    /// <returns>The number of bytes written: 1 when the flag is set, 0 otherwise.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is empty.</exception>
    public static int Write(in ChunkedLayoutMetadata value, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            throw new ArgumentException("Destination must hold at least one byte.", nameof(destination));
        }

        if (!value.HasStatsTable)
        {
            return 0;
        }

        destination[0] = 1;
        return 1;
    }

    /// <inheritdoc/>
    public bool Equals(ChunkedLayoutMetadata other) => HasStatsTable == other.HasStatsTable;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ChunkedLayoutMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HasStatsTable.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ChunkedLayoutMetadata left, ChunkedLayoutMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ChunkedLayoutMetadata left, ChunkedLayoutMetadata right) => !left.Equals(right);
}
