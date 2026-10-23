using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Keys;

/// <summary>
/// The entries of an instant column whose file is sorted slice by slice (<see cref="IndexKinds.Slices"/>):
/// the slices follow each other in time, so a seek finds its slice in the slice table, and inside the
/// slice, whose rows other columns order first, the slice's instants are read and ranked when a position
/// first needs them. A rank before a slice is the rows of the slices before it, read from the table.
/// </summary>
/// <remarks>
/// <para>
/// Two slices are held at most: the one the position is in, and the last one a rank read, so a walk
/// that ranks its next bound does not read its own slice again. A slice's instants are ranked by a
/// least significant digit radix over their distance from the slice's start, which keeps the rows of
/// one instant in the order they lie: entries in <c>(key, row)</c> order, as every source gives them.
/// </para>
/// <para>
/// Like every index the table is a hint, checked as it is used: a slice holding a null instant, or an
/// instant outside its bounds, is a file that does not say what it holds, and refused.
/// </para>
/// </remarks>
internal sealed class SlicedKeySource : KeySource
{
    /// <summary>A slice ranked below this many rows takes an insertion sort, which a radix's buckets would cost more than.</summary>
    private const int SmallSlice = 64;

    private readonly VortexFile _file;
    private readonly string _path;
    private readonly SliceTable _table;
    private readonly long[] _before;
    private Held _here = new Held();
    private Held _spare = new Held();
    private int _at = -1;

    /// <summary>
    /// The instants of the rows of the segments the last read covered, from <see cref="_segmentStart"/>: a slice
    /// of a few hundred rows lies in a segment of thousands, which a scan fetches whole whatever rows it asks
    /// for, so the slices beside it are ranked from these rather than read again, a round trip each.
    /// </summary>
    private long[] _segment = [];
    private long _segmentStart = -1;
    private int _segmentRows;

    /// <summary>The bounds of the instant column's segments, from the first read on; null before.</summary>
    private SplitPlan? _segments;

    private SlicedKeySource(VortexFile file, string path, SliceTable table)
    {
        _file = file;
        _path = path;
        _table = table;
        IReadOnlyList<TimeSlice> slices = table.Slices;
        _before = new long[slices.Count + 1];
        for (int s = 0; s < slices.Count; s++)
        {
            _before[s + 1] = _before[s] + slices[s].Rows;
        }
    }

    internal override FilterLiteralKind KeyKind => FilterLiteralKind.Signed;

    internal override long? EntryCount => _before[^1];

    internal override int Runs => _table.Slices.Count;

    internal override bool IsValid => _at >= 0;

    internal override FilterLiteral Key => FilterLiteral.From(_here.Keys[_at]);

    internal override ReadOnlySpan<byte> KeyBytes => default;

    internal override long Row => _table.Slices[_here.Slice].FirstRow + _here.Rows[_at];

    /// <summary>The time slice the current entry lies in.</summary>
    internal int Slice => _here.Slice;

    /// <summary>The time slice <paramref name="slice"/>: its bounds and its rows.</summary>
    internal TimeSlice SliceAt(int slice) => _table.Slices[slice];

