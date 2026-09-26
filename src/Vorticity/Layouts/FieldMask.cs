using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Vorticity.Layouts;

/// <summary>
/// Which fields of a struct subtree a scan wants, as an immutable tree.
/// </summary>
/// <remarks>
/// <para>
/// A mask is either <see cref="All"/> (every field, recursively), <see cref="Empty"/> (no field at
/// all), or a subset naming the wanted fields of the struct at this level, each with its own mask
/// for its subtree. It is a tree because projection paths nest; the scan builds it and the layout
/// readers consume it. <see cref="Descend"/> walks one level down.
/// </para>
/// <para>
/// <c>default(FieldMask)</c> is <see cref="All"/>, deliberately: a reader handed a default mask
/// materializes everything, which is slower than intended but never wrong, where the opposite
/// default would silently drop columns.
/// </para>
/// </remarks>
internal readonly struct FieldMask
{
    private const byte KindAll = 0;
    private const byte KindEmpty = 1;
    private const byte KindSubset = 2;
    private const byte KindSingle = 3;

    private readonly FieldMaskNode? _node;
    private readonly int _field;
    private readonly byte _kind;

    private FieldMask(byte kind, FieldMaskNode? node, int field = 0)
    {
        _kind = kind;
        _node = node;
        _field = field;
    }

    /// <summary>Every field, recursively. This is also <c>default(FieldMask)</c>.</summary>
    public static FieldMask All => default;

    /// <summary>No field at all. A struct read under it still yields its own rows and validity.</summary>
    public static FieldMask Empty => new FieldMask(KindEmpty, null);

    /// <summary>One field of this level, whole, and nothing else: held in the mask itself, with no node.</summary>
    /// <param name="fieldIndex">0-based field index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldIndex"/> is negative.</exception>
    public static FieldMask Single(int fieldIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldIndex);
        return new FieldMask(KindSingle, null, fieldIndex);
    }

    /// <summary><see langword="true"/> when every field is wanted, recursively.</summary>
    public bool IsAll => _kind == KindAll;

    /// <summary><see langword="true"/> when no field is wanted.</summary>
    public bool IsEmpty => _kind == KindEmpty || (_kind == KindSubset && _node!.Count == 0);

    /// <summary>
    /// <see langword="true"/> when field <paramref name="fieldIndex"/> of the struct at this level
    /// is wanted, directly or because a descendant of it is.
    /// </summary>
    /// <param name="fieldIndex">0-based field index in the struct's own dtype order.</param>
    public bool Includes(int fieldIndex)
    {
        if (_kind == KindAll)
        {
            return fieldIndex >= 0;
        }

        if (_kind == KindEmpty)
        {
            return false;
        }

        if (_kind == KindSingle)
        {
            return fieldIndex == _field;
        }

        return _node!.IndexOf(fieldIndex) >= 0;
    }

    /// <summary>The mask to hand field <paramref name="fieldIndex"/>'s subtree.</summary>
    /// <param name="fieldIndex">0-based field index in the struct's own dtype order.</param>
    /// <returns><see cref="Empty"/> when the field is not wanted at all.</returns>
    public FieldMask Descend(int fieldIndex)
    {
        if (_kind == KindAll)
        {
            return fieldIndex >= 0 ? All : Empty;
        }

        if (_kind == KindEmpty)
        {
            return Empty;
        }

        if (_kind == KindSingle)
        {
            return fieldIndex == _field ? All : Empty;
        }

        int slot = _node!.IndexOf(fieldIndex);
        return slot < 0 ? Empty : _node.ChildAt(slot);
    }

    /// <summary>
    /// How many fields this mask names at this level, or <c>-1</c> when it is <see cref="All"/>
    /// and the count therefore depends on the struct it is applied to.
    /// </summary>
    public int NamedFieldCount => _kind switch
    {
        KindAll => -1,
        KindEmpty => 0,
        KindSingle => 1,
        _ => _node!.Count,
    };

    /// <summary>
    /// How many fields of a struct of <paramref name="fieldCount"/> fields this mask selects, which
    /// are the positions <see cref="SelectedField"/> and <see cref="SelectedMask"/> take.
    /// </summary>
    /// <param name="fieldCount">The struct's field count.</param>
    /// <remarks>
    /// A reader walks the selected fields by position rather than every field of the struct asking
    /// <see cref="Includes"/>: a projection of two columns out of a thousand costs two steps and
    /// not a thousand searches, at every batch.
    /// </remarks>
    public int SelectedCount(int fieldCount) => _kind switch
    {
        KindAll => fieldCount,
        KindEmpty => 0,
        KindSingle => _field < fieldCount ? 1 : 0,
        _ => _node!.CountBelow(fieldCount),
    };

    /// <summary>The field at <paramref name="position"/> among those this mask selects, ascending.</summary>
    /// <param name="position">0-based, below <see cref="SelectedCount"/>.</param>
    public int SelectedField(int position) => _kind switch
    {
        KindAll => position,
        KindSingle => _field,
        _ => _node!.FieldAt(position),
    };

    /// <summary>
    /// The mask to hand the subtree of the field at <paramref name="position"/> among those this mask
    /// selects: what <see cref="Descend"/> gives for it, without searching for it again.
    /// </summary>
    /// <param name="position">0-based, below <see cref="SelectedCount"/>.</param>
    public FieldMask SelectedMask(int position) => _kind == KindSubset ? _node!.ChildAt(position) : All;

    /// <summary>The <paramref name="index"/>-th field index this mask names, ascending.</summary>
    /// <param name="index">0-based, below <see cref="NamedFieldCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The mask is <see cref="All"/>, or the index is out of range.</exception>
    public int GetNamedField(int index)
    {
        if (_kind == KindSingle && index == 0)
        {
            return _field;
        }

        if (_kind != KindSubset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, "Only a subset mask names individual fields.");
        }

        return _node!.FieldAt(index);
    }

    internal static FieldMask Subset(FieldMaskNode node) =>
        node.Count == 0 ? Empty : new FieldMask(KindSubset, node);
}

