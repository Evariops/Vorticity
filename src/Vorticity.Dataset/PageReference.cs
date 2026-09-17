// A page reference - docs/13-dataset.md §3: "(version, offset, length, XXH3-128): the commit object
// that wrote the page, where it lies in it, and its content hash".
//
// WHY THE VERSION IS PART OF THE ADDRESS, and why that is the design's hinge. "A commit references
// the pages it did not change where they already are, in older commit objects" (§3). So a reference
// names the OBJECT that holds the page as well as the place inside it -- which means a commit that
// changes O(depth) pages writes O(depth) pages and points at everything else, and there is no page
// object, no pack, and nothing to garbage-collect but data objects and whole commits.
//
// WHY THE HASH IS IN THE REFERENCE RATHER THAN IN THE PAGE. Three things at once (§7): the reader
// checks what it read against what sent it there, so a torn or misdirected page is caught at the
// only moment it matters; a writer that recognises a page's content can reference the copy it
// already knows instead of writing it again, which the prolly rule makes common after a rebase
// (§4.3); and `verify` has something to compare offline.
using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Vorticity.Dataset;

/// <summary>Where a page lies and what it must hash to.</summary>
/// <param name="Version">The commit object that holds it; 0 is "no page".</param>
/// <param name="Offset">
/// Its offset from the start of that object's PAGES REGION, which is one past its header.
/// </param>
/// <param name="Length">Its bytes.</param>
/// <param name="Hash">XXH3-128 of its bytes.</param>
/// <remarks>
/// WHY THE OFFSET IS RELATIVE, decided at step 38 after the absolute form failed. An internal page
/// holds its children's references, so an absolute offset would have to be known before the page's
/// bytes exist — and the page's bytes decide the header's length, which decides where the pages
/// region starts, which decides the absolute offsets. The circle is real and it is not the one the
/// header's own references broke: those are patched at layout time, a page's are baked into content
/// that is then hashed. Relative offsets cut it, and they buy something the absolute form could
/// never have: a page's bytes no longer depend on where its object put it, so two identical trees
/// written into two commit objects are identical byte for byte, which is what 13 §4.1 promises and
/// what §14's oracle compares. The reader adds the region's start, which is the header's length and
/// lies in the object's first sixteen bytes.
/// </remarks>
public readonly record struct PageReference(ulong Version, long Offset, int Length, UInt128 Hash)
{
    /// <summary>The reference that names no page.</summary>
    public static PageReference None => default;

    /// <summary>Whether this names a page.</summary>
    public bool Exists => Version != 0 || Length != 0;

    /// <summary>The bytes a reference takes when written: version, offset, length, hash.</summary>
    internal const int Bytes = 8 + 8 + 4 + 16;

    /// <summary>Writes the reference, little-endian, into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="Bytes"/> bytes.</param>
    internal void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Version);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], Offset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], Length);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[20..], (ulong)(Hash >> 64));
        BinaryPrimitives.WriteUInt64LittleEndian(destination[28..], (ulong)Hash);
    }

    /// <summary>Reads a reference written by <see cref="Write"/>.</summary>
    /// <param name="source">At least <see cref="Bytes"/> bytes.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="CommitFormatException">The bytes cannot be a reference.</exception>
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

    /// <summary>The reference, as a person reads it.</summary>
    /// <returns>Version, offset, length and the hash's first hex digits.</returns>
    public override string ToString() => Exists
        ? string.Create(CultureInfo.InvariantCulture, $"v{Version}@{Offset}+{Length} {Hash:x32}")
        : "(none)";
}
