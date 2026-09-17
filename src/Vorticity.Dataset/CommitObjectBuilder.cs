// Assembles one commit object - docs/13-dataset.md §3 and §4.3: "those pages are laid out inside
// the commit object with the header and the fragments ... One PutIfAbsent creates the commit".
//
// THE ORDER OF OPERATIONS, and why the header is written twice. A page's offset is only known once
// the header's length is, and the header holds the page's offset: that is circular, and the circle
// is broken by making every page reference a fixed 36 bytes (see CommitHeader). So this builder
// collects the pages, serializes the header once with the pages region at zero to learn its length,
// then serializes it again with the region where it really starts. The second pass produces exactly
// as many bytes as the first, and that is CHECKED rather than trusted: a length that moved would
// mean a reference is not fixed-width after all, and the object would name pages that are not there.
//
// WHAT A REFERENCE HANDED OUT HERE MEANS. `AddPage` returns a reference whose version is this
// commit's and whose offset is RELATIVE to the pages region. `Build` makes it absolute. A reference
// to another version -- a page an older commit wrote and this one did not change -- is already
// absolute and passes through untouched, which is what makes §4.3's "every page the commit did not
// change is referenced where it already lies" one line of code rather than a bookkeeping problem.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>Builds the bytes of one commit object.</summary>
public sealed class CommitObjectBuilder : IPageSink
{
    private readonly ulong _version;
    private readonly List<byte[]> _pages = [];
    private readonly List<PageReference> _pageReferences = [];
    private readonly List<byte[]> _fragments = [];
    private readonly List<PageReference> _fragmentReferences = [];
    private long _pageBytes;
    private long _fragmentBytes;

    /// <summary>Starts a commit object for <paramref name="version"/>.</summary>
    /// <param name="version">The version this object commits; never 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is 0.</exception>
    public CommitObjectBuilder(ulong version)
    {
        ArgumentOutOfRangeException.ThrowIfZero(version);
        _version = version;
    }

    /// <summary>The version being committed.</summary>
    public ulong Version => _version;

    /// <summary>The pages added so far.</summary>
    public int PageCount => _pages.Count;

    /// <summary>The fragments added so far.</summary>
    public int FragmentCount => _fragments.Count;

    /// <summary>Adds a tree page and returns the reference that will name it.</summary>
    /// <param name="page">Its bytes; copied.</param>
    /// <returns>A reference with this commit's version and an offset this builder will rebase.</returns>
    public PageReference AddPage(ReadOnlySpan<byte> page)
    {
        PageReference reference = new PageReference(_version, _pageBytes, page.Length, XxHash128.HashToUInt128(page));
        _pages.Add(page.ToArray());
        _pageReferences.Add(reference);
        _pageBytes += page.Length;
        return reference;
    }

    /// <inheritdoc/>
    /// <remarks>The tree's seam onto this builder: a page it emits is a page of this commit.</remarks>
    PageReference IPageSink.WritePage(ReadOnlySpan<byte> page) => AddPage(page);

    /// <summary>The bytes of a page this builder holds, for a caller that wants to inline it.</summary>
    /// <param name="reference">The reference this builder handed out.</param>
    /// <param name="page">Receives its bytes.</param>
    /// <returns>Whether this builder wrote that page.</returns>
    public bool TryGetPage(PageReference reference, out ReadOnlyMemory<byte> page)
    {
        for (int i = 0; i < _pageReferences.Count; i++)
        {
            if (_pageReferences[i] == reference)
            {
                page = _pages[i];
                return true;
            }
        }

        page = default;
        return false;
    }

    /// <summary>Adds an index fragment (§6.4) and returns the reference that will name it.</summary>
    /// <param name="fragment">Its bytes; copied.</param>
    /// <returns>A reference with this commit's version and an offset this builder will rebase.</returns>
    /// <remarks>
    /// Fragments lie after the pages, so their offsets are rebased by the pages region's start AND
    /// by the pages' own bytes; a caller never computes either.
    /// </remarks>
    public PageReference AddFragment(ReadOnlySpan<byte> fragment)
    {
        PageReference reference = new PageReference(
            _version, _pageBytes + _fragmentBytes, fragment.Length, XxHash128.HashToUInt128(fragment));
        _fragments.Add(fragment.ToArray());
        _fragmentReferences.Add(reference);
        _fragmentBytes += fragment.Length;
        return reference;
    }

