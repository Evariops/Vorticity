using System;
using System.Buffers.Binary;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity;

/// <summary>One little-endian primitive value as a filter literal.</summary>
internal static class PrimitiveLiterals
{
    internal static FilterLiteral Of(PType ptype, ReadOnlySpan<byte> bytes) => ptype switch
    {
        PType.U8 => FilterLiteral.From((ulong)bytes[0]),
        PType.U16 => FilterLiteral.From((ulong)BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
        PType.U32 => FilterLiteral.From((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes)),
        PType.U64 => FilterLiteral.From(BinaryPrimitives.ReadUInt64LittleEndian(bytes)),
        PType.I8 => FilterLiteral.From((long)(sbyte)bytes[0]),
        PType.I16 => FilterLiteral.From((long)BinaryPrimitives.ReadInt16LittleEndian(bytes)),
        PType.I32 => FilterLiteral.From((long)BinaryPrimitives.ReadInt32LittleEndian(bytes)),
        PType.I64 => FilterLiteral.From(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
        PType.F16 => FilterLiteral.From((double)BinaryPrimitives.ReadHalfLittleEndian(bytes)),
        PType.F32 => FilterLiteral.From((double)BinaryPrimitives.ReadSingleLittleEndian(bytes)),
        _ => FilterLiteral.From(BinaryPrimitives.ReadDoubleLittleEndian(bytes)),
    };
}