/// <summary>One level of a subset <see cref="FieldMask"/>: the wanted field indices and their subtrees.</summary>
internal sealed class FieldMaskNode
{
    private readonly int[] _fields;
    private readonly FieldMask[]? _children;

    /// <summary>The wanted fields, ascending and distinct, and each one's mask.</summary>
    /// <param name="fields">The field indices.</param>
    /// <param name="children">One mask per field, or null when every field is wanted whole.</param>
    internal FieldMaskNode(int[] fields, FieldMask[]? children)
    {
        _fields = fields;
        _children = children;
    }

    internal int Count => _fields.Length;

    internal int FieldAt(int index)
    {
        if ((uint)index >= (uint)_fields.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Outside the mask.");
        }

        return _fields[index];
    }

    internal FieldMask ChildAt(int slot) => _children is null ? FieldMask.All : _children[slot];

    /// <summary>
    /// How many of the fields lie below <paramref name="fieldCount"/>: all of them, unless the mask
    /// names more fields than the struct has.
    /// </summary>
    internal int CountBelow(int fieldCount)
    {
        int[] fields = _fields;
        if (fields.Length == 0 || fields[^1] < fieldCount)
        {
            return fields.Length;
        }

        int lo = 0;
        int hi = fields.Length;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (fields[mid] < fieldCount)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Binary search: the field list is built ascending and distinct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int IndexOf(int field)
    {
        int[] fields = _fields;
        int lo = 0;
        int hi = fields.Length - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int value = fields[mid];
            if (value == field)
            {
                return mid;
            }

            if (value < field)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }
}

/// <summary>
/// Builds a <see cref="FieldMask"/> from projection paths.
/// </summary>
/// <remarks>
/// The format itself gives <see cref="FieldMask"/> only <see cref="FieldMask.All"/> as a
/// construction path, which leaves the scan's projection compiler with no way to express a partial
/// projection. This builder is the missing piece; it allocates, is used once per scan, and is never
/// touched on a decode path.
/// </remarks>
internal sealed class FieldMaskBuilder
{
    private readonly Level _root = new Level();

