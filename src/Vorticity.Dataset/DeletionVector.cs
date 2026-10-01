using System;
using System.Collections.Generic;
using Vorticity.Scanning;

namespace Vorticity.Dataset;

/// <summary>
/// The rows a delete took out of a data object without rewriting it: sorted, disjoint runs of the
/// object's own row positions, which its leaf entry carries. A read leaves them out; a rewrite of the
/// object, a compaction's or that of a delete finding too many, drops them with the object.
/// </summary>
/// <remarks>
/// Runs rather than positions, because the rows a filter on the clustering key takes out of a sorted
/// object are contiguous: ten of them are one run, two varints. Every query is a binary search over
/// the runs, and none allocates.
/// </remarks>
internal sealed class DeletionVector : IRowExclusion, IEquatable<DeletionVector>
{
    private readonly long[] _starts;
    private readonly long[] _ends;

    /// <summary>The rows deleted before each run, and past the last one every deleted row.</summary>
    private readonly long[] _before;

    private DeletionVector(long[] starts, long[] ends)
    {
        _starts = starts;
        _ends = ends;
        _before = new long[starts.Length + 1];
        for (int run = 0; run < starts.Length; run++)
        {
            _before[run + 1] = _before[run] + (ends[run] - starts[run]);
        }
    }

    /// <summary>No row deleted.</summary>
    public static DeletionVector Empty { get; } = new DeletionVector([], []);

    /// <summary>How many rows are deleted.</summary>
    public long Count => _before[^1];

    /// <summary>How many runs they form.</summary>
    public int Runs => _starts.Length;

    /// <summary>Whether no row is deleted.</summary>
    public bool IsEmpty => _starts.Length == 0;

    /// <summary>The row the <paramref name="run"/>-th run starts at.</summary>
    public long StartOf(int run) => _starts[run];

    /// <summary>The row past the <paramref name="run"/>-th run's last.</summary>
    public long EndOf(int run) => _ends[run];

    /// <summary>The vector of <paramref name="rows"/>, ascending and distinct.</summary>
    /// <exception cref="ArgumentException">The rows are not ascending and distinct, or one is negative.</exception>
    public static DeletionVector Of(ReadOnlySpan<long> rows) => Empty.With(rows);

    /// <summary>
    /// This vector with <paramref name="rows"/> deleted too: ascending and distinct, none of them
    /// deleted already.
    /// </summary>
    /// <exception cref="ArgumentException">The rows are not ascending and distinct, one is negative, or one is deleted already.</exception>
    public DeletionVector With(ReadOnlySpan<long> rows)
    {
        if (rows.IsEmpty)
        {
            return this;
        }

        List<long> starts = new List<long>(_starts.Length + 1);
        List<long> ends = new List<long>(_starts.Length + 1);
        int run = 0;
        int at = 0;
        long previous = -1;
        while (run < _starts.Length || at < rows.Length)
        {
            long start;
            long end;
            if (at < rows.Length && (run >= _starts.Length || rows[at] < _starts[run]))
            {
                long row = rows[at++];
                if (row <= previous || row < 0)
                {
                    throw new ArgumentException("The rows deleted are ascending, distinct and not negative.", nameof(rows));
                }

                previous = row;
                start = row;
                end = row + 1;
            }
            else
            {
                start = _starts[run];
                end = _ends[run++];
            }

            if (starts.Count > 0 && start < ends[^1])
            {
                throw new ArgumentException($"Row {start} is deleted already.", nameof(rows));
            }

            if (starts.Count > 0 && start == ends[^1])
            {
                ends[^1] = end;
            }
            else
            {
                starts.Add(start);
                ends.Add(end);
            }
        }

        return new DeletionVector([.. starts], [.. ends]);
    }

    /// <inheritdoc/>
    long IRowExclusion.ExcludedCount => Count;

    /// <inheritdoc/>
    bool IRowExclusion.Excludes(long row) => Contains(row);

    /// <inheritdoc/>
    long IRowExclusion.ExcludedBefore(long row) => DeletedBefore(row);

    /// <inheritdoc/>
    long IRowExclusion.KeptRow(long kept) => Physical(kept);

    /// <summary>Whether <paramref name="row"/> is deleted.</summary>
    public bool Contains(long row)
    {
        int run = LastStartingAtOrBefore(row);
        return run >= 0 && row < _ends[run];
    }

    /// <summary>How many deleted rows lie before <paramref name="row"/>.</summary>
    public long DeletedBefore(long row)
    {
        int run = LastStartingAtOrBefore(row - 1);
        return run < 0 ? 0 : _before[run] + (Math.Min(row, _ends[run]) - _starts[run]);
    }

    /// <summary>
    /// Where a physical row lies among the live ones: its own place for a live row, and the place of
    /// the first live row after it for a deleted one.
    /// </summary>
    public long Logical(long physical) => physical - DeletedBefore(physical);