    /// <summary>Lays the object out.</summary>
    /// <param name="header">
    /// The header, holding references this builder handed out and references into older commits.
    /// </param>
    /// <returns>The object's bytes, ready for one <see cref="IObjectStore.PutIfAbsentAsync"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="header"/> is null.</exception>
    /// <exception cref="CommitFormatException">
    /// The header names a version other than this builder's, or it does not serialize to a stable
    /// length.
    /// </exception>
    public byte[] Build(CommitHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.Version != _version)
        {
            throw new CommitFormatException(
                $"The header commits version {header.Version} and the builder version {_version}.");
        }

        byte[] headerBytes = Serialize(header);
        int headerLength = headerBytes.Length;
        long pagesStart = CommitFormat.PreambleBytes + headerLength;
        byte[] table = BuildTable();
        long tableOffset = pagesStart + _pageBytes + _fragmentBytes;
        long length = tableOffset + table.Length + CommitFormat.TrailerBytes;
        if (length > int.MaxValue)
        {
            throw new CommitFormatException($"A commit object of {length} bytes is past this builder's reach.");
        }

        byte[] bytes = new byte[length];
        Span<byte> destination = bytes;
        CommitFormat.WritePreamble(destination, headerLength);
        headerBytes.CopyTo(destination[CommitFormat.PreambleBytes..]);

        int at = (int)pagesStart;
        foreach (byte[] page in _pages)
        {
            page.CopyTo(destination[at..]);
            at += page.Length;
        }

        foreach (byte[] fragment in _fragments)
        {
            fragment.CopyTo(destination[at..]);
            at += fragment.Length;
        }

        table.CopyTo(destination[at..]);

        // §7: "a commit object carries an XXH3-64 over its header and its table".
        XxHash3 checksum = new XxHash3();
        checksum.Append(headerBytes);
        checksum.Append(table);
        CommitFormat.WriteTrailer(
            destination[(int)(length - CommitFormat.TrailerBytes)..],
            tableOffset,
            table.Length,
            length,
            checksum.GetCurrentHashAsUInt64());
        return bytes;
    }

    private static byte[] Serialize(CommitHeader header)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            header.Write(ref writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The table of §3: what this object holds, pages then fragments.</summary>
    /// <remarks>Offsets are relative to the pages region, as every offset of this format is.</remarks>
    private byte[] BuildTable()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            foreach (PageReference reference in _pageReferences)
            {
                WriteEntry(ref writer, CommitTable.Field.Page, reference);
            }

            foreach (PageReference reference in _fragmentReferences)
            {
                WriteEntry(ref writer, CommitTable.Field.Fragment, reference);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteEntry(ref ProtoWriter writer, int field, PageReference reference)
    {
        Span<byte> bytes = stackalloc byte[PageReference.Bytes];
        reference.Write(bytes);
        writer.WriteBytes(field, bytes);
    }
}

/// <summary>The commit object's table: what it holds, for `verify` and for repack (§10).</summary>
public sealed record CommitTable(IReadOnlyList<PageReference> Pages, IReadOnlyList<PageReference> Fragments)
{
    /// <summary>An empty table.</summary>
    public static CommitTable Empty { get; } = new CommitTable([], []);

    /// <summary>The field numbers, so that the writer and the reader cannot drift.</summary>
    internal static class Field
    {
        internal const int Page = 1;
        internal const int Fragment = 2;
    }

    /// <summary>Reads a table.</summary>
    /// <param name="bytes">Its bytes.</param>
    /// <returns>The table.</returns>
    /// <exception cref="CommitFormatException">The bytes are not a table.</exception>
    internal static CommitTable Read(ReadOnlySpan<byte> bytes)
    {
        List<PageReference> pages = [];
        List<PageReference> fragments = [];
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                switch (field, wire)
                {
                    case (Field.Page, ProtoWireType.LengthDelimited):
                        pages.Add(PageReference.Read(reader.ReadLengthDelimited()));
                        break;
                    case (Field.Fragment, ProtoWireType.LengthDelimited):
                        fragments.Add(PageReference.Read(reader.ReadLengthDelimited()));
                        break;
                    default:
                        reader.SkipField(wire);
                        break;
                }
            }
        }
        catch (VortexFormatException cause)
        {
            throw new CommitFormatException($"The commit table does not decode: {cause.Message}", cause);
        }

        return new CommitTable(pages, fragments);
    }
}
