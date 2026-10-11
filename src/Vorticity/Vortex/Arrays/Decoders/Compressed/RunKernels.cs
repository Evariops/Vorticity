using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// The expansion of runs into rows, which is all <c>vortex.runend</c> does: each run's value stored
/// over its rows a vector at a time, the last vector written past the run's end into rows the runs
/// after it write again.
/// </summary>
/// <remarks>
/// <para>
/// The writer takes run-end from four rows a run on average, so what a run costs is its entry, not
/// its stores: a load of its end, a compare, a broadcast of its value, and a branch on its length.
/// With 256-bit vectors a run therefore stores eight rows whatever its length, one vector of
/// values of four bytes or less, two of eight bytes, four of sixteen, and only a longer run loops,
/// four vectors a step. Measured on a Ryzen 9 7950X over a window of 65 536 rows, runs of 4 to 12
/// rows of eight bytes took 5.6 µs this way against 9.1 for a vector a step, and sixteen-byte
/// values 10.8 against 12.5 for a fill a run.
/// </para>
/// <para>
/// Vectors of 512 bits are not used even where they are accelerated: a Zen 4 core issues one as two
/// of 256, an unaligned one always spans two cache lines, and the JIT builds a sixteen-byte value's
/// broadcast by merging into the register the run before used. Two of them a run took 8.8 µs where
/// two of 256 took 5.6. At 128 bits a step is four stores, the step the loop was measured at on an
/// Apple M4 Pro, where 256-bit vectors are not accelerated.
/// </para>
/// <para>
/// The vector loop runs while a run ends far enough from the last row that its stores stay inside
/// the rows, which it tests on the end it loaded: a store never leaves the destination whatever the
/// ends say, so an end a concurrent rewrite of a mapped file changed costs a wrong row, never memory.
/// The runs past that point, the window's last ones, go through the loop that clamps, which the
/// vector loop agrees with row for row on any ends at all.
/// </para>
/// </remarks>
internal static class RunKernels
{
    /// <summary>
    /// Writes each row of <paramref name="destination"/> as the value of the run it falls in, from
    /// run <paramref name="firstRun"/>, a run ending at its end less <paramref name="offset"/>.
    /// </summary>
    /// <typeparam name="TEnd">The ends' physical type.</typeparam>
    /// <typeparam name="TValue">A value's unit: 1, 2, 4, 8 or 16 bytes, and any other size a fill a run.</typeparam>
    /// <param name="ends">The run ends.</param>
    /// <param name="values">One value a run, at least as many as <paramref name="ends"/>.</param>
    /// <param name="firstRun">The run the first row falls in.</param>
    /// <param name="offset">Subtracted from every end, wrapping, as <c>vortex.runend</c> defines it.</param>
    /// <param name="destination">The rows.</param>
    /// <returns>
    /// The row reached: the rows' count, unless the ends stop short of it. A run whose end, less the
    /// offset, is past the rows ends at the last row, and one that does not end past the run before
    /// it writes nothing.
    /// </returns>
    /// <exception cref="ArgumentException">There are fewer values than ends.</exception>
    internal static int Expand<TEnd, TValue>(
        ReadOnlySpan<TEnd> ends, ReadOnlySpan<TValue> values, int firstRun, ulong offset, Span<TValue> destination)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        if (values.Length < ends.Length)
        {
            ThrowValues();
        }

