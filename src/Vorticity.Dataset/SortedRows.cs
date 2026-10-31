using System;
using System.Collections.Generic;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.RowEncoding;

namespace Vorticity.Dataset;

/// <summary>The row-encoded keys of rows that come in the key's order, by their place among them.</summary>
internal interface IOrderedKeys
{
    /// <summary>The key of row <paramref name="row"/>.</summary>
    ReadOnlySpan<byte> KeyAt(int row);
}

/// <summary>
/// What a sorted object's rows give its writer as they come in the key's order (<see cref="SortKey"/>): the indexed prefix
/// of the key of each granule's first row, which its sparse index keeps, its last key, and the rows of the granules that
/// hold one value of the prefix, whose first and last rows hold the same, the keys following one another; and, for each
/// component of the prefix that is a function of a column, the values it takes in each granule with their rows
/// (<see cref="GranuleKeyRuns"/>). What a compaction, an append and a rewrite share of every object they write sorted. A
/// granule is the writer's block.
/// </summary>
/// <param name="keys">The key the rows come in the order of.</param>
/// <param name="prefix">The components of the key the object indexes.</param>
/// <param name="skip">
/// The components that lead the keys before the key's own, the partition's, one value in an object, which its index
/// leaves out.
/// </param>
internal sealed class SortedRows(SortKey keys, int prefix, int skip = 0)
{
    // The components of the prefix, those skipped included: they hold one value in an object.
    private readonly int _cut = skip + prefix;

    // The keys of the object's granules, as they come; the rows taken; the keys of the first and the last.
    private readonly List<byte[]> _granuleKeys = [];
    private long _rows;
    private byte[]? _firstKey;
    private byte[] _lastKey = [];
    private int _lastKeyLength = -1;

    // The rows of the granules closed that hold one value of the prefix; the rows of a granule.
    private long _pureRows;
    private int _granuleRows;

    // Each component of the prefix that is a function of a column, by its place in the key, its values by granule, and
    // whether they still hold: a value that reads as no integer gives the component up. The bytes of a run's prefix.
    private readonly (int Position, GranuleKeyRuns.Builder Runs)[] _runs = FunctionsOf(keys, skip, prefix);
    private readonly bool[] _givenUp = new bool[FunctionCount(keys, skip, prefix)];
    private byte[] _runPrefix = new byte[64];

    /// <summary>The keys of the first and the last rows taken, whole; null before one.</summary>
    internal (byte[] First, byte[] Last)? Bounds => _firstKey is null || _lastKeyLength < 0 ? null : (_firstKey, _lastKey[.._lastKeyLength]);

    /// <summary>
    /// Takes rows <paramref name="from"/> to <paramref name="to"/> of <paramref name="source"/>, the object's next rows in the
    /// key's order: the keys of those that start a granule of <paramref name="granuleRows"/> rows, and the last one's key.
    /// </summary>
    internal void Take<TKeys>(TKeys source, int from, int to, int granuleRows)
        where TKeys : IOrderedKeys
    {
        if (to <= from)
        {
            return;
        }

        _firstKey ??= source.KeyAt(from).ToArray();
        _granuleRows = granuleRows;

        // The keys of the rows that start a granule: the object's rows before them are those taken.
        for (long row = (_rows + granuleRows - 1) / granuleRows * granuleRows; row < _rows + (to - from); row += granuleRows)
        {
            _granuleKeys.Add(Prefixed(source.KeyAt(from + (int)(row - _rows))).ToArray());
        }

        // The granules these rows close, each holding one value of the prefix when its last row holds its first's.
        for (long end = ((_rows / granuleRows) + 1) * granuleRows; end <= _rows + (to - from); end += granuleRows)
        {
            int granule = (int)(end / granuleRows) - 1;
            if (granule < _granuleKeys.Count && Prefixed(source.KeyAt(from + (int)(end - 1 - _rows))).SequenceEqual(_granuleKeys[granule]))
            {
                _pureRows += granuleRows;
            }
        }

        // The values of each component that is a function of a column, run by run: a run of one value of the prefix up to
        // it lies together, found by a gallop over the keys, a few looks a run and none a row.
        for (int c = 0; c < _runs.Length; c++)
        {
            (int position, GranuleKeyRuns.Builder runs) = _runs[c];
            for (int row = from; row < to && !_givenUp[c];)
            {
                int end = RunEnd(source, row, to, position + 1);
                if (!TryValue(source.KeyAt(row), position, out long value))
                {
                    _givenUp[c] = true;
                    break;
                }

                for (long at = _rows + (row - from), stop = _rows + (end - from); at < stop;)
                {
                    long granule = at / granuleRows;
                    long until = Math.Min(stop, (granule + 1) * granuleRows);
                    runs.Add(granule, value, until - at);
                    at = until;
                }

                row = end;
            }
        }

        _rows += to - from;
        _lastKeyLength = Keep(ref _lastKey, source.KeyAt(to - 1));
    }

