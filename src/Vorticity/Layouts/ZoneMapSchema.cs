// The zones child's dtype, which a reader must reconstruct to read the zone map at all: a Layout
// carries no dtype and the zones table's shape is derived from the column dtype plus the
// aggregates the metadata names.
//
// Sources, transcribed rather than guessed:
//   vortex-layout-0.86.1/src/layouts/zoned/schema.rs   aggregate_stats_table_dtype,
//                                                      legacy_stats_table_dtype,
//                                                      aggregate_state_dtype
//   vortex-array-0.86.1/src/aggregate_fn/fns/{min,max,bounded_min,bounded_max,null_count,
//                                             nan_count,min_max,sum,uncompressed_size_in_bytes}
//   vortex-array-0.86.1/src/expr/stats/mod.rs          Stat::dtype, Stat::name
//
// PHASE1-CONTRACTS.md §11.3 restates all of it; where the two disagree the Rust wins, and they do
// not disagree.
using System;
using System.Collections.Generic;
using System.Globalization;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// The nine legacy statistics of <c>vortex.stats</c>. The value <b>is</b> the bit index in the
/// metadata's stat bitset (vortex-array-0.86.1/src/expr/stats/mod.rs).
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

internal static class ZoneMapSchema
{
    /// <summary>Highest defined <see cref="LegacyStat"/>; higher bits are silently ignored.</summary>
    internal const int MaxLegacyStat = (int)LegacyStat.NaNCount;

    /// <summary>
    /// How far the walk of <c>supports_uncompressed_size_in_bytes</c> goes before it starts
    /// remembering. A DType arena deduplicates nodes, so <c>Struct(["a","b"], [d, d])</c> nested 64
    /// deep is a 65-node DAG with 2^64 root-to-leaf paths: the depth cap bounds the stack, not the
    /// work (docs/03-architecture.md §6). It is a fast path and not a cap - the same dedup makes a
    /// 5000-column struct four distinct nodes but 5001 visits, and that dtype is perfectly legal.
    /// </summary>
    private const int SupportVisitBudget = 4096;

    /// <summary>
    /// Builds the <c>vortex.zoned</c> zones-table dtype from the aggregate specs, in wire order.
    /// </summary>
    /// <param name="types">The arena derived dtypes are built in.</param>
    /// <param name="column">The zoned layout's own dtype.</param>
    /// <param name="specs">The aggregate specs, already resolved by <see cref="AggregateSpecList"/>.</param>
    /// <param name="tableDType">The zones child's dtype: a non-nullable struct, one field per contributing aggregate.</param>
    /// <param name="aggregates">One entry per spec, in wire order.</param>
    /// <param name="columnIndices">One entry per spec: its column in <paramref name="tableDType"/>, or -1.</param>
    /// <returns>
    /// <see langword="false"/> when an aggregate id is unknown or its options are unusable. The
    /// zones table then cannot be reconstructed, so pruning is disabled and the zones child is left
    /// unresolved — exactly what upstream does
    /// (<c>try_aggregate_fns_from_specs</c> returning <c>Ok(None)</c>). Never an error.
    /// </returns>
    internal static bool TryBuildAggregateTable(
        DTypeArena types,
        DType column,
        AggregateSpecList specs,
        out DType tableDType,
        out AggregateId[] aggregates,
        out int[] columnIndices)
    {
        tableDType = default;
        int count = specs.Count;
        aggregates = count == 0 ? [] : new AggregateId[count];
        columnIndices = count == 0 ? [] : new int[count];

        // Every aggregate id is resolved first, so the caller can report the whole list even when
        // one of them defeats the table derivation.
        bool resolvable = true;
        for (int i = 0; i < count; i++)
        {
            AggregateId aggregate = specs.GetAggregate(i);
            aggregates[i] = aggregate;
            columnIndices[i] = -1;
            resolvable &= aggregate != AggregateId.Unknown;
        }

        if (!resolvable)
        {
            return false;
        }

        string[] names = count == 0 ? [] : new string[count];
        DType[] fields = count == 0 ? [] : new DType[count];
        int columns = 0;

        for (int i = 0; i < count; i++)
        {
            AggregateId aggregate = aggregates[i];
            if (!TryDisplayName(aggregate, specs.GetOptions(i), out string? name))
            {
                return false;
            }

            DType state = AggregateStateDType(types, column, aggregate);
            if (state.IsDefault)
            {
                // "if the aggregate has no state DType for that column, it contributes no column
                // and is skipped entirely" - schema.rs filter_map.
                continue;
            }

            names[columns] = name!;

            // The state dtype may be the column's own, which lives in the file schema's arena;
            // a struct's children must all come from the arena it is built in. IMPORT FIRST, then
            // flip: WithNullability on a foreign node calls DTypeArena.CloneWithNullability, which
            // WRITES to that node's arena - and this parse runs once per scan, so on a file
            // schema's arena it is an unsynchronized write shared by every concurrent scan.
            fields[columns] = DTypeImport.Into(types, state).WithNullability(Nullability.Nullable);
            columnIndices[i] = columns;
            columns++;
        }

        tableDType = types.Struct(
            names.AsSpan(0, columns), fields.AsSpan(0, columns), Nullability.NonNullable);
        return true;
    }

