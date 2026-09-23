using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// The nine legacy statistics of <c>vortex.stats</c>. The value <b>is</b> the bit index in the
/// metadata's stat bitset, so the order is part of the wire format and cannot be reshuffled.
/// </summary>
internal enum LegacyStat : byte
{
    IsConstant = 0,
    IsSorted = 1,
    IsStrictSorted = 2,
    Max = 3,
    Min = 4,
    Sum = 5,
    NullCount = 6,
    UncompressedSizeInBytes = 7,
    NaNCount = 8,
}

/// <summary>
/// Reconstructs the dtype of a zone map's zones child. A layout carries no dtype on the wire, so a
/// reader has to derive the zones table's shape from the column's own dtype plus the aggregates the
/// layout metadata names, or it cannot read the zone map at all.
/// </summary>
internal static class ZoneMapSchema
{
    /// <summary>Highest defined <see cref="LegacyStat"/>; higher bits are silently ignored.</summary>
    internal const int MaxLegacyStat = (int)LegacyStat.NaNCount;

    /// <summary>
    /// How far the walk of <c>supports_uncompressed_size_in_bytes</c> goes before it starts
    /// remembering. A DType arena deduplicates nodes, so <c>Struct(["a","b"], [d, d])</c> nested 64
    /// deep is a 65-node graph with 2^64 root-to-leaf paths: the depth cap bounds the stack, not
    /// the work. It is a fast path and not a cap - the same dedup makes a 5000-column struct four
    /// distinct nodes but 5001 visits, and that dtype is perfectly legal.
    /// </summary>
    private const int SupportVisitBudget = 4096;

    /// <summary>
    /// Builds the <c>vortex.zoned</c> zones-table dtype from the aggregate specs, in wire order.
    /// </summary>
    /// <param name="types">The arena derived dtypes are built in.</param>
    /// <param name="column">The zoned layout's own dtype.</param>
    /// <param name="specs">The aggregate specs, already resolved by <see cref="AggregateSpecList"/>.</param>
    /// <param name="tableDType">The zones child's dtype: a non-nullable struct, one field per contributing aggregate.</param>
    /// <param name="aggregates">
    /// One int per spec, in wire order: the aggregate's id and its column in
    /// <paramref name="tableDType"/> or -1, packed as <see cref="ZoneMap.Pack"/> packs them.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when an aggregate id is unknown or its options are unusable, or when
    /// the map declares more aggregates than a packed column holds. The zones table then cannot be
    /// reconstructed, so pruning is disabled and the zones child is left unresolved. Never an error:
    /// an unreadable zone map only costs the chance to skip data.
    /// </returns>
    internal static bool TryBuildAggregateTable(
        DTypeArena types,
        DType column,
        AggregateSpecList specs,
        out DType tableDType,
        Span<int> aggregates)
    {
        tableDType = default;
        int count = specs.Count;

        // Every aggregate id is resolved first, so the caller can report the whole list even when
        // one of them defeats the table derivation.
        bool resolvable = count <= ZoneMap.MaxResolvedAggregates;
        for (int i = 0; i < count; i++)
        {
            AggregateId aggregate = specs.GetAggregate(i);
            aggregates[i] = ZoneMap.Pack(aggregate, -1);
            resolvable &= aggregate != AggregateId.Unknown;
        }

        if (!resolvable)
        {
            return false;
        }

        // Scratch for the struct the table is: the struct keeps its own copy of both.
        int[] names = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        DType[] fields = ArrayPool<DType>.Shared.Rent(Math.Max(count, 1));
        Span<byte> name = stackalloc byte[MaxDisplayNameBytes];
        try
        {
            int columns = 0;
            for (int i = 0; i < count; i++)
            {
                AggregateId aggregate = specs.GetAggregate(i);
                int nameLength = WriteDisplayName(aggregate, specs.GetOptions(i), name);
                if (nameLength < 0)
                {
                    return false;
                }

                DType state = AggregateStateDType(types, column, aggregate);
                if (state.IsDefault)
                {
                    // An aggregate with no state dtype for this column contributes no column at
                    // all, rather than an empty one.
                    continue;
                }

                names[columns] = types.InternName(name[..nameLength]);

                // The state dtype may be the column's own, which lives in the file schema's arena,
                // and a struct's children must all come from the arena it is built in. Import
                // first, then flip the nullability: flipping a foreign node writes to that node's
                // own arena, and the file schema's arena is shared by every concurrent scan, so
                // that write would be a data race.
                fields[columns] = DTypeImport.Into(types, state).WithNullability(Nullability.Nullable);
                aggregates[i] = ZoneMap.Pack(aggregate, columns);
                columns++;
            }

            tableDType = types.Struct(
                new ReadOnlySpan<int>(names, 0, columns), fields.AsSpan(0, columns), Nullability.NonNullable);
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(names);
            ArrayPool<DType>.Shared.Return(fields, clearArray: true);
        }
    }

