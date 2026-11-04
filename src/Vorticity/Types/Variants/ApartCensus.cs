using System;
using System.Numerics;
using Vorticity.Arrays;

namespace Vorticity.Types.Variants;

/// <summary>
/// The paths of a shredded variant column counted as its shredding holds them, not put back together: the group the Parquet
/// standard shreds a variant into (<c>metadata</c>, <c>value</c>, <c>typed_value</c>), a typed column counted a word of 64
/// rows at a time by its validity, and the values the typed columns leave, the residual's fields and a key's values of other
/// forms, walked as they lie. The rows put back together, a row of a log's attributes shredded into some four hundred keys
/// reading each key's column for every row, cost what reading an edge granule again cost; so counted, a granule costs the
/// words of its typed columns and the residual's bytes.
/// </summary>
/// <remarks>
/// A path counts what a census of the rows put back together counts (<see cref="PathCensus"/>): a row's value by the class a
/// question reads it as, an object's keys as paths below it, a list as a path of its own and its elements as none; a row the
/// column holds no value for counts nothing.
/// </remarks>
internal static class ApartCensus
{
    /// <summary>Whether the node <paramref name="node"/> of <paramref name="arena"/> is a variant's group as its shredding holds it.</summary>
    internal static bool IsGroup(CanonicalArena arena, int node)
    {
        DType type = arena.GetNode(node).DType;
        return type.Kind == DTypeKind.Struct && type.FieldCount == 3
            && type.GetFieldNameUtf8(0).SequenceEqual("metadata"u8) && type.GetFieldNameUtf8(1).SequenceEqual("value"u8)
            && type.GetFieldNameUtf8(2).SequenceEqual("typed_value"u8);
    }

    /// <summary>
    /// Adds to <paramref name="into"/> the paths of the rows of the group <paramref name="group"/> that <paramref name="kept"/>
    /// selects, as rows kept, and those <paramref name="left"/> selects, as others: a bit a row of the group's
    /// <paramref name="rows"/>, the bits past them clear.
    /// </summary>
    internal static void Add(CanonicalArena arena, int group, int rows, ReadOnlySpan<ulong> kept, ReadOnlySpan<ulong> left, EdgeCounts into)
    {
        int words = (rows + 63) >> 6;
        Walker walker = new Walker(arena, into);
        ulong[] keep = new ulong[words];
        ulong[] other = new ulong[words];
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, group);
        for (int w = 0; w < words; w++)
        {
            ulong held = valid.IsEmpty ? ulong.MaxValue : valid[w];
            keep[w] = kept[w] & held;
            other[w] = left[w] & held;
        }

