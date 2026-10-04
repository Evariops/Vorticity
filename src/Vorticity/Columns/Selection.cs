using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity;

/// <summary>
/// Which rows of a delivered batch passed the scan's filter: all of them, or those whose bit is set.
/// </summary>
/// <remarks>
/// A compacted batch — the default — is all selected. A batch delivered whole under
/// <c>ScanOptions.Compact = false</c>, and a take by row position, carry the rows that were asked for
/// here instead of copying them out.
/// </remarks>
public readonly ref struct Selection
{
    private readonly ReadOnlySpan<ulong> _words;
    private readonly int _length;
    private readonly int _count;

    // The words that may hold a set bit, [_firstWord, _endWord): a selection of a range of rows
    // inside a block is enumerated over the range's words, not the block's.
    private readonly int _firstWord;
    private readonly int _endWord;

    internal Selection(int length)
    {
        _words = default;
        _length = length;
        _count = length;
        IsAll = true;
    }

    internal Selection(ReadOnlySpan<ulong> words, int length, int count)
        : this(words, length, count, 0, words.Length)
    {
    }

    /// <summary>A selection whose set bits all lie in words [<paramref name="firstWord"/>, <paramref name="endWord"/>), every other word zero.</summary>
    internal Selection(ReadOnlySpan<ulong> words, int length, int count, int firstWord, int endWord)
    {
        _words = words;
        _length = length;
        _count = count;
        _firstWord = firstWord;
        _endWord = endWord;
        IsAll = false;
    }

    /// <summary>Whether every row is selected; <see cref="Words"/> is then empty.</summary>
    public bool IsAll { get; }

    /// <summary>The number of selected rows.</summary>
    public int Count => _count;

    /// <summary>The selection as 64-bit words, bit <c>i % 64</c> of word <c>i / 64</c> for row <c>i</c>; empty when <see cref="IsAll"/>.</summary>
    public ReadOnlySpan<ulong> Words => _words;

    /// <summary>Whether row <paramref name="row"/> is selected.</summary>
    /// <param name="row">A row of the batch.</param>
    /// <returns>Whether it passed.</returns>
    public bool Contains(int row)
    {
        if ((uint)row >= (uint)_length)
        {
            return false;
        }

        return IsAll || ((_words[row >> 6] >> (row & 63)) & 1) != 0;
    }

    /// <summary>The selected rows, in order.</summary>
    /// <returns>The enumerator.</returns>
    public Enumerator GetEnumerator() => new Enumerator(_words, _length, IsAll, _firstWord, _endWord);

    /// <summary>Walks the selected rows by <see cref="BitOperations.TrailingZeroCount(ulong)"/>, sixty-four rows per word.</summary>
    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<ulong> _words;
        private readonly int _length;
        private readonly bool _all;
        private readonly int _endWord;
        private int _word;
        private ulong _bits;
        private int _current;

        internal Enumerator(ReadOnlySpan<ulong> words, int length, bool all, int firstWord, int endWord)
        {
            _words = words;
            _length = length;
            _all = all;
            _endWord = Math.Min(endWord, words.Length);
            _word = firstWord - 1;
            _bits = 0;
            _current = -1;
        }

        /// <summary>The current row.</summary>
        public readonly int Current => _current;

        /// <summary>Moves to the next selected row.</summary>
        /// <returns>Whether there is one.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_all)
            {
                return ++_current < _length;
            }

            while (_bits == 0)
            {
                if (++_word >= _endWord)
                {
                    return false;
                }

                _bits = _words[_word];
            }

            _current = (_word << 6) + BitOperations.TrailingZeroCount(_bits);
            _bits &= _bits - 1;
            return true;
        }
    }
}

/// <summary>How a delivered column holds its values before anything decodes them.</summary>
public enum ColumnEncoding : byte
{
    /// <summary>Plain values, contiguous.</summary>
    Canonical,

    /// <summary>Codes into a dictionary of distinct values; <c>AsDictionary()</c> reads both without decoding.</summary>
    Dictionary,

    /// <summary>Runs of equal values; <c>AsRunEnd()</c> reads the run ends and one value per run.</summary>
    RunEnd,

    /// <summary>One value for every row; <c>AsConstant()</c> reads it.</summary>
    Constant,
}

/// <summary>A dictionary-encoded column as the file holds it: a code per row, and the distinct values.</summary>
/// <typeparam name="T">The column's .NET type.</typeparam>
public readonly ref struct DictionaryView<T>
{
    internal DictionaryView(ReadOnlySpan<uint> codes, Column<T> values)
    {
        Codes = codes;
        Values = values;
    }

    /// <summary>One code per row, the index of its value in <see cref="Values"/>; widened to 32 bits when the file stores narrower codes.</summary>
    /// <remarks>A null row's code is 0, and the column's validity is what says the row is null.</remarks>
    public ReadOnlySpan<uint> Codes { get; }

    /// <summary>The distinct values, in code order.</summary>
    public Column<T> Values { get; }

    /// <summary>The number of distinct values.</summary>
    public int Cardinality => Values.Length;
}

/// <summary>A run-end-encoded column as the file holds it: where each run ends, and its value.</summary>
/// <typeparam name="T">The column's .NET type.</typeparam>
public readonly ref struct RunEndView<T>
{
    internal RunEndView(ReadOnlySpan<uint> runEnds, Column<T> values)
    {
        RunEnds = runEnds;
        Values = values;
    }

    /// <summary>The exclusive end of each run, relative to the batch.</summary>
    public ReadOnlySpan<uint> RunEnds { get; }

    /// <summary>One value per run.</summary>
    public Column<T> Values { get; }

    /// <summary>The number of runs.</summary>
    public int RunCount => RunEnds.Length;
}