    /// <summary>
    /// Builds the legacy <c>vortex.stats</c> zones-table dtype from a stat bitset.
    /// </summary>
    /// <param name="types">The arena derived dtypes are built in.</param>
    /// <param name="column">The layout's own dtype.</param>
    /// <param name="presentStats">The stat bitset, ascending by discriminant.</param>
    /// <returns>A non-nullable struct; <c>max</c> and <c>min</c> each add a truncation-flag column.</returns>
    /// <remarks>
    /// No writer in the reference corpus emits a legacy stats table, so this derivation has no
    /// fixture behind it and is exercised only by files found in the wild.
    /// </remarks>
    internal static DType LegacyStatsTable(DTypeArena types, DType column, ReadOnlySpan<LegacyStat> presentStats)
    {
        // Two columns per stat at most: the value and its truncation flag. Scratch for the struct
        // the table is, which keeps its own copy of both.
        int capacity = Math.Max(presentStats.Length * 2, 1);
        string[] names = ArrayPool<string>.Shared.Rent(capacity);
        DType[] fields = ArrayPool<DType>.Shared.Rent(capacity);
        try
        {
            int columns = 0;
            for (int i = 0; i < presentStats.Length; i++)
            {
                LegacyStat stat = presentStats[i];
                DType value = LegacyStatDType(types, column, stat);
                if (value.IsDefault)
                {
                    continue;
                }

                names[columns] = LegacyStatName(stat);
                // Import first, then flip the nullability, so the flip never writes to the arena
                // the column's dtype came from.
                fields[columns] = DTypeImport.Into(types, value).WithNullability(Nullability.Nullable);
                columns++;

                // Max and Min each emit a second, immediately-following non-nullable Bool column.
                if (stat == LegacyStat.Max)
                {
                    names[columns] = "max_is_truncated";
                    fields[columns] = types.Bool(Nullability.NonNullable);
                    columns++;
                }
                else if (stat == LegacyStat.Min)
                {
                    names[columns] = "min_is_truncated";
                    fields[columns] = types.Bool(Nullability.NonNullable);
                    columns++;
                }
            }

            return types.Struct(names.AsSpan(0, columns), fields.AsSpan(0, columns), Nullability.NonNullable);
        }
        finally
        {
            ArrayPool<string>.Shared.Return(names, clearArray: true);
            ArrayPool<DType>.Shared.Return(fields, clearArray: true);
        }
    }

    /// <summary>
    /// Decodes the raw stat bitset, LSB-first within each byte, ascending by discriminant.
    /// </summary>
    /// <param name="bitset">The bytes after the 4-byte zone length; may be empty.</param>
    /// <param name="stats">Receives the present stats; must hold <see cref="MaxLegacyStat"/> + 1.</param>
    /// <returns>How many entries of <paramref name="stats"/> were written.</returns>
    /// <remarks>
    /// Bit indices above <see cref="MaxLegacyStat"/> are silently ignored: that is the
    /// forward-compatibility rule for this bitset, and rejecting them would make a file written by
    /// a newer producer unreadable for no gain.
    /// </remarks>
    internal static int ReadLegacyStatBitset(ReadOnlySpan<byte> bitset, Span<LegacyStat> stats)
    {
        int written = 0;
        int limit = Math.Min(bitset.Length, (MaxLegacyStat / 8) + 1);
        for (int b = 0; b < limit; b++)
        {
            byte value = bitset[b];
            for (int bit = 0; bit < 8; bit++)
            {
                int index = (b * 8) + bit;
                if (index > MaxLegacyStat)
                {
                    break;
                }

                if ((value & (1 << bit)) != 0)
                {
                    stats[written++] = (LegacyStat)index;
                }
            }
        }

        return written;
    }

