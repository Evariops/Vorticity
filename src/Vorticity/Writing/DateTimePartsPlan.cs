using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// A timestamp chunk's instants split into the days since the epoch, the seconds within the day
/// and the units within the second: <c>vortex.datetimeparts</c>, whose parts are each far narrower
/// than the instant when its resolution is coarser than its unit -- instants to the second in
/// microseconds, dates at midnight -- and then take the integer schemes of their own.
/// </summary>
/// <remarks>
/// The split is the reference's truncating one -- <c>days = t / (86 400 u)</c>,
/// <c>seconds = t / u - 86 400 days</c>, <c>subseconds = t - u (t / u)</c> -- which a reader
/// inverts exactly whatever the sign. A null row splits as the instant zero, so its seconds and
/// subseconds, which have no validity of their own, stay within the valid rows' range.
/// <para>
/// Priced by the width each part bit-packs to: an upper bound on what the parts cost once each
/// takes the scheme it prefers, held a tenth under the best plan the instants have, since the
/// recomposition is one multiply-accumulate a row more to decode.
/// </para>
/// </remarks>
internal sealed class DateTimePartsPlan
{
    private const long SecondsPerDay = 86_400;

    /// <summary>A part's node in the tree besides its buffers, in bytes.</summary>
    private const long PartFraming = 48;

    private int[]? _days;
    private int[]? _seconds;
    private int[]? _subseconds;

    private DateTimePartsPlan(
        int[] days, int[] seconds, int[] subseconds, int rows, PType daysPType, PType secondsPType, PType subsecondsPType,
        long encodedSize, Ranges ranges)
    {
        _days = days;
        _seconds = seconds;
        _subseconds = subseconds;
        Rows = rows;
        DaysPType = daysPType;
        SecondsPType = secondsPType;
        SubsecondsPType = subsecondsPType;
        EncodedSize = encodedSize;
        _ranges = ranges;
    }

    private readonly Ranges _ranges;

    /// <summary>The day every valid row falls on, when they all fall on one.</summary>
    internal long? OneDay => _ranges.DaysMin == _ranges.DaysMax ? _ranges.DaysMin : null;

    /// <summary>The second within its day every row holds, when they all hold one.</summary>
    internal long? OneSecond => _ranges.SecondsMin == _ranges.SecondsMax ? _ranges.SecondsMin : null;

    /// <summary>The units within its second every row holds, when they all hold one: zero for instants to the second.</summary>
    internal long? OneSubsecond => _ranges.SubsecondsMin == _ranges.SubsecondsMax ? _ranges.SubsecondsMin : null;

    /// <summary>The chunk's rows.</summary>
    internal int Rows { get; }

    /// <summary>The narrowest signed type holding every day, a null row's zero included.</summary>
    internal PType DaysPType { get; }

    /// <summary>The narrowest signed type holding every second within its day.</summary>
    internal PType SecondsPType { get; }

    /// <summary>The narrowest signed type holding every unit within its second.</summary>
    internal PType SubsecondsPType { get; }

    /// <summary>The bytes the three parts bit-pack to, framing included.</summary>
    internal long EncodedSize { get; }

    /// <summary>Each row's day.</summary>
    internal ReadOnlySpan<int> Days => _days.AsSpan(0, Rows);

    /// <summary>Each row's second within its day.</summary>
    internal ReadOnlySpan<int> Seconds => _seconds.AsSpan(0, Rows);

    /// <summary>Each row's units within its second.</summary>
    internal ReadOnlySpan<int> Subseconds => _subseconds.AsSpan(0, Rows);

