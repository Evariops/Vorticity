// The three byte-pattern predicates of docs/12-index-reads.md §7, as pure functions over bytes.
//
// BYTES, NOT TEXT, and every consequence of that is deliberate. The comparison is bytewise, which
// for UTF-8 is code-point order; `_` in a LIKE pattern matches one BYTE, so it matches one third of
// a three-byte code point and the documentation says so rather than pretending otherwise; and there
// is no case folding, because folding needs a definition of "case" for UTF-8 that this iteration
// does not have and that docs/10-indexes.md §5.2's lower-cased trigrams would need too.
//
// `StartsWith` AND `Contains` ARE THE BCL'S, on purpose: `MemoryExtensions.StartsWith` and
// `IndexOf` over `ReadOnlySpan<byte>` are vectorised in dotnet/runtime (`SpanHelpers.Byte.cs`), and
// a hand-written loop here would be slower and would have to be tested against them anyway.
//
// LIKE IS A BACKTRACKING MATCHER, greedy on `%`, and it is linear on every pattern without adjacent
// wildcards. The quadratic case a backtracker has -- `%a%a%a%...` against a long value -- is bounded
// by the pattern, which the caller wrote, not by the file.
using System;
using System.Collections.Generic;

namespace Vorticity.Compute;

/// <summary>Matching a value's bytes against a pattern.</summary>
internal static class BytePattern
{
    private const byte Any = (byte)'%';
    private const byte One = (byte)'_';

    /// <summary>Whether <paramref name="value"/> begins with <paramref name="pattern"/>.</summary>
    /// <param name="value">The row's bytes.</param>
    /// <param name="pattern">The prefix; an empty one matches everything.</param>
    internal static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern) =>
        value.StartsWith(pattern);

    /// <summary>Whether <paramref name="pattern"/> occurs in <paramref name="value"/>.</summary>
    /// <param name="value">The row's bytes.</param>
    /// <param name="pattern">The needle; an empty one matches everything.</param>
    internal static bool Contains(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern) =>
        pattern.IsEmpty || value.IndexOf(pattern) >= 0;

    /// <summary>Whether <paramref name="value"/> matches a SQL <c>LIKE</c> pattern.</summary>
    /// <remarks>
    /// The greedy backtrack: walk both, remember where the last <c>%</c> was and how far the value
    /// had been consumed when it was taken, and on a mismatch resume one byte further along from
    /// there. An escaped wildcard is an ordinary byte, and so is an escape at the very end of the
    /// pattern — SQL leaves that undefined and treating it as a literal is the reading that never
    /// reads past the pattern.
    /// </remarks>
    /// <param name="value">The row's bytes.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="escape">The byte that quotes a wildcard or itself.</param>
    internal static bool Like(ReadOnlySpan<byte> value, ReadOnlySpan<byte> pattern, byte escape)
    {
        int v = 0;
        int p = 0;
        int star = -1;
        int resume = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length)
            {
                byte token = pattern[p];
                if (token == Any)
                {
                    // Remember the choice point and take the shortest match first; the loop widens
                    // it one byte at a time when what follows fails.
                    star = p;
                    p++;
                    resume = v;
                    continue;
                }

                bool escaped = token == escape && p + 1 < pattern.Length;
                byte literal = escaped ? pattern[p + 1] : token;
                if ((!escaped && token == One) || literal == value[v])
                {
                    p += escaped ? 2 : 1;
                    v++;
                    continue;
                }
            }

            if (star < 0)
            {
                return false;
            }

            p = star + 1;
            resume++;
            v = resume;
        }

        // Trailing `%`s match the empty rest; anything else left over does not.
        while (p < pattern.Length && pattern[p] == Any)
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// The smallest byte string strictly greater than every string beginning with
    /// <paramref name="prefix"/>, or an empty span when there is none.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT TURNS `StartsWith` INTO A RANGE, and the range is what a zone map and a sorted
    /// index can both prune with: <c>x ≥ p AND x &lt; succ(p)</c> selects exactly the values that
    /// begin with <c>p</c>, bytewise. Trailing <c>0xFF</c> bytes are dropped before the increment
    /// because <c>0xFF</c> has no successor; an empty prefix, or one that is all <c>0xFF</c>, has no
    /// upper bound at all and the caller keeps only the lower one.
    /// </remarks>
    /// <param name="prefix">The prefix.</param>
    /// <param name="destination">Receives the successor; must be at least as long as the prefix.</param>
    /// <returns>Its length, or 0 when the prefix has no successor.</returns>
    internal static int Successor(ReadOnlySpan<byte> prefix, Span<byte> destination)
    {
        int length = prefix.Length;
        while (length > 0 && prefix[length - 1] == 0xFF)
        {
            length--;
        }

        if (length == 0)
        {
            return 0;
        }

        prefix[..length].CopyTo(destination);
        destination[length - 1]++;
        return length;
    }

    /// <summary>
    /// The literal runs of a <c>LIKE</c> pattern: the stretches between unescaped wildcards, each of
    /// which a matching value must contain somewhere (docs/10-indexes.md §5.2).
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="escape">The byte that quotes a wildcard.</param>
    /// <returns>The runs, escapes resolved, empty ones left out.</returns>
    internal static List<byte[]> LiteralRuns(ReadOnlySpan<byte> pattern, byte escape)
    {
        List<byte[]> runs = [];
        List<byte> run = [];
        for (int p = 0; p < pattern.Length;)
        {
            byte token = pattern[p];
            if (token == Any || token == One)
            {
                Flush(runs, run);
                p++;
                continue;
            }

            if (token == escape && p + 1 < pattern.Length)
            {
                run.Add(pattern[p + 1]);
                p += 2;
                continue;
            }

            run.Add(token);
            p++;
        }

        Flush(runs, run);
        return runs;

        static void Flush(List<byte[]> runs, List<byte> run)
        {
            if (run.Count > 0)
            {
                runs.Add([.. run]);
                run.Clear();
            }
        }
    }

    /// <summary>
    /// The literal bytes a <c>LIKE</c> pattern must begin with, when it does not begin with a
    /// wildcard.
    /// </summary>
    /// <remarks>
    /// A pattern whose first token is <c>%</c> or <c>_</c> constrains nothing about the value's
    /// start and yields an empty prefix, which the pruner reads as "no claim". Everything up to the
    /// first unescaped wildcard is a prefix the value must have, so it prunes exactly as
    /// <see cref="StartsWith"/> does.
    /// </remarks>
    /// <param name="pattern">The pattern.</param>
    /// <param name="escape">The byte that quotes a wildcard.</param>
    /// <param name="destination">Receives the prefix; must be at least as long as the pattern.</param>
    /// <returns>Its length, 0 when the pattern claims no prefix.</returns>
    internal static int LeadingLiteral(
        ReadOnlySpan<byte> pattern, byte escape, Span<byte> destination)
    {
        int written = 0;
        for (int p = 0; p < pattern.Length;)
        {
            byte token = pattern[p];
            if (token == Any || token == One)
            {
                break;
            }

            if (token == escape && p + 1 < pattern.Length)
            {
                destination[written++] = pattern[p + 1];
                p += 2;
                continue;
            }

            destination[written++] = token;
            p++;
        }

        return written;
    }
}
