using System;
using System.Buffers;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// What a file's sort key holds in each of its granules, with their rows, in one of two shapes. The values of a component
/// that is a function of a column, a truncation, whose value each row's key holds and no column of the file does: the rows
/// of a granule come in the key's order, so a value's rows of one run of the key's prefix lie together; the runs of one
/// value, from two series, are counted as one. Or the runs of the whole indexed prefix (<see cref="WholePrefix"/>), each one
/// value of every component, the key's own bytes from its start through the prefix.
/// </summary>
/// <remarks>
/// <para>
/// A component's region: a version byte, <see cref="Version"/>; the granules, a varint; for each granule its values, a
/// varint, then each value, an integer, the function's image as <see cref="Compute.ValueFunction.TryInteger"/> reads it, in
/// zigzag difference from the one before in the granule (the first from 0), and its rows, a varint. A granule of a value
/// takes three to five bytes, one of two, eight to ten.
/// </para>
/// <para>
/// The prefix's region: a version byte, <see cref="PrefixVersion"/>; the granules, a varint; for each granule its runs, a
/// varint, 0 for a granule of more than an eighth of its rows' runs (<see cref="PrefixBuilder.MaxRuns"/>), which it does not
/// describe; then each run,
/// the bytes its key shares with the run's before, from the region's first on, a varint, the rest of its bytes, their
/// length then themselves, and its rows, a varint. A run that goes on from the granule before shares its whole key: five
/// bytes or so; one of another hour of a host, ten.
/// </para>
/// <para>
/// A question keyed by a coarser function of the same column, a day or a bucket of hours over hours, takes its groups in a
/// granule from a component's values where each value's class falls in one of its groups: no read of the column's rows. One
/// keyed by several columns of the prefix, the service and a bucket of hours, takes them run by run from the prefix's,
/// where a granule holds two services and two hours.
/// </para>
/// </remarks>
internal sealed class GranuleKeyRuns
{
    /// <summary>The encoding's version of a component's values.</summary>
    internal const byte Version = 1;

    /// <summary>The encoding's version of the runs of the whole indexed prefix.</summary>
    internal const byte PrefixVersion = 2;

    /// <summary>The place a file's runs of its whole indexed prefix are named by among its components' values.</summary>
    internal const int WholePrefix = -1;

    // Each granule's first value or run, and the values and their rows, or the runs' rows, granule after granule; each run's
    // key, back to back, from where each starts.
    private readonly int[] _starts;
    private readonly long[] _values;
    private readonly long[] _rows;
    private readonly byte[] _keys;
    private readonly int[] _keyStarts;

    private GranuleKeyRuns(int[] starts, long[] values, long[] rows, byte[] keys, int[] keyStarts, long bytes)
    {
        _starts = starts;
        _values = values;
        _rows = rows;
        _keys = keys;
        _keyStarts = keyStarts;
        Bytes = bytes;
    }

    /// <summary>The granules described.</summary>
    internal int Count => _starts.Length - 1;

    /// <summary>Whether it holds the runs of the whole indexed prefix rather than a component's values.</summary>
    internal bool IsPrefix => _keyStarts.Length > 0;

    /// <summary>The bytes held, for a cache's budget.</summary>
    internal long Bytes { get; }

    /// <summary>The values granule <paramref name="granule"/> holds, ascending as the key orders them within a run, and their rows.</summary>
    internal (ReadOnlyMemory<long> Values, ReadOnlyMemory<long> Rows) Of(int granule)
    {
        int start = _starts[granule];
        int length = _starts[granule + 1] - start;
        return (_values.AsMemory(start, length), _rows.AsMemory(start, length));
    }

    /// <summary>The runs of the prefix granule <paramref name="granule"/> holds, the first's place and their count; none for a granule not described.</summary>
    internal (int First, int Count) RunsOf(int granule) => (_starts[granule], _starts[granule + 1] - _starts[granule]);

    /// <summary>The key of run <paramref name="run"/>, row-encoded from its start through the indexed prefix.</summary>
    internal ReadOnlySpan<byte> KeyOf(int run) => _keys.AsSpan(_keyStarts[run], _keyStarts[run + 1] - _keyStarts[run]);

