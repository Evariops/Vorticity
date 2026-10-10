using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The blocks of a group by the zone maps answer (16-queries.md §9.3): every column the key reads
/// goes through its functions to one key over the block's bounds, the filter keeps the block whole,
/// and every aggregate is a count of the rows, a minimum or a maximum, which the rows and the bounds
/// give. No split reads such a block. In its place the pass folds, once the rows before it are in, a
/// block of as many rows whose every column holds its zone's minimum and, on the last row, its
/// maximum: the key it maps to, the count and the extremes are the block's, and the groups come in
/// the order the rows do, which a sorted key streams on.
/// </summary>
/// <remarks>
/// Only a key with a function is worth the zone maps' read: a column's own zone holds one key only
/// when it holds one value, which its encoding already folds as one. A dataset's objects are not
/// settled: their file statistics count the rows a deletion removed.
/// </remarks>
internal sealed class ZoneSettling
{
    private readonly int[] _blocks;
    private readonly Stored[] _columns;
    private readonly long _blockRows;

    private ZoneSettling(int[] blocks, Stored[] columns, long blockRows, BlockMask live)
    {
        _blocks = blocks;
        _columns = columns;
        _blockRows = blockRows;
        Live = live;
    }

    /// <summary>The pass's mask: the filter's live blocks, the settled ones dead.</summary>
    internal BlockMask Live { get; }

    /// <summary>How many blocks settle.</summary>
    internal int Count => _blocks.Length;