    /// <summary>The state dtype of one aggregate against a column, before the nullable flip.</summary>
    /// <returns><c>default</c> when the aggregate contributes no column.</returns>
    private static DType AggregateStateDType(DTypeArena types, DType column, AggregateId aggregate)
    {
        switch (aggregate)
        {
            case AggregateId.Min:
            case AggregateId.Max:
            case AggregateId.BoundedMin:
                // All three carry a plain scalar of the column's own type, made nullable.
                return MinMaxSupported(column)
                    ? DTypeImport.Into(types, column).WithNullability(Nullability.Nullable)
                    : default;

            case AggregateId.BoundedMax:
                // A partial state rather than a scalar: a nullable struct of the bound and a flag
                // saying the bound is not exact.
                return MinMaxSupported(column) ? BoundedMaxPartial(types, column) : default;

            case AggregateId.NullCount:
                return types.Primitive(PType.U64, Nullability.NonNullable);

            case AggregateId.NanCount:
                return NanCountDType(types, column);

            default:
                return default;
        }
    }

    private static DType BoundedMaxPartial(DTypeArena types, DType element)
    {
        // A DType holds an arena reference, so it cannot be stackalloc'd: the two sit in an inline
        // array on the stack instead.
        ReadOnlySpan<DType> fields =
        [
            DTypeImport.Into(types, element).WithNullability(Nullability.Nullable),
            types.Bool(Nullability.NonNullable),
        ];

        Span<int> names = stackalloc int[2];
        names[0] = types.InternName("bound"u8);
        names[1] = types.InternName("unknown"u8);

        return types.Struct(names, fields, Nullability.Nullable);
    }

    /// <summary>
    /// <c>nan_count</c> exists only for float primitives, and falls back to an extension's storage
    /// dtype.
    /// </summary>
    private static DType NanCountDType(DTypeArena types, DType column)
    {
        if (IsFloatPrimitive(column))
        {
            return types.Primitive(PType.U64, Nullability.NonNullable);
        }

        if (column.Kind == DTypeKind.Extension && IsFloatPrimitive(column.StorageType))
        {
            return types.Primitive(PType.U64, Nullability.NonNullable);
        }

        return default;
    }

    private static bool IsFloatPrimitive(DType dtype) =>
        dtype.Kind == DTypeKind.Primitive && dtype.PType.IsFloat();

    /// <summary>
    /// Whether a min or max aggregate is defined over this dtype at all.
    /// </summary>
    /// <remarks>
    /// The recursion follows a single child per level (list element, fixed-size-list element), so
    /// it cannot branch and the dtype depth cap already bounds it.
    /// </remarks>
    private static bool MinMaxSupported(DType dtype)
    {
        DType current = dtype;
        for (int depth = 0; depth <= VortexLimits.MaxDTypeDepth; depth++)
        {
            switch (current.Kind)
            {
                case DTypeKind.Bool:
                case DTypeKind.Primitive:
                case DTypeKind.Decimal:
                case DTypeKind.Utf8:
                case DTypeKind.Binary:
                case DTypeKind.Extension:
                    return true;

                case DTypeKind.List:
                case DTypeKind.FixedSizeList:
                    current = current.ElementType;
                    continue;

                default:
                    return false;
            }
        }

        return false;
    }

    private static string LegacyStatName(LegacyStat stat) => stat switch
    {
        LegacyStat.IsConstant => "is_constant",
        LegacyStat.IsSorted => "is_sorted",
        LegacyStat.IsStrictSorted => "is_strict_sorted",
        LegacyStat.Max => "max",
        LegacyStat.Min => "min",
        LegacyStat.Sum => "sum",
        LegacyStat.NullCount => "null_count",
        LegacyStat.UncompressedSizeInBytes => "uncompressed_size_in_bytes",
        _ => "nan_count",
    };

    /// <summary>
    /// The column dtype one legacy stat contributes, with the fallback for extension types.
    /// </summary>
    /// <returns><c>default</c> when the stat contributes no column.</returns>
    private static DType LegacyStatDType(DTypeArena types, DType column, LegacyStat stat)
    {
        DType direct = LegacyStatDTypeCore(types, column, stat);
        if (!direct.IsDefault || column.Kind != DTypeKind.Extension)
        {
            return direct;
        }

        // A file may have stored a stat for an extension type by resolving it through the storage
        // dtype, so a stat that is undefined for the extension is retried against the storage.
        return LegacyStatDTypeCore(types, column.StorageType, stat);
    }

