using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// A page of one level's tree as the key walks of a version read it: its entries or its children and
/// the rows before each. Parsed once and kept by the version, so that every walk of it shares the
/// page, the pages below it and its objects' entries, each parsed the first time a walk reaches it.
/// </summary>
/// <remarks>
/// A version holds only the pages its walks read: a seek reads one path of each level, a walk the
/// pages it crosses. Any thread may link a page below or parse an entry; two that do at once make
/// the same, and the first one published is the one every walk keeps.
/// </remarks>
internal sealed class ParsedPage
{
    private readonly IReadOnlyList<TreeEntry>? _entries;
    private readonly IReadOnlyList<InternalEntry>? _children;
    private readonly ParsedPage?[]? _below;
    private readonly ObjectEntry?[]? _objects;
    private readonly long[] _rowsBefore;

    private ParsedPage(IReadOnlyList<TreeEntry>? entries, IReadOnlyList<InternalEntry>? children)
    {
        _entries = entries;
        _children = children;
        Count = entries?.Count ?? children!.Count;
        _rowsBefore = new long[Count + 1];
        for (int i = 0; i < Count; i++)
        {
            _rowsBefore[i + 1] = _rowsBefore[i] + (entries is not null ? entries[i].Rows : children![i].Rows);
        }

        if (entries is not null)
        {
            _objects = new ObjectEntry?[Count];
        }
        else
        {
            _below = new ParsedPage?[Count];
        }
    }

    /// <summary>How many entries or children the page holds.</summary>
    internal int Count { get; }

    /// <summary>Whether the page holds objects rather than children.</summary>
    internal bool IsLeaf => _entries is not null;

    /// <summary>The tree key of a leaf's entry <paramref name="index"/>.</summary>
    internal ReadOnlyMemory<byte> Key(int index) => _entries![index].Key;

    /// <summary>The smallest tree key of entry or child <paramref name="index"/>.</summary>
    internal ReadOnlyMemory<byte> MinKey(int index) => _entries is { } entries ? entries[index].Key : _children![index].MinKey;

    /// <summary>The rows of the entries or children before <paramref name="index"/>.</summary>
    internal long RowsBefore(int index) => _rowsBefore[index];

    /// <summary>The object of a leaf's entry <paramref name="index"/>, parsed the first time it is asked for.</summary>
    internal ObjectEntry ObjectAt(int index)
    {
        ref ObjectEntry? entry = ref _objects![index];
        if (Volatile.Read(ref entry) is { } known)
        {
            return known;
        }

        ObjectEntry parsed = ObjectEntry.FromBytes(_entries![index].Value);
        return Interlocked.CompareExchange(ref entry, parsed, null) ?? parsed;
    }

    /// <summary>How many entries or children start with a bound below <paramref name="sought"/>, or at it when <paramref name="inclusive"/>.</summary>
    internal int Below(ReadOnlySpan<byte> sought, bool inclusive)
    {
        int low = 0;
        int high = Count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            int order = VortexDataset.OrderOf(MinKey(middle)).Span.SequenceCompareTo(sought);
            if (order < 0 || (order == 0 && inclusive))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>How many entries or children lie wholly below <paramref name="key"/>: their largest tree key is below it.</summary>
    internal int Before(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = Count;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            ReadOnlyMemory<byte> largest = _entries is { } entries ? entries[middle].Key : _children![middle].MaxKey;
            if (TreePage.Compare(largest.Span, key) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The page below child <paramref name="index"/>, read the first time a walk asks for it.</summary>
    internal ValueTask<ParsedPage> ChildAsync(int index, IPageSource pages, CancellationToken cancellationToken) =>
        Volatile.Read(ref _below![index]) is { } known ? new ValueTask<ParsedPage>(known) : ReadChildAsync(index, pages, cancellationToken);

    /// <summary>Reads and parses the page <paramref name="reference"/> names.</summary>
    internal static async ValueTask<ParsedPage> ReadAsync(IPageSource pages, PageReference reference, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> bytes = await pages.ReadPageAsync(reference, cancellationToken).ConfigureAwait(false);
        return TreePage.KindOf(bytes.Span) == TreePageKind.Leaf
            ? new ParsedPage(TreePage.ReadLeaf(bytes), null)
            : new ParsedPage(null, TreePage.ReadInternal(bytes));
    }

    private async ValueTask<ParsedPage> ReadChildAsync(int index, IPageSource pages, CancellationToken cancellationToken)
    {
        ParsedPage child = await ReadAsync(pages, _children![index].Child, cancellationToken).ConfigureAwait(false);
        return Interlocked.CompareExchange(ref _below![index], child, null) ?? child;
    }
}
