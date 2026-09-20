using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>
/// Builds the bytes of one commit object. Every reference it hands out is relative to the body,
/// the region after the header, so the header's bytes do not depend on its own length and it is
/// serialized once. The body is laid out in the order things are added, which makes an offset
/// final as soon as it is returned — it has to be, since a reference may already be hashed into a
/// page this same commit writes later.
/// </summary>
public sealed class CommitObjectBuilder : IPageSink
{
    private readonly ulong _version;
    private readonly List<byte[]> _pages = [];
    private readonly List<PageReference> _pageReferences = [];
    private readonly List<PageReference> _fragmentReferences = [];
    private readonly List<byte[]> _body = [];
    private long _bodyBytes;

    /// <summary>Starts a commit object for a version, which is never 0.</summary>
    public CommitObjectBuilder(ulong version)
    {
        ArgumentOutOfRangeException.ThrowIfZero(version);
        _version = version;
    }

    public ulong Version => _version;

    /// <summary>The pages added so far.</summary>
    public int PageCount => _pages.Count;

    /// <summary>The fragments added so far.</summary>
    public int FragmentCount => _fragmentReferences.Count;

    /// <summary>Copies a tree page in and returns the reference that names it.</summary>
    public PageReference AddPage(ReadOnlySpan<byte> page)
    {
        byte[] held = page.ToArray();
        PageReference reference = Append(held);
        _pages.Add(held);
        _pageReferences.Add(reference);
        return reference;
    }

    PageReference IPageSink.WritePage(ReadOnlySpan<byte> page) => AddPage(page);

    /// <summary>
    /// The bytes of a page this builder wrote, for a caller that wants to inline it. A linear walk
    /// rather than a map: the question is asked rarely enough that indexing the list would not pay.
    /// </summary>
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

    /// <summary>Copies an index fragment in and returns the reference that names it.</summary>
    public PageReference AddFragment(ReadOnlySpan<byte> fragment)
    {
        PageReference reference = Append(fragment.ToArray());
        _fragmentReferences.Add(reference);
        return reference;
    }

    /// <summary>Puts bytes at the end of the body and names where they lie.</summary>
    private PageReference Append(byte[] bytes)
    {
        PageReference reference = new PageReference(_version, _bodyBytes, bytes.Length, XxHash128.HashToUInt128(bytes));
        _body.Add(bytes);
        _bodyBytes += bytes.Length;
        return reference;
    }

    /// <summary>
    /// Lays the object out, ready for one <see cref="IObjectStore.PutIfAbsentAsync"/>. The header
    /// may hold references this builder handed out and references into older commits, but it must
    /// name this builder's version.
    /// </summary>
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
        long tableOffset = pagesStart + _bodyBytes;
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
        foreach (byte[] held in _body)
        {
            held.CopyTo(destination[at..]);
            at += held.Length;
        }

        table.CopyTo(destination[at..]);

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

    /// <summary>What this object holds, pages then fragments, at offsets relative to the body.</summary>
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

/// <summary>The commit object's table: what it holds, for verification and for repack.</summary>
public sealed record CommitTable(IReadOnlyList<PageReference> Pages, IReadOnlyList<PageReference> Fragments)
{
    /// <summary>An empty table.</summary>
    public static CommitTable Empty { get; } = new CommitTable([], []);

    internal static class Field
    {
        internal const int Page = 1;
        internal const int Fragment = 2;
    }

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
