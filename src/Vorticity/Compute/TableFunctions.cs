using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// A small table of a column's values to texts (spec G, 5), <c>endpoint → team</c>: its keys, texts or integers, each with
/// the number of its image among the table's distinct images, those in the order of their bytes. Built once from the
/// application's dictionary; a value the table does not hold has no image, a null.
/// </summary>
internal sealed class ValueTable
{
    private readonly Dictionary<long, int>? _integers;
    private readonly int[] _slots = [];
    private readonly byte[][] _texts = [];
    private readonly int _mask;

    private ValueTable(FilterLiteral[] keys, byte[]?[] utf8Keys, long[]? integerKeys, string[] images)
    {
        // The images, distinct, in the order of their bytes; each key with the number of its own.
        byte[][] encoded = new byte[images.Length][];
        for (int i = 0; i < images.Length; i++)
        {
            encoded[i] = Encoding.UTF8.GetBytes(images[i]);
        }

        byte[][] distinct = [.. encoded];
        Array.Sort(distinct, static (a, b) => a.AsSpan().SequenceCompareTo(b));
        int count = 0;
        for (int i = 0; i < distinct.Length; i++)
        {
            if (count == 0 || !distinct[i].AsSpan().SequenceEqual(distinct[count - 1]))
            {
                distinct[count++] = distinct[i];
            }
        }

        Images = distinct[..count];
        Keys = keys;
        ImageOf = new int[keys.Length];
        for (int k = 0; k < keys.Length; k++)
        {
            ImageOf[k] = Array.BinarySearch(Images, encoded[k], ByteOrder.Instance);
        }

        if (integerKeys is not null)
        {
            _integers = new Dictionary<long, int>(integerKeys.Length);
            for (int k = 0; k < integerKeys.Length; k++)
            {
                _integers[integerKeys[k]] = ImageOf[k];
            }
        }
        else
        {
            // An open table of the keys' bytes, twice their number of slots at least.
            int size = (int)Math.Max(4, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, keys.Length * 2)));
            _slots = new int[size];
            Array.Fill(_slots, -1);
            _mask = size - 1;
            _texts = new byte[keys.Length][];
            for (int k = 0; k < keys.Length; k++)
            {
                _texts[k] = utf8Keys[k]!;
                int slot = (int)(XxHash3.HashToUInt64(_texts[k]) & (ulong)_mask);
                while (_slots[slot] >= 0)
                {
                    slot = (slot + 1) & _mask;
                }

                _slots[slot] = k;
            }
        }

        XxHash3 identity = new XxHash3();
        for (int k = 0; k < keys.Length; k++)
        {
            identity.Append(integerKeys is null ? _texts[k] : BitConverter.GetBytes(integerKeys[k]));
            identity.Append([0]);
            identity.Append(encoded[k]);
            identity.Append([0]);
        }

        Text = $"Map({keys.Length}#{identity.GetCurrentHashAsUInt64():x16})";
    }

    /// <summary>The keys, as literals of the column's values, in the table's order.</summary>
    internal FilterLiteral[] Keys { get; }

    /// <summary>Each key's image, by its number among <see cref="Images"/>.</summary>
    internal int[] ImageOf { get; }

    /// <summary>The distinct images, as UTF-8, in the order of their bytes.</summary>
    internal byte[][] Images { get; }

    /// <summary>The table as a plan shows it and an identity compares it: its size and a hash of its pairs.</summary>
    internal string Text { get; }

    /// <summary>The table of a text column's values.</summary>
    /// <exception cref="ArgumentException">An image is null.</exception>
    internal static ValueTable Of(IReadOnlyDictionary<string, string> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        List<(byte[] Key, string Image)> pairs = new List<(byte[], string)>(table.Count);
        foreach ((string key, string image) in table)
        {
            ArgumentNullException.ThrowIfNull(key);
            pairs.Add((Encoding.UTF8.GetBytes(key), image ?? throw new ArgumentException($"The table maps '{key}' to no text: leave the key out.", nameof(table))));
        }

        pairs.Sort(static (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        FilterLiteral[] keys = new FilterLiteral[pairs.Count];
        byte[]?[] utf8 = new byte[]?[pairs.Count];
        string[] images = new string[pairs.Count];
        for (int k = 0; k < keys.Length; k++)
        {
            keys[k] = FilterLiteral.From(pairs[k].Key);
            utf8[k] = pairs[k].Key;
            images[k] = pairs[k].Image;
        }

        return new ValueTable(keys, utf8, null, images);
    }

    /// <summary>The table of an integer column's values.</summary>
    /// <exception cref="ArgumentException">An image is null.</exception>
    internal static ValueTable Of<TKey>(IReadOnlyDictionary<TKey, string> table)
        where TKey : System.Numerics.IBinaryInteger<TKey>
    {
        ArgumentNullException.ThrowIfNull(table);
        List<(long Key, string Image)> pairs = new List<(long, string)>(table.Count);
        foreach ((TKey key, string image) in table)
        {
            pairs.Add((long.CreateChecked(key), image ?? throw new ArgumentException($"The table maps {key} to no text: leave the key out.", nameof(table))));
        }

        pairs.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        FilterLiteral[] keys = new FilterLiteral[pairs.Count];
        long[] integers = new long[pairs.Count];
        string[] images = new string[pairs.Count];
        for (int k = 0; k < keys.Length; k++)
        {
            keys[k] = FilterLiteral.From(pairs[k].Key);
            integers[k] = pairs[k].Key;
            images[k] = pairs[k].Image;
        }

        return new ValueTable(keys, new byte[]?[keys.Length], integers, images);
    }

    /// <summary>Whether the table's keys are texts.</summary>
    internal bool TextKeys => _integers is null;

    /// <summary>The number of the image of <paramref name="key"/>, a text's bytes; -1 for none.</summary>
    internal int ImageOfText(ReadOnlySpan<byte> key)
    {
        int slot = (int)(XxHash3.HashToUInt64(key) & (ulong)_mask);
        while (_slots[slot] is int at and >= 0)
        {
            if (_texts[at].AsSpan().SequenceEqual(key))
            {
                return ImageOf[at];
            }

            slot = (slot + 1) & _mask;
        }

        return -1;
    }

    /// <summary>The number of the image of the integer <paramref name="key"/>; -1 for none.</summary>
    internal int ImageOfInteger(long key) => _integers!.TryGetValue(key, out int image) ? image : -1;

    /// <summary>The order of bytes, ordinal.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        internal static readonly ByteOrder Instance = new ByteOrder();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>
/// A column's values through a table (<see cref="ValueTable"/>, spec G, 5): <c>r.Endpoint.Map(teams)</c>. Read, it is a
/// dictionary of the table's images, its codes a row each: on a dictionary, the table applies to its distinct values once
/// and the codes are translated; elsewhere, a value looked up a row. A filter never reads it: a predicate on it becomes one
/// on the column (<see cref="TableFunctions.Preimages"/>), which the column's structures prune by.
/// </summary>
internal sealed class MappedFieldExpr : FieldExpr
{
    private static readonly DType TextType = new DTypeArena().Utf8(Nullability.Nullable);
    private static readonly DType BitType = new DTypeArena().Bool(Nullability.NonNullable);

    internal MappedFieldExpr(FieldExpr column, ValueTable table)
        : base(column.Segments ?? column.Path.Split('.'))
    {
        Column = column;
        Table = table;
    }

    /// <summary>The column, or the function of it, the table applies to.</summary>
    internal FieldExpr Column { get; }

    internal ValueTable Table { get; }

    internal override string Key => $"{Column.Key}.{Table.Text}";

    /// <summary>The same table over another reference to the column: its name in an object of a dataset.</summary>
    internal MappedFieldExpr Over(FieldExpr column) =>
        new MappedFieldExpr(Column is FunctionFieldExpr inner ? inner.Over(column) : column, Table);

    /// <summary>The images of the column's node <paramref name="node"/>, a dictionary of the table's, null where it has none.</summary>
    internal int Apply(CanonicalArena arena, int node)
    {
        if (Column is FunctionFieldExpr inner)
        {
            node = inner.Apply(arena, node);
        }

        node = ComparisonKernels.Unwrap(arena, node);
        CanonicalNode source = arena.GetNode(node);
        int rows = source.Length;
        if (source.Kind == CanonicalKind.Null || rows == 0)
        {
            return arena.AddNull(TextType, rows);
        }

        VortexBuffer codesBuffer = arena.AllocateUninitialized(rows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> codes = MemoryMarshal.Cast<byte, uint>(codeBytes);
        VortexBuffer validBuffer = arena.Allocate(Math.Max(CanonicalSupport.BitmapByteCount(rows), 1), 1, out Span<byte> valid);
        ValidityMask mask = ValidityMask.From(arena, source.Validity);
        int mapped = 0;
        if (source.Kind == CanonicalKind.Dictionary)
        {
            // The table applies to the dictionary's values once, and the codes are translated a row each.
            int values = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> from);
            int distinct = arena.GetNode(values).Length;
            int[] translated = ArrayPool<int>.Shared.Rent(Math.Max(distinct, 1));
            try
            {
                Translate(arena, values, translated.AsSpan(0, distinct));
                for (int row = 0; row < rows; row++)
                {
                    int image = mask.IsValid(row) ? translated[from[row]] : -1;
                    mapped += Set(codes, valid, row, image);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(translated);
            }
        }
        else
        {
            node = EncodedForms.Canonical(arena, node);
            source = arena.GetNode(node);
            mapped = Lookup(source, ValidityMask.From(arena, source.Validity), codes, valid);
        }

        Validity validity = mapped == rows ? Validity.AllValid
            : mapped == 0 ? Validity.AllInvalid
            : Validity.Bitmap(arena.AddBool(BitType, rows, Validity.NonNullable, validBuffer, 0));
        return arena.AddDictionary(TextType, rows, validity, codesBuffer, Images(arena));
    }

    /// <summary>Each value of <paramref name="values"/>, a dictionary's, by the number of its image; -1 for none.</summary>
    private void Translate(CanonicalArena arena, int values, Span<int> into)
    {
        int canonical = EncodedForms.Canonical(arena, ComparisonKernels.Unwrap(arena, values));
        CanonicalNode node = arena.GetNode(canonical);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        Span<uint> codes = MemoryMarshal.Cast<int, uint>(into);
        Span<byte> valid = stackalloc byte[0];
        byte[]? rented = null;
        try
        {
            int bytes = Math.Max(CanonicalSupport.BitmapByteCount(into.Length), 1);
            valid = bytes <= 256 ? stackalloc byte[bytes] : (rented = ArrayPool<byte>.Shared.Rent(bytes)).AsSpan(0, bytes);
            valid.Clear();
            Lookup(node, mask, codes, valid);

            // A value of no image is -1, the codes of those with one their numbers.
            for (int i = 0; i < into.Length; i++)
            {
                if (!CanonicalSupport.BitAt(valid, i))
                {
                    into[i] = -1;
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// The images of the values of <paramref name="node"/>, a canonical column, as their numbers in <paramref name="codes"/>
    /// and a bit in <paramref name="valid"/> each that has one; how many have. A column of another kind than the table's keys
    /// has none.
    /// </summary>
    private int Lookup(CanonicalNode node, ValidityMask mask, Span<uint> codes, Span<byte> valid)
    {
        if (node.Kind == CanonicalKind.VarBinView)
        {
            return Table.TextKeys ? Texts(node, in mask, codes, valid) : None(codes);
        }

        if (node.Kind != CanonicalKind.Primitive || Table.TextKeys)
        {
            return None(codes);
        }

        ReadOnlySpan<byte> raw = node.Values.Span;
        return node.PType switch
        {
            PType.I8 => Integers(MemoryMarshal.Cast<byte, sbyte>(raw), in mask, codes, valid),
            PType.I16 => Integers(MemoryMarshal.Cast<byte, short>(raw), in mask, codes, valid),
            PType.I32 => Integers(MemoryMarshal.Cast<byte, int>(raw), in mask, codes, valid),
            PType.I64 => Integers(MemoryMarshal.Cast<byte, long>(raw), in mask, codes, valid),
            PType.U8 => Integers(raw, in mask, codes, valid),
            PType.U16 => Integers(MemoryMarshal.Cast<byte, ushort>(raw), in mask, codes, valid),
            PType.U32 => Integers(MemoryMarshal.Cast<byte, uint>(raw), in mask, codes, valid),
            PType.U64 => Integers(MemoryMarshal.Cast<byte, ulong>(raw), in mask, codes, valid),
            _ => None(codes),
        };
    }

    /// <summary>The images of a text column's values, a lookup a row; how many have one.</summary>
    private int Texts(CanonicalNode node, in ValidityMask mask, Span<uint> codes, Span<byte> valid)
    {
        int mapped = 0;
        for (int row = 0; row < codes.Length; row++)
        {
            mapped += Set(codes, valid, row, mask.IsValid(row) ? Table.ImageOfText(LiteralReader.ViewAt(node, row)) : -1);
        }

        return mapped;
    }

    /// <summary>The images of an integer column's values, a lookup a row; how many have one.</summary>
    private int Integers<T>(ReadOnlySpan<T> values, in ValidityMask mask, Span<uint> codes, Span<byte> valid)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        int mapped = 0;
        for (int row = 0; row < codes.Length; row++)
        {
            // A value past what a key holds, an unsigned one above the largest signed, has no image.
            T value = values[row];
            long key = long.CreateTruncating(value);
            int image = mask.IsValid(row) && (key >= 0 || T.IsNegative(value)) ? Table.ImageOfInteger(key) : -1;
            mapped += Set(codes, valid, row, image);
        }

        return mapped;
    }

    /// <summary>No row with an image: a column the table's keys are not of the kind of.</summary>
    private static int None(Span<uint> codes)
    {
        codes.Clear();
        return 0;
    }

    /// <summary>Row <paramref name="row"/>'s code, and its bit when it has an image; how many rows that makes, 0 or 1.</summary>
    private static int Set(Span<uint> codes, Span<byte> valid, int row, int image)
    {
        if (image < 0)
        {
            codes[row] = 0;
            return 0;
        }

        codes[row] = (uint)image;
        CanonicalSupport.SetBit(valid, row);
        return 1;
    }

    /// <summary>The table's images as a column of views, in the arena of the batch that reads them.</summary>
    private int Images(CanonicalArena arena)
    {
        byte[][] images = Table.Images;
        if (images.Length == 0)
        {
            // A dictionary's values are never empty while a row names one: an image no row names.
            images = [[]];
        }

        int bytes = 0;
        foreach (byte[] image in images)
        {
            bytes += image.Length > 12 ? image.Length : 0;
        }

        VortexBuffer views = arena.Allocate(images.Length * 16, 16, out Span<byte> viewBytes);
        VortexBuffer data = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> heap);
        int offset = 0;
        for (int i = 0; i < images.Length; i++)
        {
            byte[] image = images[i];
            if (CanonicalSupport.WriteView(viewBytes.Slice(i * 16, 16), image, image.Length, 0, offset))
            {
                image.CopyTo(heap[offset..]);
                offset += image.Length;
            }
        }

        return arena.AddVarBinView(TextType.WithNullability(Nullability.NonNullable), images.Length, Validity.NonNullable, views, [data], padded: true);
    }
}

/// <summary>
/// The predicates on a column through a table, as predicates on the column itself (spec G, 5): the keys whose image meets
/// one, an <c>IN</c> the column's localisation, postings and zone maps prune by.
/// </summary>
internal static class TableFunctions
{
    /// <summary>
    /// <paramref name="filter"/> with every predicate on a column through a table (<see cref="MappedFieldExpr"/>) as one on
    /// the column: the keys whose image meets it. Its negations are pushed down to its predicates first, so that a value the
    /// table does not hold, whose image is null, is kept by no predicate on the image, negated or not, as over the image
    /// itself. The filter itself when it reads no table.
    /// </summary>
    internal static VortexExpr? Preimages(VortexExpr? filter) =>
        filter is null || !ReadsTable(filter) ? filter : Rewrite(filter, negated: false);

    private static bool ReadsTable(VortexExpr expr) => expr switch
    {
        LogicalExpr logical => ReadsTable(logical.Left) || ReadsTable(logical.Right),
        NotExpr not => ReadsTable(not.Operand),
        ComparisonExpr { Field: MappedFieldExpr } or InExpr { Field: MappedFieldExpr } or NullCheckExpr { Field: MappedFieldExpr }
            or StringMatchExpr { Field: MappedFieldExpr } => true,
        _ => false,
    };

    private static VortexExpr Rewrite(VortexExpr expr, bool negated)
    {
        switch (expr)
        {
            case NotExpr not:
                return Rewrite(not.Operand, !negated);
            case LogicalExpr logical:
                // De Morgan: under a negation an AND is an OR of the negations, and the other way round.
                return Expr.Logical(logical.IsAnd != negated, Rewrite(logical.Left, negated), Rewrite(logical.Right, negated));
            case NullCheckExpr { Field: MappedFieldExpr mapped } check:
            {
                // Whether the image is null is never unknown: a null row, or a value the table holds none for.
                VortexExpr held = Keys(mapped, static (_, _) => true);
                return check.IsNull != negated ? Expr.Logical(false, new NullCheckExpr(mapped.Column, true), new NotExpr(held)) : held;
            }

            case ComparisonExpr { Field: MappedFieldExpr mapped } comparison:
                return comparison.Value.Kind == FilterLiteralKind.Null
                    ? Nothing(mapped)
                    : Keys(mapped, (image, _) => Compare(image, comparison.Op, comparison.Value.BytesValue) != negated);
            case InExpr { Field: MappedFieldExpr mapped } @in:
                return Keys(mapped, (image, _) => In(image, @in.Literals) != negated);
            case StringMatchExpr { Field: MappedFieldExpr mapped } match:
                return Keys(mapped, (image, _) => Matches(image, match) != negated);
            default:
                return negated ? new NotExpr(expr) : expr;
        }
    }

    /// <summary>The column of <paramref name="mapped"/> among the keys whose image meets <paramref name="kept"/>; nothing for none.</summary>
    private static VortexExpr Keys(MappedFieldExpr mapped, Func<byte[], int, bool> kept)
    {
        ValueTable table = mapped.Table;
        List<FilterLiteral> keys = [];
        for (int k = 0; k < table.Keys.Length; k++)
        {
            if (kept(table.Images[table.ImageOf[k]], k))
            {
                keys.Add(table.Keys[k]);
            }
        }

        return keys.Count == 0 ? Nothing(mapped) : Expr.In(mapped.Column, [.. keys]);
    }

    /// <summary>A predicate no row of the column meets: below its least value, unknown on a null.</summary>
    private static VortexExpr Nothing(MappedFieldExpr mapped) =>
        new ComparisonExpr(mapped.Column, ComparisonOp.Less, mapped.Table.TextKeys ? FilterLiteral.From(string.Empty) : FilterLiteral.From(long.MinValue));

    private static bool Compare(byte[] image, ComparisonOp op, ReadOnlySpan<byte> value)
    {
        int order = image.AsSpan().SequenceCompareTo(value);
        return op switch
        {
            ComparisonOp.Equal => order == 0,
            ComparisonOp.NotEqual => order != 0,
            ComparisonOp.Less => order < 0,
            ComparisonOp.LessOrEqual => order <= 0,
            ComparisonOp.Greater => order > 0,
            _ => order >= 0,
        };
    }

    private static bool In(byte[] image, FilterLiteral[] literals)
    {
        foreach (FilterLiteral literal in literals)
        {
            if (literal.Kind == FilterLiteralKind.Bytes && image.AsSpan().SequenceEqual(literal.BytesValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(byte[] image, StringMatchExpr match) => match.Op switch
    {
        StringMatchOp.StartsWith => BytePattern.StartsWith(image, match.Pattern.BytesValue),
        StringMatchOp.Contains => BytePattern.Contains(image, match.Pattern.BytesValue),
        StringMatchOp.Token => BytePattern.HasToken(image, match.Pattern.BytesValue),
        _ => BytePattern.LikeText(image, match.Pattern.BytesValue, match.Escape),
    };
}