    /// <summary>
    /// Builds the legacy <c>vortex.stats</c> zones-table dtype from a stat bitset.
    /// </summary>
    /// <param name="types">The arena derived dtypes are built in.</param>
    /// <param name="column">The layout's own dtype.</param>
    /// <param name="presentStats">The stat bitset, ascending by discriminant.</param>
    /// <returns>A non-nullable struct; <c>max</c> and <c>min</c> each add a truncation-flag column.</returns>
    /// <remarks>
    /// UNTESTED: no fixture exists in the 0.86.1 corpus (manifest skipped/layouts/vortex_stats);
    /// written against vortex-layout-0.86.1/src/layouts/zoned/{mod.rs,schema.rs}.
    /// </remarks>
    internal static DType LegacyStatsTable(DTypeArena types, DType column, ReadOnlySpan<LegacyStat> presentStats)
    {
        // Two columns per stat at most: the value and its truncation flag.
        int capacity = presentStats.Length * 2;
        string[] names = capacity == 0 ? [] : new string[capacity];
        DType[] fields = capacity == 0 ? [] : new DType[capacity];
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
            // Import first, then flip: see the note in AggregateTable.
            fields[columns] = DTypeImport.Into(types, value).WithNullability(Nullability.Nullable);
            columns++;

            // Max and Min each emit a second, immediately-following NON-nullable Bool column.
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

    /// <summary>
    /// Decodes the raw stat bitset, LSB-first within each byte, ascending by discriminant.
    /// </summary>
    /// <param name="bitset">The bytes after the 4-byte zone length; may be empty.</param>
    /// <param name="stats">Receives the present stats; must hold <see cref="MaxLegacyStat"/> + 1.</param>
    /// <returns>How many entries of <paramref name="stats"/> were written.</returns>
    /// <remarks>
    /// Bit indices above <see cref="MaxLegacyStat"/> are silently ignored: that is the
    /// forward-compatibility rule for this bitset, and rejecting them would break read-forever
    /// (docs/02-format.md §5.3).
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
                // min/max: MinMax.return_dtype(..).map(|_| input.as_nullable()).
                // bounded_min: supported_dtype(..).map(DType::as_nullable) - a plain scalar.
                return MinMaxSupported(column)
                    ? DTypeImport.Into(types, column).WithNullability(Nullability.Nullable)
                    : default;

            case AggregateId.BoundedMax:
                // make_bounded_max_partial_dtype: Struct({bound: element?, unknown: bool}, Nullable).
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
        // A DType holds an arena reference, so it is a managed type and cannot be stackalloc'd.
        // This runs once per zoned layout node at parse time, never on a decode path.
        DType[] fields = new DType[2];
        fields[0] = DTypeImport.Into(types, element).WithNullability(Nullability.Nullable);
        fields[1] = types.Bool(Nullability.NonNullable);

        Span<int> names = stackalloc int[2];
        names[0] = types.InternName("bound"u8);
        names[1] = types.InternName("unknown"u8);

        return types.Struct(names, fields, Nullability.Nullable);
    }

