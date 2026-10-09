using System;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// A nested column's rows shredded into the standard's entries: from the top-level field down to the
/// leaf, a row an entry, a list's elements an entry each, every entry with the repetition level at
/// which it starts and the definition level it reaches, and the leaf's row of the value it holds.
/// </summary>
/// <remarks>
/// <para>
/// The walk goes a field at a time over every entry. A field that may be null stops the entries of
/// its null rows, which keep the definition level of the field above it, and raises the others to
/// its own. A list stops the entries of its empty rows likewise, and replaces each other one with
/// an entry per element: the first keeps the row's repetition level, the others repeat at the
/// list's. An entry that stopped is carried down unchanged. A field that may not be null and is
/// given one where its holder is not null is the caller's error.
/// </para>
/// <para>
/// The values are the leaf's rows of the entries that reach it, in entry order: a range of the leaf
/// when its lists lie back to back, as a list's elements mostly do, which the column writer then
/// stages without gathering them.
/// </para>
/// </remarks>
internal sealed class Shredder
{
    private byte[] _repetition = new byte[1024];
    private byte[] _definition = new byte[1024];
    private int[] _rows = new int[1024];
    private byte[] _nextRepetition = new byte[1024];
    private byte[] _nextDefinition = new byte[1024];
    private int[] _nextRows = new int[1024];
    private int[] _valueRows = new int[1024];

    /// <summary>The entries of the rows shredded last.</summary>
    internal int Entries { get; private set; }

    /// <summary>The entries among them that hold a value.</summary>
    internal int Values { get; private set; }

    /// <summary>The leaf's node, whose rows the values are.</summary>
    internal int Leaf { get; private set; }

    /// <summary>Whether the values' rows are a range of the leaf, from <see cref="ValueRows"/>' first.</summary>
    internal bool Contiguous { get; private set; }

    internal ReadOnlySpan<byte> Repetition => _repetition.AsSpan(0, Entries);

    internal ReadOnlySpan<byte> Definition => _definition.AsSpan(0, Entries);

    /// <summary>The leaf's row of each value, in entry order.</summary>
    internal ReadOnlySpan<int> ValueRows => _valueRows.AsSpan(0, Values);

    /// <summary>Shreds <paramref name="count"/> rows of <paramref name="node"/>, the column's top-level field, from <paramref name="start"/>.</summary>
    internal void Shred(CanonicalArena arena, int node, int start, int count, WriteColumn column)
    {
        Grow(ref _repetition, ref _definition, ref _rows, count);
        for (int i = 0; i < count; i++)
        {
            _rows[i] = start + i;
        }

        _repetition.AsSpan(0, count).Clear();
        _definition.AsSpan(0, count).Clear();
        Entries = count;
        int current = node;
        foreach (ShredStep step in column.Steps)
        {
            CanonicalNode field = arena.GetNode(current);
            Define(arena, field, step.Nullable, step.DefinedAt, column);
            if (step.Kind == ShredKind.Struct)
            {
                current = EncodedForms.Canonical(arena, field.GetFieldIndex(step.Child));
                continue;
            }

            current = Expand(arena, field, step);
            if (step.Kind == ShredKind.Map)
            {
                current = EncodedForms.Canonical(arena, arena.GetNode(current).GetFieldIndex(step.Child));
            }
        }

        if (column.ThroughStorage)
        {
            current = EncodedForms.Canonical(arena, arena.GetNode(current).StorageIndex);
        }

        Define(arena, arena.GetNode(current), column.Nullable, column.MaxDefinitionLevel, column);
        Leaf = current;
        Collect();
    }

