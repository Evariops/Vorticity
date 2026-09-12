// Renaming one component id inside a real corpus file, in memory.
//
// Several tests need a file that is structurally perfect and names a component no build can
// resolve. Picking a real-but-unimplemented id for that -- vortex.fsst, say -- works exactly until
// that decoder lands, at which point the test either fails or, worse, passes for the opposite
// reason: "scanning without projecting the unknown column succeeds" stays green when the column
// became decodable, and the property it was guarding is gone with nothing to say so.
//
// A LENGTH-PRESERVING patch of the id string has no such expiry. Every offset in the file stays
// valid, the only thing that changed is a name the registry cannot resolve, and no future decoder
// can rescue it.
using System;
using System.Buffers.Binary;
using System.Text;

namespace Vorticity.Tests.File;

/// <summary>Byte-patches a component id in a corpus file to one nothing resolves.</summary>
internal static class ForgedEncodingId
{
    /// <summary>
    /// Reads <paramref name="corpusId"/> and replaces the last occurrence of the FlatBuffers string
    /// <paramref name="known"/> with <paramref name="forged"/>.
    /// </summary>
    /// <param name="corpusId">The corpus entry to start from.</param>
    /// <param name="known">The id as the file spells it.</param>
    /// <param name="forged">Its replacement; must be the same length.</param>
    /// <returns>The patched bytes.</returns>
    /// <exception cref="ArgumentException">The two ids differ in length.</exception>
    /// <exception cref="InvalidOperationException">The file carries no such id.</exception>
    internal static byte[] Patch(string corpusId, ReadOnlySpan<byte> known, ReadOnlySpan<byte> forged)
    {
        if (known.Length != forged.Length)
        {
            throw new ArgumentException(
                "The replacement id must be the same length, or every offset after it moves.",
                nameof(forged));
        }

        byte[] bytes = CorpusManifest.Bytes(corpusId);
        forged.CopyTo(bytes.AsSpan(Find(bytes, known)));
        return bytes;
    }

    /// <summary>
    /// The offset of the last FlatBuffers string equal to <paramref name="value"/>: a
    /// little-endian <c>u32</c> length, the bytes, then a NUL terminator.
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <param name="value">The string to find.</param>
    /// <exception cref="InvalidOperationException">There is no such string.</exception>
    internal static int Find(byte[] bytes, ReadOnlySpan<byte> value)
    {
        for (int at = bytes.Length - value.Length - 1; at >= 4; at--)
        {
            if (!bytes.AsSpan(at, value.Length).SequenceEqual(value))
            {
                continue;
            }

            if (bytes[at + value.Length] != 0)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at - 4, 4)) != (uint)value.Length)
            {
                continue;
            }

            return at;
        }

        throw new InvalidOperationException(
            $"No FlatBuffers string '{Encoding.UTF8.GetString(value)}' in the fixture.");
    }
}
