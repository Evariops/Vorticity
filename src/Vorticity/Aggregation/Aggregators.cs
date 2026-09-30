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
        Func<AggregateSlot<T>> create = shape.Kind switch
        {
            StorageKind.Primitive => shape.PType switch
            {
                PType.I8 => static () => new FixedSlot<sbyte, SumState<Int128>, SignedSum<sbyte>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.I16 => static () => new FixedSlot<short, SumState<Int128>, SignedSum<short>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.I32 => static () => new FixedSlot<int, SumState<Int128>, SignedSum<int>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.I64 => static () => new FixedSlot<long, SumState<Int128>, SignedSum<long>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.U8 => static () => new FixedSlot<byte, SumState<UInt128>, UnsignedSum<byte>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.U16 => static () => new FixedSlot<ushort, SumState<UInt128>, UnsignedSum<ushort>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.U32 => static () => new FixedSlot<uint, SumState<UInt128>, UnsignedSum<uint>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.U64 => static () => new FixedSlot<ulong, SumState<UInt128>, UnsignedSum<ulong>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.F16 => static () => new FixedSlot<Half, SumState<double>, FloatSum<Half>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                PType.F32 => static () => new FixedSlot<float, SumState<double>, FloatSum<float>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
                _ => static () => new FixedSlot<double, SumState<double>, FloatSum<double>, T>(StorageKind.Primitive, static s => T.CreateChecked(s.Sum)),
            },
            StorageKind.Decimal or StorageKind.Decimal256 => DecimalSumSlot<T>(shape),
            _ => throw shape.Unsupported("a sum"),
        };

        return new Sym<T>(new AggregateNode<T>(AggregateKind.Sum, shape, create, (StatisticsView view, out T value) => SettleSum(shape, view, out value)));
    }

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

    internal static Sym<double?> Avg(ColumnShape shape)
    {
        Func<AggregateSlot<double?>> create;
        switch (shape.Kind)
        {
            case StorageKind.Primitive:
                create = shape.PType switch
                {
                    PType.I8 => static () => new FixedSlot<sbyte, SumState<Int128>, SignedSum<sbyte>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.I16 => static () => new FixedSlot<short, SumState<Int128>, SignedSum<short>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.I32 => static () => new FixedSlot<int, SumState<Int128>, SignedSum<int>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.I64 => static () => new FixedSlot<long, SumState<Int128>, SignedSum<long>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U8 => static () => new FixedSlot<byte, SumState<UInt128>, UnsignedSum<byte>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U16 => static () => new FixedSlot<ushort, SumState<UInt128>, UnsignedSum<ushort>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U32 => static () => new FixedSlot<uint, SumState<UInt128>, UnsignedSum<uint>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.U64 => static () => new FixedSlot<ulong, SumState<UInt128>, UnsignedSum<ulong>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.F16 => static () => new FixedSlot<Half, SumState<double>, FloatSum<Half>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    PType.F32 => static () => new FixedSlot<float, SumState<double>, FloatSum<float>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                    _ => static () => new FixedSlot<double, SumState<double>, FloatSum<double>, double?>(StorageKind.Primitive, static s => Mean(s.Sum, s.Count)),
                };
                break;
            case StorageKind.Decimal when shape.Type.Precision <= 18:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = () => new FixedSlot<Int128, SumState<Int128>, DecimalSum, double?>(StorageKind.Decimal, s => s.Count == 0 ? null : (double)s.Sum / unit / s.Count);
                break;
            }

            case StorageKind.Decimal:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = () => new FixedSlot<Int128, SumState<WideSum>, WideDecimalSum, double?>(StorageKind.Decimal, s => s.Count == 0 ? null : s.Sum.ToDouble() / unit / s.Count);
                break;
            }

            case StorageKind.Decimal256:
            {
                double unit = Math.Pow(10, shape.Type.Scale);
                create = () => new FixedSlot<Int256, SumState<WideSum>, Decimal256Sum, double?>(StorageKind.Decimal256, s => s.Count == 0 ? null : s.Sum.ToDouble() / unit / s.Count);
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
            StorageKind.Decimal256 => OrderedExtreme<Int256, T>(shape, max),
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
                $"'{shape.Path}' is {shape.Type}; an aggregator over {ClrFit.Name(typeof(T))} reads a column stored as {ClrFit.Name(typeof(T))}.");
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
