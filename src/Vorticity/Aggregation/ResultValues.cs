using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The values of a result column as the .NET type that reads or fills it, a batch at a time: what a
/// result of one value delivers, and what an aggregate's answers are written through. The test on
/// the type is resolved once per instantiation.
/// </summary>
internal static class ResultValues
{
    /// <summary>Whether a column holds values of <typeparamref name="T"/>, as a result: a scalar type of the mapping table, or a record.</summary>
    internal static bool IsColumnType<T>(IVortexRecord? record) =>
        record is not null || ClrShape.For<T>.Value.Kind is not (ClrKind.Unsupported or ClrKind.List or ClrKind.Extension);

    /// <summary>Appends <paramref name="values"/> to <paramref name="store"/>, each converted to the column's storage.</summary>
    /// <param name="store">The column's store, an extension's or a leaf.</param>
    /// <param name="values">The values, one per row.</param>
    /// <param name="count">How many of <paramref name="values"/> to append.</param>
    /// <param name="record">How to write a record, when <typeparamref name="T"/> is one.</param>
    internal static void Append<T>(ColumnStore store, T[] values, int count, IVortexRecord? record)
    {
        if (record is not null)
        {
            ColumnStore leaf = store.Leaf;
            StructStore structure = leaf as StructStore
                ?? throw new VortexSchemaException($"A record of {typeof(T).Name} is written to a struct column, not to {leaf.Type}.");
            record.WriteRecords(structure, values, count);
            return;
        }

        ElementWriter.Append(store.Leaf, new ReadOnlySpan<T>(values, 0, count));
    }