    /// <summary>The rows of run <paramref name="run"/>.</summary>
    internal long RowsOf(int run) => _rows[run];

    /// <summary>The region <paramref name="bytes"/> holds, of either shape; null when it does not decode as one.</summary>
    internal static GranuleKeyRuns? From(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] is not (Version or PrefixVersion))
        {
            return null;
        }

        try
        {
            ProtoReader reader = new ProtoReader(bytes[1..]);
            ulong granules = reader.ReadVarint();
            if (granules > int.MaxValue / 2 || granules > (ulong)bytes.Length)
            {
                return null;
            }

            return bytes[0] == Version ? Values(ref reader, (int)granules, bytes.Length) : Runs(ref reader, (int)granules, bytes.Length);
        }
        catch (VortexFormatException)
        {
            return null;
        }
    }

    /// <summary>A component's values over <paramref name="granules"/> granules, read from <paramref name="reader"/>.</summary>
    private static GranuleKeyRuns? Values(ref ProtoReader reader, int granules, int length)
    {
        int[] starts = new int[granules + 1];
        ArrayBufferWriter<long> values = new ArrayBufferWriter<long>(Math.Max(1, granules));
        ArrayBufferWriter<long> rows = new ArrayBufferWriter<long>(Math.Max(1, granules));
        for (int g = 0; g < granules; g++)
        {
            starts[g] = values.WrittenCount;
            ulong count = reader.ReadVarint();
            if (count > (ulong)length)
            {
                return null;
            }

            long value = 0;
            for (ulong v = 0; v < count; v++)
            {
                ulong zigzag = reader.ReadVarint();
                value += (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
                values.GetSpan(1)[0] = value;
                values.Advance(1);
                rows.GetSpan(1)[0] = checked((long)reader.ReadVarint());
                rows.Advance(1);
            }
        }

        starts[^1] = values.WrittenCount;
        if (!reader.End)
        {
            return null;
        }

        return new GranuleKeyRuns(starts, values.WrittenSpan.ToArray(), rows.WrittenSpan.ToArray(), [], [], (values.WrittenCount * 16L) + (starts.Length * 4L) + 64);
    }

    /// <summary>The runs of the prefix over <paramref name="granules"/> granules, read from <paramref name="reader"/>, each key whole.</summary>
    private static GranuleKeyRuns? Runs(ref ProtoReader reader, int granules, int length)
    {
        int[] starts = new int[granules + 1];
        ArrayBufferWriter<long> rows = new ArrayBufferWriter<long>(Math.Max(1, granules));
        ArrayBufferWriter<int> keyStarts = new ArrayBufferWriter<int>(Math.Max(1, granules));
        ArrayBufferWriter<byte> keys = new ArrayBufferWriter<byte>(Math.Max(16, length * 2));
        int previous = -1;
        for (int g = 0; g < granules; g++)
        {
            starts[g] = rows.WrittenCount;
            ulong count = reader.ReadVarint();
            if (count > (ulong)reader.Remaining)
            {
                return null;
            }

            for (ulong r = 0; r < count; r++)
            {
                ulong shared = reader.ReadVarint();
                ReadOnlySpan<byte> rest = reader.ReadLengthDelimited();
                int before = previous < 0 ? 0 : keys.WrittenCount - previous;
                if (shared > (ulong)before)
                {
                    return null;
                }

                // The run's key: the bytes it shares with the one before, then its own.
                int start = keys.WrittenCount;
                Span<byte> key = keys.GetSpan((int)shared + rest.Length);
                if (shared > 0)
                {
                    keys.WrittenSpan.Slice(previous, (int)shared).CopyTo(key);
                }

                rest.CopyTo(key[(int)shared..]);
                keys.Advance((int)shared + rest.Length);
                keyStarts.GetSpan(1)[0] = start;
                keyStarts.Advance(1);
                previous = start;
                rows.GetSpan(1)[0] = checked((long)reader.ReadVarint());
                rows.Advance(1);
            }
        }

        starts[^1] = rows.WrittenCount;
        if (!reader.End)
        {
            return null;
        }

        keyStarts.GetSpan(1)[0] = keys.WrittenCount;
        keyStarts.Advance(1);
        return new GranuleKeyRuns(
            starts, [], rows.WrittenSpan.ToArray(), keys.WrittenSpan.ToArray(), keyStarts.WrittenSpan.ToArray(),
            keys.WrittenCount + (rows.WrittenCount * 12L) + (starts.Length * 4L) + 64);
    }

    /// <summary>
    /// Builds the region of one component as a file's rows come in its key's order: the values of the granule open, merged
    /// by value, closed into the region as the next granule starts, and the rows of the granules of one value.
    /// </summary>
    internal sealed class Builder
    {
        private ProtoWriter _granules = new ProtoWriter(256);
        private int _count;
        private long _open = -1;
        private long[] _values = new long[4];
        private long[] _rows = new long[4];
        private int _held;

        // The rows of the granules closed that hold one value.
        private long _singleRows;

        /// <summary>Adds <paramref name="rows"/> rows of <paramref name="value"/> to granule <paramref name="granule"/>, the open one or one after it.</summary>
        internal void Add(long granule, long value, long rows)
        {
            while (_open < granule)
            {
                Close();
                _open = _open < 0 ? granule : _open + 1;
            }

            for (int i = 0; i < _held; i++)
            {
                if (_values[i] == value)
                {
                    _rows[i] += rows;
                    return;
                }
            }

            if (_held == _values.Length)
            {
                Array.Resize(ref _values, _held * 2);
                Array.Resize(ref _rows, _held * 2);
            }

            _values[_held] = value;
            _rows[_held] = rows;
            _held++;
        }

        /// <summary>The region, the open granule closed, and the rows of the granules of one value; then the builder starts over.</summary>
        internal (byte[] Region, long SingleRows) Finish()
        {
            Close();
            ProtoWriter region = new ProtoWriter(_granules.WrittenSpan.Length + 16);
            region.WriteRawBytes([Version]);
            region.WriteVarint((ulong)_count);
            region.WriteRawBytes(_granules.WrittenSpan);
            byte[] bytes = region.WrittenSpan.ToArray();
            long single = _singleRows;
            region.Dispose();
            _granules.Dispose();
            _granules = new ProtoWriter(256);
            _count = 0;
            _open = -1;
            _singleRows = 0;
            return (bytes, single);
        }

        /// <summary>Writes the open granule's values, if any.</summary>
        private void Close()
        {
            if (_open < 0)
            {
                return;
            }

            _granules.WriteVarint((ulong)_held);
            long previous = 0;
            long total = 0;
            for (int i = 0; i < _held; i++)
            {
                long difference = _values[i] - previous;
                _granules.WriteVarint((ulong)((difference << 1) ^ (difference >> 63)));
                _granules.WriteVarint((ulong)_rows[i]);
                previous = _values[i];
                total += _rows[i];
            }

            _singleRows += _held == 1 ? total : 0;
            _count++;
            _held = 0;
        }
    }

    /// <summary>
    /// Builds the region of the runs of the whole indexed prefix as a file's rows come in its key's order: the runs of the
    /// granule open, a run cut by the rows' batches joined again, closed into the region as the next granule starts; the
    /// rows of the granules it describes, of at most <see cref="MaxRuns"/> runs, and of those of one.
    /// </summary>
    internal sealed class PrefixBuilder
    {
        /// <summary>
        /// The runs a granule it describes holds at most: an eighth of its rows, so that a run a row costs the region a byte or
        /// so a row at worst, and <see cref="GranuleStats.ListedValues"/> at least. Small services of many hosts hold four
        /// hundred runs and more in a granule of 8 192 rows, which the runs answer for kilobytes where its rows cost hundreds.
        /// </summary>
        internal int MaxRuns { get; set; } = GranuleStats.ListedValues;

        private ProtoWriter _granules = new ProtoWriter(256);
        private int _count;
        private long _open = -1;

        // The open granule's runs: their keys back to back from where each starts, their rows, and whether it holds more than
        // it lists. The key of the last run written, which the next shares bytes with.
        private byte[] _keys = new byte[256];
        private int[] _keyStarts = new int[17];
        private long[] _rows = new long[16];
        private int _held;
        private bool _over;
        private byte[] _previous = new byte[64];
        private int _previousLength;

        // The rows of the granules closed that it describes, and of those of one run.
        private long _describedRows;
        private long _singleRows;

        /// <summary>Whether granule <paramref name="granule"/> is the open one and holds more runs than it lists: it takes no more.</summary>
        internal bool Over(long granule) => _open == granule && _over;

        /// <summary>Adds <paramref name="rows"/> rows of the run of key <paramref name="key"/> to granule <paramref name="granule"/>, the open one or one after it.</summary>
        internal void Add(long granule, ReadOnlySpan<byte> key, long rows)
        {
            while (_open < granule)
            {
                Close();
                _open = _open < 0 ? granule : _open + 1;
            }

            if (_held > 0 && key.SequenceEqual(_keys.AsSpan(_keyStarts[_held - 1], _keyStarts[_held] - _keyStarts[_held - 1])))
            {
                _rows[_held - 1] += rows;
                return;
            }

            if (_over || _held >= MaxRuns)
            {
                _over = true;
                return;
            }

            if (_held + 1 == _rows.Length)
            {
                Array.Resize(ref _rows, _rows.Length * 2);
                Array.Resize(ref _keyStarts, _keyStarts.Length * 2);
            }

            int start = _keyStarts[_held];
            if (_keys.Length < start + key.Length)
            {
                Array.Resize(ref _keys, Math.Max(start + key.Length, _keys.Length * 2));
            }

            key.CopyTo(_keys.AsSpan(start));
            _rows[_held] = rows;
            _held++;
            _keyStarts[_held] = start + key.Length;
        }

        /// <summary>
        /// The region, the open granule closed, the rows of the granules it describes and of those of one run; then the
        /// builder starts over.
        /// </summary>
        internal (byte[] Region, long DescribedRows, long SingleRows) Finish()
        {
            Close();
            ProtoWriter region = new ProtoWriter(_granules.WrittenSpan.Length + 16);
            region.WriteRawBytes([PrefixVersion]);
            region.WriteVarint((ulong)_count);
            region.WriteRawBytes(_granules.WrittenSpan);
            byte[] bytes = region.WrittenSpan.ToArray();
            (long described, long single) = (_describedRows, _singleRows);
            region.Dispose();
            _granules.Dispose();
            _granules = new ProtoWriter(256);
            (_count, _open, _previousLength, _describedRows, _singleRows) = (0, -1, 0, 0, 0);
            return (bytes, described, single);
        }

        /// <summary>Writes the open granule's runs, if any; none for a granule of more than it lists.</summary>
        private void Close()
        {
            if (_open < 0)
            {
                return;
            }

            if (_over)
            {
                _granules.WriteVarint(0);
            }
            else
            {
                _granules.WriteVarint((ulong)_held);
                long total = 0;
                for (int i = 0; i < _held; i++)
                {
                    ReadOnlySpan<byte> key = _keys.AsSpan(_keyStarts[i], _keyStarts[i + 1] - _keyStarts[i]);
                    int shared = key.CommonPrefixLength(_previous.AsSpan(0, _previousLength));
                    _granules.WriteVarint((ulong)shared);
                    _granules.WriteVarint((ulong)(key.Length - shared));
                    _granules.WriteRawBytes(key[shared..]);
                    _granules.WriteVarint((ulong)_rows[i]);
                    total += _rows[i];
                    if (_previous.Length < key.Length)
                    {
                        _previous = new byte[key.Length * 2];
                    }

                    key.CopyTo(_previous);
                    _previousLength = key.Length;
                }

                _describedRows += total;
                _singleRows += _held == 1 ? total : 0;
            }

            _count++;
            _held = 0;
            _over = false;
        }
    }
}
