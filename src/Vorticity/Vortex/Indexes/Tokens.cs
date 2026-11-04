using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;

namespace Vorticity.Indexes;

/// <summary>
/// The words of a text, for the token index (spec G, 6): each a maximal run of ASCII letters and digits and of non-ASCII
/// bytes, so that a word of UTF-8 stays whole, cut to its first <see cref="Longest"/> bytes, folded to ASCII lower case when
/// the index folds. What the index inserts a value's words as, and what a predicate requires of a matching value before
/// the index may rule its granule out.
/// </summary>
internal static class Tokens
{
    /// <summary>The bytes of a word the index keeps: a longer one is cut, as the index and the question cut it alike.</summary>
    internal const int Longest = 64;

    /// <summary>Whether <paramref name="b"/> belongs to a word: an ASCII letter or digit, or a byte of a non-ASCII character.</summary>
    internal static bool InWord(byte b) => b >= 0x80 || (uint)((b | 0x20) - 'a') < 26u || (uint)(b - '0') < 10u;

    /// <summary>
    /// The next word of <paramref name="value"/> from <paramref name="at"/>, cut and folded into <paramref name="word"/>
    /// (<see cref="Longest"/> bytes); false when none is left.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="at">Where to look from, moved past the word.</param>
    /// <param name="fold">Whether to fold the word to ASCII lower case.</param>
    /// <param name="word">The word's bytes.</param>
    /// <param name="length">How many of them.</param>
    internal static bool Next(ReadOnlySpan<byte> value, ref int at, bool fold, Span<byte> word, out int length)
    {
        while (at < value.Length && !InWord(value[at]))
        {
            at++;
        }

        int start = at;
        while (at < value.Length && InWord(value[at]))
        {
            at++;
        }

        length = Math.Min(at - start, Longest);
        for (int i = 0; i < length; i++)
        {
            byte b = value[start + i];
            word[i] = fold && b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
        }

        return at > start;
    }

    /// <summary>Whether <paramref name="text"/> is one word, which <c>HasToken</c> looks for.</summary>
    internal static bool IsWord(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (byte b in text)
        {
            if (!InWord(b))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The words a value that meets <paramref name="match"/> holds, each once, folded as the index was: a token's own; for a
    /// prefix, a substring or a pattern, the words its literal parts hold whole, a separator or the literal's own end on each
    /// side where the value's end is sure, since the first and the last of a part may be pieces of longer words. None for a
    /// predicate that requires none, which the index then claims nothing of.
    /// </summary>
    internal static List<byte[]> Required(StringMatchExpr match, bool fold)
    {
        ReadOnlySpan<byte> pattern = match.Pattern.BytesValue;
        List<byte[]> words = [];
        switch (match.Op)
        {
            case StringMatchOp.Token:
                Whole(pattern, sureStart: true, sureEnd: true, fold, words);
                break;
            case StringMatchOp.StartsWith:
                // The value's start is the prefix's: its first word is whole on that side.
                Whole(pattern, sureStart: true, sureEnd: false, fold, words);
                break;
            case StringMatchOp.Contains:
                Whole(pattern, sureStart: false, sureEnd: false, fold, words);
                break;
            default:
                foreach (byte[] run in BytePattern.LiteralRuns(pattern, match.Escape))
                {
                    Whole(run, sureStart: false, sureEnd: false, fold, words);
                }

                break;
        }

        return words;
    }

    /// <summary>
    /// The words <paramref name="text"/>, a literal part of a predicate, holds whole, added to <paramref name="words"/> once
    /// each: a word that touches an end of the literal may run on in the value, unless that end is the value's own.
    /// </summary>
    private static void Whole(ReadOnlySpan<byte> text, bool sureStart, bool sureEnd, bool fold, List<byte[]> words)
    {
        Span<byte> word = stackalloc byte[Longest];
        int at = 0;
        while (true)
        {
            int start = at;
            while (start < text.Length && !InWord(text[start]))
            {
                start++;
            }

            if (!Next(text, ref at, fold, word, out int length))
            {
                return;
            }

            bool whole = (start > 0 || sureStart) && (at < text.Length || sureEnd);
            if (whole && !Held(words, word[..length]))
            {
                words.Add(word[..length].ToArray());
            }
        }
    }

    /// <summary>Whether <paramref name="words"/> holds <paramref name="word"/> already.</summary>
    private static bool Held(List<byte[]> words, ReadOnlySpan<byte> word)
    {
        foreach (byte[] known in words)
        {
            if (known.AsSpan().SequenceEqual(word))
            {
                return true;
            }
        }

        return false;
    }
}
