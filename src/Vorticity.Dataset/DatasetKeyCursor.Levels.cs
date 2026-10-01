using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

internal sealed partial class DatasetKeyCursor
{
    /// <summary>
    /// Where a walk stands among the objects of one level: a place between two of them, in the order
    /// of their tree keys, which is the order of their bounds. A seek finds it by one path down the
    /// level's tree, and a step moves it past one object, reading a page only when it crosses into
    /// one.
    /// </summary>
    /// <remarks>
    /// The place is a path from the root. Above the deepest page on it, each page says which child the
    /// path goes through; the deepest says before which of its entries, or of its children, the place
    /// lies. A place between two children the walk has not entered lies in their parent, so that a
    /// seek to either end of the level reads its root alone, and the key of the object past the place
    /// is still known: the smallest of its subtree, which the parent carries.
    /// </remarks>
    private sealed class LevelWalk
    {
        private readonly DatasetKeyCursor _owner;
        private readonly Frame[] _path;
        private int _deepest = -1;

        // The tree key of the object past the place, copied out of its page when the place moves: a
        // walk compares it at every step, and the place moves only when the walk takes an object.
        private byte[] _next = [];
        private int _nextLength;

        internal LevelWalk(DatasetKeyCursor owner, int number, DatasetTree tree)
        {
            _owner = owner;
            _path = new Frame[tree.Depth];
            Number = number;
        }

        /// <summary>The level's number.</summary>
        internal int Number { get; }

        /// <summary>Whether the level's objects are key-disjoint, in the order of their keys.</summary>
        internal bool InKeyOrder => DatasetLevels.InKeyOrder(Number);

        /// <summary>Whether an object lies past the place.</summary>
        internal bool HasNext => _nextLength > 0;

        /// <summary>The tree key of the object just past the place; empty when none is, or before the first seek.</summary>
        internal ReadOnlySpan<byte> NextKey => _next.AsSpan(0, _nextLength);

        /// <summary>The bound of the object just past the place, its tree key without the uid; empty when none is.</summary>
        internal ReadOnlySpan<byte> NextBound => _next.AsSpan(0, Math.Max(_nextLength - VortexDataset.UidBytes, 0));

