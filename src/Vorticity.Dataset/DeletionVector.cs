using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        List<long> starts = [];
        List<long> ends = [];
        long previous = -1;
        foreach (long row in rows)
        {
            if (row <= previous || row < 0)
            {
                throw new ArgumentException("The rows deleted are ascending, distinct and not negative.", nameof(rows));
            }

            if (ends.Count > 0 && ends[^1] == row)
            {
                ends[^1] = row + 1;
            }
            else
            {
                starts.Add(row);
                ends.Add(row + 1);
            }

            previous = row;
        }

        return WithRuns(CollectionsMarshal.AsSpan(starts), CollectionsMarshal.AsSpan(ends));
    }

    /// <summary>
    /// This vector with the rows of the runs <c>[starts[i], ends[i])</c> deleted too: ascending, apart
    /// or meeting, none of their rows deleted already. What a delete marks comes as runs, the rows a
    /// range takes as one, so no row is listed on its own.
    /// </summary>
    /// <exception cref="ArgumentException">The runs are not ascending and disjoint, one is empty or negative, or one holds a row deleted already.</exception>
    public DeletionVector WithRuns(ReadOnlySpan<long> starts, ReadOnlySpan<long> ends)
    {
        if (starts.Length != ends.Length)
        {
            throw new ArgumentException("A run has a start and an end.", nameof(ends));
        }

        if (starts.IsEmpty)
        {
            return this;
        }

        List<long> mergedStarts = new List<long>(_starts.Length + starts.Length);
        List<long> mergedEnds = new List<long>(_starts.Length + starts.Length);
        int run = 0;
        int at = 0;
        long previous = -1;
        while (run < _starts.Length || at < starts.Length)
        {
            long start;
            long end;
            if (at < starts.Length && (run >= _starts.Length || starts[at] < _starts[run]))
            {
                start = starts[at];
                end = ends[at++];
                if (start < 0 || end <= start || start < previous)
                {
                    throw new ArgumentException("The runs deleted are ascending, disjoint, not empty and not negative.", nameof(starts));
                }

                previous = end;
            }
            else
            {
                start = _starts[run];
                end = _ends[run++];
            }

            if (mergedStarts.Count > 0 && start < mergedEnds[^1])
            {
                throw new ArgumentException($"Row {start} is deleted already.", nameof(starts));
            }

            if (mergedStarts.Count > 0 && start == mergedEnds[^1])
            {
                mergedEnds[^1] = end;
            }
            else
            {
                mergedStarts.Add(start);
                mergedEnds.Add(end);
            }
        }

        return new DeletionVector([.. mergedStarts], [.. mergedEnds]);
    }

    /// <summary>
    /// A bit per row of <c>[start, start + rows)</c> into <paramref name="bits"/>, set for the live
    /// ones: every word set, then each run that meets the window cleared a word at a time.
    /// </summary>
    public void Live(long start, int rows, Span<ulong> bits)
    {
        int words = (rows + 63) >> 6;
        Span<ulong> window = bits[..words];
        window.Fill(ulong.MaxValue);
        int tail = rows & 63;
        if (tail != 0)
        {
            window[^1] = (1UL << tail) - 1;
        }

        long end = start + rows;
        for (int run = FirstEndingAfter(start); run < _starts.Length && _starts[run] < end; run++)
        {
            Clear(window, (int)(Math.Max(_starts[run], start) - start), (int)(Math.Min(_ends[run], end) - start));
        }
    }

    /// <summary>Clears the bits of <c>[from, to)</c>: the words between whole, the two at the ends in part.</summary>
    private static void Clear(Span<ulong> bits, int from, int to)
    {
        if (from >= to)
        {
            return;
        }

        int first = from >> 6;
        int last = (to - 1) >> 6;
        ulong head = ulong.MaxValue << (from & 63);
        ulong tail = ulong.MaxValue >> (63 - ((to - 1) & 63));
        if (first == last)
        {
            bits[first] &= ~(head & tail);
            return;
        }

        bits[first] &= ~head;
        bits[(first + 1)..last].Clear();
        bits[last] &= ~tail;
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

    /// <summary>Every deleted row, ascending, in one array.</summary>
    public long[] Rows()
    {
        long[] rows = new long[Count];
        int at = 0;
        for (int run = 0; run < _starts.Length; run++)
        {
            for (long row = _starts[run]; row < _ends[run]; row++)
            {
                rows[at++] = row;
            }
        }

        return rows;
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
