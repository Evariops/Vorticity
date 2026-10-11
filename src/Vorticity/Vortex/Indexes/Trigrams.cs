using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Indexes;

/// <summary>
/// Byte trigrams, for the text indexes: what a text index inserts, and what a string predicate
/// requires of a value before the index may rule it out. A predicate needs every trigram of each of
/// its literal runs, so a run shorter than three bytes yields none, and a predicate that yields none
/// claims nothing and is left to the scan.
/// </summary>
internal static class Trigrams
{
    /// <summary>Bytes in a trigram.</summary>
    internal const int Length = 3;

    /// <summary>
    /// Copies a trigram, ASCII-lower-cased when asked. Both the index and the probe fold, which keeps
    /// the answer a superset for a case-sensitive predicate as well; bytes outside ASCII are left
    /// alone, since folding encoded text one byte at a time would not be folding.
    /// </summary>
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