        CanonicalNode node = arena.GetNode(group);
        walker.Metadata = new ViewValues(arena.GetNode(EncodedForms.Canonical(arena, node.GetFieldIndex(0))));
        walker.Root(EncodedForms.Canonical(arena, node.GetFieldIndex(1)), arena.Decoded(node.GetFieldIndex(2)), keep, other);
    }

    /// <summary>The class a question reads the variant value <paramref name="value"/> as, from its header, as a census counts it.</summary>
    internal static PathClass ClassOf(ReadOnlySpan<byte> value)
    {
        byte header = value[0];
        switch (header & 0x03)
        {
            case ParquetVariant.BasicShortString:
                return PathClass.String;
            case ParquetVariant.BasicObject:
                return PathClass.Object;
            case ParquetVariant.BasicArray:
                return PathClass.List;
        }

        return (header >> 2) switch
        {
            0 => PathClass.Null,
            1 or 2 => PathClass.Boolean,
            >= 3 and <= 6 => PathClass.Integer,
            7 or 14 => PathClass.Double,
            12 or 18 => PathClass.Timestamp,
            16 => PathClass.String,
            _ => PathClass.Other,
        };
    }

    /// <summary>The class a question reads a typed column of <paramref name="type"/> as.</summary>
    private static PathClass ClassOf(DType type) => type.Kind switch
    {
        DTypeKind.Bool => PathClass.Boolean,
        DTypeKind.Primitive when type.PType is PType.F16 or PType.F32 or PType.F64 => PathClass.Double,
        DTypeKind.Primitive => PathClass.Integer,
        DTypeKind.Utf8 => PathClass.String,
        DTypeKind.Struct => PathClass.Object,
        DTypeKind.List or DTypeKind.FixedSizeList => PathClass.List,
        DTypeKind.Null => PathClass.Null,
        _ => PathClass.Other,
    };

    /// <summary>A walk of a group's columns, its path built in a key, its counts added to the edges.</summary>
    private ref struct Walker(CanonicalArena arena, EdgeCounts into)
    {
        private readonly PathKey _key = new PathKey();

        /// <summary>The rows' metadata, which the keys of a value walked as it lies are named by.</summary>
        internal ViewValues Metadata;

        /// <summary>The group's value and typed value: an object's typed fields and its residual's, or a value whole.</summary>
        internal void Root(int value, int typed, ReadOnlySpan<ulong> keep, ReadOnlySpan<ulong> other)
        {
            int words = keep.Length;
            ReadOnlySpan<ulong> typedValid = ArenaWords.Validity(arena, typed);
            ulong[] typedKeep = new ulong[words];
            ulong[] typedOther = new ulong[words];
            for (int w = 0; w < words; w++)
            {
                ulong held = typedValid.IsEmpty ? ulong.MaxValue : typedValid[w];
                typedKeep[w] = keep[w] & held;
                typedOther[w] = other[w] & held;
            }

            // A row the typed value holds: its class, and below it an object's typed fields; its residual's fields beside them.
            Typed(0, typed, typedKeep, typedOther);
            Values(0, value, keep, other, typedKeep, typedOther, root: true);
        }

        /// <summary>
        /// The rows of the typed column <paramref name="typed"/> the selections keep, already within its validity, counted at the
        /// path built so far: by its class, and an object's fields below it.
        /// </summary>
        private void Typed(int length, int typed, ReadOnlySpan<ulong> keep, ReadOnlySpan<ulong> other)
        {
            CanonicalNode node = arena.GetNode(typed);
            PathClass @class = ClassOf(node.DType);
            Add(length, @class, Count(keep), Count(other));
            if (@class != PathClass.Object)
            {
                return;
            }

            for (int f = 0; f < node.FieldCount; f++)
            {
                int at = _key.Append(length, node.DType.GetFieldNameUtf8(f));
                Field(at, arena.Decoded(node.GetFieldIndex(f)), keep, other);
            }
        }

        /// <summary>
        /// A field's group, a struct of its <c>value</c>, its <c>typed_value</c> or both, over the rows the object holds: those
        /// whose typed value it holds counted as typed, those whose value it holds as values.
        /// </summary>
        private void Field(int length, int group, ReadOnlySpan<ulong> keep, ReadOnlySpan<ulong> other)
        {
            CanonicalNode node = arena.GetNode(group);
            int value = -1;
            int typed = -1;
            for (int f = 0; f < node.FieldCount; f++)
            {
                ReadOnlySpan<byte> name = node.DType.GetFieldNameUtf8(f);
                if (name.SequenceEqual("typed_value"u8))
                {
                    typed = arena.Decoded(node.GetFieldIndex(f));
                }
                else if (name.SequenceEqual("value"u8))
                {
                    value = EncodedForms.Canonical(arena, node.GetFieldIndex(f));
                }
            }

            // The rows the object holds the field in: a group of its value alone wears the absent rows as its own nulls.
            int words = keep.Length;
            ReadOnlySpan<ulong> present = ArenaWords.Validity(arena, group);
            if (!present.IsEmpty)
            {
                ulong[] heldKeep = new ulong[words];
                ulong[] heldOther = new ulong[words];
                for (int w = 0; w < words; w++)
                {
                    heldKeep[w] = keep[w] & present[w];
                    heldOther[w] = other[w] & present[w];
                }

                keep = heldKeep;
                other = heldOther;
            }

            ulong[] typedKeep = new ulong[words];
            ulong[] typedOther = new ulong[words];
            if (typed >= 0)
            {
                ReadOnlySpan<ulong> held = ArenaWords.Validity(arena, typed);
                for (int w = 0; w < words; w++)
                {
                    ulong valid = held.IsEmpty ? ulong.MaxValue : held[w];
                    typedKeep[w] = keep[w] & valid;
                    typedOther[w] = other[w] & valid;
                }

                Typed(length, typed, typedKeep, typedOther);
            }

            if (value >= 0)
            {
                Values(length, value, keep, other, typedKeep, typedOther, root: false);
            }
        }

        /// <summary>
        /// The rows of the binary values <paramref name="value"/> the selections keep, walked as they lie at the path built so
        /// far: a row the typed value holds as an object, its residual's fields alone below it, the object counted already;
        /// any other, its value whole. At a <paramref name="root"/>, a row neither holds is the variant null.
        /// </summary>
        private void Values(int length, int value, ReadOnlySpan<ulong> keep, ReadOnlySpan<ulong> other, ReadOnlySpan<ulong> typedKeep, ReadOnlySpan<ulong> typedOther, bool root)
        {
            ReadOnlySpan<ulong> held = ArenaWords.Validity(arena, value);
            ViewValues values = new ViewValues(arena.GetNode(value));
            for (int w = 0; w < keep.Length; w++)
            {
                ulong valid = held.IsEmpty ? ulong.MaxValue : held[w];
                for (int pass = 0; pass < 2; pass++)
                {
                    bool kept = pass == 0;
                    ulong selected = kept ? keep[w] : other[w];
                    ulong underTyped = kept ? typedKeep[w] : typedOther[w];
                    ulong bits = selected & valid;
                    while (bits != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        int row = (w << 6) + bit;
                        Walk(length, Metadata.At(row), values.At(row), kept, fieldsOnly: (underTyped & (1UL << bit)) != 0);
                    }

                    if (root)
                    {
                        // A row neither column holds is the variant null.
                        ulong neither = selected & ~valid & ~underTyped;
                        if (neither != 0)
                        {
                            Add(length, PathClass.Null, kept ? BitOperations.PopCount(neither) : 0, kept ? 0 : BitOperations.PopCount(neither));
                        }
                    }
                }
            }
        }

        /// <summary>A value as it lies at the path built so far: its class, and an object's keys below it; with <paramref name="fieldsOnly"/>, its keys alone.</summary>
        private void Walk(int length, ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value, bool kept, bool fieldsOnly, int depth = 1)
        {
            PathClass @class = ClassOf(value);
            if (!fieldsOnly)
            {
                Add(length, @class, kept ? 1 : 0, kept ? 0 : 1);
            }

            if (@class != PathClass.Object || depth >= VortexLimits.MaxDTypeDepth / 2)
            {
                return;
            }

            VariantDictionary dictionary = VariantDictionary.Read(metadata);
            VariantNested fields = VariantNested.Read(value);
            for (int i = 0; i < fields.Count; i++)
            {
                int at = _key.Append(length, dictionary[fields.IdAt(i)]);
                Walk(at, metadata, fields.ValueAt(i), kept, fieldsOnly: false, depth + 1);
            }
        }

        private readonly void Add(int length, PathClass @class, long kept, long left)
        {
            if (kept + left > 0)
            {
                into.Add(_key.Span(_key.Close(length, @class)), kept, left);
            }
        }

        private static long Count(ReadOnlySpan<ulong> words)
        {
            long count = 0;
            foreach (ulong word in words)
            {
                count += BitOperations.PopCount(word);
            }

            return count;
        }
    }
}