    /// <summary>
    /// The source of <paramref name="path"/> when the file's slice table is on that column, a column of
    /// 64-bit instants, and covers the file's rows; otherwise null, and why.
    /// </summary>
    internal static async ValueTask<(SlicedKeySource? Source, string? Reason)> OpenAsync(
        VortexFile file, string path, CancellationToken cancellationToken)
    {
        if (!file.HasIndexDirectory)
        {
            return (null, "the file carries no index directory");
        }

        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return (null, $"the file's index directory was refused: {file.IndexDirectoryRefusal}");
        }

        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.Slices
                || !KeyIndexPruner.TryResolve(file.DType, entry.ColumnPath, out string resolved, out DType dtype)
                || !string.Equals(resolved, path, StringComparison.Ordinal))
            {
                continue;
            }

            DType storage = dtype;
            while (storage.Kind == DTypeKind.Extension)
            {
                storage = storage.StorageType;
            }

            if (storage.Kind != DTypeKind.Primitive || storage.PType != PType.I64)
            {
                return (null, $"the slice table is on a {storage.Kind} column, not one of 64-bit instants");
            }

            return SliceTable.FromBytes(entry.Options) is { } table && table.Rows == file.RowCount
                ? (new SlicedKeySource(file, path, table), null)
                : (null, "the slice table does not decode, or does not cover the file's rows");
        }

        return (null, "the index directory has no slice table on this column");
    }

    internal override async ValueTask<bool> SeekAsync(FilterLiteral key, SeekMode op, CancellationToken cancellationToken)
    {
        long k = key.SignedValue;
        switch (op)
        {
            case SeekMode.After:
                return await FirstFromAsync(FirstEndingAfter(k), k, upper: true, cancellationToken).ConfigureAwait(false);
            case SeekMode.AtOrBefore:
                return await LastFromAsync(LastStartingBefore(k, inclusive: true), k, upper: true, cancellationToken).ConfigureAwait(false);
            case SeekMode.Before:
                return await LastFromAsync(LastStartingBefore(k, inclusive: false), k, upper: false, cancellationToken).ConfigureAwait(false);
            default:
                bool found = await FirstFromAsync(FirstEndingAfter(k), k, upper: false, cancellationToken).ConfigureAwait(false);
                if (op == SeekMode.Exact && found && _here.Keys[_at] != k)
                {
                    Invalidate();
                    return false;
                }

                return found;
        }
    }

    internal override async ValueTask<bool> SeekFirstAsync(CancellationToken cancellationToken)
    {
        await MoveToAsync(0, cancellationToken).ConfigureAwait(false);
        _at = 0;
        return true;
    }

    internal override async ValueTask<bool> SeekLastAsync(CancellationToken cancellationToken)
    {
        await MoveToAsync(_table.Slices.Count - 1, cancellationToken).ConfigureAwait(false);
        _at = _here.Count - 1;
        return true;
    }

    internal override ValueTask<bool> NextAsync(CancellationToken cancellationToken)
    {
        if (_at + 1 < _here.Count)
        {
            _at++;
            return new ValueTask<bool>(true);
        }

        return FirstOfAsync(_here.Slice + 1, cancellationToken);
    }

    internal override ValueTask<bool> PreviousAsync(CancellationToken cancellationToken)
    {
        if (_at > 0)
        {
            _at--;
            return new ValueTask<bool>(true);
        }

        return LastOfAsync(_here.Slice - 1, cancellationToken);
    }

    internal override ValueTask<bool> NextKeyAsync(CancellationToken cancellationToken)
    {
        // A key lies in one slice, the one its instant falls in.
        int past = KeyGallop.Past(new Run(_here, _at), _at, _here.Count - 1, forward: true);
        if (past >= 0)
        {
            _at = past;
            return new ValueTask<bool>(true);
        }

        return FirstOfAsync(_here.Slice + 1, cancellationToken);
    }

    internal override ValueTask<bool> PreviousKeyAsync(CancellationToken cancellationToken)
    {
        int past = KeyGallop.Past(new Run(_here, _at), _at, 0, forward: false);
        if (past >= 0)
        {
            _at = past;
            return new ValueTask<bool>(true);
        }

        return LastOfAsync(_here.Slice - 1, cancellationToken);
    }

    internal override ValueTask<long> RankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        RankAsync(key.SignedValue, upper: false, cancellationToken);

    internal override ValueTask<long> UpperRankAsync(FilterLiteral key, CancellationToken cancellationToken) =>
        RankAsync(key.SignedValue, upper: true, cancellationToken);

    internal override ValueTask<long> RankOfAsync(KeySource other, bool upper, CancellationToken cancellationToken) =>
        RankAsync(other.Key.SignedValue, upper, cancellationToken);

    internal override async ValueTask<bool> SeekRankAsync(long rank, CancellationToken cancellationToken)
    {
        if (rank < 0 || rank >= _before[^1])
        {
            Invalidate();
            return false;
        }

        // The last slice whose entries before it are at or below the rank.
        int low = 0;
        int high = _table.Slices.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >>> 1;
            if (_before[middle] <= rank)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        await MoveToAsync(low, cancellationToken).ConfigureAwait(false);
        _at = (int)(rank - _before[low]);
        return true;
    }

    internal override ValueTask<long> CountAtKeyAsync(CancellationToken cancellationToken)
    {
        int end = KeyGallop.Past(new Run(_here, _at), _at, _here.Count - 1, forward: true);
        int before = KeyGallop.Past(new Run(_here, _at), _at, 0, forward: false);
        return new ValueTask<long>((end < 0 ? _here.Count : end) - before - 1);
    }

    internal override void Invalidate() => _at = -1;

    public override ValueTask DisposeAsync()
    {
        _at = -1;
        _here = new Held();
        _spare = new Held();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Sorts the first <paramref name="count"/> of <paramref name="keys"/> ascending, <paramref name="rows"/>
    /// moved alongside, stably: the rows of one key stay in the order they come. Every key is at or
    /// above <paramref name="low"/>, and a radix pass sorts 11 bits of their distance from it, so a
    /// slice of a minute in microseconds takes three passes and one of a day four.
    /// </summary>
    internal static void SortByKey(long[] keys, int[] rows, int count, long low)
    {
        if (count < SmallSlice)
        {
            for (int i = 1; i < count; i++)
            {
                long key = keys[i];
                int row = rows[i];
                int j = i - 1;
                while (j >= 0 && keys[j] > key)
                {
                    keys[j + 1] = keys[j];
                    rows[j + 1] = rows[j];
                    j--;
                }

                keys[j + 1] = key;
                rows[j + 1] = row;
            }

            return;
        }

        // The keys sorted as their distances from the low one, which are words of a few bits, then given back their low.
        ulong spread = 0;
        for (int i = 0; i < count; i++)
        {
            keys[i] -= low;
            spread |= (ulong)keys[i];
        }

        int bits = 64 - BitOperations.LeadingZeroCount(spread);
        long[] keyScratch = ArrayPool<long>.Shared.Rent(count);
        int[] rowScratch = ArrayPool<int>.Shared.Rent(count);
        try
        {
            Span<int> counts = stackalloc int[RadixSort.Counts];
            Span<ulong> words = MemoryMarshal.Cast<long, ulong>(keys.AsSpan(0, count));
            Span<ulong> spareWords = MemoryMarshal.Cast<long, ulong>(keyScratch.AsSpan(0, count));
            if (RadixSort.Sort(words, rows.AsSpan(0, count), spareWords, rowScratch.AsSpan(0, count), bits, counts))
            {
                spareWords.CopyTo(words);
                rowScratch.AsSpan(0, count).CopyTo(rows);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(keyScratch);
            ArrayPool<int>.Shared.Return(rowScratch);
            for (int i = 0; i < count; i++)
            {
                keys[i] += low;
            }
        }
    }

    /// <summary>The first slice whose end is past <paramref name="key"/>: the first that may hold it or a key above it.</summary>
    private int FirstEndingAfter(long key)
    {
        IReadOnlyList<TimeSlice> slices = _table.Slices;
        int low = 0;
        int high = slices.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (slices[middle].End > key)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    /// <summary>The last slice starting before <paramref name="key"/>, or at it when <paramref name="inclusive"/>; -1 for none.</summary>
    private int LastStartingBefore(long key, bool inclusive)
    {
        IReadOnlyList<TimeSlice> slices = _table.Slices;
        int low = 0;
        int high = slices.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            long start = slices[middle].Start;
            if (start < key || (inclusive && start == key))
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

    /// <summary>
    /// The first entry at or after <paramref name="key"/>, or after it with <paramref name="upper"/>,
    /// from slice <paramref name="slice"/> on: in it, or else the next slice's first, whose keys all lie
    /// past the key.
    /// </summary>
    private async ValueTask<bool> FirstFromAsync(int slice, long key, bool upper, CancellationToken cancellationToken)
    {
        if (slice >= _table.Slices.Count)
        {
            Invalidate();
            return false;
        }

        await MoveToAsync(slice, cancellationToken).ConfigureAwait(false);
        int at = _here.Bound(key, upper);
        if (at < _here.Count)
        {
            _at = at;
            return true;
        }

        return await FirstOfAsync(slice + 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The last entry below <paramref name="key"/>, or at or below it with <paramref name="upper"/>, from slice <paramref name="slice"/> back.</summary>
    private async ValueTask<bool> LastFromAsync(int slice, long key, bool upper, CancellationToken cancellationToken)
    {
        if (slice < 0)
        {
            Invalidate();
            return false;
        }

        await MoveToAsync(slice, cancellationToken).ConfigureAwait(false);
        int at = _here.Bound(key, upper) - 1;
        if (at >= 0)
        {
            _at = at;
            return true;
        }

        return await LastOfAsync(slice - 1, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> FirstOfAsync(int slice, CancellationToken cancellationToken)
    {
        if (slice >= _table.Slices.Count)
        {
            Invalidate();
            return false;
        }

        await MoveToAsync(slice, cancellationToken).ConfigureAwait(false);
        _at = 0;
        return true;
    }

    private async ValueTask<bool> LastOfAsync(int slice, CancellationToken cancellationToken)
    {
        if (slice < 0)
        {
            Invalidate();
            return false;
        }

        await MoveToAsync(slice, cancellationToken).ConfigureAwait(false);
        _at = _here.Count - 1;
        return true;
    }

    private async ValueTask<long> RankAsync(long key, bool upper, CancellationToken cancellationToken)
    {
        int slice = FirstEndingAfter(key);
        if (slice >= _table.Slices.Count)
        {
            return _before[^1];
        }

        // A key at or below the slice's start ranks before all of it, without a read.
        long start = _table.Slices[slice].Start;
        if (key < start || (!upper && key == start))
        {
            return _before[slice];
        }

        Held held = await HeldAsync(slice, cancellationToken).ConfigureAwait(false);
        return _before[slice] + held.Bound(key, upper);
    }

    /// <summary>Makes <paramref name="slice"/> the slice the position is in; the position is the caller's to set.</summary>
    private async ValueTask MoveToAsync(int slice, CancellationToken cancellationToken)
    {
        Held held = await HeldAsync(slice, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(held, _here))
        {
            (_here, _spare) = (_spare, _here);
        }
    }

    /// <summary>The slice ranked: one already held, or read into the one the position is not in.</summary>
    private async ValueTask<Held> HeldAsync(int slice, CancellationToken cancellationToken)
    {
        if (_here.Slice == slice)
        {
            return _here;
        }

        if (_spare.Slice != slice)
        {
            await LoadAsync(_spare, slice, cancellationToken).ConfigureAwait(false);
        }

        return _spare;
    }

    /// <summary>
    /// Ranks the instants of <paramref name="slice"/> into <paramref name="held"/>: from the segments the last
    /// read covered when they hold the slice's rows, and otherwise from a read of the segments that do.
    /// </summary>
    private async ValueTask LoadAsync(Held held, int slice, CancellationToken cancellationToken)
    {
        TimeSlice span = _table.Slices[slice];
        int count = checked((int)span.Rows);
        held.Slice = -1;
        held.Reserve(count);
        if (_segmentStart < 0 || span.FirstRow < _segmentStart || span.FirstRow + count > _segmentStart + _segmentRows)
        {
            await ReadSegmentsAsync(span, cancellationToken).ConfigureAwait(false);
        }

        ReadOnlySpan<long> keys = _segment.AsSpan(checked((int)(span.FirstRow - _segmentStart)), count);
        for (int i = 0; i < count; i++)
        {
            long key = keys[i];
            if (key < span.Start || key >= span.End)
            {
                throw new VortexFormatException(
                    $"Row {span.FirstRow + i} of '{_path}' lies outside its slice [{span.Start}, {span.End}).");
            }

            held.Keys[i] = key;
            held.Rows[i] = i;
        }

        SortByKey(held.Keys, held.Rows, count, span.Start);
        held.Count = count;
        held.Slice = slice;
    }

    /// <summary>
    /// Reads the instants of the rows of the segments <paramref name="span"/> lies in: those a read of the
    /// slice's rows alone would fetch anyway, from the bound at or before its first row to the one at or after
    /// its last, in one scan, so in one round trip when they are several.
    /// </summary>
    private async ValueTask ReadSegmentsAsync(TimeSlice span, CancellationToken cancellationToken)
    {
        (long from, long to) = SegmentsOf(span.FirstRow, span.FirstRow + span.Rows);
        int rows = checked((int)(to - from));
        if (_segment.Length < rows)
        {
            _segment = new long[rows];
        }

        _segmentStart = -1;
        FieldExpr column = Expr.Field(_path);
        long filled = 0;
        await foreach (RecordBatch batch in _file.ScanBuilder()
            .Project(_path)
            .Rows(new RowRange(from, to))
            .WithPruning(false)
            .WithIndexes(false)
            .ExecuteAsync()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            filled += Copy(batch, column, checked((int)(batch.StartRow - from)));
        }

        if (filled != rows)
        {
            throw new VortexFormatException($"The file gives rows [{from}, {to}) of '{_path}' {filled} instants.");
        }

        _segmentStart = from;
        _segmentRows = rows;
    }

    /// <summary>
    /// The rows of the instant column's segments that rows [<paramref name="start"/>, <paramref name="end"/>)
    /// lie in, from the file's layout; those rows alone for an instant below the top level.
    /// </summary>
    private (long From, long To) SegmentsOf(long start, long end)
    {
        int field = _file.DType.Kind == DTypeKind.Struct ? _file.DType.IndexOfField(_path) : -1;
        if (field < 0)
        {
            return (start, end);
        }

        LayoutTree tree = _file.LayoutTree;
        _segments ??= SplitPlan.Compute(tree, new RowRange(0, _before[^1]), FieldMask.Single(field), SplitPlan.NaturalBatchRows(tree));
        long from = 0;
        long to = _before[^1];
        for (int b = 0; b < _segments.BoundaryCount; b++)
        {
            long at = _segments.BoundaryAt(b);
            if (at <= start)
            {
                from = at;
            }

            if (at >= end)
            {
                to = at;
                break;
            }
        }

        return (from, to);
    }

    /// <summary>A batch's instants copied to their place among the segments' rows; refused when one is null.</summary>
    private int Copy(RecordBatch batch, FieldExpr column, int offset)
    {
        CanonicalArena arena = batch.Arena;
        int rows = batch.RowCount;
        CanonicalNode values = arena.GetNode(ComparisonKernels.Unwrap(arena, FilterEvaluator.Resolve(arena, batch.RootIndex, column, rows)));
        if (values.Kind != CanonicalKind.Primitive || values.PType != PType.I64 || offset < 0 || offset + rows > _segment.Length)
        {
            throw new VortexFormatException($"The rows of a slice of '{_path}' do not read as the slice table says.");
        }

        ValidityMask valid = ValidityMask.From(arena, values.Validity);
        for (int i = 0; i < rows; i++)
        {
            if (!valid.IsValid(i))
            {
                throw new VortexFormatException($"Row {batch.StartRow + i} of '{_path}' holds no instant.");
            }
        }

        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(values.Values.Span)[..rows].CopyTo(_segment.AsSpan(offset, rows));
        return rows;
    }

    /// <summary>One slice's instants in key order, and each one's row in the slice.</summary>
    private sealed class Held
    {
        internal int Slice = -1;
        internal long[] Keys = [];
        internal int[] Rows = [];
        internal int Count;

        internal void Reserve(int count)
        {
            if (Keys.Length < count)
            {
                Keys = new long[count];
                Rows = new int[count];
            }

            Count = 0;
        }

        /// <summary>The first position whose key is at or after <paramref name="key"/>, or after it when <paramref name="upper"/>.</summary>
        internal int Bound(long key, bool upper)
        {
            int low = 0;
            int high = Count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                long at = Keys[middle];
                if (at < key || (upper && at == key))
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
    }

    /// <summary>The entries holding the key of one position.</summary>
    private readonly struct Run(Held held, int at) : KeyGallop.IKeyed
    {
        public bool Holds(int index) => held.Keys[index] == held.Keys[at];
    }
}