    /// <summary>Copies every row of column <paramref name="node"/> into <paramref name="destination"/>, a null or the default for a null row.</summary>
    /// <param name="batch">The batch the column belongs to.</param>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="node">The column's node.</param>
    /// <param name="type">The column's type.</param>
    /// <param name="extensions">The session's registered extensions.</param>
    /// <param name="destination">At least the column's rows.</param>
    /// <param name="record">How to read a record, when <typeparamref name="T"/> is one.</param>
    internal static void Copy<T>(RecordBatch? batch, CanonicalArena arena, int node, VortexType type, VortexExtensionRegistry? extensions, T[] destination, IVortexRecord? record)
    {
        // A date, a time, a timestamp or a uuid is its storage plus a label, as a column reads it.
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        int rows = arena.RecordRef(node).Length;
        Span<T> into = destination.AsSpan(0, rows);
        if (record is not null)
        {
            CopyRecords(batch, arena, node, type, extensions, destination, rows, record);
            return;
        }

        if (IsPrimitive<T>())
        {
            ReadOnlySpan<byte> bytes = ColumnData.Values(arena, node);
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(bytes)), rows).CopyTo(into);
            return;
        }

        if (CopyNullablePrimitive(arena, node, into))
        {
            return;
        }

        if (typeof(T) == typeof(bool))
        {
            ColumnData.CopyBits(arena, ValuesNode(arena, node), As<T, bool>(into));
        }
        else if (typeof(T) == typeof(bool?))
        {
            ColumnData.CopyBits(arena, node, ValuesNode(arena, node), As<T, bool?>(into));
        }
        else if (typeof(T) == typeof(string))
        {
            ColumnData.CopyStrings(arena, node, into);
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>) || typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            CopyBytes(arena, node, into);
        }
        else if (typeof(T) == typeof(decimal))
        {
            ColumnData.CopyDecimals(arena, node, As<T, decimal>(into));
        }
        else if (typeof(T) == typeof(decimal?))
        {
            ColumnData.CopyDecimals(arena, node, As<T, decimal?>(into));
        }
        else if (typeof(T) == typeof(VortexDecimal))
        {
            ColumnData.CopyWide(arena, node, As<T, VortexDecimal>(into));
        }
        else if (typeof(T) == typeof(VortexDecimal?))
        {
            ColumnData.CopyWide(arena, node, As<T, VortexDecimal?>(into));
        }
        else
        {
            CopyConverted(arena, node, type, into);
        }
    }

    private static void CopyConverted<T>(CanonicalArena arena, int node, VortexType type, Span<T> into)
    {
        if (typeof(T) == typeof(DateOnly))
        {
            Temporal.Copy<DateOnly, Temporal.Dates>(arena, node, type, As<T, DateOnly>(into));
        }
        else if (typeof(T) == typeof(DateOnly?))
        {
            Temporal.Copy<DateOnly, Temporal.Dates>(arena, node, type, As<T, DateOnly?>(into));
        }
        else if (typeof(T) == typeof(TimeOnly))
        {
            Temporal.Copy<TimeOnly, Temporal.Times>(arena, node, type, As<T, TimeOnly>(into));
        }
        else if (typeof(T) == typeof(TimeOnly?))
        {
            Temporal.Copy<TimeOnly, Temporal.Times>(arena, node, type, As<T, TimeOnly?>(into));
        }
        else if (typeof(T) == typeof(DateTime))
        {
            Temporal.Copy<DateTime, Temporal.Timestamps>(arena, node, type, As<T, DateTime>(into));
        }
        else if (typeof(T) == typeof(DateTime?))
        {
            Temporal.Copy<DateTime, Temporal.Timestamps>(arena, node, type, As<T, DateTime?>(into));
        }
        else if (typeof(T) == typeof(DateTimeOffset))
        {
            Temporal.Copy<DateTimeOffset, Temporal.Zoneds>(arena, node, type, As<T, DateTimeOffset>(into));
        }
        else if (typeof(T) == typeof(DateTimeOffset?))
        {
            Temporal.Copy<DateTimeOffset, Temporal.Zoneds>(arena, node, type, As<T, DateTimeOffset?>(into));
        }
        else if (typeof(T) == typeof(Guid))
        {
            ColumnData.CopyGuids(arena, node, As<T, Guid>(into));
        }
        else if (typeof(T) == typeof(Guid?))
        {
            ColumnData.CopyGuids(arena, node, As<T, Guid?>(into));
        }
        else if (typeof(T) == typeof(TimeSpan))
        {
            ColumnData.CopySpans(arena, node, As<T, TimeSpan>(into));
        }
        else if (typeof(T) == typeof(TimeSpan?))
        {
            ColumnData.CopySpans(arena, node, As<T, TimeSpan?>(into));
        }
        else if (typeof(T) == typeof(Int128))
        {
            ColumnData.CopyIntegers128(arena, node, As<T, Int128>(into));
        }
        else if (typeof(T) == typeof(Int128?))
        {
            ColumnData.CopyIntegers128(arena, node, As<T, Int128?>(into));
        }
        else if (typeof(T) == typeof(UInt128))
        {
            ColumnData.CopyUIntegers128(arena, node, As<T, UInt128>(into));
        }
        else if (typeof(T) == typeof(UInt128?))
        {
            ColumnData.CopyUIntegers128(arena, node, As<T, UInt128?>(into));
        }
        else if (typeof(T) == typeof(BigInteger))
        {
            ColumnData.CopyBigs(arena, node, As<T, BigInteger>(into));
        }
        else if (typeof(T) == typeof(BigInteger?))
        {
            ColumnData.CopyBigs(arena, node, As<T, BigInteger?>(into));
        }
        else
        {
            throw new NotSupportedException($"A result of {ClrFit.Name(typeof(T))} is not read as a value; read it through a record, As<TRecord>().");
        }
    }

    private static void CopyRecords<T>(RecordBatch? batch, CanonicalArena arena, int structure, VortexType type, VortexExtensionRegistry? extensions, T[] destination, int rows, IVortexRecord record)
    {
        record.ReadRecords(batch, arena, structure, type.NonNullable, extensions, destination, rows);
        if (!type.IsNullable)
        {
            return;
        }

        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, structure);
        for (int i = 0; i < rows && !valid.IsEmpty; i++)
        {
            if (!ColumnData.IsValid(valid, i))
            {
                destination[i] = default!;
            }
        }
    }

    private static void CopyBytes<T>(CanonicalArena arena, int node, Span<T> into)
    {
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        for (int i = 0; i < into.Length; i++)
        {
            if (typeof(T) == typeof(ReadOnlyMemory<byte>?) && !ColumnData.IsValid(valid, i))
            {
                into[i] = default!;
                continue;
            }

            ReadOnlyMemory<byte> bytes = ColumnData.IsValid(valid, i) ? ColumnData.Bytes(arena, node, i).ToArray() : default;
            into[i] = Unsafe.As<ReadOnlyMemory<byte>, T>(ref bytes);
        }
    }

    private static bool CopyNullablePrimitive<T>(CanonicalArena arena, int node, Span<T> into)
    {
        if (typeof(T) == typeof(sbyte?)) { ColumnData.CopyNullable(arena, node, As<T, sbyte?>(into)); }
        else if (typeof(T) == typeof(short?)) { ColumnData.CopyNullable(arena, node, As<T, short?>(into)); }
        else if (typeof(T) == typeof(int?)) { ColumnData.CopyNullable(arena, node, As<T, int?>(into)); }
        else if (typeof(T) == typeof(long?)) { ColumnData.CopyNullable(arena, node, As<T, long?>(into)); }
        else if (typeof(T) == typeof(byte?)) { ColumnData.CopyNullable(arena, node, As<T, byte?>(into)); }
        else if (typeof(T) == typeof(ushort?)) { ColumnData.CopyNullable(arena, node, As<T, ushort?>(into)); }
        else if (typeof(T) == typeof(uint?)) { ColumnData.CopyNullable(arena, node, As<T, uint?>(into)); }
        else if (typeof(T) == typeof(ulong?)) { ColumnData.CopyNullable(arena, node, As<T, ulong?>(into)); }
        else if (typeof(T) == typeof(Half?)) { ColumnData.CopyNullable(arena, node, As<T, Half?>(into)); }
        else if (typeof(T) == typeof(float?)) { ColumnData.CopyNullable(arena, node, As<T, float?>(into)); }
        else if (typeof(T) == typeof(double?)) { ColumnData.CopyNullable(arena, node, As<T, double?>(into)); }
        else if (typeof(T) == typeof(char?)) { ColumnData.CopyNullable(arena, node, As<T, char?>(into)); }
        else if (typeof(T) == typeof(nint?)) { ColumnData.CopyNullable(arena, node, As<T, nint?>(into)); }
        else if (typeof(T) == typeof(nuint?)) { ColumnData.CopyNullable(arena, node, As<T, nuint?>(into)); }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>The node that holds a bool column's values: the node, or its decoded twin.</summary>
    private static int ValuesNode(CanonicalArena arena, int node) =>
        arena.RecordRef(node).Kind is CanonicalKind.Constant or CanonicalKind.Dictionary or CanonicalKind.RunEnd
            ? EncodedForms.Canonical(arena, node)
            : node;

    private static bool IsPrimitive<T>() =>
        typeof(T) == typeof(sbyte) || typeof(T) == typeof(short) || typeof(T) == typeof(int) || typeof(T) == typeof(long)
        || typeof(T) == typeof(byte) || typeof(T) == typeof(ushort) || typeof(T) == typeof(uint) || typeof(T) == typeof(ulong)
        || typeof(T) == typeof(Half) || typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T).IsEnum
        || typeof(T) == typeof(char) || typeof(T) == typeof(nint) || typeof(T) == typeof(nuint);

    private static Span<TOut> As<T, TOut>(Span<T> values) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<T, TOut>(ref MemoryMarshal.GetReference(values)), values.Length);
}
