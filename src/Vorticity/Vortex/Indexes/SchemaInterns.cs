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
/// <para>
/// A dataset's handle interns the schema of each version it reads, so a table written under that schema finds it here. A
/// table whose hash names a schema the process never met, an object written before a change of schema that the handle
/// did not read through, reads as no table: its object is opened to learn its columns, as a file imported is. A hash
/// names its bytes for good, so what is interned is never replaced, and the schemas of a process are few.
/// </para>
/// <para>
/// XXH3-128 is not made to resist a schema chosen to collide with another's, and the schemas of every dataset a process
/// reads meet here. A hash two different schemas are interned under names neither from then on: a table that names it
/// reads as no table, and its object is opened, which is right whatever the bytes. A dataset's handle interns its schema
/// before it reads a table written under it, so a schema made to collide with it is caught before it is used.
/// </para>
/// </remarks>
internal static class SchemaInterns
{
    private static readonly ConcurrentDictionary<UInt128, byte[]> Known = new ConcurrentDictionary<UInt128, byte[]>();

    // What a hash two different schemas were interned under names from then on: nothing.
    private static readonly byte[] Collided = [];

    /// <summary>The hash a schema's bytes are named by.</summary>
    internal static UInt128 HashOf(ReadOnlySpan<byte> schema) => XxHash128.HashToUInt128(schema);

    /// <summary>Keeps <paramref name="schema"/>, as <see cref="Types.Serialization.DTypeFlatBuffers"/> writes it, and returns its hash.</summary>
    internal static UInt128 Intern(byte[] schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        UInt128 hash = HashOf(schema);
        Intern(hash, schema);
        return hash;
    }

    /// <summary>Keeps <paramref name="schema"/> under <paramref name="hash"/>, or names nothing by it when another schema is kept under it.</summary>
    internal static void Intern(UInt128 hash, byte[] schema)
    {
        byte[] kept = Known.GetOrAdd(hash, schema);
        if (!ReferenceEquals(kept, schema) && !ReferenceEquals(kept, Collided) && !kept.AsSpan().SequenceEqual(schema))
        {
            Known[hash] = Collided;
        }
    }

    /// <summary>The schema <paramref name="hash"/> names, or null when the process met none of that hash, or two.</summary>
    internal static byte[]? Find(UInt128 hash) =>
        Known.TryGetValue(hash, out byte[]? schema) && !ReferenceEquals(schema, Collided) ? schema : null;
}