    /// <summary>
    /// The split of an <c>i64</c> chunk of instants in units of <c>1 / unitsPerSecond</c> seconds,
    /// when its parts bit-pack a tenth under <paramref name="ceiling"/>; null otherwise, or when a
    /// day falls outside 32 bits.
    /// </summary>
    internal static DateTimePartsPlan? TryBuild(CanonicalArena arena, in CanonicalNode node, long unitsPerSecond, long ceiling)
    {
        if (node.Kind != CanonicalKind.Primitive || node.PType != PType.I64 || node.Length == 0)
        {
            return null;
        }

        int rows = node.Length;
        ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(node.Values.Span)[..rows];
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (Hopeless(values, in mask, unitsPerSecond, ceiling))
        {
            return null;
        }

        int[] days = ArrayPool<int>.Shared.Rent(rows);
        int[] seconds = ArrayPool<int>.Shared.Rent(rows);
        int[] subseconds = ArrayPool<int>.Shared.Rent(rows);
        Ranges ranges = default;
        bool split = unitsPerSecond switch
        {
            1 => Split<InSeconds>(values, in mask, days, seconds, subseconds, ref ranges),
            1_000 => Split<InMilliseconds>(values, in mask, days, seconds, subseconds, ref ranges),
            1_000_000 => Split<InMicroseconds>(values, in mask, days, seconds, subseconds, ref ranges),
            1_000_000_000 => Split<InNanoseconds>(values, in mask, days, seconds, subseconds, ref ranges),
            _ => false,
        };

        long cost = split
            ? Cost(ranges.DaysMin, ranges.DaysMax, rows) + Cost(ranges.SecondsMin, ranges.SecondsMax, rows)
                + Cost(ranges.SubsecondsMin, ranges.SubsecondsMax, rows)
            : long.MaxValue;
        if (cost > ceiling - (ceiling / 10))
        {
            ArrayPool<int>.Shared.Return(days);
            ArrayPool<int>.Shared.Return(seconds);
            ArrayPool<int>.Shared.Return(subseconds);
            return null;
        }

        // A null row's day is the zero it split as, which the type has to hold too.
        bool nulls = !mask.AllValid;
        return new DateTimePartsPlan(
            days, seconds, subseconds, rows,
            Narrowest(nulls ? Math.Min(ranges.DaysMin, 0) : ranges.DaysMin, nulls ? Math.Max(ranges.DaysMax, 0) : ranges.DaysMax),
            Narrowest(ranges.SecondsMin, ranges.SecondsMax),
            Narrowest(ranges.SubsecondsMin, ranges.SubsecondsMax),
            cost,
            ranges);
    }

    /// <summary>
    /// Whether rows spread evenly over the chunk already split into parts too wide to come in under
    /// <paramref name="ceiling"/>: a sample's parts span no more than the chunk's, so its cost is a
    /// bound from below, and a chunk of full-resolution instants is refused on 256 rows rather than
    /// on the pass over all of them.
    /// </summary>
    private static bool Hopeless(ReadOnlySpan<long> values, in ValidityMask mask, long unitsPerSecond, long ceiling)
    {
        const int Sample = 256;
        int rows = values.Length;
        int step = Math.Max(1, rows / Sample);
        long daysMin = long.MaxValue;
        long daysMax = long.MinValue;
        long secondsMin = long.MaxValue;
        long secondsMax = long.MinValue;
        long subMin = long.MaxValue;
        long subMax = long.MinValue;
        bool all = mask.AllValid;
        for (int i = 0; i < rows; i += step)
        {
            if (!all && !mask.IsValid(i))
            {
                continue;
            }

            long t = values[i];
            long total = t / unitsPerSecond;
            long day = total / SecondsPerDay;
            long second = total - (day * SecondsPerDay);
            long sub = t - (total * unitsPerSecond);
            daysMin = Math.Min(daysMin, day);
            daysMax = Math.Max(daysMax, day);
            secondsMin = Math.Min(secondsMin, second);
            secondsMax = Math.Max(secondsMax, second);
            subMin = Math.Min(subMin, sub);
            subMax = Math.Max(subMax, sub);
        }

        long lower = Cost(daysMin, daysMax, rows) + Cost(secondsMin, secondsMax, rows) + Cost(subMin, subMax, rows);
        return lower > ceiling - (ceiling / 10);
    }

    /// <summary>Hands the parts back to the pool, once; the plan is spent.</summary>
    internal void Release()
    {
        if (_days is not null)
        {
            ArrayPool<int>.Shared.Return(_days);
            ArrayPool<int>.Shared.Return(_seconds!);
            ArrayPool<int>.Shared.Return(_subseconds!);
            _days = null;
            _seconds = null;
            _subseconds = null;
        }
    }

