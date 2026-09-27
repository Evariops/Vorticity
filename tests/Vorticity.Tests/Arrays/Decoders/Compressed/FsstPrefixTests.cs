using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using Vorticity.Arrays.Decoders.Compressed;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

/// <summary>
/// A prefix matched on a row's FSST codes against <see cref="MemoryExtensions.StartsWith{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>
/// on the row's bytes: a table whose symbols overlap and nest, up to the full eight bytes, and
/// which leaves some bytes to the escape, over random rows of that alphabet and prefixes cut from
/// rows at every length, past their end, and with their last byte changed.
/// </summary>
public sealed class FsstPrefixTests
{
    // 'q' and a lone 'x' have no symbol, so they escape.
    private static readonly string[] Symbols = ["ab", "a", "b", "c", "d", "e", "abc", "abcdefgh", "bcd", "dea", "xyz", "cab", "ee", "eeeeeeee", "y", "z"];

    private const string Alphabet = "abcdeqxyz";

    [Fact]
    public void APrefixMatchesOnCodesAsOnBytes()
    {
        byte[] symbols = new byte[Symbols.Length * FsstSymbolTable.SymbolSize];
        byte[] lengths = new byte[Symbols.Length];

        // A symbol's word past its width is padding the format lets hold anything.
        symbols.AsSpan().Fill(0xEE);
        for (int code = 0; code < Symbols.Length; code++)
        {
            System.Text.Encoding.ASCII.GetBytes(Symbols[code]).CopyTo(symbols, code * FsstSymbolTable.SymbolSize);
            lengths[code] = (byte)Symbols[code].Length;
        }

        FsstSymbolTable table = FsstSymbolTable.Create(symbols, lengths, "test");
        Random random = new Random(20260927);
        List<byte[]> rows = [];
        List<byte[]> codes = [];
        for (int n = 0; n < 400; n++)
        {
            byte[] row = new byte[random.Next(0, 61)];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = (byte)Alphabet[random.Next(Alphabet.Length)];
            }

            // Rows that open alike, so that many share a prefix's codes.
            if (n % 3 == 0 && rows.Count > 0)
            {
                byte[] earlier = rows[random.Next(rows.Count)];
                earlier.AsSpan(0, Math.Min(earlier.Length, row.Length)).CopyTo(row);
            }

            byte[] compressed = new byte[FsstSymbolTable.MaxCompressedLength(row.Length) + 1];
            Assert.True(table.TryCompress(row, compressed, out int written));
            rows.Add(row);
            codes.Add(compressed[..written]);
        }

        int shared = 0;
        int longest = 0;
        foreach (byte[] source in rows.GetRange(0, 60))
        {
            for (int cut = 0; cut <= source.Length + 1; cut++)
            {
                byte[] prefix = cut <= source.Length ? source[..cut] : [.. source, (byte)'a'];
                Check(table, prefix, rows, codes, ref shared, ref longest);
                if (prefix.Length > 0)
                {
                    prefix[^1] = (byte)Alphabet[(Alphabet.IndexOf((char)prefix[^1], StringComparison.Ordinal) + 1) % Alphabet.Length];
                    Check(table, prefix, rows, codes, ref shared, ref longest);
                }
            }
        }

        // The shared codes were taken, and past sixteen bytes of them, where the middle is compared on its own.
        Assert.True(shared > 1_000, $"only {shared} codes shared");
        Assert.True(longest > 16, $"at most {longest} codes shared");
    }

    private static void Check(FsstSymbolTable table, byte[] prefix, List<byte[]> rows, List<byte[]> codes, ref int shared, ref int longest)
    {
        byte[] scratch = new byte[FsstSymbolTable.MaxCompressedLength(prefix.Length) + 1];
        Assert.True(FsstPrefix.TryCreate(table, prefix, scratch, out FsstPrefix matcher));
        shared += matcher.Shared.Length;
        longest = Math.Max(longest, matcher.Shared.Length);
        for (int n = 0; n < rows.Count; n++)
        {
            bool expected = rows[n].AsSpan().StartsWith(prefix);
            Assert.True(
                expected == matcher.Match(codes[n]),
                $"'{System.Text.Encoding.ASCII.GetString(rows[n])}' against '{System.Text.Encoding.ASCII.GetString(prefix)}': expected {expected}");
        }

        // Codes that differ from the shared ones in any one byte do not open with them.
        byte[] opens = matcher.Shared.ToArray();
        int opening = codes.FindIndex(c => c.AsSpan().StartsWith(opens));
        if (opening >= 0)
        {
            for (int k = 0; k < matcher.Shared.Length; k++)
            {
                byte[] changed = [.. codes[opening]];
                changed[k] ^= 0x01;
                Assert.False(matcher.Match(changed), $"codes changed at byte {k} of {matcher.Shared.Length} still open with the shared ones");
            }
        }
    }
}
