using System;
using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity;

/// <summary>One value of a column of any mapped type, for the rare path that does not know the family: a constant, a key.</summary>
internal static class ColumnReader
{
    internal static T Read<T>(Column<T> column, int index)
    {
        if (!column.IsValid(index))
        {
            return default!;
        }

        return (T)ReadBoxed(ClrShape.For<T>.Value, column.Arena, column.Node, column.Type, column.Extensions, index);
    }

    internal static object ReadBoxed(ClrShape shape, CanonicalArena arena, int node, VortexType type, VortexExtensionRegistry? extensions, int index)
    {
        if (type.Kind == VortexTypeKind.Extension && extensions is not null && extensions.TryGet(type.ExtensionId!, out ExtensionRegistration registration))
        {
            CanonicalNode storage = arena.GetNode(EncodedForms.Canonical(arena, node));
            int width = storage.PType.ByteWidth();
            return registration.FromStorage(storage.Values.Span.Slice(index * width, width), type.ExtensionMetadata.Span);
        }

        switch (shape.Kind)
        {
            case ClrKind.Bool:
            {
                CanonicalNode bits = arena.GetNode(EncodedForms.Canonical(arena, node));
                int bit = bits.BitOffset + index;
                return ((bits.Bits.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
            }

            case ClrKind.Signed:
            case ClrKind.Unsigned:
            case ClrKind.Float:
                return Numeric(shape.PType, ColumnData.Values(arena, node), index);
            case ClrKind.String:
                return ColumnData.String(arena, node, index)!;
            case ClrKind.Binary:
                return new ReadOnlyMemory<byte>(ColumnData.Bytes(arena, node, index).ToArray());
            case ClrKind.Decimal:
                return ColumnData.Decimal(arena, node, index);
            case ClrKind.VortexDecimal:
                return ColumnData.Wide(arena, node, index);
            case ClrKind.DateOnly:
                return Temporal.Date(arena, node, type, index);
            case ClrKind.TimeOnly:
                return Temporal.Time(arena, node, type, index);
            case ClrKind.DateTime:
                return Temporal.Timestamp(arena, node, type, index);
            case ClrKind.DateTimeOffset:
                return Temporal.Zoned(arena, node, type, index);
            case ClrKind.Guid:
                return ColumnData.Guid(arena, node, index);
            default:
                throw new NotSupportedException($"A single value of {shape.Type} is read through its column's accessors.");
        }
    }

    private static object Numeric(PType ptype, ReadOnlySpan<byte> values, int index) => ptype switch
    {
        PType.U8 => values[index],
        PType.U16 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(values[(index * 2)..]),
        PType.U32 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(values[(index * 4)..]),
        PType.U64 => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(values[(index * 8)..]),
        PType.I8 => (sbyte)values[index],
        PType.I16 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(values[(index * 2)..]),
        PType.I32 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(values[(index * 4)..]),
        PType.I64 => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(values[(index * 8)..]),
        PType.F16 => System.Buffers.Binary.BinaryPrimitives.ReadHalfLittleEndian(values[(index * 2)..]),
        PType.F32 => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(values[(index * 4)..]),
        _ => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(values[(index * 8)..]),
    };
}