    /// <summary>The physical row of the <paramref name="logical"/>-th live row, counting from 0.</summary>
    public long Physical(long logical)
    {
        // The first run with more live rows before it than the position: every run before that one
        // lies before the row, and adds its rows to the position.
        int low = 0;
        int high = _starts.Length;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            if (_starts[middle] - _before[middle] > logical)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return logical + _before[low];
    }

    /// <summary>The first run that ends past <paramref name="row"/>; <see cref="Runs"/> when none does.</summary>
    public int FirstEndingAfter(long row)
    {
        int low = 0;
        int high = _ends.Length;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            if (_ends[middle] <= row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>How many deleted rows lie in <c>[start, end)</c>.</summary>
    public long DeletedIn(long start, long end) => end <= start ? 0 : DeletedBefore(end) - DeletedBefore(start);

    /// <summary>The deleted rows of <c>[start, end)</c>, ascending, written into <paramref name="rows"/>; how many.</summary>
    public int Collect(long start, long end, List<long> rows)
    {
        int written = 0;
        for (int run = FirstEndingAfter(start); run < _starts.Length && _starts[run] < end; run++)
        {
            for (long row = Math.Max(_starts[run], start); row < Math.Min(_ends[run], end); row++)
            {
                rows.Add(row);
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// The vector's canonical bytes: the run count, then per run the gap from the end of the one
    /// before and its length less one, each a varint. The runs are merged, so two vectors of the
    /// same rows are one encoding, as an entry inside a content-addressed page must be.
    /// </summary>
    public byte[] ToBytes()
    {
        int bytes = TreePage.VarintBytes((ulong)_starts.Length);
        long end = 0;
        for (int run = 0; run < _starts.Length; run++)
        {
            bytes += TreePage.VarintBytes((ulong)(_starts[run] - end)) + TreePage.VarintBytes((ulong)(_ends[run] - _starts[run] - 1));
            end = _ends[run];
        }

        byte[] value = new byte[bytes];
        Span<byte> at = TreePage.WriteVarint(value, (ulong)_starts.Length);
        end = 0;
        for (int run = 0; run < _starts.Length; run++)
        {
            at = TreePage.WriteVarint(at, (ulong)(_starts[run] - end));
            at = TreePage.WriteVarint(at, (ulong)(_ends[run] - _starts[run] - 1));
            end = _ends[run];
        }

        return value;
    }

    /// <summary>The bytes <see cref="ToBytes"/> would write, without writing them.</summary>
    public int EncodedBytes
    {
        get
        {
            int bytes = TreePage.VarintBytes((ulong)_starts.Length);
            long end = 0;
            for (int run = 0; run < _starts.Length; run++)
            {
                bytes += TreePage.VarintBytes((ulong)(_starts[run] - end)) + TreePage.VarintBytes((ulong)(_ends[run] - _starts[run] - 1));
                end = _ends[run];
            }

            return bytes;
        }
    }

    /// <summary>Reads a vector <see cref="ToBytes"/> wrote.</summary>
    /// <exception cref="CommitFormatException">The bytes are not a vector: cut short, a run of no row, or runs that meet.</exception>
    public static DeletionVector FromBytes(ReadOnlySpan<byte> value)
    {
        int at = 0;
        ulong runs = TreePage.ReadVarint(value, ref at, "A deletion vector");
        if (runs > (ulong)value.Length)
        {
            throw new CommitFormatException("A deletion vector counts more runs than it has bytes.");
        }

        long[] starts = new long[(int)runs];
        long[] ends = new long[(int)runs];
        long end = 0;
        for (int run = 0; run < starts.Length; run++)
        {
            ulong gap = TreePage.ReadVarint(value, ref at, "A deletion vector");
            ulong length = TreePage.ReadVarint(value, ref at, "A deletion vector");

            // A gap of zero after the first run would be two runs that meet, which the encoding
            // never writes: a vector has one encoding, or pages holding it are not content.
            if ((gap == 0 && run > 0) || gap > long.MaxValue / 2 || length > long.MaxValue / 2)
            {
                throw new CommitFormatException("A deletion vector's runs are not disjoint, ascending and apart.");
            }

            starts[run] = checked(end + (long)gap);
            ends[run] = checked(starts[run] + (long)length + 1);
            end = ends[run];
        }

        if (at != value.Length)
        {
            throw new CommitFormatException($"A deletion vector has {value.Length - at} bytes left over.");
        }

        return runs == 0 ? Empty : new DeletionVector(starts, ends);
    }

    /// <inheritdoc/>
    public bool Equals(DeletionVector? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && _starts.AsSpan().SequenceEqual(other._starts)
            && _ends.AsSpan().SequenceEqual(other._ends);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DeletionVector);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(_starts.Length);
        for (int run = 0; run < _starts.Length; run++)
        {
            hash.Add(_starts[run]);
            hash.Add(_ends[run]);
        }

        return hash.ToHashCode();
    }

    /// <summary>The last run starting at or before <paramref name="row"/>; -1 when none does.</summary>
    private int LastStartingAtOrBefore(long row)
    {
        int low = 0;
        int high = _starts.Length;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            if (_starts[middle] <= row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - 1;
    }
}