    /// <summary>
    /// <c>nan_count</c> exists only for float primitives, and falls back to an extension's storage
    /// dtype (schema.rs <c>aggregate_state_dtype</c>).
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
    /// <c>minmax_supported_dtype</c> (vortex-array-0.86.1/src/aggregate_fn/fns/min_max/mod.rs).
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
    /// <c>Stat::dtype</c> with <c>legacy_stats_table_dtype</c>'s extension fallback.
    /// </summary>
    /// <returns><c>default</c> when the stat contributes no column.</returns>
    private static DType LegacyStatDType(DTypeArena types, DType column, LegacyStat stat)
    {
        DType direct = LegacyStatDTypeCore(types, column, stat);
        if (!direct.IsDefault || column.Kind != DTypeKind.Extension)
        {
            return direct;
        }

        // "Backward compat: older files may have stored stats (e.g. Sum) for extension types by
        // resolving through the storage dtype."
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
    /// <c>Sum::return_dtype</c>. Every result is nullable: an overflowing sum is recorded as null.
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
    /// <c>supports_uncompressed_size_in_bytes</c>, walked iteratively.
    /// </summary>
    /// <remarks>
    /// The visit budget is a fast path, not a cap. A DTypeArena deduplicates, so a struct of N
    /// identically-typed fields is ONE child node referenced N times and a dtype nested that way
    /// 64 deep is a 65-node DAG with 2^64 root-to-leaf paths: the depth cap bounds the stack, not
    /// the work. But a flat count also rejects ordinary BREADTH - a 5000-column feature block is
    /// four distinct nodes and 5001 visits - so crossing the budget switches the walk to a visited
    /// set keyed on the source node index instead of failing a legal dtype.
    /// </remarks>
    private static bool SupportsUncompressedSize(DType dtype)
    {
        // An explicit stack rather than recursion: the branching cases (struct, union, map) make
        // this the one derivation whose work is not bounded by the dtype depth cap. A DType is a
        // managed type, so the stack is a heap array; this is parse-time code for a layout the
        // 0.86.1 writer cannot produce, never a decode path.
        DType[] stack = new DType[32];
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

    private static void Push(ref DType[] stack, ref int top, DType value)
    {
        if (top == stack.Length)
        {
            Array.Resize(ref stack, stack.Length * 2);
        }

        stack[top++] = value;
    }

    /// <summary>
    /// The struct field name a zone-map column carries: <c>"{id}({optionsDisplay})"</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the options payload cannot be read, which disables pruning
    /// rather than failing the read.
    /// </returns>
    private static bool TryDisplayName(AggregateId aggregate, ReadOnlySpan<byte> options, out string? name)
    {
        switch (aggregate)
        {
            case AggregateId.Min:
            case AggregateId.Max:
            {
                // NumericalAggregateOpts { bool skip_nans = 1 } has IMPLICIT presence, so the
                // default configuration (skip_nans = true) is written as `08 01` and an EMPTY
                // payload decodes to skip_nans = false. Display shows only the non-default case,
                // "so that aggregates with default options render identically to their
                // pre-options form" (aggregate_fn/vtable.rs).
                bool skipNans = ReadSkipNans(options);
                string id = aggregate == AggregateId.Min ? "vortex.min" : "vortex.max";
                name = skipNans ? id + "()" : id + "(skip_nans=false)";
                return true;
            }

            case AggregateId.BoundedMin:
            case AggregateId.BoundedMax:
            {
                if (!AggregateRegistry.TryGetBoundLength(aggregate, options, out uint boundLength))
                {
                    // Upstream fails the whole read here; we degrade to "no zone map", which is
                    // strictly more permissive and can never produce a wrong value because Phase 1
                    // prunes with nothing.
                    name = null;
                    return false;
                }

                string id = aggregate == AggregateId.BoundedMin ? "vortex.bounded_min" : "vortex.bounded_max";
                name = id + "(" + boundLength.ToString(CultureInfo.InvariantCulture) + ")";
                return true;
            }

            case AggregateId.NullCount:
                name = "vortex.null_count()";
                return true;

            case AggregateId.NanCount:
                name = "vortex.nan_count()";
                return true;

            default:
                name = null;
                return false;
        }
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
