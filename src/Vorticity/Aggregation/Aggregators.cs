using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// The aggregates a scan offers, as symbols: each picks, once, the state and the kernels its
/// column's storage calls for.
/// </summary>
internal static class Aggregators
{
    internal static ColumnShape Input<TRecord, T>(RecordBinding? binding, Func<Probe<TRecord>, Sym<T>> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (binding is null)
        {
            throw new InvalidOperationException("An aggregate is built inside the lambda of Select or AggAsync, which hands it its columns.");
        }

        ColumnSym read = column(new Probe<TRecord>(binding)).Column;
        if (read.Field is ResultFieldExpr)
        {
            throw new InvalidOperationException(
                $"'{read.Field.Path}' is a result of the group, not a column of its rows: an aggregate reads columns, and a chosen row's column is no aggregate's input.");
        }

        return new ColumnShape(read);
    }

    /// <summary>
    /// A group's chosen row, kept as its position: its first or last in file order, or the one
    /// holding the smallest or largest value of <paramref name="by"/>.
    /// </summary>
    internal static AggregateNode<long> Chosen(AggregateKind kind, ColumnShape? by, RowFilter? filter)
    {
        Func<AggregateSlot<long>> create = kind switch
        {
            AggregateKind.First => static () => new RowSlot(last: false),
            AggregateKind.Last => static () => new RowSlot(last: true),
            _ => ChosenBy(by!, kind == AggregateKind.MaxBy),
        };

        return new AggregateNode<long>(kind, by, create, null, filter: filter);
    }

    private static Func<AggregateSlot<long>> ChosenBy(ColumnShape by, bool max) => by.Kind switch
    {
        StorageKind.Primitive => by.PType switch
        {
            PType.I8 => () => new ChosenBySlot<sbyte>(max, StorageKind.Primitive),
            PType.I16 => () => new ChosenBySlot<short>(max, StorageKind.Primitive),
            PType.I32 => () => new ChosenBySlot<int>(max, StorageKind.Primitive),
            PType.I64 => () => new ChosenBySlot<long>(max, StorageKind.Primitive),
            PType.U8 => () => new ChosenBySlot<byte>(max, StorageKind.Primitive),
            PType.U16 => () => new ChosenBySlot<ushort>(max, StorageKind.Primitive),
            PType.U32 => () => new ChosenBySlot<uint>(max, StorageKind.Primitive),
            PType.U64 => () => new ChosenBySlot<ulong>(max, StorageKind.Primitive),
            PType.F16 => () => new ChosenBySlot<Half>(max, StorageKind.Primitive),
            PType.F32 => () => new ChosenBySlot<float>(max, StorageKind.Primitive),
            _ => () => new ChosenBySlot<double>(max, StorageKind.Primitive),
        },
        StorageKind.Decimal => () => new ChosenBySlot<Int128>(max, StorageKind.Decimal),
        _ => throw by.Unsupported(max ? "a MaxBy" : "a MinBy"),
    };

    /// <summary>The predicate of a filtered group, built over the rows' probe.</summary>
    internal static Predicate Rows<TRecord>(RecordBinding? binding, Func<Probe<TRecord>, Predicate> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (binding is null)
        {
            throw new InvalidOperationException("A filtered group is built inside the lambda of Select or AggAsync, which hands it its columns.");
        }

        return predicate(new Probe<TRecord>(binding));
    }

    /// <summary>The aggregate over the rows <paramref name="filter"/> keeps of its group: the aggregate itself for none.</summary>
    internal static Sym<T> Filtered<T>(Sym<T> aggregate, RowFilter? filter) =>
        filter is null ? aggregate : new Sym<T>(((AggregateNode<T>)aggregate.Node).Filtered(filter));

    internal static Sym<long> Count() =>
        new Sym<long>(new AggregateNode<long>(AggregateKind.Count, null, static source => Counter(source), static (StatisticsView view, out long value) =>
        {
            value = view.Rows;
            return true;
        }));

    /// <summary>A count of 32 bits where the source's rows are known to stay below 2^32, of 64 otherwise.</summary>
    private static AggregateSlot<long> Counter(ScanSource? source) =>
        source is { RowBound: >= 0 and <= uint.MaxValue } ? new CountSlot<uint>() : new CountSlot<long>();

    /// <summary>
    /// Whether the group holds a row <paramref name="filter"/> keeps: <c>Any(p)</c> when its last
    /// condition is <c>p</c> true, and with <paramref name="all"/>, <c>All(p)</c>, when it is
    /// <c>p</c> not true.
    /// </summary>
    internal static Sym<bool> Exists(RowFilter? filter, bool all) =>
        new Sym<bool>(new AggregateNode<bool>(all ? AggregateKind.All : AggregateKind.Any, null, all ? static () => new ExistsSlot(true) : static () => new ExistsSlot(false), null, filter: filter));