        /// <summary>Whether an object lies before the place.</summary>
        internal bool HasPrevious
        {
            get
            {
                for (int height = _deepest; height >= 0; height--)
                {
                    if (_path[height].At > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>The level's root page, which the version reads the first time a walk asks; null for an empty level.</summary>
        internal ValueTask<ParsedPage?> RootAsync(CancellationToken cancellationToken) => _owner._version.RootAsync(Number, cancellationToken);

        /// <summary>Places the walk before the first object under <paramref name="root"/>, the level's root.</summary>
        internal void Start(ParsedPage root) => Place(0, root, 0);

        /// <summary>Places the walk after the last object under <paramref name="root"/>, the level's root.</summary>
        internal void End(ParsedPage root) => Place(0, root, root.Count);

        /// <summary>
        /// Places the walk past every object under <paramref name="root"/>, the level's root, whose
        /// bound is below <paramref name="sought"/>, or at it when <paramref name="inclusive"/>; with
        /// <paramref name="before"/>, before the last of them instead. With none, the walk stands
        /// before the level's first object.
        /// </summary>
        internal async ValueTask SeekAsync(ParsedPage root, byte[] sought, bool inclusive, bool before, CancellationToken cancellationToken)
        {
            ParsedPage page = root;
            for (int height = 0; ; height++)
            {
                int below = page.Below(sought, inclusive);
                if (below == 0 || page.IsLeaf)
                {
                    Place(height, page, below > 0 && before ? below - 1 : below);
                    return;
                }

                // The last of them lies in the last child whose first object is one: those past it
                // start past the key.
                Place(height, page, below - 1);
                page = await page.ChildAsync(below - 1, _owner._version.Pages, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Moves the place past the object just after it, and returns that object's state.</summary>
        internal async ValueTask<Slot> TakeNextAsync(CancellationToken cancellationToken)
        {
            // Up to the page with a child past the one the path goes through...
            if (_path[_deepest].At == _path[_deepest].Page.Count)
            {
                int height = _deepest - 1;
                while (_path[height].At + 1 == _path[height].Page.Count)
                {
                    height--;
                }

                Place(height, _path[height].Page, _path[height].At + 1);
            }

            // ...then down the leftmost path of the child past the place, to the leaf it starts with.
            while (!_path[_deepest].Page.IsLeaf)
            {
                Frame frame = _path[_deepest];
                ParsedPage child = await frame.Page.ChildAsync(frame.At, _owner._version.Pages, cancellationToken).ConfigureAwait(false);
                Place(_deepest + 1, child, 0);
            }

            Frame leaf = _path[_deepest];
            _path[_deepest].At = leaf.At + 1;
            Moved();
            return _owner.SlotOf(leaf.Page, leaf.Slots!, leaf.At, Number);
        }

        /// <summary>Moves the place before the object just before it, and returns that object's state.</summary>
        internal async ValueTask<Slot> TakePreviousAsync(CancellationToken cancellationToken)
        {
            // Up to the page with a child before the one the path goes through, the place lying
            // before that one...
            if (_path[_deepest].At == 0)
            {
                int height = _deepest - 1;
                while (_path[height].At == 0)
                {
                    height--;
                }

                _deepest = height;
            }

            // ...then down the rightmost path of the child before the place, to the leaf it ends with.
            while (!_path[_deepest].Page.IsLeaf)
            {
                Frame frame = _path[_deepest];
                ParsedPage child = await frame.Page.ChildAsync(frame.At - 1, _owner._version.Pages, cancellationToken).ConfigureAwait(false);
                _path[_deepest].At = frame.At - 1;
                Place(_deepest + 1, child, child.Count);
            }

            Frame leaf = _path[_deepest];
            _path[_deepest].At = leaf.At - 1;
            Moved();
            return _owner.SlotOf(leaf.Page, leaf.Slots!, leaf.At - 1, Number);
        }

        /// <summary>
        /// The rows of the level's objects whose tree key is below <paramref name="key"/>: one path
        /// down, the children wholly below the key counted from their parent's entries.
        /// </summary>
        internal async ValueTask<long> RowsBeforeAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
        {
            long rows = 0;
            for (ParsedPage? page = await RootAsync(cancellationToken).ConfigureAwait(false); page is not null;)
            {
                // The first entry or child not wholly below the key holds some of its rows only when
                // it starts below it.
                int below = page.Before(key.Span);
                rows += page.RowsBefore(below);
                if (page.IsLeaf || below == page.Count || TreePage.Compare(page.MinKey(below).Span, key.Span) >= 0)
                {
                    break;
                }

                page = await page.ChildAsync(below, _owner._version.Pages, cancellationToken).ConfigureAwait(false);
            }

            return rows;
        }

        /// <summary>Makes <paramref name="page"/> the deepest on the path, at <paramref name="height"/>, the place before its entry or child <paramref name="at"/>.</summary>
        private void Place(int height, ParsedPage page, int at)
        {
            // A leaf's state is the walk's own: the frame keeps it, found once each time the path
            // enters another leaf.
            Slot?[]? slots = !page.IsLeaf ? null : _path[height].Page == page ? _path[height].Slots : _owner.SlotsOf(page);
            _path[height] = new Frame(page, at, slots);
            _deepest = height;
            Moved();
        }

        /// <summary>
        /// Finds the tree key past the place: the deepest page's entry or child past it, or above it,
        /// the child past the one the path goes through.
        /// </summary>
        private void Moved()
        {
            _nextLength = 0;
            for (int height = _deepest; height >= 0; height--)
            {
                Frame frame = _path[height];
                int next = height == _deepest ? frame.At : frame.At + 1;
                if (next < frame.Page.Count)
                {
                    ReadOnlySpan<byte> key = frame.Page.MinKey(next).Span;
                    if (key.Length > _next.Length)
                    {
                        _next = new byte[key.Length];
                    }

                    key.CopyTo(_next);
                    _nextLength = key.Length;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A page on a level walk's path, and the entry or child the place, or the path, is at there; a
    /// leaf's with the walk's state of its objects.
    /// </summary>
    private struct Frame(ParsedPage page, int at, Slot?[]? slots)
    {
        internal readonly ParsedPage Page = page;
        internal readonly Slot?[]? Slots = slots;
        internal int At = at;
    }
}