    /// <summary>A part's bytes bit-packed over its range, in blocks of 1 024; one value is a constant.</summary>
    private static long Cost(long min, long max, int rows)
    {
        if (max <= min)
        {
            return PartFraming;
        }

        int width = 64 - BitOperations.LeadingZeroCount((ulong)(max - min));
        return ((rows + 1023L) / 1024 * 128 * width) + PartFraming;
    }

    private static PType Narrowest(long min, long max) =>
        min > max ? PType.I8
        : min >= sbyte.MinValue && max <= sbyte.MaxValue ? PType.I8
        : min >= short.MinValue && max <= short.MaxValue ? PType.I16
        : PType.I32;

    /// <summary>
    /// Splits every row, the unit a compile-time constant so that each division is a multiplication,
    /// then bounds each part a register at a time: the days over the valid rows, the seconds and
    /// subseconds over all. The divisions have no vector form, so the loop that holds them holds
    /// nothing else.
    /// </summary>
    private static bool Split<TUnit>(
        ReadOnlySpan<long> values, in ValidityMask mask, Span<int> days, Span<int> seconds, Span<int> subseconds,
        ref Ranges ranges)
        where TUnit : struct, IUnit
    {
        int rows = values.Length;
        days = days[..rows];
        seconds = seconds[..rows];
        subseconds = subseconds[..rows];
        ref int day0 = ref MemoryMarshal.GetReference(days);
        ref int second0 = ref MemoryMarshal.GetReference(seconds);
        ref int sub0 = ref MemoryMarshal.GetReference(subseconds);
        long perDay = SecondsPerDay * TUnit.PerSecond;
        bool all = mask.AllValid;

        // A day past 32 bits -- instants in seconds or milliseconds beyond five million years --
        // has no part to go in; tested on the way rather than bounded afterwards, since the
        // truncation to 32 bits would hide it.
        long wide = 0;
        for (int i = 0; i < rows; i++)
        {
            long t = all || mask.IsValid(i) ? values[i] : 0;
            long day = t / perDay;
            long total = t / TUnit.PerSecond;
            Unsafe.Add(ref day0, i) = (int)day;
            Unsafe.Add(ref second0, i) = (int)(total - (day * SecondsPerDay));
            Unsafe.Add(ref sub0, i) = (int)(t - (total * TUnit.PerSecond));
            wide |= day - (int)day;
        }

        if (wide != 0)
        {
            return false;
        }

        long daysMin = 0;
        long daysMax = -1;
        if (all)
        {
            BlockStatsPass.Bounds<int>(days, out int least, out int most);
            (daysMin, daysMax) = (least, most);
        }
        else if (!mask.AllInvalid && BlockStatsPass.MaskedBounds<int>(days, in mask, 0, out int least, out int most))
        {
            (daysMin, daysMax) = (least, most);
        }

        BlockStatsPass.Bounds<int>(seconds, out int secondsMin, out int secondsMax);
        BlockStatsPass.Bounds<int>(subseconds, out int subMin, out int subMax);
        ranges = new Ranges(daysMin, daysMax, secondsMin, secondsMax, subMin, subMax);
        return true;
    }

    private readonly record struct Ranges(
        long DaysMin, long DaysMax, long SecondsMin, long SecondsMax, long SubsecondsMin, long SubsecondsMax);

    /// <summary>A timestamp unit, as the number of it in a second.</summary>
    private interface IUnit
    {
        static abstract long PerSecond { get; }
    }

    private readonly struct InSeconds : IUnit
    {
        public static long PerSecond => 1;
    }

    private readonly struct InMilliseconds : IUnit
    {
        public static long PerSecond => 1_000;
    }

    private readonly struct InMicroseconds : IUnit
    {
        public static long PerSecond => 1_000_000;
    }

    private readonly struct InNanoseconds : IUnit
    {
        public static long PerSecond => 1_000_000_000;
    }
}