    internal static Sym<long> CountDistinct(ColumnShape shape)
    {
        Func<AggregateSlot<long>> create = shape.Kind switch
        {
            StorageKind.Primitive => shape.PType switch
            {
                PType.I8 => static () => new FixedDistinctSlot<sbyte>(StorageKind.Primitive),
                PType.I16 => static () => new FixedDistinctSlot<short>(StorageKind.Primitive),
                PType.I32 => static () => new FixedDistinctSlot<int>(StorageKind.Primitive),
                PType.I64 => static () => new FixedDistinctSlot<long>(StorageKind.Primitive),
                PType.U8 => static () => new FixedDistinctSlot<byte>(StorageKind.Primitive),
                PType.U16 => static () => new FixedDistinctSlot<ushort>(StorageKind.Primitive),
                PType.U32 => static () => new FixedDistinctSlot<uint>(StorageKind.Primitive),
                PType.U64 => static () => new FixedDistinctSlot<ulong>(StorageKind.Primitive),
                PType.F16 => static () => new FixedDistinctSlot<Half>(StorageKind.Primitive),
                PType.F32 => static () => new FixedDistinctSlot<float>(StorageKind.Primitive),
                _ => static () => new FixedDistinctSlot<double>(StorageKind.Primitive),
            },
            StorageKind.Decimal => static () => new FixedDistinctSlot<Int128>(StorageKind.Decimal),
            StorageKind.Decimal256 => static () => new FixedDistinctSlot<Int256>(StorageKind.Decimal256),
            StorageKind.Uuid => static () => new FixedDistinctSlot<UInt128>(StorageKind.Uuid),
            StorageKind.Bool => static () => new BoolSlot<long>(BoolFlags.Distinct),
            StorageKind.Bytes => static () => new BytesDistinctSlot(),
            _ => throw shape.Unsupported("a distinct count"),
        };
        return new Sym<long>(new AggregateNode<long>(AggregateKind.CountDistinct, shape, create, null));
    }