    /// <summary>
    /// The settling of <paramref name="pass"/>, or null when no block settles: a source other than a
    /// file, a take, a filter on a group, an aggregate the bounds do not give, no key with a
    /// function, a column without exact zones, or splits that cross the blocks.
    /// </summary>
    /// <param name="source">The scan's source.</param>
    /// <param name="pass">The pass, before the settling.</param>
    /// <param name="plan">The aggregation.</param>
    /// <param name="metrics">The scan's sink, to which the zone maps read here are added.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async ValueTask<ZoneSettling?> PlanAsync(
        ScanSource source, ScanSpec pass, AggregationPlan plan, ScanCounters metrics, CancellationToken cancellationToken)
    {
        if (source is not FileScanSource { File: { } file } || !Applies(pass, plan) || !file.Schema.RootIsStruct)
        {
            return null;
        }

        // The columns the keys and the extremes read, each once, as top-level fields of the file.
        List<Stored> columns = [];
        int[] keys = new int[plan.Keys.Length];
        for (int k = 0; k < keys.Length; k++)
        {
            keys[k] = Add(columns, plan.Keys[k], file.DType);
        }

        List<int> extremes = [];
        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            if (aggregate.Input is { } input)
            {
                extremes.Add(Add(columns, input, file.DType));
            }
        }

        if (columns.Exists(column => column.Index < 0))
        {
            return null;
        }

        LayoutTree tree = file.LayoutTree;
        long blockRows = SplitPlan.NaturalBatchRows(tree);
        RowRange whole = new RowRange(0, file.RowCount);
        RowRange rows = pass.Rows is { } asked ? asked.Intersect(whole) : whole;
        if (blockRows <= 0 || rows.IsEmpty || Crosses(tree, rows, pass, blockRows))
        {
            return null;
        }

        // The zones of those columns, read as a filter on them would read them.
        VortexExpr? every = null;
        foreach (Stored column in columns)
        {
            VortexExpr present = Expr.IsNotNull(Expr.Field(column.Path));
            every = every is null ? present : Expr.And(every, present);
        }

        ZonePruner? zones = (await ZonePruningPlan
            .PlanAsync(file, tree, every!, cancellationToken, steps: null, metrics, indexes: false)
            .ConfigureAwait(false)).Zones;
        foreach (Stored column in columns)
        {
            column.Zones = zones?.Column(column.Path);
            if (column.Zones is not { } map || map.ZoneLength != blockRows || map.RowCount != file.RowCount)
            {
                return null;
            }
        }

        // The filter's mask, and its zones, which say whether it keeps a block whole.
        VortexExpr? filter = pass.Filter is { } written ? FunctionFieldExpr.Ranges(written) : null;
        ZonePruningPlan.PruningPlan pruning = filter is null
            ? default
            : await ZonePruningPlan
                .PlanAsync(file, tree, filter, cancellationToken, steps: null, metrics, pass.Options.UseIndexes)
                .ConfigureAwait(false);
        BlockMask live = pruning.Live ?? new BlockMask(file.RowCount, blockRows);
        if (live.BlockRows != blockRows || (filter is not null && pruning.Zones is null))
        {
            return null;
        }

        List<int> settled = [];
        for (int block = 0; block < live.BlockCount; block++)
        {
            RowRange range = live.BlockRange(block);
            if (live.IsLive(block) && range.Start >= rows.Start && range.End <= rows.End
                && (filter is null || pruning.Zones!.MustMatch(range))
                && Settles(plan, columns, keys, extremes, block, range.Length))
            {
                settled.Add(block);
                live.Kill(block);
            }
        }

        return settled.Count == 0 ? null : new ZoneSettling([.. settled], [.. columns], blockRows, live);
    }

    /// <summary>
    /// <paramref name="pass"/> as it runs beside the settling: the mask with the settled blocks
    /// dead, and splits of a block at most, so that no batch reads a row of one.
    /// </summary>
    internal ScanSpec Pass(ScanSpec pass) => pass with
    {
        Pruned = true,
        Live = Live,
        Options = pass.Options with
        {
            BatchRows = pass.Options.BatchRows > 0 && pass.Options.BatchRows < _blockRows ? pass.Options.BatchRows : (int)_blockRows,
        },
    };

    /// <summary>The settled blocks that lie in <paramref name="rows"/>: the first, and one past the last.</summary>
    internal (int First, int End) Within(RowRange rows)
    {
        int first = 0;
        while (first < _blocks.Length && Start(first) < rows.Start)
        {
            first++;
        }

        int end = first;
        while (end < _blocks.Length && Start(end) < rows.End)
        {
            end++;
        }

        return (first, end);
    }

    /// <summary>The row settled block <paramref name="index"/> starts at.</summary>
    internal long Start(int index) => _blocks[index] * _blockRows;

    /// <summary>What a partition folds the settled blocks in, made once for it.</summary>
    internal Scratch NewScratch() => new Scratch(_columns);

    /// <summary>
    /// The block folded in place of settled block <paramref name="index"/>, in
    /// <paramref name="scratch"/>'s arena, emptied first: valid until the next.
    /// </summary>
    internal RecordBatch Batch(int index, Scratch scratch)
    {
        CanonicalArena arena = scratch.Arena;
        arena.Reset();
        int block = _blocks[index];
        int rows = (int)Live.BlockRange(block).Length;
        int[] fields = scratch.Fields;
        for (int c = 0; c < _columns.Length; c++)
        {
            fields[c] = Column(arena, scratch, c, block, rows);
        }

        int root = arena.AddStruct(scratch.Root, rows, Validity.NonNullable, fields);
        return new RecordBatch(arena, root, Start(index));
    }

    /// <summary>
    /// Whether every aggregate is one the rows and the bounds give and every key a column of the
    /// file's, one with a function: a count of the rows, and the extremes of a column, which skip
    /// the nulls and the NaN a zone's bounds skip too.
    /// </summary>
    private static bool Applies(ScanSpec pass, AggregationPlan plan)
    {
        if (!plan.Grouped || pass.Take is not null || pass.MatchesNothing || !pass.Options.UseStatistics
            || plan.Filters.Filters.Length > 0 || plan.Chosen.Length > 0
            || !Array.Exists(plan.Keys, key => key.Field is FunctionFieldExpr))
        {
            return false;
        }

        foreach (ColumnShape key in plan.Keys)
        {
            if (!Readable(key))
            {
                return false;
            }
        }

        foreach (IAggregateNode aggregate in plan.Aggregates)
        {
            bool answered = aggregate.Filter is null && aggregate.Kind switch
            {
                AggregateKind.Count => aggregate.Input is null,
                AggregateKind.Min or AggregateKind.Max => aggregate.Input is { } input && Readable(input),
                _ => false,
            };
            if (!answered)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a column's zone bounds are its values: a top-level number, or an extension over one.</summary>
    private static bool Readable(ColumnShape column) =>
        column.Kind == StorageKind.Primitive && column.Column.FieldPath.Length == 1;

    /// <summary>The column <paramref name="shape"/> reads, added once; its index among them.</summary>
    private static int Add(List<Stored> columns, ColumnShape shape, DType schema)
    {
        int index = shape.Column.FieldPath[0];
        int found = columns.FindIndex(column => column.Index == index);
        if (found >= 0)
        {
            return found;
        }

        DType field = schema.GetField(index);
        DType storage = field.Kind == DTypeKind.Extension ? field.StorageType : field;
        columns.Add(new Stored(
            storage.Kind == DTypeKind.Primitive ? index : -1, schema.GetFieldName(index), field, storage.Kind == DTypeKind.Primitive ? storage.PType : default));
        return columns.Count - 1;
    }

    /// <summary>Whether a split of the pass would read rows of two blocks: one settled would be folded and read.</summary>
    private static bool Crosses(LayoutTree tree, RowRange rows, ScanSpec pass, long blockRows)
    {
        FieldMask mask = pass.Projection ?? FieldMask.All;
        long cap = pass.Options.BatchRows > 0 && pass.Options.BatchRows < blockRows ? pass.Options.BatchRows : blockRows;
        SplitCursor cursor = SplitPlan.Compute(tree, rows, in mask, cap).CreateCursor();
        while (cursor.TryNext(out RowRange split))
        {
            if (split.Start / blockRows != (split.End - 1) / blockRows)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether block <paramref name="block"/> settles: each key column without a null or a NaN, its
    /// exact bounds one key through the key's functions, and each extreme's column exact, or with no
    /// value at all.
    /// </summary>
    private static bool Settles(AggregationPlan plan, List<Stored> columns, int[] keys, List<int> extremes, int block, long rows)
    {
        for (int k = 0; k < keys.Length; k++)
        {
            Stored column = columns[keys[k]];
            ZoneBounds bounds = column.Zones!.Bounds(block);
            if (!bounds.HasNullCount || bounds.NullCount != 0 || !bounds.IsExact || !bounds.HasMin || !bounds.HasMax
                || (column.PType.IsFloat() && (!bounds.HasNanCount || bounds.NanCount != 0))
                || !TryMap(plan.Keys[k].Field, bounds.Min, out FilterLiteral low)
                || !TryMap(plan.Keys[k].Field, bounds.Max, out FilterLiteral high)
                || !Same(low, high))
            {
                return false;
            }
        }

        foreach (int extreme in extremes)
        {
            Stored column = columns[extreme];
            ZoneBounds bounds = column.Zones!.Bounds(block);
            bool exact = bounds.IsExact && bounds.HasMin && bounds.HasMax;
            bool none = !bounds.HasMin && !bounds.HasMax && bounds.HasNullCount
                && (bounds.NullCount >= rows
                    ? column.DType.IsNullable
                    : column.PType.IsFloat() && bounds.HasNanCount && bounds.NullCount + bounds.NanCount >= rows);
            if (!exact && !none)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A value of a column through the functions <paramref name="field"/> applies to it, the innermost first.</summary>
    private static bool TryMap(FieldExpr field, FilterLiteral value, out FilterLiteral mapped)
    {
        if (field is FunctionFieldExpr function)
        {
            mapped = default;
            return TryMap(function.Column, value, out FilterLiteral inner) && function.Function.TryMap(inner, out mapped);
        }

        mapped = value;
        return true;
    }

    /// <summary>Whether two bounds are one value, bit for bit: two zeros of two signs are two keys.</summary>
    private static bool Same(FilterLiteral left, FilterLiteral right) =>
        left.Kind == right.Kind && left.Kind switch
        {
            FilterLiteralKind.Signed => left.SignedValue == right.SignedValue,
            FilterLiteralKind.Unsigned => left.UnsignedValue == right.UnsignedValue,
            FilterLiteralKind.Float => BitConverter.DoubleToInt64Bits(left.FloatValue) == BitConverter.DoubleToInt64Bits(right.FloatValue),
            _ => false,
        };

    /// <summary>
    /// Column <paramref name="c"/> of the block folded in place of <paramref name="block"/>: its
    /// zone's minimum, then its maximum on the last row; one value when they are one; no value
    /// when the zone holds none, its rows null or NaN.
    /// </summary>
    private int Column(CanonicalArena arena, Scratch scratch, int c, int block, int rows)
    {
        Stored column = _columns[c];
        ZoneBounds bounds = column.Zones!.Bounds(block);
        DType storage = scratch.Storage[c];
        PType ptype = column.PType;
        int width = ptype.ByteWidth();
        Validity valid = storage.IsNullable ? Validity.AllValid : Validity.NonNullable;
        Span<byte> element = stackalloc byte[16];
        int node;
        if (!bounds.HasMin || !bounds.HasMax)
        {
            bool nulls = bounds.NullCount >= rows;
            if (nulls)
            {
                element[..width].Clear();
            }
            else
            {
                ZoneMapWriter.Write(element[..width], ptype, FilterLiteral.From(double.NaN));
            }

            node = arena.AddConstant(storage, rows, nulls ? Validity.AllInvalid : valid, element[..width]);
        }
        else if (rows == 1 || Same(bounds.Min, bounds.Max))
        {
            ZoneMapWriter.Write(element[..width], ptype, bounds.Min);
            node = arena.AddConstant(storage, rows, valid, element[..width]);
        }
        else
        {
            VortexBuffer values = arena.AllocateUninitialized(2 * width, 64, out Span<byte> into);
            ZoneMapWriter.Write(into[..width], ptype, bounds.Min);
            ZoneMapWriter.Write(into[width..], ptype, bounds.Max);
            int extremes = arena.AddPrimitive(storage, 2, valid, ptype, values);
            VortexBuffer ends = arena.AllocateUninitialized(2 * sizeof(uint), sizeof(uint), out Span<byte> runEnds);
            Span<uint> at = MemoryMarshal.Cast<byte, uint>(runEnds);
            at[0] = (uint)(rows - 1);
            at[1] = (uint)rows;
            node = arena.AddRunEnd(storage, rows, valid, ends, extremes);
        }

        return column.DType.Kind == DTypeKind.Extension ? arena.AddExtension(scratch.Types[c], rows, node) : node;
    }

    /// <summary>A column the settled blocks carry: a top-level field of the file, and its zones.</summary>
    internal sealed class Stored(int index, string path, DType dtype, PType ptype)
    {
        /// <summary>Its index among the file's fields; -1 for a column whose zones are no number's.</summary>
        internal int Index { get; } = index;

        internal string Path { get; } = path;

        internal DType DType { get; } = dtype;

        /// <summary>The type of its storage's numbers.</summary>
        internal PType PType { get; } = ptype;

        internal ZoneColumn? Zones { get; set; }
    }

    /// <summary>What one partition folds the settled blocks in: an arena emptied for each, and the dtypes, made once.</summary>
    internal sealed class Scratch
    {
        internal Scratch(Stored[] columns)
        {
            DTypeArena types = new DTypeArena();
            Types = new DType[columns.Length];
            Storage = new DType[columns.Length];
            Fields = new int[columns.Length];
            Span<int> names = columns.Length <= 16 ? stackalloc int[columns.Length] : new int[columns.Length];
            for (int c = 0; c < columns.Length; c++)
            {
                Types[c] = DTypeImport.Into(types, columns[c].DType);
                Storage[c] = Types[c].Kind == DTypeKind.Extension ? Types[c].StorageType : Types[c];
                names[c] = types.InternName(columns[c].Path);
            }

            Root = types.Struct(names, Types, Nullability.NonNullable);
            DTypes = types;
        }

        internal CanonicalArena Arena { get; } = new CanonicalArena();

        /// <summary>The arena of the dtypes below, kept with them.</summary>
        internal DTypeArena DTypes { get; }

        /// <summary>The struct of the columns, each named as in the file.</summary>
        internal DType Root { get; }

        /// <summary>Each column's dtype, an extension's included.</summary>
        internal DType[] Types { get; }

        /// <summary>Each column's numbers: its storage's dtype.</summary>
        internal DType[] Storage { get; }

        /// <summary>The nodes of a block's columns, as it is built.</summary>
        internal int[] Fields { get; }
    }
}
