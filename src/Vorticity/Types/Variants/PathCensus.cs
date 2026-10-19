using System;
using System.Collections.Generic;
using System.Text;

namespace Vorticity.Types.Variants;

/// <summary>
/// The paths a variant column's rows hold, counted: of every value, its class, and of an object's keys
/// the same, key by key and as deep as objects nest. What a shredding is chosen from.
/// </summary>
/// <remarks>
/// A list counts by the class all its elements share, or as of no class when they do not share one,
/// since only a list whose elements all fit is shredded. Key names are read from a row's metadata once
/// for every run of rows whose metadata bytes are the same.
/// </remarks>
internal sealed class PathCensus
{
    private byte[] _metadata = [];
    private int _metadataLength = -1;
    private string?[] _names = [];

    /// <summary>The counts of the column's values themselves.</summary>
    internal PathCount Root { get; } = new PathCount();

    /// <summary>The rows counted, nulls included.</summary>
    internal long Rows { get; private set; }

    /// <summary>The bytes of the rows' values, as the residual would hold them unshredded.</summary>
    internal long ValueBytes { get; private set; }

    /// <summary>Counts a null row.</summary>
    internal void AddNull() => Rows++;

    /// <summary>Counts a row of <paramref name="value"/>, its keys named by <paramref name="metadata"/>.</summary>
    /// <exception cref="VortexFormatException">The value or the metadata does not decode.</exception>
    internal void Add(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        Rows++;
        ValueBytes += value.Length;
        if (metadata.Length != _metadataLength || !metadata.SequenceEqual(_metadata.AsSpan(0, _metadataLength)))
        {
            if (_metadata.Length < metadata.Length)
            {
                _metadata = new byte[Math.Max(metadata.Length, 2 * _metadata.Length)];
            }

            metadata.CopyTo(_metadata);
            _metadataLength = metadata.Length;
            Array.Clear(_names);
        }

        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        Root.Add(this, in dictionary, value, depth: 1);
    }

    /// <summary>Key <paramref name="id"/> of the current metadata, decoded once per run of rows that share it.</summary>
    private string Name(in VariantDictionary dictionary, int id)
    {
        if (_names.Length < dictionary.Count)
        {
            _names = new string?[Math.Max(dictionary.Count, 2 * _names.Length)];
        }

        return _names[id] ??= Encoding.UTF8.GetString(dictionary[id]);
    }

    /// <summary>The counts of the values at one path: by class, and of an object's keys.</summary>
    internal sealed class PathCount
    {
        /// <summary>Occurrences by class: the four shreddable classes, an object, a list of each class, and anything else.</summary>
        private readonly long[] _classes = new long[Slots];

        private Dictionary<string, PathCount>? _keys;

        private const int ObjectSlot = 4;
        private const int ListSlot = 5;
        private const int OtherSlot = 9;
        private const int EmptyListSlot = 10;
        private const int Slots = 11;

        /// <summary>The values counted at this path.</summary>
        internal long Values { get; private set; }

        /// <summary>The keys of the objects at this path, each with its counts; empty when no object was.</summary>
        internal IReadOnlyDictionary<string, PathCount> Keys => _keys ?? (IReadOnlyDictionary<string, PathCount>)EmptyKeys;

        private static readonly Dictionary<string, PathCount> EmptyKeys = [];

        /// <summary>The values of <paramref name="class"/>.</summary>
        internal long Of(VariantClass @class) => _classes[(int)@class];

        /// <summary>The lists whose elements are all of <paramref name="class"/>, the empty ones included.</summary>
        internal long ListsOf(VariantClass @class) => _classes[ListSlot + (int)@class] + _classes[EmptyListSlot];

        /// <summary>The objects.</summary>
        internal long Objects => _classes[ObjectSlot];

        internal void Add(PathCensus census, in VariantDictionary dictionary, ReadOnlySpan<byte> value, int depth)
        {
            Values++;
            switch (value[0] & 0x03)
            {
                case ParquetVariant.BasicObject:
                {
                    _classes[ObjectSlot]++;
                    if (depth >= VortexLimits.MaxDTypeDepth / 2)
                    {
                        return;
                    }

                    VariantNested fields = VariantNested.Read(value);
                    _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);
                    for (int i = 0; i < fields.Count; i++)
                    {
                        string name = census.Name(in dictionary, fields.IdAt(i));
                        if (!_keys.TryGetValue(name, out PathCount? key))
                        {
                            _keys[name] = key = new PathCount();
                        }

                        key.Add(census, in dictionary, fields.ValueAt(i), depth + 1);
                    }

                    return;
                }

                case ParquetVariant.BasicArray:
                {
                    VariantNested elements = VariantNested.Read(value);
                    if (elements.Count == 0)
                    {
                        _classes[EmptyListSlot]++;
                        return;
                    }

                    int shared = -1;
                    for (int i = 0; i < elements.Count; i++)
                    {
                        int slot = ClassOf(elements.ValueAt(i));
                        if (slot >= ObjectSlot || (shared >= 0 && slot != shared))
                        {
                            shared = OtherSlot;
                            break;
                        }

                        shared = slot;
                    }

                    _classes[shared == OtherSlot ? OtherSlot : ListSlot + shared]++;
                    return;
                }

                default:
                    _classes[ClassOf(value)]++;
                    return;
            }
        }

        /// <summary>The slot of a value that is not an object or a list: its class, or anything else.</summary>
        private static int ClassOf(ReadOnlySpan<byte> value)
        {
            if ((value[0] & 0x03) is ParquetVariant.BasicObject or ParquetVariant.BasicArray)
            {
                return OtherSlot;
            }

            VariantScalar scalar = VariantScalar.Read(value);
            return scalar.Kind switch
            {
                VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => (int)VariantClass.Integer,
                VariantKind.Double => (int)VariantClass.Double,
                VariantKind.String => (int)VariantClass.String,
                VariantKind.Bool => (int)VariantClass.Boolean,
                _ => OtherSlot,
            };
        }
    }
}
