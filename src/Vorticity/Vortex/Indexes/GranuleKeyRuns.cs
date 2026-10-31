using System;
using System.Buffers;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// The values a component of a file's sort key takes in each of its granules, with their rows: a component that is a
/// function of a column, a truncation, whose value each row's key holds and no column of the file does. The rows of a
/// granule come in the key's order, so a value's rows of one run of the key's prefix lie together; the runs of one value,
/// from two series, are counted as one.
/// </summary>
/// <remarks>
/// <para>
/// The region: a version byte; the granules, a varint; for each granule its values, a varint, then each value, an integer,
/// the function's image as <see cref="Compute.ValueFunction.TryInteger"/> reads it, in zigzag difference from the one
/// before in the granule (the first from 0), and its rows, a varint. A granule of a value takes three to five bytes, one of
/// two, eight to ten.
/// </para>
/// <para>
/// A question keyed by a coarser function of the same column, a day or a bucket of hours over hours, takes its groups in a
/// granule from these values where each value's class falls in one of its groups: no read of the column's rows.
/// </para>
/// </remarks>
internal sealed class GranuleKeyRuns
{
    /// <summary>The encoding's version.</summary>
    internal const byte Version = 1;

    // Each granule's first value among the values, and the values and their rows, granule after granule.
    private readonly int[] _starts;
    private readonly long[] _values;
    private readonly long[] _rows;

    private GranuleKeyRuns(int[] starts, long[] values, long[] rows, long bytes)
    {
        _starts = starts;
        _values = values;
        _rows = rows;
        Bytes = bytes;
    }

    /// <summary>The granules described.</summary>
    internal int Count => _starts.Length - 1;

    /// <summary>The bytes held, for a cache's budget.</summary>
    internal long Bytes { get; }

    /// <summary>The values granule <paramref name="granule"/> holds, ascending as the key orders them within a run, and their rows.</summary>
    internal (ReadOnlyMemory<long> Values, ReadOnlyMemory<long> Rows) Of(int granule)
    {
        int start = _starts[granule];
        int length = _starts[granule + 1] - start;
        return (_values.AsMemory(start, length), _rows.AsMemory(start, length));
    }

    /// <summary>The region <paramref name="bytes"/> holds; null when it does not decode as one.</summary>
    internal static GranuleKeyRuns? From(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != Version)
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

            int[] starts = new int[(int)granules + 1];
            ArrayBufferWriter<long> values = new ArrayBufferWriter<long>(Math.Max(1, (int)granules));
            ArrayBufferWriter<long> rows = new ArrayBufferWriter<long>(Math.Max(1, (int)granules));
            for (int g = 0; g < (int)granules; g++)
            {
                starts[g] = values.WrittenCount;
                ulong count = reader.ReadVarint();
                if (count > (ulong)bytes.Length)
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

            return new GranuleKeyRuns(starts, values.WrittenSpan.ToArray(), rows.WrittenSpan.ToArray(), (values.WrittenCount * 16L) + (starts.Length * 4L) + 64);
        }
        catch (VortexFormatException)
        {
            return null;
        }
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
}