    internal static Sym<T> Sum<T>(ColumnShape shape)
        where T : INumber<T>
    {
        Func<ScanSource?, AggregateSlot<T>> create = shape.Kind switch
        {
            StorageKind.Primitive => shape.PType switch
            {
                PType.I8 => Signed<sbyte, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.I16 => Signed<short, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.I32 => Signed<int, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.I64 => Signed<long, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.U8 => Unsigned<byte, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.U16 => Unsigned<ushort, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.U32 => Unsigned<uint, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.U64 => Unsigned<ulong, T>(shape, static s => T.CreateChecked(s.Sum), static s => T.CreateChecked(s.Sum)),
                PType.F16 => static _ => new FixedSlot<Half, IndexedSum, IndexedFloatSum<Half>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Value)),
                PType.F32 => static _ => new FixedSlot<float, IndexedSum, IndexedFloatSum<float>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Value)),
                _ => static _ => new FixedSlot<double, IndexedSum, IndexedFloatSum<double>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Value)),
            },
            StorageKind.Decimal or StorageKind.Decimal256 => Unsourced(DecimalSumSlot<T>(shape)),
            _ => throw shape.Unsupported("a sum"),
        };

        Func<ScanSource?, AggregateSlot<T>>? alone = shape.Kind != StorageKind.Primitive ? null : shape.PType switch
        {
            PType.I8 => SignedAlone<sbyte, T>(shape),
            PType.I16 => SignedAlone<short, T>(shape),
            PType.I32 => SignedAlone<int, T>(shape),
            PType.I64 => SignedAlone<long, T>(shape),
            PType.U8 => UnsignedAlone<byte, T>(shape),
            PType.U16 => UnsignedAlone<ushort, T>(shape),
            PType.U32 => UnsignedAlone<uint, T>(shape),
            PType.U64 => UnsignedAlone<ulong, T>(shape),
            _ => null,
        };

        return new Sym<T>(new AggregateNode<T>(AggregateKind.Sum, shape, create, (StatisticsView view, out T value) => SettleSum(shape, view, out value), alone: alone));
    }

    /// <summary>
    /// The state of a sum of signed integers, chosen as the run starts: 64 bits where the source's
    /// statistics prove its rows times the column's largest magnitude stay below 2^63, so that no sum
    /// of any of them overflows; 128 bits where they prove nothing. The width is the engine's, the
    /// exactness the promise.
    /// </summary>
    private static Func<ScanSource?, AggregateSlot<TResult>> Signed<TValue, TResult>(
        ColumnShape shape, Func<SumState<long>, TResult> narrow, Func<SumState<Int128>, TResult> wide)
        where TValue : unmanaged, IBinaryInteger<TValue> =>
        source => Proven(source, shape, (UInt128)long.MaxValue)
            ? new FixedSlot<TValue, SumState<long>, NarrowSignedSum<TValue>, TResult>(StorageKind.Primitive, narrow)
            : new FixedSlot<TValue, SumState<Int128>, SignedSum<TValue>, TResult>(StorageKind.Primitive, wide);

    /// <summary>The state of a sum of unsigned integers: 64 bits where the statistics prove its rows times its largest value stay below 2^64.</summary>
    private static Func<ScanSource?, AggregateSlot<TResult>> Unsigned<TValue, TResult>(
        ColumnShape shape, Func<SumState<ulong>, TResult> narrow, Func<SumState<UInt128>, TResult> wide)
        where TValue : unmanaged, IBinaryInteger<TValue> =>
        source => Proven(source, shape, ulong.MaxValue)
            ? new FixedSlot<TValue, SumState<ulong>, NarrowUnsignedSum<TValue>, TResult>(StorageKind.Primitive, narrow)
            : new FixedSlot<TValue, SumState<UInt128>, UnsignedSum<TValue>, TResult>(StorageKind.Primitive, wide);

    /// <summary>
    /// The state of a sum of signed integers that no mean reads: its total alone, of the width
    /// <see cref="Signed{TValue, TResult}"/> chooses, without the count only a mean divides by.
    /// </summary>
    private static Func<ScanSource?, AggregateSlot<TResult>> SignedAlone<TValue, TResult>(ColumnShape shape)
        where TValue : unmanaged, IBinaryInteger<TValue>
        where TResult : INumber<TResult> =>
        source => Proven(source, shape, (UInt128)long.MaxValue)
            ? new FixedSlot<TValue, long, NarrowSignedTotal<TValue>, TResult>(StorageKind.Primitive, static s => TResult.CreateChecked(s))
            : new FixedSlot<TValue, Int128, SignedTotal<TValue>, TResult>(StorageKind.Primitive, static s => TResult.CreateChecked(s));

    /// <summary>The state of a sum of unsigned integers that no mean reads: its total alone.</summary>
    private static Func<ScanSource?, AggregateSlot<TResult>> UnsignedAlone<TValue, TResult>(ColumnShape shape)
        where TValue : unmanaged, IBinaryInteger<TValue>
        where TResult : INumber<TResult> =>
        source => Proven(source, shape, ulong.MaxValue)
            ? new FixedSlot<TValue, ulong, NarrowUnsignedTotal<TValue>, TResult>(StorageKind.Primitive, static s => TResult.CreateChecked(s))
            : new FixedSlot<TValue, UInt128, UnsignedTotal<TValue>, TResult>(StorageKind.Primitive, static s => TResult.CreateChecked(s));

    /// <summary>Whether the source's rows times the column's largest magnitude, from its statistics, stay at or below <paramref name="limit"/>.</summary>
    private static bool Proven(ScanSource? source, ColumnShape shape, UInt128 limit)
    {
        if (source is null || source.RowBound < 0
            || !source.TryBounds(shape.Column.FieldPath, out Expressions.FilterLiteral min, out Expressions.FilterLiteral max)
            || !TryMagnitude(min, out UInt128 low) || !TryMagnitude(max, out UInt128 high))
        {
            return false;
        }

        // Each magnitude is below 2^64 and the rows below 2^63: the product fits 128 bits.
        return UInt128.Max(low, high) * (ulong)source.RowBound <= limit;
    }

    private static bool TryMagnitude(Expressions.FilterLiteral bound, out UInt128 magnitude)
    {
        switch (bound.Kind)
        {
            case Expressions.FilterLiteralKind.Signed:
                magnitude = (UInt128)Int128.Abs(bound.SignedValue);
                return true;
            case Expressions.FilterLiteralKind.Unsigned:
                magnitude = bound.UnsignedValue;
                return true;
            default:
                magnitude = UInt128.Zero;
                return false;
        }
    }

    /// <summary>A state that takes nothing from the run's source.</summary>
    private static Func<ScanSource?, AggregateSlot<T>> Unsourced<T>(Func<AggregateSlot<T>> create) => _ => create();

    /// <summary>The sum of a decimal column as a <see cref="VortexDecimal"/>, exact at any precision.</summary>
    internal static Sym<VortexDecimal> SumDecimal(ColumnShape shape) =>
        shape.Kind is StorageKind.Decimal or StorageKind.Decimal256
            ? new Sym<VortexDecimal>(new AggregateNode<VortexDecimal>(AggregateKind.Sum, shape, DecimalSumSlot<VortexDecimal>(shape), null))
            : throw shape.Unsupported("a sum as VortexDecimal");

    /// <summary>
    /// The state of a decimal sum: a 128-bit total for eighteen digits or fewer, which cannot overflow
    /// below 2^67 rows, and above that a narrow total spilled into 320 bits, which never does.
    /// </summary>
    private static Func<AggregateSlot<T>> DecimalSumSlot<T>(ColumnShape shape)
    {
        int precision = shape.Type.Precision;
        int scale = shape.Type.Scale;
        return shape.Kind switch
        {
            StorageKind.Decimal when precision <= 18 =>
                () => new FixedSlot<Int128, SumState<Int128>, DecimalSum, T>(StorageKind.Decimal, s => DecimalAs<T>(new Int256(s.Sum), precision, scale)),
            StorageKind.Decimal =>
                () => new FixedSlot<Int128, SumState<WideSum>, WideDecimalSum, T>(StorageKind.Decimal, s => DecimalAs<T>(in s.Sum, precision, scale)),
            _ => () => new FixedSlot<Int256, SumState<WideSum>, Decimal256Sum, T>(StorageKind.Decimal256, s => DecimalAs<T>(in s.Sum, precision, scale)),
        };
    }

    internal static Sym<double?> Average(ColumnShape shape)
    {
        Func<ScanSource?, AggregateSlot<double?>> create;
        switch (shape.Kind)
        {
            case StorageKind.Primitive:
                create = shape.PType switch
                {
                    PType.I8 => Signed<sbyte, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.I16 => Signed<short, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.I32 => Signed<int, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.I64 => Signed<long, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.U8 => Unsigned<byte, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.U16 => Unsigned<ushort, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.U32 => Unsigned<uint, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.U64 => Unsigned<ulong, double?>(shape, static s => Mean(s.Sum, s.Count), static s => Mean(s.Sum, s.Count)),
                    PType.F16 => static _ => new FixedSlot<Half, IndexedSum, IndexedFloatSum<Half>, double?>(StorageKind.Primitive, static s => Mean(s.Value, s.Count)),
                    PType.F32 => static _ => new FixedSlot<float, IndexedSum, IndexedFloatSum<float>, double?>(StorageKind.Primitive, static s => Mean(s.Value, s.Count)),
                    _ => static _ => new FixedSlot<double, IndexedSum, IndexedFloatSum<double>, double?>(StorageKind.Primitive, static s => Mean(s.Value, s.Count)),
                };
                break;
            case StorageKind.Decimal when shape.Type.Precision <= 18:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = _ => new FixedSlot<Int128, SumState<Int128>, DecimalSum, double?>(StorageKind.Decimal, s => s.Count == 0 ? null : (double)s.Sum / unit / s.Count);
                break;
            }

            case StorageKind.Decimal:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = _ => new FixedSlot<Int128, SumState<WideSum>, WideDecimalSum, double?>(StorageKind.Decimal, s => s.Count == 0 ? null : s.Sum.ToDouble() / unit / s.Count);
                break;
            }

            case StorageKind.Decimal256:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = _ => new FixedSlot<Int256, SumState<WideSum>, Decimal256Sum, double?>(StorageKind.Decimal256, s => s.Count == 0 ? null : s.Sum.ToDouble() / unit / s.Count);
                break;
            }

            default:
                throw shape.Unsupported("a mean");
        }

        return new Sym<double?>(new AggregateNode<double?>(AggregateKind.Average, shape, create, (StatisticsView view, out double? value) => SettleAvg(shape, view, out value)));
    }

    /// <summary>
    /// The sample variance of a numeric column, or with <paramref name="deviation"/> its square root:
    /// from the indexed sums of <c>x − c</c> and <c>(x − c)²</c>, <c>c</c> fixed for the run.
    /// </summary>
    internal static Sym<double?> Variance(ColumnShape shape, bool deviation)
    {
        Func<VarianceState, double?> finish = deviation
            ? static s => s.Variance is double variance ? Math.Sqrt(variance) : null
            : static s => s.Variance;
        Func<ScanSource?, AggregateSlot<double?>> create = shape.Kind switch
        {
            StorageKind.Primitive => shape.PType switch
            {
                PType.I8 => source => VarianceSlot<sbyte>(source, shape, 1, finish),
                PType.I16 => source => VarianceSlot<short>(source, shape, 1, finish),
                PType.I32 => source => VarianceSlot<int>(source, shape, 1, finish),
                PType.I64 => source => VarianceSlot<long>(source, shape, 1, finish),
                PType.U8 => source => VarianceSlot<byte>(source, shape, 1, finish),
                PType.U16 => source => VarianceSlot<ushort>(source, shape, 1, finish),
                PType.U32 => source => VarianceSlot<uint>(source, shape, 1, finish),
                PType.U64 => source => VarianceSlot<ulong>(source, shape, 1, finish),
                PType.F16 => source => VarianceSlot<Half>(source, shape, 1, finish),
                PType.F32 => source => VarianceSlot<float>(source, shape, 1, finish),
                _ => source => VarianceSlot<double>(source, shape, 1, finish),
            },
            StorageKind.Decimal => source => VarianceSlot<Int128>(source, shape, Math.Pow(10, -shape.Type.Scale), finish),
            _ => throw shape.Unsupported(deviation ? "a standard deviation" : "a variance"),
        };

        return new Sym<double?>(new AggregateNode<double?>(deviation ? AggregateKind.StandardDeviation : AggregateKind.Variance, shape, create, null));
    }

    /// <summary>
    /// A variance's slot, whose op holds its center: the middle of the column's bounds over the whole
    /// source where its statistics hold them, zero where they do not; the same for every group and
    /// every partition of the run, and so held once rather than in each group's state.
    /// </summary>
    private static FixedSlot<TValue, VarianceState, VarianceOp<TValue>, double?> VarianceSlot<TValue>(
        ScanSource? source, ColumnShape shape, double unit, Func<VarianceState, double?> finish)
        where TValue : unmanaged, INumberBase<TValue>
    {
        double center = 0;
        if (source is not null && source.TryBounds(shape.Column.FieldPath, out Expressions.FilterLiteral min, out Expressions.FilterLiteral max))
        {
            double middle = (Number(min, unit) * 0.5) + (Number(max, unit) * 0.5);
            center = double.IsFinite(middle) ? middle : 0;
        }

        return new FixedSlot<TValue, VarianceState, VarianceOp<TValue>, double?>(shape.Kind, finish, new VarianceOp<TValue>(center, unit));
    }

    private static double Number(Expressions.FilterLiteral bound, double unit) => bound.Kind switch
    {
        Expressions.FilterLiteralKind.Signed => bound.SignedValue * unit,
        Expressions.FilterLiteralKind.Unsigned => bound.UnsignedValue * unit,
        Expressions.FilterLiteralKind.Float => bound.FloatValue,
        _ => double.NaN,
    };

    internal static Sym<T?> Extreme<T>(ColumnShape shape, bool max)
    {
        Func<ScanSource?, AggregateSlot<T?>> create = shape.Kind switch
        {
            StorageKind.Primitive => shape.PType switch
            {
                PType.I8 => Extreme<sbyte, T>(shape, max),
                PType.I16 => Extreme<short, T>(shape, max),
                PType.I32 => Extreme<int, T>(shape, max),
                PType.I64 => Extreme<long, T>(shape, max),
                PType.U8 => Extreme<byte, T>(shape, max),
                PType.U16 => Extreme<ushort, T>(shape, max),
                PType.U32 => Extreme<uint, T>(shape, max),
                PType.U64 => Extreme<ulong, T>(shape, max),
                PType.F16 => Extreme<Half, T>(shape, max),
                PType.F32 => Extreme<float, T>(shape, max),
                _ => Extreme<double, T>(shape, max),
            },
            StorageKind.Decimal => Extreme<Int128, T>(shape, max),
            StorageKind.Decimal256 => Unsourced(OrderedExtreme<Int256, T>(shape, max)),
            StorageKind.Uuid => Extreme<UInt128, T>(shape, max),
            StorageKind.Bool => Unsourced(BoolExtreme<T>(shape, max)),
            StorageKind.Bytes => Unsourced<T?>(() => new BytesExtremeSlot<T?>(shape, max)),
            _ => throw shape.Unsupported(max ? "a maximum" : "a minimum"),
        };
        return new Sym<T?>(new AggregateNode<T?>(max ? AggregateKind.Max : AggregateKind.Min, shape, create, (StatisticsView view, out T? value) => SettleExtreme(shape, max, view, out value)));
    }

    internal static Sym<TState> Custom<T, TAggregator, TState>(ColumnShape shape)
        where T : unmanaged
        where TAggregator : IAggregator<T, TState>
    {
        ClrShape clr = ClrShape.For<T>.Value;
        if (shape.Kind != StorageKind.Primitive || clr.Kind is not (ClrKind.Signed or ClrKind.Unsigned or ClrKind.Float) || clr.PType != shape.PType)
        {
            throw new VortexSchemaException(
                $"'{shape.Path}' is {shape.Type}; an aggregator over {ClrFit.Name(typeof(T))} reads a column stored as {ClrFit.Name(typeof(T))}.");
        }

        // A state that is a record is written and read through it; the seed is a value of the type to ask.
        IVortexRecord? record = TAggregator.Seed() as IVortexRecord;
        return new Sym<TState>(new AggregateNode<TState>(AggregateKind.Custom, shape, CustomFactory<T, TAggregator, TState>.Create, null, typeof(TAggregator), record));
    }

    private static Func<AggregateSlot<TState>> EncodedFactory<T, TAggregator, TState>()
        where T : unmanaged
        where TAggregator : IEncodedAggregator<T, TState> =>
        static () => new EncodedCustomSlot<T, TAggregator, TState>();

    /// <summary>
    /// The state of a caller's aggregator, chosen once per aggregator type: its encoded steps when it
    /// has them and the runtime can instantiate the generic that calls them, its canonical step otherwise.
    /// </summary>
    private static class CustomFactory<T, TAggregator, TState>
        where T : unmanaged
        where TAggregator : IAggregator<T, TState>
    {
        internal static readonly Func<AggregateSlot<TState>> Create = Choose();

        private static Func<AggregateSlot<TState>> Choose()
        {
            if (typeof(IEncodedAggregator<T, TState>).IsAssignableFrom(typeof(TAggregator)))
            {
                if (RuntimeFeature.IsDynamicCodeSupported)
                {
                    MethodInfo encoded = typeof(Aggregators)
                        .GetMethod(nameof(EncodedFactory), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(typeof(T), typeof(TAggregator), typeof(TState));
                    return (Func<AggregateSlot<TState>>)encoded.Invoke(null, null)!;
                }
            }

            return static () => new CustomSlot<T, TAggregator, TState>();
        }
    }

    /// <summary>
    /// The state of an extreme: the value alone, from a seed no row holds, where the column leaves one
    /// (<see cref="Unreached"/>); the value and whether one was seen otherwise.
    /// </summary>
    private static Func<ScanSource?, AggregateSlot<T?>> Extreme<TValue, T>(ColumnShape shape, bool max)
        where TValue : unmanaged, INumber<TValue>, IMinMaxValue<TValue>
    {
        Func<ExtremeState<TValue>, T?> finish = s => s.Has ? StorageValues.ToClr<TValue, T>(s.Value, shape) : default;
        return source =>
        {
            if (!Unreached(source, shape, max, out TValue seed))
            {
                return max
                    ? new FixedSlot<TValue, ExtremeState<TValue>, MaxOp<TValue>, T?>(shape.Kind, finish)
                    : new FixedSlot<TValue, ExtremeState<TValue>, MinOp<TValue>, T?>(shape.Kind, finish);
            }

            // A group still at the seed, or at NaN, saw no value.
            Func<TValue, T?> value = s => TValue.IsNaN(s) || s == seed ? default : StorageValues.ToClr<TValue, T>(s, shape);
            return max
                ? new FixedSlot<TValue, TValue, SeededMaxOp<TValue>, T?>(shape.Kind, value, seed)
                : new FixedSlot<TValue, TValue, SeededMinOp<TValue>, T?>(shape.Kind, value, seed);
        };
    }

    /// <summary>
    /// The value an extreme's state starts from, which says that no value was seen, when no row of the
    /// source can hold it: NaN for a float, which no add keeps; for a decimal of 38 digits or fewer, the
    /// end of 128 bits, past all of them; for an integer, the end of its range the extreme moves away
    /// from, where the statistics prove the column stops short of it. A uuid may hold any 128 bits.
    /// </summary>
    private static bool Unreached<TValue>(ScanSource? source, ColumnShape shape, bool max, out TValue seed)
        where TValue : unmanaged, INumber<TValue>, IMinMaxValue<TValue>
    {
        if (typeof(TValue) == typeof(double) || typeof(TValue) == typeof(float) || typeof(TValue) == typeof(Half))
        {
            seed = TValue.CreateTruncating(double.NaN);
            return true;
        }

        seed = max ? TValue.MinValue : TValue.MaxValue;
        if (shape.Kind == StorageKind.Decimal)
        {
            return true;
        }

        if (shape.Kind != StorageKind.Primitive || source is null
            || !source.TryBounds(shape.Column.FieldPath, out Expressions.FilterLiteral low, out Expressions.FilterLiteral high))
        {
            return false;
        }

        // The column's bound on the seed's side, which must stop short of it.
        Expressions.FilterLiteral bound = max ? low : high;
        Int128 end = Int128.CreateTruncating(seed);
        return bound.Kind switch
        {
            Expressions.FilterLiteralKind.Signed => max ? bound.SignedValue > end : bound.SignedValue < end,
            Expressions.FilterLiteralKind.Unsigned => max ? bound.UnsignedValue > end : bound.UnsignedValue < end,
            _ => false,
        };
    }

    private static Func<AggregateSlot<T?>> OrderedExtreme<TValue, T>(ColumnShape shape, bool max)
        where TValue : unmanaged, IComparable<TValue>
    {
        Func<ExtremeState<TValue>, T?> finish = s => s.Has ? StorageValues.ToClr<TValue, T>(s.Value, shape) : default;
        if (max)
        {
            return () => new FixedSlot<TValue, ExtremeState<TValue>, OrderedExtremeOp<TValue, Yes>, T?>(shape.Kind, finish);
        }

        return () => new FixedSlot<TValue, ExtremeState<TValue>, OrderedExtremeOp<TValue, No>, T?>(shape.Kind, finish);
    }

    private static Func<AggregateSlot<T?>> BoolExtreme<T>(ColumnShape shape, bool max)
    {
        Func<byte, T?> finish = max
            ? flags => BoolFlags.Max(flags) is bool most ? StorageValues.BoolToClr<T>(most, shape) : default
            : flags => BoolFlags.Min(flags) is bool least ? StorageValues.BoolToClr<T>(least, shape) : default;
        return () => new BoolSlot<T?>(finish);
    }

    private static double? Mean<TAcc>(TAcc sum, long count)
        where TAcc : INumberBase<TAcc> =>
        count == 0 ? null : double.CreateTruncating(sum) / count;

    /// <summary>An exact decimal total as the sum's type, <see cref="OverflowException"/> when it does not fit.</summary>
    private static T DecimalAs<T>(in WideSum total, int precision, int scale)
    {
        if (typeof(T) == typeof(BigInteger))
        {
            BigInteger big = total.ToBigInteger();
            return Unsafe.As<BigInteger, T>(ref big);
        }

        return total.TryToInt256(out Int256 narrow)
            ? DecimalAs<T>(narrow, precision, scale)
            : throw new OverflowException($"The sum of a decimal({precision}, {scale}) column passes 76 digits.");
    }

    /// <summary>An unscaled total as a <see cref="decimal"/> or a <see cref="VortexDecimal"/> of the column's scale.</summary>
    /// <remarks>
    /// A <see cref="VortexDecimal"/> sum carries 38 digits while it fits them, as a sum of a narrower
    /// decimal does in SQL, and 76 beyond, which is as far as a decimal goes.
    /// </remarks>
    private static T DecimalAs<T>(Int256 total, int precision, int scale)
    {
        if (typeof(T) == typeof(VortexDecimal))
        {
            if (!DecimalDigits.Fits(total, 76))
            {
                throw new OverflowException($"The sum of a decimal({precision}, {scale}) column passes 76 digits.");
            }

            byte digits = (byte)(precision <= 38 && DecimalDigits.Fits(total, 38) ? 38 : 76);
            VortexDecimal wide = new VortexDecimal(total, digits, (sbyte)scale);
            return Unsafe.As<VortexDecimal, T>(ref wide);
        }

        // An integer read from a decimal of scale 0: the unscaled sum is the sum.
        if (typeof(T) == typeof(Int128))
        {
            Int128 integer = total.TryToInt128(out Int128 narrow) ? narrow : throw new OverflowException("The sum does not fit an Int128.");
            return Unsafe.As<Int128, T>(ref integer);
        }

        if (typeof(T) == typeof(UInt128))
        {
            total.GetLimbs(out ulong l0, out ulong l1, out ulong l2, out ulong l3);
            UInt128 unsigned = (l2 | l3) == 0 ? new UInt128(l1, l0) : throw new OverflowException("The sum does not fit a UInt128.");
            return Unsafe.As<UInt128, T>(ref unsigned);
        }

        if (typeof(T) == typeof(BigInteger))
        {
            Span<byte> bytes = stackalloc byte[Int256.ByteCount];
            total.WriteLittleEndianBytes(bytes);
            BigInteger big = new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
            return Unsafe.As<BigInteger, T>(ref big);
        }

        if (typeof(T) != typeof(decimal))
        {
            throw new VortexSchemaException($"A decimal column sums as decimal or VortexDecimal, not as {ClrFit.Name(typeof(T))}.");
        }

        total.GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);
        if (!Int256.FitsIn96Bits(m1, m2, m3) || scale is < 0 or > 28)
        {
            throw new OverflowException("The sum does not fit a decimal.");
        }

        decimal value = Int256.MakeDecimal(m0, m1, total.IsNegative, (byte)scale);
        return Unsafe.As<decimal, T>(ref value);
    }

    private static bool SettleSum<T>(ColumnShape shape, StatisticsView view, out T value)
        where T : INumber<T>
    {
        value = T.Zero;
        if (!view.TryField(shape, out FieldStatistics statistics))
        {
            return false;
        }

        switch (shape.Kind)
        {
            case StorageKind.Primitive when shape.PType.IsSignedInteger():
                if (statistics.TryGetSum(out long signed))
                {
                    value = T.CreateChecked(signed);
                    return true;
                }

                return false;
            case StorageKind.Primitive when shape.PType.IsUnsignedInteger():
                if (statistics.TryGetSum(out ulong unsigned))
                {
                    value = T.CreateChecked(unsigned);
                    return true;
                }

                return false;
            case StorageKind.Primitive:
                // A float sum is the indexed sum's, the same bits under every cut; the writer's is
                // a plain one, whose last bits follow its order.
                return false;
            case StorageKind.Decimal when typeof(T) == typeof(decimal) && shape.Type.Precision + 10 <= 28:
                if (statistics.TryGetSum(out decimal total))
                {
                    value = Unsafe.As<decimal, T>(ref total);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static bool SettleAvg(ColumnShape shape, StatisticsView view, out double? value)
    {
        value = null;
        if (!view.TryField(shape, out FieldStatistics statistics) || !statistics.TryGetNullCount(out long nulls))
        {
            return false;
        }

        double sum;
        switch (shape.Kind)
        {
            case StorageKind.Primitive when shape.PType.IsSignedInteger():
                if (!statistics.TryGetSum(out long signed))
                {
                    return false;
                }

                sum = signed;
                break;
            case StorageKind.Primitive when shape.PType.IsUnsignedInteger():
                if (!statistics.TryGetSum(out ulong unsigned))
                {
                    return false;
                }

                sum = unsigned;
                break;
            case StorageKind.Primitive:
                // A float mean is the indexed sum's over the count, which the writer's sum is not.
                return false;
            case StorageKind.Decimal when shape.Type.Precision + 10 <= 28:
                if (!statistics.TryGetSum(out decimal total))
                {
                    return false;
                }

                sum = (double)total;
                break;
            default:
                return false;
        }

        long count = view.Rows - nulls;
        value = count > 0 ? sum / count : null;
        return true;
    }

    private static bool SettleExtreme<T>(ColumnShape shape, bool max, StatisticsView view, out T? value)
    {
        value = default;
        if (ClrShape.For<T>.Value.Kind is ClrKind.Unsupported or ClrKind.List or ClrKind.Extension
            || !view.TryField(shape, out FieldStatistics statistics))
        {
            return false;
        }

        return max ? statistics.TryGetMax(out value) : statistics.TryGetMin(out value);
    }
}

/// <summary>The file statistics of a scan that reads the whole file with no filter: the one case where they are the answer.</summary>
internal sealed class StatisticsView
{
    private readonly VortexFile _file;

    private StatisticsView(VortexFile file)
    {
        _file = file;
        Rows = file.RowCount;
    }

    internal long Rows { get; }

    /// <summary>The view of <paramref name="source"/> for <paramref name="spec"/>, or null when the statistics do not describe the scan's rows.</summary>
    internal static StatisticsView? For(ScanSource source, ScanSpec spec) =>
        source is FileScanSource file && file.File.HasFileStatistics && spec.Filter is null && !spec.MatchesNothing
        && spec.Rows is null && spec.Take is null && spec.Options.Pruning
            ? new StatisticsView(file.File)
            : null;

    /// <summary>The statistics of a top-level column; the file keeps none deeper.</summary>
    internal bool TryField(ColumnShape shape, out FieldStatistics statistics)
    {
        int[] path = shape.Column.FieldPath;
        VortexFileStatistics all = _file.Statistics;
        if (path.Length != 1 || !_file.Schema.RootIsStruct || path[0] >= all.Count)
        {
            statistics = default;
            return false;
        }

        statistics = all[path[0]];
        return true;
    }
}
