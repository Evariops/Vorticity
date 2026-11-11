using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// A run of a filter tree's words where they lie: in a segment whose owner the reader holds, or in memory that does
/// not move, as long as whoever made the view keeps it. A pointer and a length, as small as the array, start and
/// length it replaced, since a question keeps one for every node and leaf it reads.
/// </summary>
internal readonly unsafe struct BloomWords
{
    private readonly uint* _pointer;

    private BloomWords(uint* pointer, int length)
    {
        _pointer = pointer;
        Length = length;
    }

    /// <summary>The words of <paramref name="bytes"/>, read where they lie; valid while their owner is held.</summary>
    /// <param name="bytes">Little-endian words.</param>
    internal static BloomWords InPlace(VortexBuffer bytes) =>
        new BloomWords((uint*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(bytes.Span)), bytes.Length / sizeof(uint));

    /// <summary>The words of <paramref name="pinned"/>, which the caller keeps fixed while the view is used.</summary>
    /// <param name="pinned">Words that do not move: pinned, native, or on the stack of a frame that outlives the view.</param>
    internal static BloomWords Pinned(ReadOnlySpan<uint> pinned) =>
        new BloomWords((uint*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(pinned)), pinned.Length);

    /// <summary>How many words.</summary>
    internal int Length { get; }

    /// <summary>The words.</summary>
    internal ReadOnlySpan<uint> Span => new ReadOnlySpan<uint>(_pointer, Length);

    /// <summary><paramref name="length"/> words from <paramref name="start"/>, which the caller keeps within these.</summary>
    /// <param name="start">The first.</param>
    /// <param name="length">How many.</param>
    internal BloomWords Slice(int start, int length) => new BloomWords(_pointer + start, length);
}

