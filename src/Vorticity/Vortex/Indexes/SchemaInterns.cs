using System;
using System.Collections.Concurrent;
using System.IO.Hashing;

namespace Vorticity.Indexes;

/// <summary>
/// The schemas this process met, by the XXH3-128 of their bytes: what lets a table of contents name its file's schema by
/// its hash (<see cref="ObjectContents"/>), where every small object of a dataset would repeat the 1.3 KB its dataset
/// already holds once.
/// </summary>
/// <remarks>
/// A dataset's handle interns the schema of each version it reads, so a table written under that schema finds it here. A
/// table whose hash names a schema the process never met, an object written before a change of schema that the handle
/// did not read through, reads as no table: its object is opened to learn its columns, as a file imported is. A hash
/// names its bytes for good, so what is interned is never replaced, and the schemas of a process are few.
/// </remarks>
internal static class SchemaInterns
{
    private static readonly ConcurrentDictionary<UInt128, byte[]> Known = new ConcurrentDictionary<UInt128, byte[]>();

    /// <summary>The hash a schema's bytes are named by.</summary>
    internal static UInt128 HashOf(ReadOnlySpan<byte> schema) => XxHash128.HashToUInt128(schema);

    /// <summary>Keeps <paramref name="schema"/>, as <see cref="Types.Serialization.DTypeFlatBuffers"/> writes it, and returns its hash.</summary>
    internal static UInt128 Intern(byte[] schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        UInt128 hash = HashOf(schema);
        Known.TryAdd(hash, schema);
        return hash;
    }

    /// <summary>The schema <paramref name="hash"/> names, or null when the process met none of that hash.</summary>
    internal static byte[]? Find(UInt128 hash) => Known.TryGetValue(hash, out byte[]? schema) ? schema : null;
}
