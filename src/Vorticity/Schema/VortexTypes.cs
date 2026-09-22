using System;
using Vorticity.Types;

namespace Vorticity;

/// <summary>The bridge between the public <see cref="VortexType"/> and the engine's arena dtypes.</summary>
internal static class VortexTypes
{
    internal static VortexSchema SchemaOf(DType root)
    {
        if (root.Kind != DTypeKind.Struct)
        {
            return VortexSchema.Single(FromDType(root));
        }

        VortexField[] fields = new VortexField[root.FieldCount];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new VortexField(root.GetFieldName(i), FromDType(root.GetField(i)));
        }

        return VortexSchema.Create(fields);
    }

    internal static VortexType FromDType(DType d)
    {
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
                    fields[i] = new VortexField(d.GetFieldName(i), FromDType(d.GetField(i)));
                }

                type = VortexType.Struct(fields);
                break;
            }

            case DTypeKind.List:
                type = VortexType.List(FromDType(d.ElementType));
                break;
            case DTypeKind.FixedSizeList:
                type = VortexType.FixedSizeList(FromDType(d.ElementType), checked((int)d.FixedSize));
                break;
            case DTypeKind.Extension:
                return string.IsNullOrEmpty(d.ExtensionId)
                    ? throw new VortexFormatException("An extension dtype names no extension id.")
                    : VortexType.Extension(d.ExtensionId, FromDType(d.StorageType), d.ExtensionMetadata.ToArray());
            case DTypeKind.Map:
                return VortexType.Map(FromDType(d.KeyType), FromDType(d.ValueType), nullable);
            case DTypeKind.Union:
            {
                VortexField[] variants = new VortexField[d.FieldCount];
                for (int i = 0; i < variants.Length; i++)
                {
                    variants[i] = new VortexField(d.GetFieldName(i), FromDType(d.GetField(i)));
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
}