/// <summary>
/// One node of a filter tree, parsed in place over the little-endian words that hold it: a leaf is a
/// block's filter, and a node covers up to sixteen children with the filter of their union when that
/// union fits the filter's ceiling, having none at all when it does not.
/// </summary>
/// <remarks>
/// A node's children are one region, written before the node, so a probe the node does not stop
/// reads a whole level in one ranged read. Every node is checked against what its parent says of it
/// -- its level, its blocks, its word count -- and its children's region against the checksum the
/// node carries, since a node that does not check out must claim nothing for the blocks beneath it.
/// A value, kept where its reader keeps the nodes it read: a question reads hundreds to thousands of
/// them, an object each before (2026-11-11).
/// </remarks>
internal readonly struct BloomNode
{
    /// <summary>The words before a node's child sizes.</summary>
    internal const int HeaderWords = 9;

    /// <summary>The deepest level: 16^15 blocks is past anything a 64-bit row count can hold.</summary>
    internal const int MaxLevel = 15;

    private const uint LayoutVersion = 1;

    private const uint BareFlag = 1u << 24;

    private readonly BloomWords _words;
    private readonly int _childWords;

    private BloomNode(BloomWords words, int level, long leaves, int children, bool bare, int filterBlocks, IndexSegment? region)
    {
        _words = words;
        _childWords = bare ? 0 : children;
        Level = level;
        Leaves = leaves;
        ChildCount = children;
        FilterBlocks = filterBlocks;
        Children = region;
    }

    /// <summary>1 when its children are leaves.</summary>
    internal int Level { get; }

    /// <summary>The blocks under it.</summary>
    internal long Leaves { get; }

    /// <summary>Its children.</summary>
    internal int ChildCount { get; }

    /// <summary>Its filter's 256-bit blocks; 0 when it has none.</summary>
    internal int FilterBlocks { get; }

    /// <summary>The region of its children, or null when none has a word.</summary>
    internal IndexSegment? Children { get; }

    /// <summary>Each child's words: a leaf's filter, or a node's own words; empty for a bare node.</summary>
    internal ReadOnlySpan<uint> ChildWords => _words.Span.Slice(HeaderWords, _childWords);

    /// <summary>Its filter's words; empty when it has none.</summary>
    internal ReadOnlySpan<uint> Filter => _words.Span.Slice(HeaderWords + _childWords, FilterBlocks * SplitBlockBloom.WordsPerBlock);

    /// <summary>
    /// The words of a node with <paramref name="childWords"/> listed and a filter of
    /// <paramref name="filterBlocks"/> blocks: none listed for a bare node.
    /// </summary>
    /// <param name="childWords">The child sizes it lists.</param>
    /// <param name="filterBlocks">Its filter's blocks.</param>
    internal static int WordsOf(int childWords, int filterBlocks) =>
        checked(HeaderWords + childWords + (filterBlocks * SplitBlockBloom.WordsPerBlock));

    /// <summary>Whether a node of <paramref name="level"/> with these children is bare: level 1, no leaf with a filter.</summary>
    /// <remarks>
    /// A bare node lists no child size, which is most of what it would cost: a column with too few
    /// values a block to earn any block filter would otherwise pay sixteen empty words per node.
    /// </remarks>
    /// <param name="level">Its level.</param>
    /// <param name="childWords">Each child's words.</param>
    internal static bool IsBare(int level, ReadOnlySpan<int> childWords) =>
        level == 1 && childWords.IndexOfAnyExcept(0) < 0;

    /// <summary>The blocks one child of a node of <paramref name="level"/> covers, but the last.</summary>
    /// <param name="level">The node's level, at least 1.</param>
    internal static long ChildSpan(int level) => 1L << (4 * (level - 1));

    /// <summary>The children of a node of <paramref name="level"/> over <paramref name="leaves"/> blocks.</summary>
    /// <param name="level">The node's level.</param>
    /// <param name="leaves">Its blocks.</param>
    internal static int ChildrenOf(int level, long leaves)
    {
        long span = ChildSpan(level);
        return (int)((leaves + span - 1) / span);
    }

    /// <summary>The blocks under child <paramref name="child"/> of a node of <paramref name="level"/> over <paramref name="leaves"/>.</summary>
    /// <param name="level">The node's level.</param>
    /// <param name="leaves">Its blocks.</param>
    /// <param name="child">The child.</param>
    internal static long ChildLeaves(int level, long leaves, int child)
    {
        long span = ChildSpan(level);
        return Math.Min(span, leaves - (child * span));
    }

    /// <summary>The root's level over <paramref name="leaves"/> blocks: the least at which one node holds them all.</summary>
    /// <param name="leaves">The tree's blocks, at least 1.</param>
    internal static int LevelFor(long leaves)
    {
        int level = 1;
        while (level < MaxLevel && ChildSpan(level + 1) < leaves)
        {
            level++;
        }

        return level;
    }

    /// <summary>Writes a node into <paramref name="destination"/>, which holds exactly its words.</summary>
    /// <param name="destination">The node's words.</param>
    /// <param name="level">Its level.</param>
    /// <param name="leaves">Its blocks.</param>
    /// <param name="children">Its children's region, or null when none has a word.</param>
    /// <param name="childWords">Each child's words.</param>
    /// <param name="filter">Its filter's words.</param>
    internal static void Write(
        Span<uint> destination, int level, long leaves, IndexSegment? children, ReadOnlySpan<int> childWords,
        ReadOnlySpan<uint> filter)
    {
        bool bare = IsBare(level, childWords);
        destination[0] = LayoutVersion | ((uint)level << 8) | ((uint)childWords.Length << 16) | (bare ? BareFlag : 0);
        destination[1] = checked((uint)leaves);
        destination[2] = (uint)(filter.Length / SplitBlockBloom.WordsPerBlock);
        IndexSegment region = children ?? default;
        destination[3] = unchecked((uint)region.Offset);
        destination[4] = (uint)(region.Offset >> 32);
        destination[5] = region.Length;
        ulong checksum = region.Checksum ?? 0;
        destination[6] = unchecked((uint)checksum);
        destination[7] = (uint)(checksum >> 32);
        destination[8] = region.AlignmentExponent;
        int listed = bare ? 0 : childWords.Length;
        for (int i = 0; i < listed; i++)
        {
            destination[HeaderWords + i] = checked((uint)childWords[i]);
        }

        filter.CopyTo(destination[(HeaderWords + listed)..]);
    }

    /// <summary>
    /// Parses the node held by <paramref name="words"/>[<paramref name="start"/>..], which its parent
    /// says is <paramref name="length"/> words, of <paramref name="level"/>, over <paramref name="leaves"/> blocks.
    /// </summary>
    /// <param name="words">The region's words; kept, not copied, so the node is read only while they are held.</param>
    /// <param name="start">Where the node starts.</param>
    /// <param name="length">Its words.</param>
    /// <param name="level">Its level.</param>
    /// <param name="leaves">Its blocks.</param>
    /// <param name="node">The node.</param>
    /// <returns>Whether the words are that node.</returns>
    internal static bool TryRead(BloomWords words, int start, int length, int level, long leaves, out BloomNode node)
    {
        node = default;
        if (length < HeaderWords || start < 0 || (long)start + length > words.Length || level is < 1 or > MaxLevel || leaves < 1)
        {
            return false;
        }

        BloomWords own = words.Slice(start, length);
        ReadOnlySpan<uint> header = own.Span[..HeaderWords];
        int children = (int)((header[0] >> 16) & 0xFF);
        uint flags = header[0] >> 24;
        bool bare = flags == BareFlag >> 24;
        int listed = bare ? 0 : children;
        if ((header[0] & 0xFF) != LayoutVersion || ((header[0] >> 8) & 0xFF) != level
            || (flags != 0 && !bare) || (bare && level != 1)
            || header[1] != leaves || children != ChildrenOf(level, leaves)
            || children is < 1 or > BloomIndexOptions.Fanout
            || header[2] > BloomBuilderLimits.MaxFilterBlocks
            || length != (long)HeaderWords + listed + ((long)header[2] * SplitBlockBloom.WordsPerBlock)
            || header[8] > 16)
        {
            return false;
        }

        long total = 0;
        foreach (uint child in own.Span.Slice(HeaderWords, listed))
        {
            bool fits = level == 1
                ? child % SplitBlockBloom.WordsPerBlock == 0 && child <= BloomBuilderLimits.MaxFilterBlocks * SplitBlockBloom.WordsPerBlock
                : child > HeaderWords && child <= HeaderWords + BloomIndexOptions.Fanout + (BloomBuilderLimits.MaxFilterBlocks * SplitBlockBloom.WordsPerBlock);
            if (!fits)
            {
                return false;
            }

            total += child;
        }

        ulong offset = header[3] | ((ulong)header[4] << 32);
        ulong checksum = header[6] | ((ulong)header[7] << 32);
        IndexSegment? region = null;
        if (total > 0)
        {
            // The region holds the children's words and the blob around them: never fewer bytes.
            if (header[5] < total * sizeof(uint) || offset + header[5] < offset)
            {
                return false;
            }

            region = new IndexSegment(offset, header[5], (byte)header[8], checksum);
        }
        else if (offset != 0 || header[5] != 0 || checksum != 0)
        {
            return false;
        }

        // A node that lists its children lists at least one word: an all-zero list is a bare node's.
        if (!bare && total == 0 && level == 1)
        {
            return false;
        }

        node = new BloomNode(own, level, leaves, children, bare, (int)header[2], region);
        return true;
    }
}

/// <summary>
/// The options of a filter tree's run: a run's payload is its root, and an array blob does not carry
/// its own length, so the options state how many words the root has.
/// </summary>
internal static class BloomTreeRun
{
    private const int RootWordsField = 1;

    /// <summary>Serializes a run's options.</summary>
    /// <param name="rootWords">The root's words.</param>
    internal static byte[] Options(int rootWords)
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            writer.WriteUInt32Always(RootWordsField, checked((uint)rootWords));
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The root's words a run's options state, or 0 when they state none.</summary>
    /// <param name="bytes">The run's options.</param>
    internal static int RootWords(ReadOnlySpan<byte> bytes)
    {
        try
        {
            ProtoReader reader = new ProtoReader(bytes);
            uint words = 0;
            while (reader.TryReadTag(out int field, out ProtoWireType wire))
            {
                if (field == RootWordsField && wire == ProtoWireType.Varint)
                {
                    words = reader.ReadVarint32();
                }
                else
                {
                    reader.SkipField(wire);
                }
            }

            return words <= int.MaxValue ? (int)words : 0;
        }
        catch (VortexFormatException)
        {
            return 0;
        }
    }
}
