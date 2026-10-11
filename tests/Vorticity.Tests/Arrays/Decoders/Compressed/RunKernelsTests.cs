using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Compressed;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// The run expansion against its definition, a fill a run with each end clamped to the rows: every
/// value width and end type, runs short and long, offsets and first runs inside the ends, row counts
/// around every vector width, and ends that do not ascend, with the bytes past the rows watched.
/// </summary>
public sealed class RunKernelsTests
{
    private const int Guard = 256;

    [Theory]
    [InlineData(1, 4)]
    [InlineData(1, 40)]
    [InlineData(4, 12)]
    [InlineData(16, 64)]
    [InlineData(200, 1_000)]
    public void ExpandsAsAFillARun(int shortest, int longest)
    {
        Random random = new Random(20261011 + shortest);
        for (int round = 0; round < 40; round++)
        {
            int rows = round < 24 ? round * 7 : random.Next(0, 5_000);
            Check<byte, byte>(random, rows, shortest, longest);
            Check<ushort, ushort>(random, rows, shortest, longest);
            Check<uint, uint>(random, rows, shortest, longest);
            Check<ushort, ulong>(random, rows, shortest, longest);
            Check<uint, ulong>(random, rows, shortest, longest);
            Check<ulong, ulong>(random, rows, shortest, longest);
            Check<uint, Vector128<byte>>(random, rows, shortest, longest);
            Check<ushort, Vector128<byte>>(random, rows, shortest, longest);
            Check<int, uint>(random, rows, shortest, longest);
            Check<long, byte>(random, rows, shortest, longest);
        }
    }

    [Fact]
    public void EndsThatDoNotAscendStayInsideTheRows()
    {
        Random random = new Random(7);
        for (int round = 0; round < 200; round++)
        {
            int rows = random.Next(0, 600);
            CheckArbitrary<uint, ulong>(random, rows);
            CheckArbitrary<ushort, uint>(random, rows);
            CheckArbitrary<byte, Vector128<byte>>(random, rows);
            CheckArbitrary<ulong, byte>(random, rows);
        }
    }

    [Fact]
    public void ARunPastTheRowsEndsAtTheLast()
    {
        ulong[] ends = [3, 1_000];
        ulong[] values = [7, 9];
        ulong[] rows = new ulong[200];
        Assert.Equal(200, RunKernels.Expand<ulong, ulong>(ends, values, 0, 0, rows));
        Assert.Equal([7UL, 7UL, 7UL], rows[..3]);
        Assert.All(rows[3..], value => Assert.Equal(9UL, value));
    }

    [Fact]
    public void EndsShortOfTheRowsSayWhereTheyStopped()
    {
        uint[] ends = [100, 150];
        uint[] values = [1, 2];
        uint[] rows = new uint[400];
        Assert.Equal(150, RunKernels.Expand<uint, uint>(ends, values, 0, 0, rows));
    }

    [Fact]
    public void FewerValuesThanEndsAreRefused()
    {
        uint[] ends = [1, 2];
        uint[] values = [1];
        uint[] rows = new uint[2];
        Assert.Throws<ArgumentException>(() => RunKernels.Expand<uint, uint>(ends, values, 0, 0, rows));
    }

    private static void Check<TEnd, TValue>(Random random, int rows, int shortest, int longest)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        // Ascending ends from row 0, and a window of the node from an offset inside them, whose
        // first run is the one the offset falls in, as a range of a node is read.
        ulong offset = random.Next(3) == 0 ? (ulong)random.Next(0, 300) : 0;
        ulong max = MaxOf<TEnd>();
        int runs = 0;
        ulong[] wide = new ulong[16];
        ulong at = 0;
        while (runs == 0 || wide[runs - 1] < offset + (ulong)rows)
        {
            at += (ulong)random.Next(shortest, longest + 1);
            if (at > max)
            {
                return;
            }

            if (runs == wide.Length)
            {
                Array.Resize(ref wide, runs * 2);
            }

            wide[runs++] = at;
        }

        TEnd[] ends = new TEnd[runs];
        for (int i = 0; i < runs; i++)
        {
            ends[i] = FromUlong<TEnd>(wide[i]);
        }

        int firstRun = 0;
        while (firstRun < runs && wide[firstRun] <= offset)
        {
            firstRun++;
        }