    /// <summary>
    /// The field at <paramref name="node"/>, for every entry that reaches it: where it is null the
    /// entry stops, else it reaches <paramref name="definedAt"/>.
    /// </summary>
    private void Define(CanonicalArena arena, CanonicalNode node, bool nullable, int definedAt, WriteColumn column)
    {
        Span<int> rows = _rows.AsSpan(0, Entries);
        Span<byte> definition = _definition.AsSpan(0, Entries);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (mask.AllValid)
        {
            if (nullable)
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i] >= 0)
                    {
                        definition[i] = (byte)definedAt;
                    }
                }
            }

            return;
        }

        ReadOnlySpan<byte> bits = mask.Bits;
        int offset = mask.BitOffset;
        bool none = mask.AllInvalid;
        for (int i = 0; i < rows.Length; i++)
        {
            int row = rows[i];
            if (row < 0)
            {
                continue;
            }

            if (!none && CanonicalSupport.BitAt(bits, offset + row))
            {
                definition[i] = (byte)definedAt;
            }
            else if (nullable)
            {
                rows[i] = -1;
            }
            else
            {
                throw new VortexSchemaException($"The column '{string.Join('.', column.Path)}' is not nullable and is given a null.");
            }
        }
    }

    /// <summary>
    /// Replaces every entry at a list's row with an entry per element, or stops it where the list is
    /// empty; the list's elements' node.
    /// </summary>
    private int Expand(CanonicalArena arena, CanonicalNode list, ShredStep step)
    {
        bool fixedSize = list.Kind == CanonicalKind.FixedSizeList;
        if (!fixedSize && list.Kind != CanonicalKind.ListView)
        {
            ArraysThrow.Kind(list.Kind, "ListView or FixedSizeList");
        }

        int elements = EncodedForms.Canonical(arena, list.ElementsIndex);
        long elementCount = arena.GetNode(elements).Length;
        int size = fixedSize ? (int)list.FixedSize : 0;
        ReadOnlySpan<byte> offsets = fixedSize ? default : list.Offsets.Span;
        ReadOnlySpan<byte> sizes = fixedSize ? default : list.Sizes.Span;
        PType offsetType = fixedSize ? default : list.OffsetPType;
        PType sizeType = fixedSize ? default : list.SizePType;

        // A first pass counts the entries the lists make, so that the second writes them in place.
        long total = 0;
        for (int i = 0; i < Entries; i++)
        {
            int row = _rows[i];
            long length = row < 0 ? 0 : fixedSize ? size : CanonicalSupport.ReadInteger(sizes, sizeType, row);
            total += length > 0 ? length : 1;
        }

        if (total > Array.MaxLength)
        {
            ArraysThrow.Format($"Lists of {total} elements do not fit a batch's entries.");
        }

        Grow(ref _nextRepetition, ref _nextDefinition, ref _nextRows, (int)total);
        byte repeatedAt = (byte)step.RepeatedAt;
        byte elementsAt = (byte)step.ElementsAt;
        int at = 0;
        for (int i = 0; i < Entries; i++)
        {
            int row = _rows[i];
            long length = 0;
            long first = 0;
            if (row >= 0)
            {
                length = fixedSize ? size : CanonicalSupport.ReadInteger(sizes, sizeType, row);
                first = fixedSize ? (long)row * size : CanonicalSupport.ReadInteger(offsets, offsetType, row);
                if (length < 0 || first < 0 || first + length > elementCount)
                {
                    ArraysThrow.Format($"A list's elements at {first}, {length} of them, lie outside its {elementCount}.");
                }
            }

            if (length == 0)
            {
                _nextRepetition[at] = _repetition[i];
                _nextDefinition[at] = _definition[i];
                _nextRows[at++] = -1;
                continue;
            }

            _nextRepetition[at] = _repetition[i];
            _nextDefinition[at] = elementsAt;
            _nextRows[at++] = (int)first;
            for (int k = 1; k < length; k++)
            {
                _nextRepetition[at] = repeatedAt;
                _nextDefinition[at] = elementsAt;
                _nextRows[at++] = (int)(first + k);
            }
        }

        (_repetition, _nextRepetition) = (_nextRepetition, _repetition);
        (_definition, _nextDefinition) = (_nextDefinition, _definition);
        (_rows, _nextRows) = (_nextRows, _rows);
        Entries = at;
        return elements;
    }

    /// <summary>The leaf's rows of the entries that reach it, and whether they are a range.</summary>
    private void Collect()
    {
        if (_valueRows.Length < Entries)
        {
            _valueRows = new int[Math.Max(Entries, _valueRows.Length * 2)];
        }

        int values = 0;
        bool contiguous = true;
        for (int i = 0; i < Entries; i++)
        {
            int row = _rows[i];
            if (row >= 0)
            {
                contiguous &= values == 0 || row == _valueRows[values - 1] + 1;
                _valueRows[values++] = row;
            }
        }

        Values = values;
        Contiguous = contiguous;
    }

    private static void Grow(ref byte[] repetition, ref byte[] definition, ref int[] rows, int count)
    {
        if (rows.Length >= count)
        {
            return;
        }

        int size = Math.Max(count, rows.Length * 2);
        repetition = new byte[size];
        definition = new byte[size];
        rows = new int[size];
    }
}
