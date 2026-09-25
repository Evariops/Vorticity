using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.Types;

namespace Vorticity;

/// <summary>The bridge between the public <see cref="VortexType"/> and the engine's arena dtypes.</summary>
internal static class VortexTypes
{
    /// <summary>
    /// The name bytes one conversion spells out at most. A writer stores every name it spells, so
    /// only names sharing one string spell out more than their buffer holds, and a few kilobytes of
    /// them would otherwise spell out gigabytes.
    /// </summary>
    private const long MaxSpelledNameBytes = 64L << 20;

    internal static VortexSchema SchemaOf(DType root)
    {
        Budget budget = default;
        if (root.Kind != DTypeKind.Struct)
        {
            return VortexSchema.Single(Convert(root, ref budget));
        }

        VortexField[] fields = new VortexField[root.FieldCount];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new VortexField(budget.Name(root, i), Convert(root.GetField(i), ref budget));
        }

        return VortexSchema.Create(fields);
    }

    internal static VortexType FromDType(DType d)
    {
        Budget budget = default;
        return Convert(d, ref budget);
    }

    private static VortexType Convert(DType d, ref Budget budget)
    {
        budget.Visit();
        bool nullable = d.IsNullable;
        VortexType type;
        switch (d.Kind)
        {
            case DTypeKind.Null:
                return VortexType.Null;
            case DTypeKind.Bool:
                type = VortexType.Bool;
                break;
            case DTypeKind.Primitive:
                type = VortexType.Primitive(d.PType);
                break;
            case DTypeKind.Decimal:
                type = VortexType.Decimal(d.Precision, d.Scale);
                break;
            case DTypeKind.Utf8:
                type = VortexType.Utf8;
                break;
            case DTypeKind.Binary:
                type = VortexType.Binary;
                break;
            case DTypeKind.Struct:
            {
                VortexField[] fields = new VortexField[d.FieldCount];
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = new VortexField(budget.Name(d, i), Convert(d.GetField(i), ref budget));
                }

                type = VortexType.Struct(fields);
                break;
            }

            case DTypeKind.List:
                type = VortexType.List(Convert(d.ElementType, ref budget));
                break;
            case DTypeKind.FixedSizeList:
                type = VortexType.FixedSizeList(Convert(d.ElementType, ref budget), checked((int)d.FixedSize));
                break;
            case DTypeKind.Extension:
            {
                string id = budget.ExtensionId(d);
                return id.Length == 0
                    ? throw new VortexFormatException("An extension dtype names no extension id.")
                    : VortexType.Extension(id, Convert(d.StorageType, ref budget), d.ExtensionMetadata.ToArray());
            }

            case DTypeKind.Map:
                return VortexType.Map(Convert(d.KeyType, ref budget), Convert(d.ValueType, ref budget), nullable);
            case DTypeKind.Union:
            {
                VortexField[] variants = new VortexField[d.FieldCount];
                for (int i = 0; i < variants.Length; i++)
                {
                    variants[i] = new VortexField(budget.Name(d, i), Convert(d.GetField(i), ref budget));
                }

                return VortexType.Union(variants, nullable);
            }

            case DTypeKind.Variant:
                return VortexType.Variant(nullable);
            default:
                throw new VortexUnsupportedException(d.Kind.ToString(), ComponentKind.DType);
        }

        return nullable ? type.Nullable : type;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSpelledOut() => throw new VortexFormatException(
        $"The dtype spells out to more than {VortexLimits.MaxFlatBufferTables} types or {MaxSpelledNameBytes} name bytes: " +
        "no stored dtype does, only one whose tables or names are shared.");

    internal static DType ToDType(VortexSchema schema, DTypeArena arena)
    {
        if (!schema.RootIsStruct)
        {
            return ToDType(schema[0].Type, arena);
        }

        string[] names = new string[schema.Count];
        DType[] fields = new DType[schema.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = schema[i].Name;
            fields[i] = ToDType(schema[i].Type, arena);
        }

        return arena.Struct(names, fields, Nullability.NonNullable);
    }

    internal static DType ToDType(VortexType type, DTypeArena arena)
    {
        Nullability nullability = type.IsNullable ? Nullability.Nullable : Nullability.NonNullable;
        switch (type.Kind)
        {
            case VortexTypeKind.Null:
                return arena.Null(Nullability.Nullable);
            case VortexTypeKind.Bool:
                return arena.Bool(nullability);
            case VortexTypeKind.Primitive:
                return arena.Primitive(type.PrimitiveType, nullability);
            case VortexTypeKind.Decimal:
                return arena.Decimal((byte)type.Precision, (sbyte)type.Scale, nullability);
            case VortexTypeKind.Utf8:
                return arena.Utf8(nullability);
            case VortexTypeKind.Binary:
                return arena.Binary(nullability);
            case VortexTypeKind.Struct:
            {
                ReadOnlySpan<VortexField> source = type.Fields;
                string[] names = new string[source.Length];
                DType[] fields = new DType[source.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = source[i].Name;
                    fields[i] = ToDType(source[i].Type, arena);
                }

                return arena.Struct(names, fields, nullability);
            }

            case VortexTypeKind.List:
                return arena.List(ToDType(type.ElementType!, arena), nullability);
            case VortexTypeKind.FixedSizeList:
                return arena.FixedSizeList(ToDType(type.ElementType!, arena), (uint)type.FixedSize, nullability);
            case VortexTypeKind.Extension:
                return arena.Extension(type.ExtensionId!, ToDType(type.StorageType!, arena), type.ExtensionMetadata.Span);
            case VortexTypeKind.Map:
                return arena.Map(ToDType(type.Fields[0].Type, arena), ToDType(type.Fields[1].Type, arena), keysSorted: false, nullability);
            case VortexTypeKind.Variant:
                return arena.Variant(nullability);
            default:
                throw new VortexUnsupportedException(type.Kind.ToString(), ComponentKind.DType, "A union cannot be written: its type ids are not part of the public type.");
        }
    }

    /// <summary>What one conversion has spelled out: the types, and the bytes of the names.</summary>
    /// <remarks>
    /// The engine walks a dtype once per node, while its public form spells out every path through
    /// it: a dtype whose tables are shared level after level spells out 2^depth types. No stored
    /// dtype spells out more types than a FlatBuffer may hold tables.
    /// </remarks>
    private struct Budget
    {
        private int _types;
        private long _nameBytes;

        internal void Visit()
        {
            if (++_types > VortexLimits.MaxFlatBufferTables)
            {
                ThrowSpelledOut();
            }
        }

        internal string Name(DType d, int index) => Spell(d.GetFieldNameUtf8(index));

        internal string ExtensionId(DType d) => Spell(d.ExtensionIdUtf8);

        private string Spell(ReadOnlySpan<byte> utf8)
        {
            _nameBytes += utf8.Length;
            if (_nameBytes > MaxSpelledNameBytes)
            {
                ThrowSpelledOut();
            }

            return Encoding.UTF8.GetString(utf8);
        }
    }
}
