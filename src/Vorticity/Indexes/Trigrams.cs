// The byte trigrams of docs/10-indexes.md §5.2 and §6.4: what the two text indexes insert, and what a
// string predicate needs to find.
//
// A PREDICATE REQUIRES THE TRIGRAMS OF ITS LITERAL RUNS. `StartsWith(p)` and `Contains(p)` need every
// trigram of `p`; `LIKE` needs every trigram of every stretch between its wildcards. A run shorter
// than three bytes yields none, and a predicate that yields none claims nothing -- `LIKE '%a_b%'` is
// answered by the scan alone.
//
// CASE FOLDING IS ASCII AND ON BOTH SIDES. A case-insensitive index inserts lower-cased trigrams; a
// probe against one lower-cases what it looks for, which keeps the answer a superset for a
// case-SENSITIVE predicate too: a value containing `Foo` contains `foo` once folded. Bytes above
// 0x7F are left alone, since folding UTF-8 bytewise is not folding, and both sides agree on that.
using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Indexes;

/// <summary>Byte trigrams, for the text indexes.</summary>
internal static class Trigrams
{
    /// <summary>Bytes in a trigram.</summary>
    internal const int Length = 3;

    /// <summary>Copies a trigram, ASCII-lower-cased when asked.</summary>
    /// <param name="trigram">Three bytes.</param>
    /// <param name="fold">Whether to lower-case.</param>
    /// <param name="destination">Three bytes.</param>
    internal static void Copy(ReadOnlySpan<byte> trigram, bool fold, Span<byte> destination)
    {
        for (int i = 0; i < Length; i++)
        {
            byte b = trigram[i];
            destination[i] = fold && b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
        }
    }

    /// <summary>
    /// The distinct trigrams a string predicate requires of a matching value, or none when it claims
    /// nothing.
    /// </summary>
    /// <param name="match">The predicate.</param>
    /// <param name="fold">Whether to lower-case them for a case-insensitive index.</param>
    internal static List<byte[]> Required(StringMatchExpr match, bool fold)
    {
        List<byte[]> runs = match.Op == StringMatchOp.Like
            ? BytePattern.LiteralRuns(match.Pattern.BytesValue, match.Escape)
            : [match.Pattern.BytesValue.ToArray()];
        List<byte[]> trigrams = [];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        Span<byte> scratch = stackalloc byte[Length];
        foreach (byte[] run in runs)
        {
            for (int i = 0; i + Length <= run.Length; i++)
            {
                Copy(run.AsSpan(i, Length), fold, scratch);
                if (seen.Add(Convert.ToHexString(scratch)))
                {
                    trigrams.Add(scratch.ToArray());
                }
            }
        }

        return trigrams;
    }
}