    private static DType LegacyStatDTypeCore(DTypeArena types, DType column, LegacyStat stat)
    {
        switch (stat)
        {
            case LegacyStat.IsConstant:
            case LegacyStat.IsSorted:
            case LegacyStat.IsStrictSorted:
                return types.Bool(Nullability.NonNullable);

            case LegacyStat.Max:
            case LegacyStat.Min:
                return column.Kind == DTypeKind.Null ? default : column;

            case LegacyStat.NullCount:
                return types.Primitive(PType.U64, Nullability.NonNullable);

            case LegacyStat.UncompressedSizeInBytes:
                return SupportsUncompressedSize(column)
                    ? types.Primitive(PType.U64, Nullability.NonNullable)
                    : default;

            case LegacyStat.NaNCount:
                return IsFloatPrimitive(column)
                    ? types.Primitive(PType.U64, Nullability.NonNullable)
                    : default;

            default:
                return SumDType(types, column);
        }
    }

    /// <summary>
    /// The widened accumulator a sum is stored in. Every result is nullable: an overflowing sum is
    /// recorded as null.
    /// </summary>
    private static DType SumDType(DTypeArena types, DType column)
    {
        switch (column.Kind)
        {
            case DTypeKind.Bool:
                return types.Primitive(PType.U64, Nullability.Nullable);

            case DTypeKind.Primitive:
                PType widened = column.PType switch
                {
                    PType.U8 or PType.U16 or PType.U32 or PType.U64 => PType.U64,
                    PType.I8 or PType.I16 or PType.I32 or PType.I64 => PType.I64,
                    _ => PType.F64,
                };
                return types.Primitive(widened, Nullability.Nullable);

            case DTypeKind.Decimal:
                int precision = Math.Min(DTypeArena.MaxDecimalPrecision, column.Precision + 10);
                return types.Decimal((byte)precision, column.Scale, Nullability.Nullable);

            default:
                return default;
        }
    }

    /// <summary>
    /// Whether an uncompressed-size stat is defined over this dtype, walked iteratively.
    /// </summary>
    /// <remarks>
    /// The visit budget is a fast path, not a cap. A DTypeArena deduplicates, so a struct of n
    /// identically-typed fields is one child node referenced n times, and a dtype nested that way
    /// 64 deep is a 65-node graph with 2^64 root-to-leaf paths: the depth cap bounds the stack, not
    /// the work. But a flat count also rejects ordinary breadth - a 5000-column feature block is
    /// four distinct nodes and 5001 visits - so crossing the budget switches the walk to a visited
    /// set keyed on the source node index instead of failing a legal dtype.
    /// </remarks>
    private static bool SupportsUncompressedSize(DType dtype)
    {
        // An explicit stack rather than recursion: the branching cases (struct, union, map) make
        // this the one derivation whose work is not bounded by the dtype depth cap. A DType is a
        // managed type, so the stack is a rented array; this runs at parse time for a legacy
        // layout, never on a decode path.
        DType[] stack = ArrayPool<DType>.Shared.Rent(32);
        try
        {
            int top = 0;
            stack[top++] = dtype;

            int visits = 0;
            HashSet<int>? seen = null;
            while (top > 0)
            {
                if (seen is null && ++visits > SupportVisitBudget)
                {
                    seen = new HashSet<int>();
                }

                DType current = stack[--top];
                if (seen is not null && !seen.Add(current.NodeIndex))
                {
                    // Already inspected; a second visit can only reach the same verdict.
                    continue;
                }
                switch (current.Kind)
                {
                    case DTypeKind.Null:
                    case DTypeKind.Bool:
                    case DTypeKind.Primitive:
                    case DTypeKind.Decimal:
                    case DTypeKind.Utf8:
                    case DTypeKind.Binary:
                        continue;

                    case DTypeKind.Variant:
                        return false;

                    case DTypeKind.List:
                    case DTypeKind.FixedSizeList:
                        Push(ref stack, ref top, current.ElementType);
                        continue;

                    case DTypeKind.Extension:
                        Push(ref stack, ref top, current.StorageType);
                        continue;

                    case DTypeKind.Map:
                        Push(ref stack, ref top, current.KeyType);
                        Push(ref stack, ref top, current.ValueType);
                        continue;

                    default:
                        // Struct and Union: every field, respectively every variant.
                        for (int i = 0; i < current.ChildCount; i++)
                        {
                            Push(ref stack, ref top, current.GetChild(i));
                        }

                        continue;
                }
            }

            return true;
        }
        finally
        {
            // Cleared: a DType holds its arena, which the pool would otherwise keep alive.
            ArrayPool<DType>.Shared.Return(stack, clearArray: true);
        }
    }

