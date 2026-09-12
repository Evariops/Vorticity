// Shared plumbing for the row-encoding tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.RowEncoding;

namespace Vorticity.Tests.RowEncoding;

internal static class RowTestHelp
{
    /// <summary>Encodes and materializes each row, so a test can assert on bytes that outlive the keys.</summary>
    internal static byte[][] Rows(RowFixture fixture, int[] columns, params RowSortField[] fields)
    {
        using RowKeys keys = RowEncoder.Encode(fixture.Arena, columns, fields);
        byte[][] rows = new byte[keys.RowCount][];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = keys.Row(i).ToArray();
        }

        return rows;
    }

    /// <summary>One column, one field.</summary>
    internal static byte[][] Rows(RowFixture fixture, int column, RowSortField field) =>
        Rows(fixture, [column], field);

    /// <summary>Parses a whitespace-separated hex string into bytes, so expectations read as bytes.</summary>
    internal static byte[] Hex(string text)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        byte[] bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>The indices of <paramref name="rows"/> in ascending byte order.</summary>
    internal static int[] ByteOrder(byte[][] rows)
    {
        int[] order = new int[rows.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Ties broken by index in BOTH orderings, so the two permutations a test compares are
        // canonical and an equal pair cannot make the comparison fail for a reason that is not a bug.
        Array.Sort(order, (a, b) =>
        {
            int c = rows[a].AsSpan().SequenceCompareTo(rows[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>The indices of <paramref name="keys"/> in ascending order of the comparer.</summary>
    internal static int[] ValueOrder<T>(IReadOnlyList<T> keys, Comparison<T> comparison)
    {
        int[] order = new int[keys.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            int c = comparison(keys[a], keys[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
