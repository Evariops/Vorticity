using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Vorticity.Dataset;

/// <summary>Where a page lies and what it must hash to.</summary>
/// <param name="Version">The commit object that holds it; 0 is "no page".</param>
/// <param name="Offset">
/// Its offset from the start of that object's pages region, which is one past its header. Relative
/// rather than absolute so that a page's bytes do not depend on where its object put it, which is
/// what makes two identical trees written into two commit objects identical byte for byte.
/// </param>
/// <param name="Length">Its bytes.</param>
/// <param name="Hash">XXH3-128 of its bytes.</param>
public readonly record struct PageReference(ulong Version, long Offset, int Length, UInt128 Hash)
{
    /// <summary>The reference that names no page.</summary>
    public static PageReference None => default;

    /// <summary>Whether this names a page.</summary>
    public bool Exists => Version != 0 || Length != 0;

    internal const int Bytes = 8 + 8 + 4 + 16;

    /// <summary>Writes the reference, little-endian, into a span of at least <see cref="Bytes"/> bytes.</summary>
    internal void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Version);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], Offset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], Length);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[20..], (ulong)(Hash >> 64));
        BinaryPrimitives.WriteUInt64LittleEndian(destination[28..], (ulong)Hash);
    }

    /// <summary>Reads a reference written by <see cref="Write"/>.</summary>
    internal static PageReference Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Bytes)
        {
            throw new CommitFormatException(
                $"A page reference is {Bytes} bytes and {source.Length} were left.");
        }

        long offset = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(source[16..]);
        if (offset < 0 || length < 0)
        {
            throw new CommitFormatException($"A page at {offset} of {length} bytes is not a page.");
        }

        UInt128 hash = ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(source[20..]) << 64)
            | BinaryPrimitives.ReadUInt64LittleEndian(source[28..]);
        return new PageReference(BinaryPrimitives.ReadUInt64LittleEndian(source), offset, length, hash);
    }

    /// <summary>Version, offset, length and the hash, as a person reads them.</summary>
    public override string ToString() => Exists
        ? string.Create(CultureInfo.InvariantCulture, $"v{Version}@{Offset}+{Length} {Hash:x32}")
        : "(none)";
}
