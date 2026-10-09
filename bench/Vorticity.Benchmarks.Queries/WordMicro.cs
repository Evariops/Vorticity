using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A short text key's groups found alone, in memory (<c>--micro words</c>): texts of 5, 8 and 12 bytes, a
/// hundred to a hundred thousand of them drawn at random, handed batch after batch to
/// <see cref="ShortTextKeys"/>.Assign in the canonical views a decoder makes of them, every view holding
/// its value whole. No file, no decode, no aggregate: what a change to the words does, in seconds.
/// </summary>
/// <remarks>
/// The shapes are db-benchmark's: <c>id1</c> and <c>id2</c>, a hundred texts of 5 bytes; <c>id3</c>, texts
/// of 12 bytes, a hundred thousand of them at 10⁷ rows. Run it with <c>DOTNET_TieredCompilation=0</c>, so
/// that every pass runs the code at its last tier.
/// </remarks>
internal static class WordMicro
{
    /// <summary>The rows of a batch: a chunk of the bench's db-benchmark file.</summary>
    private const int BatchRows = 65_536;

    private const int ViewSize = 16;

    internal static int Run(int rounds)
    {
        Console.WriteLine($"{"words",-28} {"rows",11} {"groups",10} {"best ms",10} {"ns/row",8}");
        Measure("5 bytes, 1e2 (id1)", 10_000_000, 5, 100, rounds);
        Measure("8 bytes, 1e4", 10_000_000, 8, 10_000, rounds);
        Measure("12 bytes, 1e2", 10_000_000, 12, 100, rounds);
        Measure("12 bytes, 1e5 (id3)", 10_000_000, 12, 100_000, rounds);
        return 0;
    }

    /// <summary>The best of <paramref name="rounds"/> passes over <paramref name="rows"/> rows of <paramref name="values"/> texts of <paramref name="length"/> bytes.</summary>
    private static void Measure(string name, int rows, int length, int values, int rounds)
    {
        byte[] views = Views(rows, length, values);
        double best = double.MaxValue;
        int groups = 0;
        for (int round = 0; round < rounds; round++)
        {
            (double ms, int count) = Pass(views, rows);
            best = Math.Min(best, ms);
            groups = count;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-28} {rows,11:N0} {groups,10:N0} {best,10:F1} {best * 1e6 / rows,8:F2}"));
    }

    /// <summary>
    /// Every row's view: its length, then its text, <c>id</c> and a number of <paramref name="length"/> − 2
    /// digits drawn among <paramref name="values"/>, zero past it, as a decoder writes an inline view.
    /// </summary>
    private static byte[] Views(int rows, int length, int values)
    {
        byte[] views = new byte[rows * ViewSize];
        string format = "D" + (length - 2).ToString(CultureInfo.InvariantCulture);
        for (int row = 0; row < rows; row++)
        {
            int value = (int)(Mix((ulong)row) % (ulong)values);
            Span<byte> view = views.AsSpan(row * ViewSize, ViewSize);
            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)length);
            Encoding.ASCII.GetBytes("id" + value.ToString(format, CultureInfo.InvariantCulture), view[4..]);
        }

        return views;
    }

    /// <summary>One pass: every batch's views a canonical node of an arena, assigned to groups.</summary>
    private static (double Ms, int Groups) Pass(byte[] views, int rows)
    {
        GC.Collect();
        RetainingArena arena = new RetainingArena();
        DType dtype = new DTypeArena().Utf8(Nullability.NonNullable);
        ColumnShape shape = new ColumnShape(new ColumnSym(Expr.Field("k"), VortexType.Utf8, null, null, -1, []));
        ShortTextKeys groups = new ShortTextKeys(shape, sorted: false);
        ulong[] selection = [];
        int[] rowGroups = new int[BatchRows];
        GroupRanges ranges = new GroupRanges();
        long start = Stopwatch.GetTimestamp();
        for (int at = 0; at < rows; at += BatchRows)
        {
            int count = Math.Min(BatchRows, rows - at);
            arena.ResetKeepingBlocks();
            VortexBuffer buffer = arena.Allocate(count * ViewSize, ViewSize, out Span<byte> bytes);
            views.AsSpan(at * ViewSize, count * ViewSize).CopyTo(bytes);
            int node = arena.AddVarBinView(dtype, count, Validity.NonNullable, buffer, []);
            ranges.Clear();
            groups.Assign(arena, [node], count, selection, rowGroups, ranges);
        }

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        arena.Reset();
        return (ms, groups.Count);
    }

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