        int run = firstRun;
        int position = 0;
        if (run >= 0 && Unsafe.SizeOf<TValue>() is 1 or 2 or 4 or 8 or 16)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                position = Runs256(ends, values, ref run, offset, destination);
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                position = Runs128(ends, values, ref run, offset, destination);
            }
        }

        return Clamped(ends, values, run, offset, destination, position);
    }

    /// <summary>
    /// Sets the validity of every row whose run's value is valid, over the rows
    /// <see cref="Expand"/> wrote from the same runs: the runs walked as it walks them.
    /// </summary>
    /// <typeparam name="TEnd">The ends' physical type.</typeparam>
    /// <param name="ends">The run ends.</param>
    /// <param name="firstRun">The run the first row falls in.</param>
    /// <param name="offset">Subtracted from every end, wrapping.</param>
    /// <param name="length">The rows.</param>
    /// <param name="valid">The run values' validity.</param>
    /// <param name="bits">The rows' validity, all clear, a bit a row from bit 0.</param>
    internal static void SetValidRuns<TEnd>(
        ReadOnlySpan<TEnd> ends, int firstRun, ulong offset, int length, in ValidityReader valid, Span<byte> bits)
        where TEnd : unmanaged
    {
        ulong rows = (ulong)(uint)length;
        int position = 0;
        for (int run = firstRun; run < ends.Length && position < length; run++)
        {
            ulong end = Widen(ends[run]) - offset;
            if (end > rows)
            {
                end = rows;
            }

            int endRow = (int)end;
            if (endRow <= position)
            {
                continue;
            }

            if (valid.IsValid(run))
            {
                BitmapKernels.SetRange(bits, position, endRow - position);
            }

            position = endRow;
        }
    }

    /// <summary>The runs at 256 bits, from <paramref name="run"/>, while their stores stay inside the rows; returns the row reached.</summary>
    private static int Runs256<TEnd, TValue>(
        ReadOnlySpan<TEnd> ends, ReadOnlySpan<TValue> values, ref int run, ulong offset, Span<TValue> destination)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        nuint size = (nuint)Unsafe.SizeOf<TValue>();
        nuint lanes = (nuint)Vector256<byte>.Count / size;

        // The rows every run stores whatever its length: eight, or a vector of the narrow values.
        nuint first = lanes >= 8 ? lanes : 8;
        if ((nuint)destination.Length < first)
        {
            return 0;
        }

        // A run whose end is at most this row stores its first vectors inside the rows, and every
        // vector its loop stores starts before its end, which is as far from the last row.
        ulong limit = (ulong)destination.Length - first;
        ref TEnd endsAt = ref MemoryMarshal.GetReference(ends);
        ref TValue valuesAt = ref MemoryMarshal.GetReference(values);
        ref byte into = ref Unsafe.As<TValue, byte>(ref MemoryMarshal.GetReference(destination));
        int count = ends.Length;
        int at = run;
        nuint position = 0;
        for (; at < count; at++)
        {
            ulong end = Widen(Unsafe.Add(ref endsAt, at)) - offset;
            if (end > limit)
            {
                break;
            }

            if (end <= position)
            {
                continue;
            }

            Vector256<byte> repeated = Repeat256(ref Unsafe.Add(ref valuesAt, at));
            nuint stop = (nuint)end;

            // One, two or four vectors, the count a constant of the instantiation.
            repeated.StoreUnsafe(ref into, position * size);
            if (first > lanes)
            {
                repeated.StoreUnsafe(ref into, (position + lanes) * size);
            }

            if (first > 2 * lanes)
            {
                repeated.StoreUnsafe(ref into, (position + (2 * lanes)) * size);
                repeated.StoreUnsafe(ref into, (position + (3 * lanes)) * size);
            }

            nuint row = position + first;
            if (row < stop)
            {
                // A longer run's loop starts on a whole vector of rows, before the run's first rows
                // end: the rows it writes twice are this run's, and over the rows a pooled block
                // aligns, no store of the loop spans two cache lines.
                row &= ~(lanes - 1);
                for (; row + (4 * lanes) <= stop; row += 4 * lanes)
                {
                    repeated.StoreUnsafe(ref into, row * size);
                    repeated.StoreUnsafe(ref into, (row + lanes) * size);
                    repeated.StoreUnsafe(ref into, (row + (2 * lanes)) * size);
                    repeated.StoreUnsafe(ref into, (row + (3 * lanes)) * size);
                }

                for (; row < stop; row += lanes)
                {
                    repeated.StoreUnsafe(ref into, row * size);
                }
            }

            position = stop;
        }

        run = at;
        return (int)position;
    }

    /// <summary>
    /// The runs at 128 bits, four stores a step: the loop as an Apple M4 Pro measured it, where one
    /// window of 2 048 runs of 64 <c>i32</c> rows took 5.1 µs against 9.3 for a fill a run.
    /// </summary>
    private static int Runs128<TEnd, TValue>(
        ReadOnlySpan<TEnd> ends, ReadOnlySpan<TValue> values, ref int run, ulong offset, Span<TValue> destination)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        nuint size = (nuint)Unsafe.SizeOf<TValue>();
        nuint lanes = (nuint)Vector128<byte>.Count / size;
        if ((nuint)destination.Length < 4 * lanes)
        {
            return 0;
        }

        ulong limit = (ulong)destination.Length - (4 * lanes);
        ref TEnd endsAt = ref MemoryMarshal.GetReference(ends);
        ref TValue valuesAt = ref MemoryMarshal.GetReference(values);
        ref byte into = ref Unsafe.As<TValue, byte>(ref MemoryMarshal.GetReference(destination));
        int count = ends.Length;
        int at = run;
        nuint position = 0;
        for (; at < count; at++)
        {
            ulong end = Widen(Unsafe.Add(ref endsAt, at)) - offset;
            if (end > limit)
            {
                break;
            }

            if (end <= position)
            {
                continue;
            }

            // The step walks a reference to a bound rather than an index from the run's start: the
            // loop then holds the reference, the bound and the value, and Native AOT does not spill
            // the row it reached to the stack and reload it every step.
            Vector128<byte> repeated = Repeat128(ref Unsafe.Add(ref valuesAt, at));
            ref byte step = ref Unsafe.Add(ref into, position * size);
            ref byte stop = ref Unsafe.Add(ref into, (nuint)end * size);
            do
            {
                repeated.StoreUnsafe(ref step);
                repeated.StoreUnsafe(ref step, 16);
                repeated.StoreUnsafe(ref step, 32);
                repeated.StoreUnsafe(ref step, 48);
                step = ref Unsafe.Add(ref step, 64);
            }
            while (Unsafe.IsAddressLessThan(ref step, ref stop));

            position = (nuint)end;
        }

        run = at;
        return (int)position;
    }

    /// <summary>
    /// The runs from <paramref name="run"/>, a fill a run, each run's end clamped to the rows: the
    /// definition the vector loops agree with, and the window's last runs.
    /// </summary>
    private static int Clamped<TEnd, TValue>(
        ReadOnlySpan<TEnd> ends, ReadOnlySpan<TValue> values, int run, ulong offset, Span<TValue> destination, int position)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        int length = destination.Length;
        ulong rows = (ulong)(uint)length;
        for (; run < ends.Length && position < length; run++)
        {
            ulong end = Widen(ends[run]) - offset;
            if (end > rows)
            {
                end = rows;
            }

            int endRow = (int)end;
            if (endRow <= position)
            {
                continue;
            }

            destination[position..endRow].Fill(values[run]);
            position = endRow;
        }

        return position;
    }

    /// <summary>A value repeated over 256 bits: a sixteen-byte one is inserted beside itself, from the register it was loaded in.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Repeat256<TValue>(ref TValue value)
        where TValue : unmanaged
    {
        if (Unsafe.SizeOf<TValue>() == 1)
        {
            return Vector256.Create(Unsafe.As<TValue, byte>(ref value));
        }

        if (Unsafe.SizeOf<TValue>() == 2)
        {
            return Vector256.Create(Unsafe.As<TValue, ushort>(ref value)).AsByte();
        }

        if (Unsafe.SizeOf<TValue>() == 4)
        {
            return Vector256.Create(Unsafe.As<TValue, uint>(ref value)).AsByte();
        }

        if (Unsafe.SizeOf<TValue>() == 8)
        {
            return Vector256.Create(Unsafe.As<TValue, ulong>(ref value)).AsByte();
        }

        return Vector256.Create(Vector128.LoadUnsafe(ref Unsafe.As<TValue, byte>(ref value)));
    }

    /// <summary>A value repeated over 128 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Repeat128<TValue>(ref TValue value)
        where TValue : unmanaged
    {
        if (Unsafe.SizeOf<TValue>() == 1)
        {
            return Vector128.Create(Unsafe.As<TValue, byte>(ref value));
        }

        if (Unsafe.SizeOf<TValue>() == 2)
        {
            return Vector128.Create(Unsafe.As<TValue, ushort>(ref value)).AsByte();
        }

        if (Unsafe.SizeOf<TValue>() == 4)
        {
            return Vector128.Create(Unsafe.As<TValue, uint>(ref value)).AsByte();
        }

        if (Unsafe.SizeOf<TValue>() == 8)
        {
            return Vector128.Create(Unsafe.As<TValue, ulong>(ref value)).AsByte();
        }

        return Vector128.LoadUnsafe(ref Unsafe.As<TValue, byte>(ref value));
    }

    /// <summary>
    /// Widens one run end to <see cref="ulong"/>, folded at instantiation: every branch but one is a
    /// constant-false compare of two <c>typeof</c>s, which the JIT removes when it specializes. A
    /// signed end widens through <c>long</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Widen<TEnd>(TEnd end)
        where TEnd : unmanaged
    {
        if (typeof(TEnd) == typeof(byte))
        {
            return Unsafe.As<TEnd, byte>(ref end);
        }

        if (typeof(TEnd) == typeof(ushort))
        {
            return Unsafe.As<TEnd, ushort>(ref end);
        }

        if (typeof(TEnd) == typeof(uint))
        {
            return Unsafe.As<TEnd, uint>(ref end);
        }

        if (typeof(TEnd) == typeof(ulong))
        {
            return Unsafe.As<TEnd, ulong>(ref end);
        }

        if (typeof(TEnd) == typeof(sbyte))
        {
            return (ulong)(long)Unsafe.As<TEnd, sbyte>(ref end);
        }

        if (typeof(TEnd) == typeof(short))
        {
            return (ulong)(long)Unsafe.As<TEnd, short>(ref end);
        }

        if (typeof(TEnd) == typeof(int))
        {
            return (ulong)(long)Unsafe.As<TEnd, int>(ref end);
        }

        return (ulong)Unsafe.As<TEnd, long>(ref end);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowValues() =>
        throw new ArgumentException("A run needs a value: there are fewer values than ends.");
}
