using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Vorticity.Arrays;

namespace Vorticity.Types.Variants;

/// <summary>
/// The shape of each row of a variant column against the keys an object shreds: for each key, whether
/// the row's object holds it, in the class its typed column holds or in another, and whether anything is
/// left to the residual. Rows of one shape lie in the same ranges of every shredded column, so the shape
/// is one more synopsis column of a slice's order.
/// </summary>
/// <remarks>
/// <para>
/// A key held as data, which the object does not shred, only sets the residual's bit, so a source that
/// puts identifiers in its keys gives its rows one shape, not one each. A row that is no object, or a
/// null value, is all residual; a null row holds nothing.
/// </para>
/// <para>
/// A shape is two bits a key and one for the residual, in words of 64 bits. The codes follow the shapes'
/// order as unsigned words, the first word first, so the same rows code the same way whoever writes them.
/// Key names are matched once per run of rows whose metadata bytes are the same.
/// </para>
/// </remarks>
internal sealed class VariantShapes
{
    private const int Absent = 0;
    private const int Typed = 1;
    private const int Other = 2;

    private readonly VariantShredding _plan;
    private readonly int _words;
    private readonly int _residual;

    // The keys of the current metadata: the plan's index of each dictionary id, -1 for none, -2 unread.
    private byte[] _metadata = [];
    private int _metadataLength = -1;
    private int[] _keys = [];

    /// <summary>The shapes of rows against <paramref name="plan"/>, a shredding of an object.</summary>
    internal VariantShapes(VariantShredding plan)
    {
        _plan = plan;
        _residual = 2 * plan.Keys.Count;
        _words = (_residual / 64) + 1;
    }

    /// <summary>
    /// Each of <paramref name="rows"/> rows' shape code, in the shapes' order, of the variant column node
    /// <paramref name="column"/>, and how many shapes there are.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal (int[] Codes, int Distinct) Codes(CanonicalArena arena, int column, int rows)
    {
        CanonicalNode variant = arena.GetNode(column);
        int metadata = EncodedForms.Canonical(arena, variant.GetFieldIndex(0));
        int value = EncodedForms.Canonical(arena, variant.GetFieldIndex(1));
        CanonicalNode metadataNode = arena.GetNode(metadata);
        CanonicalNode valueNode = arena.GetNode(value);

        int[] codes = new int[rows];
        List<ulong> shapes = [];
        List<int> next = [];
        Dictionary<ulong, int> heads = [];
        ulong[] shape = new ulong[_words];
        for (int row = 0; row < rows; row++)
        {
            Array.Clear(shape);
            if (ArenaWords.IsValid(arena, column, row))
            {
                Shape(
                    Compute.LiteralReader.ViewAt(metadataNode, row),
                    ArenaWords.IsValid(arena, value, row) ? Compute.LiteralReader.ViewAt(valueNode, row) : VariantEncoding.NullValue,
                    shape);
            }

            // A shape of one word is its own key: up to 31 keys, every object but a wide one.
            if (_words == 1)
            {
                if (!heads.TryGetValue(shape[0], out int known))
                {
                    heads[shape[0]] = known = next.Count;
                    shapes.Add(shape[0]);
                    next.Add(-1);
                }

                codes[row] = known;
                continue;
            }

            codes[row] = Intern(shape, shapes, next, heads);
        }

        // The shapes sorted as unsigned words, first word first, and each code turned into its rank.
        int distinct = next.Count;
        int[] byShape = new int[distinct];
        for (int i = 0; i < distinct; i++)
        {
            byShape[i] = i;
        }

        ulong[] all = [.. shapes];
        int words = _words;
        Array.Sort(byShape, (left, right) => all.AsSpan(left * words, words).SequenceCompareTo(all.AsSpan(right * words, words)));
        int[] rank = new int[distinct];
        for (int i = 0; i < distinct; i++)
        {
            rank[byShape[i]] = i;
        }

        for (int row = 0; row < rows; row++)
        {
            codes[row] = rank[codes[row]];
        }

        return (codes, distinct);
    }

    /// <summary>The shape of a row's <paramref name="value"/>, its keys named by <paramref name="metadata"/>, into <paramref name="shape"/>.</summary>
    private void Shape(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value, Span<ulong> shape)
    {
        if ((value[0] & 0x03) != ParquetVariant.BasicObject)
        {
            Set(shape, _residual, 1);
            return;
        }

        if (metadata.Length != _metadataLength || !metadata.SequenceEqual(_metadata.AsSpan(0, _metadataLength)))
        {
            if (_metadata.Length < metadata.Length)
            {
                _metadata = new byte[Math.Max(metadata.Length, 2 * _metadata.Length)];
            }

            metadata.CopyTo(_metadata);
            _metadataLength = metadata.Length;
            _keys.AsSpan().Fill(-2);
        }

        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        if (_keys.Length < dictionary.Count)
        {
            int old = _keys.Length;
            Array.Resize(ref _keys, Math.Max(dictionary.Count, 2 * old));
            _keys.AsSpan(old).Fill(-2);
        }

        VariantNested fields = VariantNested.Read(value);
        for (int i = 0; i < fields.Count; i++)
        {
            int id = fields.IdAt(i);
            int key = _keys[id];
            if (key == -2)
            {
                _keys[id] = key = _plan.IndexOf(dictionary[id]);
            }

            if (key < 0)
            {
                Set(shape, _residual, 1);
                continue;
            }

            Set(shape, 2 * key, Holds(_plan.Keys[key].Field, fields.ValueAt(i)) ? Typed : Other);
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> is of the class the key's shredding <paramref name="field"/> types,
    /// from its header byte alone: a short string or the string primitive, the booleans' primitives, the
    /// four integers', the double's.
    /// </summary>
    private static bool Holds(VariantShredding field, ReadOnlySpan<byte> value) => field.Kind switch
    {
        VariantShreddingKind.Object => (value[0] & 0x03) == ParquetVariant.BasicObject,
        VariantShreddingKind.List => (value[0] & 0x03) == ParquetVariant.BasicArray,
        _ => VariantClasses.OfHeader(value[0]) == field.Class,
    };

    private static void Set(Span<ulong> shape, int bit, int value) => shape[bit >> 6] |= (ulong)value << (bit & 63);

    /// <summary>The index of <paramref name="shape"/> among <paramref name="shapes"/>, added when new.</summary>
    private int Intern(ulong[] shape, List<ulong> shapes, List<int> next, Dictionary<ulong, int> heads)
    {
        ulong hash = XxHash3.HashToUInt64(MemoryMarshal.AsBytes(shape.AsSpan()));
        int index = heads.TryGetValue(hash, out int head) ? head : -1;
        ReadOnlySpan<ulong> all = CollectionsMarshal.AsSpan(shapes);
        for (int at = index; at >= 0; at = next[at])
        {
            if (all.Slice(at * _words, _words).SequenceEqual(shape))
            {
                return at;
            }
        }

        int added = next.Count;
        shapes.AddRange(shape);
        next.Add(index);
        heads[hash] = added;
        return added;
    }
}