    /// <summary>How many of the first <paramref name="prefix"/> components of <paramref name="keys"/> past <paramref name="skip"/> are functions of a column.</summary>
    private static int FunctionCount(SortKey keys, int skip, int prefix)
    {
        int count = 0;
        for (int position = skip; position < Math.Min(skip + prefix, keys.Count); position++)
        {
            count += keys.IsFunctionOfColumn(position) ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// The components of the first <paramref name="prefix"/> of <paramref name="keys"/> past <paramref name="skip"/> that are
    /// functions of a column, each with a builder of its values by granule.
    /// </summary>
    private static (int Position, GranuleKeyRuns.Builder Runs)[] FunctionsOf(SortKey keys, int skip, int prefix)
    {
        List<(int, GranuleKeyRuns.Builder)> functions = [];
        for (int position = skip; position < Math.Min(skip + prefix, keys.Count); position++)
        {
            if (keys.IsFunctionOfColumn(position))
            {
                functions.Add((position, new GranuleKeyRuns.Builder()));
            }
        }

        return [.. functions];
    }

    /// <summary>
    /// The first row past <paramref name="start"/>, up to <paramref name="to"/>, whose key's first <paramref name="components"/>
    /// values are not those of row <paramref name="start"/>'s: a gallop, then a halving.
    /// </summary>
    private int RunEnd<TKeys>(TKeys source, int start, int to, int components)
        where TKeys : IOrderedKeys
    {
        ReadOnlySpan<byte> first = source.KeyAt(start);
        int length = keys.PrefixLength(first, components);
        if (length < 0)
        {
            return start + 1;
        }

        if (_runPrefix.Length < length)
        {
            _runPrefix = new byte[length * 2];
        }

        first[..length].CopyTo(_runPrefix);
        ReadOnlySpan<byte> held = _runPrefix.AsSpan(0, length);
        int low = start;
        long step = 1;
        long high = start + 1;
        while (high < to && source.KeyAt((int)high).StartsWith(held))
        {
            low = (int)high;
            step *= 2;
            high = start + step;
        }

        int bound = (int)Math.Min(high, to);
        while (low + 1 < bound)
        {
            int middle = low + ((bound - low) / 2);
            if (source.KeyAt(middle).StartsWith(held))
            {
                low = middle;
            }
            else
            {
                bound = middle;
            }
        }

        return bound;
    }

    /// <summary>The value of the key's component at <paramref name="position"/> as an integer, the function's image; false when it reads as none.</summary>
    private bool TryValue(ReadOnlySpan<byte> key, int position, out long value)
    {
        value = 0;
        int at = keys.PrefixLength(key, position);
        if (at < 0)
        {
            return false;
        }

        FilterLiteral literal = RowEncoder.ReadFirstValue(key[at..], keys.TypeAt(position));
        if (!ValueFunction.TryInteger(literal, out Int128 integer) || integer < long.MinValue || integer > long.MaxValue)
        {
            return false;
        }

        value = (long)integer;
        return true;
    }

    /// <summary>
    /// Declares to <paramref name="writer"/>, before it completes, the object's sparse index, from the keys taken and the
    /// last one, and the rows of its granules of one value of the prefix, the last one closed; then starts over for the
    /// next object.
    /// </summary>
    internal void Finish(VortexFileWriter writer)
    {
        if (_granuleKeys.Count > 0 && _lastKeyLength >= 0)
        {
            byte[] last = Prefixed(_lastKey.AsSpan(0, _lastKeyLength)).ToArray();
            long open = _granuleRows <= 0 ? 0 : _rows % _granuleRows;
            long pure = _pureRows + (open > 0 && last.AsSpan().SequenceEqual(_granuleKeys[^1]) ? open : 0);
            writer.DeclareKeyIndex(prefix, [.. _granuleKeys], last, pure);
            List<(int Component, byte[] Region, long SingleRows)> declared = [];
            for (int c = 0; c < _runs.Length; c++)
            {
                (byte[] region, long single) = _runs[c].Runs.Finish();
                if (!_givenUp[c])
                {
                    declared.Add((_runs[c].Position - skip, region, single));
                }
            }

            if (declared.Count > 0)
            {
                writer.DeclareKeyRuns(declared);
            }
        }

        Clear();
    }

    /// <summary>Starts over for the next object, with nothing declared: what an object given up leaves.</summary>
    internal void Clear()
    {
        _granuleKeys.Clear();
        _rows = 0;
        _firstKey = null;
        _lastKeyLength = -1;
        _pureRows = 0;
        for (int c = 0; c < _runs.Length; c++)
        {
            _runs[c].Runs.Finish();
            _givenUp[c] = false;
        }
    }

    /// <summary>
    /// The indexed prefix of the row-encoded key <paramref name="key"/>, past the components skipped; the whole key from
    /// there when its width is not told.
    /// </summary>
    private ReadOnlySpan<byte> Prefixed(ReadOnlySpan<byte> key)
    {
        int start = skip == 0 ? 0 : keys.PrefixLength(key, skip);
        int end = keys.PrefixLength(key, _cut);
        return start < 0 ? key : end < 0 ? key[start..] : key[start..end];
    }

    /// <summary>Copies <paramref name="key"/> into <paramref name="buffer"/>, grown when short; its length.</summary>
    internal static int Keep(ref byte[] buffer, ReadOnlySpan<byte> key)
    {
        if (buffer.Length < key.Length)
        {
            buffer = new byte[Math.Max(key.Length, buffer.Length * 2)];
        }

        key.CopyTo(buffer);
        return key.Length;
    }
}

/// <summary>
/// The keys of rows in the key's order, by their places among <paramref name="keys"/>: through <paramref name="order"/>
/// from <paramref name="start"/> on, or the rows of <paramref name="keys"/> themselves from there when there is none.
/// </summary>
internal readonly struct OrderedRows(RowKeys keys, int[]? order, int start) : IOrderedKeys
{
    public ReadOnlySpan<byte> KeyAt(int row) => keys.Row(order is null ? start + row : order[start + row]);
}
