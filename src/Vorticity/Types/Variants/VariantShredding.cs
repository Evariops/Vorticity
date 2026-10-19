using System;
using System.Collections.Generic;
using System.Text;

namespace Vorticity.Types.Variants;

/// <summary>
/// The class of values a shredded path keeps in its typed column: integers of every width in one
/// 64-bit column, which the format's equivalence classes compare by their number, and the classes of
/// one type each.
/// </summary>
internal enum VariantClass : byte
{
    /// <summary>Integers of 8 to 64 bits, kept as 64-bit integers.</summary>
    Integer,

    /// <summary>64-bit floats.</summary>
    Double,

    /// <summary>UTF-8 text.</summary>
    String,

    /// <summary>Booleans.</summary>
    Boolean,
}

/// <summary>What a node of a shredding is.</summary>
internal enum VariantShreddingKind : byte
{
    /// <summary>Some keys of an object, each with a shredding of its own.</summary>
    Object,

    /// <summary>The elements of a list, each of one class.</summary>
    List,

    /// <summary>The values of one class.</summary>
    Primitive,
}

/// <summary>
/// What a variant column shreds, chosen for the rows a writer holds: of an object, some of its keys,
/// each with what it shreds in turn; of a list, its elements, when every one is of the element's
/// class; of a primitive, one class of values in a typed column. What a row holds beyond the choice
/// stays in the variant's residual, and a value of another class in its field's own values.
/// </summary>
/// <remarks>
/// A list shreds primitives only. The reference pairs the rows' metadata with the elements' values when
/// a list's elements keep values of their own, and refuses the write, so a list whose elements do not
/// all fit stays whole in its field's values.
/// </remarks>
internal sealed class VariantShredding
{
    private VariantShredding(VariantShreddingKind kind, ShreddedKey[] keys, VariantShredding? element, VariantClass @class)
    {
        Kind = kind;
        Keys = keys;
        Element = element;
        Class = @class;
    }

    /// <summary>What the node is.</summary>
    internal VariantShreddingKind Kind { get; }

    /// <summary>An object's shredded keys, by the unsigned order of their UTF-8 bytes; empty otherwise.</summary>
    internal IReadOnlyList<ShreddedKey> Keys { get; }

    /// <summary>A list's elements; null otherwise.</summary>
    internal VariantShredding? Element { get; }

    /// <summary>A primitive's class, or a list's elements' class.</summary>
    internal VariantClass Class { get; }

    /// <summary>The values of <paramref name="class"/> in a typed column.</summary>
    internal static VariantShredding Primitive(VariantClass @class) =>
        new VariantShredding(VariantShreddingKind.Primitive, [], null, @class);

    /// <summary>A list whose elements are all of <paramref name="class"/>, its elements in a typed column.</summary>
    internal static VariantShredding List(VariantClass @class) =>
        new VariantShredding(VariantShreddingKind.List, [], Primitive(@class), @class);

    /// <summary>Some keys of an object, each with its own shredding.</summary>
    /// <exception cref="ArgumentException">No key is given, or one is given twice.</exception>
    internal static VariantShredding Object(params ReadOnlySpan<(string Key, VariantShredding Field)> fields)
    {
        if (fields.IsEmpty)
        {
            throw new ArgumentException("An object's shredding names at least one key.", nameof(fields));
        }

        ShreddedKey[] keys = new ShreddedKey[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(fields[i].Key, nameof(fields));
            ArgumentNullException.ThrowIfNull(fields[i].Field, nameof(fields));
            keys[i] = new ShreddedKey(fields[i].Key, Encoding.UTF8.GetBytes(fields[i].Key), fields[i].Field);
        }

        Array.Sort(keys, static (left, right) => left.Utf8.AsSpan().SequenceCompareTo(right.Utf8));
        for (int i = 1; i < keys.Length; i++)
        {
            if (keys[i].Utf8.AsSpan().SequenceEqual(keys[i - 1].Utf8))
            {
                throw new ArgumentException($"The key '{keys[i].Name}' is shredded twice.", nameof(fields));
            }
        }

        return new VariantShredding(VariantShreddingKind.Object, keys, null, default);
    }

    /// <summary>The position of <paramref name="key"/> among <see cref="Keys"/>, or -1.</summary>
    internal int IndexOf(ReadOnlySpan<byte> key)
    {
        int low = 0;
        int high = Keys.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int order = Keys[middle].Utf8.AsSpan().SequenceCompareTo(key);
            if (order == 0)
            {
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }
}

/// <summary>One shredded key of an object: its name, its UTF-8 bytes, and what it shreds.</summary>
internal sealed record ShreddedKey(string Name, byte[] Utf8, VariantShredding Field);
