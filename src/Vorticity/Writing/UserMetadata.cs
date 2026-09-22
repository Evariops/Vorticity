using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>The caller's metadata entries of a file: each a key in the postscript and a segment holding its value.</summary>
internal static class UserMetadata
{
    /// <summary>The entries a caller may add: the postscript's ceiling, less the identity and the index directory this library writes.</summary>
    internal const int MaxEntries = VortexLimits.MaxMetadataSegments - 2;

    /// <summary>Refuses metadata the postscript cannot carry, or a key this library writes itself.</summary>
    internal static void Validate(ImmutableDictionary<string, ReadOnlyMemory<byte>> metadata)
    {
        if (metadata.Count > MaxEntries)
        {
            throw new ArgumentException(
                $"A file carries at most {MaxEntries} metadata entries besides its own; {metadata.Count} were given.", "options");
        }

        // The entries rather than Keys, whose enumerator is an allocated iterator.
        foreach (KeyValuePair<string, ReadOnlyMemory<byte>> entry in metadata)
        {
            string key = entry.Key;
            if (key.Length == 0)
            {
                throw new ArgumentException("A metadata key is not empty.", "options");
            }

            if (Encoding.UTF8.GetByteCount(key) > VortexLimits.MaxMetadataKeyLength)
            {
                throw new ArgumentException(
                    $"The metadata key '{key}' is longer than the format's {VortexLimits.MaxMetadataKeyLength} bytes.", "options");
            }

            if (IsReserved(Encoding.UTF8.GetBytes(key)))
            {
                throw new ArgumentException($"The metadata key '{key}' belongs to this library.", "options");
            }
        }
    }

    /// <summary>The entries in key order, so that the same options write the same bytes.</summary>
    internal static KeyValuePair<string, ReadOnlyMemory<byte>>[] Ordered(ImmutableDictionary<string, ReadOnlyMemory<byte>> metadata)
    {
        if (metadata.IsEmpty)
        {
            return [];
        }

        KeyValuePair<string, ReadOnlyMemory<byte>>[] entries = [.. metadata];
        Array.Sort(entries, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return entries;
    }

    /// <summary>The caller's entries of an existing file, values copied, the library's own left out.</summary>
    internal static async ValueTask<ImmutableDictionary<string, ReadOnlyMemory<byte>>> ReadAsync(
        VortexFile file, CancellationToken cancellationToken)
    {
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Builder entries =
            ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        for (int i = 0; i < file.MetadataCount; i++)
        {
            string key = file.GetMetadataKey(i);
            if (IsReserved(Encoding.UTF8.GetBytes(key)))
            {
                continue;
            }

            using SegmentOwner owner = await file.ReadMetadataAsync(i, cancellationToken).ConfigureAwait(false);
            entries[key] = owner.Buffer.Span.ToArray();
        }

        return entries.ToImmutable();
    }

    /// <summary>Whether a key is one the library writes for itself: the file's identity, its index directory.</summary>
    internal static bool IsReserved(ReadOnlySpan<byte> keyUtf8) =>
        keyUtf8.SequenceEqual(FileIdentity.MetadataKeyUtf8) || keyUtf8.SequenceEqual(IndexDirectory.MetadataKeyUtf8);
}