        Compare<TEnd, TValue>(random, ends, firstRun, offset, rows);
    }

    private static void CheckArbitrary<TEnd, TValue>(Random random, int rows)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        int runs = random.Next(0, 40);
        TEnd[] ends = new TEnd[runs];
        ulong max = Math.Min(MaxOf<TEnd>(), (ulong)rows * 2 + 10);
        for (int i = 0; i < runs; i++)
        {
            ends[i] = FromUlong<TEnd>((ulong)random.NextInt64(0, (long)max + 1));
        }

        ulong offset = random.Next(2) == 0 ? 0 : (ulong)random.Next(0, rows + 1);
        int firstRun = runs == 0 ? 0 : random.Next(0, runs);
        Compare<TEnd, TValue>(random, ends, firstRun, offset, rows);
    }

    private static void Compare<TEnd, TValue>(Random random, TEnd[] ends, int firstRun, ulong offset, int rows)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        TValue[] values = new TValue[ends.Length];
        random.NextBytes(MemoryMarshal.AsBytes(values.AsSpan()));

        // The rows sit between guards, which the expansion must leave as they are.
        TValue[] padded = new TValue[rows + (2 * Guard)];
        random.NextBytes(MemoryMarshal.AsBytes(padded.AsSpan()));
        TValue[] before = (TValue[])padded.Clone();
        TValue[] expected = new TValue[rows];
        Array.Copy(padded, Guard, expected, 0, rows);
        int reached = Reference(ends, values, firstRun, offset, expected);

        int got = RunKernels.Expand<TEnd, TValue>(ends, values, firstRun, offset, padded.AsSpan(Guard, rows));
        Assert.Equal(reached, got);
        Assert.True(
            MemoryMarshal.AsBytes(expected.AsSpan(0, reached)).SequenceEqual(MemoryMarshal.AsBytes(padded.AsSpan(Guard, reached))),
            $"{typeof(TEnd).Name} ends, {typeof(TValue).Name} values, {rows} rows: the expanded rows differ.");
        Assert.True(
            MemoryMarshal.AsBytes(before.AsSpan(0, Guard)).SequenceEqual(MemoryMarshal.AsBytes(padded.AsSpan(0, Guard))) &&
            MemoryMarshal.AsBytes(before.AsSpan(Guard + rows)).SequenceEqual(MemoryMarshal.AsBytes(padded.AsSpan(Guard + rows))),
            $"{typeof(TEnd).Name} ends, {typeof(TValue).Name} values, {rows} rows: a byte outside the rows changed.");
    }

    /// <summary>The definition: a fill a run, each end less the offset, clamped to the rows.</summary>
    private static int Reference<TEnd, TValue>(TEnd[] ends, TValue[] values, int firstRun, ulong offset, TValue[] rows)
        where TEnd : unmanaged
        where TValue : unmanaged
    {
        int position = 0;
        for (int run = firstRun; run < ends.Length && position < rows.Length; run++)
        {
            ulong end = RunKernels.Widen(ends[run]) - offset;
            if (end > (ulong)rows.Length)
            {
                end = (ulong)rows.Length;
            }

            for (int row = position; row < (int)end; row++)
            {
                rows[row] = values[run];
            }

            position = Math.Max(position, (int)end);
        }

        return position;
    }

    private static ulong MaxOf<TEnd>()
        where TEnd : unmanaged => Unsafe.SizeOf<TEnd>() switch
        {
            1 => typeof(TEnd) == typeof(sbyte) ? (ulong)sbyte.MaxValue : byte.MaxValue,
            2 => typeof(TEnd) == typeof(short) ? (ulong)short.MaxValue : ushort.MaxValue,
            4 => typeof(TEnd) == typeof(int) ? int.MaxValue : uint.MaxValue,
            _ => long.MaxValue,
        };

    private static TEnd FromUlong<TEnd>(ulong value)
        where TEnd : unmanaged
    {
        TEnd end = default;
        MemoryMarshal.AsBytes(new Span<TEnd>(ref end))[..Unsafe.SizeOf<TEnd>()].Clear();
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        bytes[..Unsafe.SizeOf<TEnd>()].CopyTo(MemoryMarshal.AsBytes(new Span<TEnd>(ref end)));
        return end;
    }
}
