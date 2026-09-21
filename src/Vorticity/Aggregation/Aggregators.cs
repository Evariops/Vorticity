using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vorticity.Types;

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
            throw new InvalidOperationException("An aggregate is built inside the lambda of AggAsync or Agg, which hands it its columns.");
        }

        return new ColumnShape(column(new Probe<TRecord>(binding)).Column);
    }

    internal static Sym<long> Count() =>
        new Sym<long>(new AggregateNode<long>(AggregateKind.Count, null, static () => new CountSlot(), static (StatisticsView view, out long value) =>
        {
            value = view.Rows;
            return true;
        }));

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
        Func<AggregateSlot<T>> create;
        switch (shape.Kind)
        {
            case StorageKind.Primitive:
                create = shape.PType switch
                {
                    PType.I8 => static () => new FixedSlot<sbyte, SumState<long>, SignedSum<sbyte>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.I16 => static () => new FixedSlot<short, SumState<long>, SignedSum<short>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.I32 => static () => new FixedSlot<int, SumState<long>, SignedSum<int>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.I64 => static () => new FixedSlot<long, SumState<long>, SignedSum<long>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.U8 => static () => new FixedSlot<byte, SumState<ulong>, UnsignedSum<byte>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.U16 => static () => new FixedSlot<ushort, SumState<ulong>, UnsignedSum<ushort>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.U32 => static () => new FixedSlot<uint, SumState<ulong>, UnsignedSum<uint>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.U64 => static () => new FixedSlot<ulong, SumState<ulong>, UnsignedSum<ulong>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.F16 => static () => new FixedSlot<Half, SumState<double>, FloatSum<Half>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    PType.F32 => static () => new FixedSlot<float, SumState<double>, FloatSum<float>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                    _ => static () => new FixedSlot<double, SumState<double>, FloatSum<double>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                };
                break;
            case StorageKind.Decimal:
            {
                int scale = shape.Type.Scale;
                create = () => new FixedSlot<Int128, SumState<Int128>, DecimalSum, T>(StorageKind.Decimal, s => DecimalAs<T>(s.Sum, scale));
                break;
            }

            default:
                throw shape.Unsupported("a sum");
        }

        return new Sym<T>(new AggregateNode<T>(AggregateKind.Sum, shape, create, (StatisticsView view, out T value) => SettleSum(shape, view, out value)));
    }

    internal static Sym<double?> Avg(ColumnShape shape)
    {
        Func<AggregateSlot<double?>> create;
        switch (shape.Kind)
        {
            case StorageKind.Primitive:
                create = shape.PType switch
                {
                    PType.I8 => static () => new FixedSlot<sbyte, SumState<long>, SignedSum<sbyte>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.I16 => static () => new FixedSlot<short, SumState<long>, SignedSum<short>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.I32 => static () => new FixedSlot<int, SumState<long>, SignedSum<int>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.I64 => static () => new FixedSlot<long, SumState<long>, SignedSum<long>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U8 => static () => new FixedSlot<byte, SumState<ulong>, UnsignedSum<byte>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U16 => static () => new FixedSlot<ushort, SumState<ulong>, UnsignedSum<ushort>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U32 => static () => new FixedSlot<uint, SumState<ulong>, UnsignedSum<uint>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U64 => static () => new FixedSlot<ulong, SumState<ulong>, UnsignedSum<ulong>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.F16 => static () => new FixedSlot<Half, SumState<double>, FloatSum<Half>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.F32 => static () => new FixedSlot<float, SumState<double>, FloatSum<float>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    _ => static () => new FixedSlot<double, SumState<double>, FloatSum<double>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                };
                break;
            case StorageKind.Decimal:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = () => new FixedSlot<Int128, SumState<Int128>, DecimalSum, double?>(StorageKind.Decimal, s => s.Count == 0 ? null : (double)s.Sum / unit / s.Count);
                break;
            }

            default:
                throw shape.Unsupported("a mean");
        }

        return new Sym<double?>(new AggregateNode<double?>(AggregateKind.Avg, shape, create, (StatisticsView view, out double? value) => SettleAvg(shape, view, out value)));
    }

    internal static Sym<T?> Extreme<T>(ColumnShape shape, bool max)
    {
        Func<AggregateSlot<T?>> create = shape.Kind switch
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
            StorageKind.Uuid => Extreme<UInt128, T>(shape, max),
            StorageKind.Bool => BoolExtreme<T>(shape, max),
            StorageKind.Bytes => () => new BytesExtremeSlot<T?>(shape, max),
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
                $"'{shape.Path}' is {shape.Type}; an aggregator over {typeof(T).Name} reads a column stored as {typeof(T).Name}.");
        }

        return new Sym<TState>(new AggregateNode<TState>(AggregateKind.Custom, shape, CustomFactory<T, TAggregator, TState>.Create, null));
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

    private static Func<AggregateSlot<T?>> Extreme<TValue, T>(ColumnShape shape, bool max)
        where TValue : unmanaged, INumber<TValue>
    {
        Func<ExtremeState<TValue>, T?> finish = s => s.Has ? StorageValues.ToClr<TValue, T>(s.Value, shape) : default;
        if (max)
        {
            return () => new FixedSlot<TValue, ExtremeState<TValue>, MaxOp<TValue>, T?>(shape.Kind, finish);
        }

        return () => new FixedSlot<TValue, ExtremeState<TValue>, MinOp<TValue>, T?>(shape.Kind, finish);
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

    /// <summary>An unscaled sum as a <see cref="decimal"/> of the column's scale.</summary>
    /// <exception cref="OverflowException">The sum has more than 96 bits.</exception>
    private static T DecimalAs<T>(Int128 unscaled, int scale)
    {
        if (typeof(T) != typeof(decimal))
        {
            throw new VortexSchemaException($"A decimal column sums as decimal, not as {typeof(T).Name}.");
        }

        bool negative = unscaled < 0;
        UInt128 magnitude = negative ? (UInt128)(-unscaled) : (UInt128)unscaled;
        if (magnitude >> 96 != UInt128.Zero)
        {
            throw new OverflowException("The sum does not fit a decimal.");
        }

        decimal value = new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), negative, (byte)scale);
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
                if (statistics.TryGetSum(out double floating))
                {
                    value = T.CreateChecked(floating);
                    return true;
                }

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
        long nans = 0;
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
                // The mean skips a NaN as the sum does, so the count must too.
                if (!statistics.TryGetSum(out double floating) || !statistics.TryGetNanCount(out ulong nanCount) || nanCount > long.MaxValue)
                {
                    return false;
                }

                sum = floating;
                nans = (long)nanCount;
                break;
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

        long count = view.Rows - nulls - nans;
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
