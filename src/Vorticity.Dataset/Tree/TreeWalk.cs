using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// One tree's entries in key order, where a subtree the walk's test rules out stands as one step:
/// its key span and its rows, which its parent's entry carries. A merge of several trees counts the
/// rows of such a step without reading the subtree, as long as no step of another tree falls inside
/// its span; when one does, the rows interleave, and the merge has the subtree read after all with
/// <see cref="Expand"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pages are read a window ahead, as <see cref="DatasetTree.WalkAsync"/> reads them, and a subtree
/// ruled out is not read until it is expanded. Its children meet the same test once it is, so only
/// the parts of it that interleave with another tree are read down to their leaves.
/// </para>
/// <para>
/// A step names its page by its parent's page and its place in it, a few words where it held its parent's entry
/// and its reference, 150 bytes, which a walk's stack grew by for each child of each page it crossed; and a page the
/// source holds in hand is taken as it lies, where a read was a task a page whatever it cost.
/// </para>
/// </remarks>
internal sealed class TreeWalk : IAsyncDisposable
{
    private readonly IPageSource _source;
    private readonly Func<InternalEntry, bool>? _descend;
    private readonly CancellationToken _cancellationToken;
    private readonly PageReference _root;

    /// <summary>The walk's future, its top last: pages to read and subtrees ruled out.</summary>
    private readonly List<Pending> _stack = [];

    private IReadOnlyList<TreeEntry>? _leaf;
    private int _next;

    // The ruled-out step, which Expand pushes back.
    private Pending _subtree;

    /// <summary>
    /// A walk of <paramref name="tree"/> that keeps out of a subtree when
    /// <paramref name="descend"/> says false of its entry; a null test reads every subtree.
    /// </summary>
    internal TreeWalk(DatasetTree tree, IPageSource source, Func<InternalEntry, bool>? descend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _descend = descend;
        _cancellationToken = cancellationToken;
        _root = tree.Root;
        if (!tree.IsEmpty)
        {
            _stack.Add(new Pending(null, 0, tree.Depth, RuledOut: false));
        }
    }

    /// <summary>Whether the step is a subtree ruled out, rather than an entry.</summary>
    internal bool IsRuledOut { get; private set; }

    /// <summary>The step's entry, when it is one.</summary>
    internal TreeEntry Entry { get; private set; }

    /// <summary>The step's subtree, when it is one ruled out: its parent's entry for it.</summary>
    internal InternalEntry Subtree { get; private set; }

    /// <summary>The step's smallest key: the entry's, or the subtree's.</summary>
    internal ReadOnlySpan<byte> Key => IsRuledOut ? Subtree.MinKey.Span : Entry.Key.Span;

    /// <summary>The step's rows: the entry's, or every row of the subtree.</summary>
    internal long Rows => IsRuledOut ? Subtree.Rows : Entry.Rows;

    /// <summary>Moves to the next step; false once the tree has no more.</summary>
    internal async ValueTask<bool> MoveNextAsync()
    {
        while (true)
        {
            if (_leaf is not null && _next < _leaf.Count)
            {
                IsRuledOut = false;
                Entry = _leaf[_next++];
                return true;
            }

            _leaf = null;
            if (_stack.Count == 0)
            {
                return false;
            }

            // The window's pages fetched: taken as they lie when the source holds them, read otherwise.
            for (int ahead = _stack.Count - 1; ahead >= Math.Max(0, _stack.Count - DatasetTree.PrefetchWindow); ahead--)
            {
                Pending step = _stack[ahead];
                if (!step.RuledOut && !step.Fetched)
                {
                    PageReference reference = step.Reference(_root);
                    _stack[ahead] = _source.TryGetInHand(reference, out ReadOnlyMemory<byte> held)
                        ? step with { Fetched = true, Bytes = held }
                        : step with { Fetched = true, Read = _source.ReadPageAsync(reference, _cancellationToken).AsTask() };
                }
            }

            Pending top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            if (top.RuledOut)
            {
                IsRuledOut = true;
                Subtree = top.Parent;
                _subtree = top;
                return true;
            }

            ReadOnlyMemory<byte> bytes = top.Read is { } read ? await read.ConfigureAwait(false) : top.Bytes;
            if (top.Level == 1)
            {
                _leaf = TreePage.ReadLeaf(bytes);
                _next = 0;
                continue;
            }

            // Pushed in reverse so that popping walks the children in key order.
            IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(bytes);
            for (int i = page.Count - 1; i >= 0; i--)
            {
                bool ruledOut = _descend is not null && !_descend(page[i]);
                _stack.Add(new Pending(page, i, top.Level - 1, ruledOut));
            }
        }
    }

    /// <summary>Has the subtree of the step read after all: the steps that follow walk it, under the same test.</summary>
    /// <exception cref="InvalidOperationException">The step is not a subtree ruled out.</exception>
    internal void Expand()
    {
        if (!IsRuledOut)
        {
            throw new InvalidOperationException("Only a subtree the walk ruled out is read after all.");
        }

        _stack.Add(_subtree with { RuledOut = false });
        IsRuledOut = false;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // A walk stopped early leaves reads in flight: each is observed, so that one that fails
        // after nobody wants it is not an unobserved exception.
        foreach (Pending pending in _stack)
        {
            _ = pending.Read?.ContinueWith(
                static read => read.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _stack.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A page the walk has yet to read, at <paramref name="Level"/> above the leaves: child <paramref name="Index"/> of
    /// <paramref name="Page"/>, or the root when it is null; or a subtree it ruled out. Once fetched, its bytes in hand or
    /// the read of them.
    /// </summary>
    private readonly record struct Pending(IReadOnlyList<InternalEntry>? Page, int Index, int Level, bool RuledOut)
    {
        internal bool Fetched { get; init; }

        internal ReadOnlyMemory<byte> Bytes { get; init; }

        internal Task<ReadOnlyMemory<byte>>? Read { get; init; }

        /// <summary>The parent's entry for the page: its key span, its rows and its reference.</summary>
        internal InternalEntry Parent => Page![Index];

        /// <summary>The page's reference: its parent's entry's, or <paramref name="root"/>.</summary>
        internal PageReference Reference(PageReference root) => Page is null ? root : Page[Index].Child;
    }
}
