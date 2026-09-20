using System;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.chunked</c> layout metadata: not a Protobuf message at all, but a single optional
/// flag byte. The format's schema and its metadata catalogue disagree here — the catalogue calls
/// chunked layout metadata empty — and this codec follows the schema, which documents the flag.
/// </summary>
/// <remarks>
/// <para>
/// Treating a non-empty chunked metadata as an error would reject legal older files; treating the
/// flag as absent would misread the first chunk as data. Trailing bytes are ignored rather than
/// rejected, because the field has never carried anything else and a future writer may extend it.
/// </para>
/// <para>
/// Nothing validates the <see cref="HasStatsTable"/> branch: the reference implementation reads
/// chunked layout metadata as empty and rejects a payload that sets the flag, and every corpus
/// file reports zero metadata bytes for its chunked layouts. Reading the flag keeps such a file
/// openable; acting on it — skipping child 0 as a statistics table — is a path with no ground
/// truth behind it, so a layout reader should treat a true result as a reason to be careful
/// rather than as a verified shape.
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