    /// <summary>
    /// Includes the field reached by <paramref name="path"/>, a sequence of field indices from the
    /// level this builder describes.
    /// </summary>
    /// <param name="path">
    /// Field indices, outermost first. An empty path selects everything, i.e. makes the result
    /// <see cref="FieldMask.All"/>.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A path element is negative.</exception>
    public FieldMaskBuilder Include(ReadOnlySpan<int> path)
    {
        Level level = _root;
        for (int i = 0; i < path.Length; i++)
        {
            int field = path[i];
            ArgumentOutOfRangeException.ThrowIfNegative(field);
            if (level.All)
            {
                // An ancestor already selected this subtree whole; a narrower path adds nothing.
                return this;
            }

            level = level.Child(field);
        }

        level.SelectAll();
        return this;
    }

    /// <summary>Includes one field of this level, whole.</summary>
    /// <param name="fieldIndex">0-based field index.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldIndex"/> is negative.</exception>
    public FieldMaskBuilder IncludeField(int fieldIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldIndex);
        Span<int> one = stackalloc int[1];
        one[0] = fieldIndex;
        return Include(one);
    }

    /// <summary>Materializes the immutable mask.</summary>
    public FieldMask Build() => _root.Build();

    /// <summary>
    /// Merges an already-built mask into this builder.
    /// </summary>
    /// <param name="mask">The mask to union in.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The scan needs this to read the union of what a filter references and what the caller
    /// projected, while keeping the projection itself intact for the trim afterwards. Building the
    /// union by mutating the projection's own builder would leak the filter's columns into a second
    /// execution from the same builder.
    /// </remarks>
    public FieldMaskBuilder Include(in FieldMask mask)
    {
        Merge(_root, in mask);
        return this;
    }

    private static void Merge(Level level, in FieldMask mask)
    {
        if (mask.IsAll)
        {
            level.SelectAll();
            return;
        }

        int count = mask.NamedFieldCount;
        for (int i = 0; i < count; i++)
        {
            int field = mask.GetNamedField(i);
            Level child = level.Child(field);
            FieldMask childMask = mask.Descend(field);
            Merge(child, in childMask);
        }
    }

    private sealed class Level
    {
        private readonly List<int> _fields = new List<int>();
        private readonly List<Level> _children = new List<Level>();

        internal bool All { get; private set; }

        internal void SelectAll()
        {
            All = true;
            _fields.Clear();
            _children.Clear();
        }

        internal Level Child(int field)
        {
            // Linear scan, and the list stays sorted so Build needs no sort of its own. It costs
            // the square of the field count, which stays negligible against the read it prepares
            // even for a projection of a thousand fields; a map would remove it and add a field to
            // every level.
            int i = 0;
            while (i < _fields.Count && _fields[i] < field)
            {
                i++;
            }

            if (i < _fields.Count && _fields[i] == field)
            {
                return _children[i];
            }

            Level child = new Level();
            _fields.Insert(i, field);
            _children.Insert(i, child);
            return child;
        }

        internal FieldMask Build()
        {
            if (All)
            {
                return FieldMask.All;
            }

            int count = _fields.Count;
            if (count == 0)
            {
                return FieldMask.Empty;
            }

            // The common masks hold no more than they say: one whole field is the mask alone, and
            // fields all wanted whole need no mask of their own each.
            bool wholes = true;
            for (int i = 0; i < count; i++)
            {
                wholes &= _children[i].All;
            }

            if (wholes && count == 1)
            {
                return FieldMask.Single(_fields[0]);
            }

            int[] fields = new int[count];
            FieldMask[]? masks = wholes ? null : new FieldMask[count];
            for (int i = 0; i < count; i++)
            {
                fields[i] = _fields[i];
                if (masks is not null)
                {
                    masks[i] = _children[i].Build();
                }
            }

            return FieldMask.Subset(new FieldMaskNode(fields, masks));
        }
    }
}