    private static void Push(ref DType[] stack, ref int top, DType value)
    {
        if (top == stack.Length)
        {
            DType[] grown = ArrayPool<DType>.Shared.Rent(stack.Length * 2);
            stack.AsSpan().CopyTo(grown);
            ArrayPool<DType>.Shared.Return(stack, clearArray: true);
            stack = grown;
        }

        stack[top++] = value;
    }

    /// <summary>The longest display name, <c>vortex.bounded_max(4294967295)</c>, rounded up.</summary>
    internal const int MaxDisplayNameBytes = 32;

    /// <summary>
    /// Writes the struct field name a zone-map column carries, <c>"{id}({optionsDisplay})"</c>, as
    /// the UTF-8 the table's struct interns it as.
    /// </summary>
    /// <param name="aggregate">The aggregate the column holds.</param>
    /// <param name="options">The aggregate's options payload.</param>
    /// <param name="destination">At least <see cref="MaxDisplayNameBytes"/> bytes.</param>
    /// <returns>
    /// The name's length, or -1 when the options payload cannot be read, which disables pruning
    /// rather than failing the read.
    /// </returns>
    internal static int WriteDisplayName(AggregateId aggregate, ReadOnlySpan<byte> options, Span<byte> destination)
    {
        ReadOnlySpan<byte> name;
        switch (aggregate)
        {
            case AggregateId.Min:
            case AggregateId.Max:
            {
                // The options message declares `skip_nans` with implicit presence, so the usual
                // configuration (skip_nans = true) is written explicitly and an empty payload
                // decodes to false, not to the usual value. The name renders the flag only when it
                // is false, so an aggregate carrying default options keeps the name it had before
                // options existed at all.
                bool skipNans = ReadSkipNans(options);
                name = aggregate == AggregateId.Min
                    ? (skipNans ? "vortex.min()"u8 : "vortex.min(skip_nans=false)"u8)
                    : (skipNans ? "vortex.max()"u8 : "vortex.max(skip_nans=false)"u8);
                break;
            }

            case AggregateId.BoundedMin:
            case AggregateId.BoundedMax:
            {
                if (!AggregateRegistry.TryGetBoundLength(aggregate, options, out uint boundLength))
                {
                    // Unreadable bound options degrade to "no zone map" rather than failing the
                    // whole read: the map only ever serves to skip data, so losing it can cost
                    // speed but never correctness.
                    return -1;
                }

                ReadOnlySpan<byte> id = aggregate == AggregateId.BoundedMin
                    ? "vortex.bounded_min("u8
                    : "vortex.bounded_max("u8;
                id.CopyTo(destination);
                boundLength.TryFormat(destination[id.Length..], out int digits, default, CultureInfo.InvariantCulture);
                destination[id.Length + digits] = (byte)')';
                return id.Length + digits + 1;
            }

            case AggregateId.NullCount:
                name = "vortex.null_count()"u8;
                break;

            case AggregateId.NanCount:
                name = "vortex.nan_count()"u8;
                break;

            default:
                return -1;
        }

        name.CopyTo(destination);
        return name.Length;
    }

    /// <summary>Reads <c>NumericalAggregateOpts.skip_nans</c>; an absent field is proto3's <c>false</c>.</summary>
    /// <exception cref="VortexFormatException">The payload is not a well-formed message.</exception>
    private static bool ReadSkipNans(ReadOnlySpan<byte> options)
    {
        ProtoReader reader = new ProtoReader(options);
        bool skipNans = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1 && wire == ProtoWireType.Varint)
            {
                skipNans = reader.ReadBool();
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        return skipNans;
    }
}
