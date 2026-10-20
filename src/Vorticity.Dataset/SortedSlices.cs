using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of one object, arriving in time order, written slice by slice, each slice sorted by the
/// object's synopsis columns, then by its instant, as its slice table says. A slice's rows are gathered,
/// their keys encoded in the order the table names, the slice's rows sorted by those keys alone, and
/// every column gathered once through the permutation.
/// </summary>
/// <remarks>
/// <para>
/// A key is the row encoding of the synopsis columns and the instant, which sorts as the tuple does:
/// a column's values by their own order, nulls last, the codes a dictionary assigned in the order of its
/// values compared in the same order. Rows of equal keys keep the order they came in, so the same rows
/// sort the same way whoever writes them.
/// </para>
/// <para>
/// A slice is held whole while it sorts, at most twice the slice target's rows, which a burst bounds.
/// </para>
/// <para>
/// The table's columns are the object's synopsis columns, which repeat over its rows; their order is the
/// first slice's to settle. It orders them greedily by the runs each adds after those before it, measured
/// on a sample of the slice, which comes back to the order of their numbers of distinct values when they
/// are independent and puts a column nested in another right after it. A variant column the object
/// shreds adds its rows' shape (<see cref="Types.Variants.VariantShapes"/>), known only once the object has
/// chosen its paths, after the table was made, when its shapes repeat twice on average in the slice. The
/// order is then the object's for every slice, and <see cref="Table"/> records it.
/// </para>
/// <para>
/// A slice is written on another thread while the next one's rows come in and sort, each in a buffer of its own:
/// two slices are held at most, and a slice's write starts once the one before it is written, so the writes
/// keep the slices' order. <see cref="DrainAsync"/> waits for the last one.
/// </para>
/// </remarks>
internal sealed class SortedSlices : IDisposable
{
    private readonly SliceTable _table;
    private readonly int _instant;
    private readonly DType _schema;
    private readonly IReadOnlyDictionary<int, Types.Variants.VariantShapes>? _shapes;
    private readonly Buffer[] _buffers = [new Buffer(), new Buffer()];
    private int _filling;
    private Task? _writing;
    private List<int>? _order;
    private int _slice;
    private long _held;

    /// <summary>
    /// A writer of the slices <paramref name="table"/> lists, the instant field <paramref name="instant"/> of
    /// <paramref name="schema"/>, the shapes of the variant fields <paramref name="shapes"/> holds a coder for
    /// placed in the order by its first slice.
    /// </summary>
    internal SortedSlices(SliceTable table, int instant, DType schema, IReadOnlyDictionary<int, Types.Variants.VariantShapes>? shapes = null)
    {
        _table = table;
        _instant = instant;
        _schema = schema;
        _shapes = shapes is { Count: > 0 } ? shapes : null;
    }

    /// <summary>The order the slices are sorted in: the table's until the first slice settles it.</summary>
    internal IReadOnlyList<int> Order => _order ?? (IReadOnlyList<int>)_table.Order;

    /// <summary>The slices, sorted in the order the first slice settled: the table's, the shapes and paths placed in it.</summary>
    internal SliceTable Table => _order is { } order && !order.SequenceEqual(_table.Order)
        ? new SliceTable(_table.Unit, _table.Slices, order, _table.Paths)
        : _table;

    // Which of the table's paths each slice reads, once the first slice settled the order; every one before.
    private bool[]? _wanted;

    /// <summary>
    /// The paths left in the residual whose values repeat, which the first slice found: the synopsis counts
    /// their values per hour rather than sort by them.
    /// </summary>
    internal IReadOnlyList<int> CountedPaths => _counted;

    private readonly List<int> _counted = [];

    /// <summary>
    /// Takes the next rows in time order, writing each slice they complete through <paramref name="write"/>, on another
    /// thread while the rows after it come in; the last slice is written once <see cref="DrainAsync"/> completes.
    /// </summary>
    internal async ValueTask AppendAsync(RecordBatch rows, Func<SortedSlice, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        int at = 0;
        while (at < rows.RowCount)
        {
            if (_slice >= _table.Slices.Count)
            {
                throw new InvalidOperationException("Rows came past the last slice of the object's slice table.");
            }

            long wanted = _table.Slices[_slice].Rows - _held;
            int take = (int)Math.Min(wanted, rows.RowCount - at);
            RecordBatch part = at == 0 && take == rows.RowCount ? rows : rows.Window(at, take);
            Buffer buffer = _buffers[_filling];
            buffer.Parts.Add(buffer.Context.Canonical.CopyFrom(part.Arena, part.RootIndex));
            _held += take;
            at += take;
            if (_held == _table.Slices[_slice].Rows)
            {
                await FlushAsync(write, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether every slice of the table was handed to its write; <see cref="DrainAsync"/> says when the last is written.</summary>
    internal bool Complete => _slice == _table.Slices.Count && _held == 0;

    /// <summary>Waits for the slice being written, and throws what its write threw.</summary>
    internal async ValueTask DrainAsync()
    {
        if (_writing is { } writing)
        {
            _writing = null;
            await writing.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lets the buffers go, once the slice being written, if any, is done with them: a caller that gives the object up
    /// rather than drains it has its reason already, so what that write threw is observed and dropped.
    /// </summary>
    public void Dispose()
    {
        if (_writing is { } writing)
        {
            _writing = null;
            ((IAsyncResult)writing).AsyncWaitHandle.WaitOne();
            _ = writing.Exception;
        }

        foreach (Buffer buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Sorts the slice held and hands it to <paramref name="write"/> on another thread once the slice before it is
    /// written; the rows after it go to the other buffer meanwhile.
    /// </summary>
    private async ValueTask FlushAsync(Func<SortedSlice, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        Buffer buffer = _buffers[_filling];
        CanonicalArena arena = buffer.Context.Canonical;
        int rows = checked((int)_held);
        int whole = buffer.Parts.Count == 1
            ? buffer.Parts[0]
            : CanonicalConcat.Concat(buffer.Context.Decode, _schema, rows, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer.Parts));
        int sorted = Sort(buffer, whole, rows);

        // The slice before is written first, and its buffer, the one the next rows go to, let go.
        await DrainAsync().ConfigureAwait(false);
        _writing = Task.Run(async () =>
        {
            try
            {
                using RecordBatch batch = new RecordBatch(arena, sorted, 0);
                await write(new SortedSlice(batch, buffer.Codes, buffer.PathColumns), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                buffer.Clear();
            }
        }, CancellationToken.None);
        _held = 0;
        _slice++;
        _filling ^= 1;
    }

    /// <summary>
    /// The slice's rows sorted by its synopsis columns, rows of equal values in the order they came,
    /// which is the instant's: the rows arrive in time order.
    /// </summary>
    private int Sort(Buffer buffer, int node, int rows)
    {
        CanonicalArena arena = buffer.Context.Canonical;
        List<(int[] Codes, int Distinct)> columns;
        buffer.Codes.Clear();
        buffer.PathColumns.Clear();

        // The paths' values, every path's on the first slice, then those the order and the synopsis read.
        int[] paths = ReadPaths(buffer, node);
        if (_order is null && (_shapes is not null || _table.Order.Count > 1 || _table.Paths.Count > 0))
        {
            // The first slice settles the object's order, and sorts by the codes it measured it with.
            (_order, columns) = Place(arena, node, rows, paths);
            _wanted = new bool[_table.Paths.Count];
            for (int p = 0; p < _wanted.Length; p++)
            {
                _wanted[p] = _order.Contains(SynopsisPath.Entry(p)) || _counted.Contains(SynopsisPath.Entry(p))
                    || _table.Paths[p].Class is Types.Variants.VariantClass.Integer or Types.Variants.VariantClass.Double;
            }
        }
        else
        {
            _order ??= [.. _table.Order];
            columns = new List<(int[], int)>(_order.Count);
            foreach (int entry in _order)
            {
                columns.Add(SynopsisPath.Names(entry)
                    ? SliceSort.CodesOfColumn(arena, paths[SynopsisPath.Place(entry)], rows)
                    : SliceSort.CodesOf(arena, node, entry, rows, _shapes));
            }
        }

        int[]? permutation = columns.Count == 0 ? null : SliceSort.Permutation(columns, rows);
        bool identity = permutation is null || IsIdentity(permutation);

        // Each path's values, in the order the rows are sorted in.
        foreach (int column in paths)
        {
            buffer.PathColumns.Add(column < 0 || identity ? column : CanonicalFilter.Apply(arena, column, permutation!));
        }

        if (permutation is null)
        {
            return node;
        }

        // Each entry's codes, in the order the rows are sorted in: what the slice's synopsis groups by.
        if (_gathered.Length < rows)
        {
            _gathered = new int[rows];
        }

        foreach ((int[] codes, int _) in columns)
        {
            if (!identity)
            {
                for (int i = 0; i < rows; i++)
                {
                    _gathered[i] = codes[permutation[i]];
                }

                _gathered.AsSpan(0, rows).CopyTo(codes);
            }

            buffer.Codes.Add(codes);
        }

        return identity ? node : CanonicalFilter.Apply(arena, node, permutation);
    }

    private int[] _gathered = [];

    /// <summary>
    /// The rows of a slice the order is measured on, at most: on mixes of columns independent, correlated,
    /// nested and shaped, a sample of one row in 64 of a million gives the order every row gives, in 3 to
    /// 7 ms where every row takes 160 to 430 (`vortex-queries --micro runsorder`). Provisional until real
    /// mixes of logs confirm it.
    /// </summary>
    private const int OrderSampleRows = 16_384;

    /// <summary>
    /// The column of each of the table's paths over the slice's rows <paramref name="node"/>, read in one pass
    /// of each variant column: every path before the order is settled, then those the order or the synopsis
    /// read, -1 for another.
    /// </summary>
    private int[] ReadPaths(Buffer buffer, int node)
    {
        CanonicalArena arena = buffer.Context.Canonical;
        IReadOnlyList<SynopsisPath> paths = _table.Paths;
        int[] columns = new int[paths.Count];
        columns.AsSpan().Fill(-1);
        if (paths.Count == 0)
        {
            return columns;
        }

        Dictionary<int, List<int>> byField = [];
        for (int p = 0; p < paths.Count; p++)
        {
            if (_wanted?[p] ?? true)
            {
                if (!byField.TryGetValue(paths[p].Field, out List<int>? places))
                {
                    byField[paths[p].Field] = places = [];
                }

                places.Add(p);
            }
        }

        CanonicalNode root = arena.GetNode(node);
        foreach ((int field, List<int> places) in byField)
        {
            (string, Types.Variants.VariantClass)[] keys = [.. places.Select(p => (paths[p].Key, paths[p].Class))];
            int[] built = Writing.VariantShredder.KeyColumns(arena, buffer.Context.Types, root.GetFieldIndex(field), keys);
            for (int i = 0; i < places.Count; i++)
            {
                columns[places[i]] = built[i];
            }
        }

        return columns;
    }

    /// <summary>
    /// The object's order and each entry's codes over this slice: the table's columns, the shapes that repeat
    /// and the paths whose values repeat among the rows that hold them, ordered greedily by the runs each adds
    /// (<see cref="SliceSort.Greedy"/>), measured on a sample.
    /// </summary>
    private (List<int> Order, List<(int[] Codes, int Distinct)> Columns) Place(CanonicalArena arena, int node, int rows, int[] paths)
    {
        List<int> entries = [.. _table.Order];
        List<(int[] Codes, int Distinct)> candidates = [];
        foreach (int entry in entries)
        {
            candidates.Add(SliceSort.CodesOf(arena, node, entry, rows, _shapes));
        }

        List<int> fields = [.. _shapes?.Keys ?? []];
        fields.Sort();
        foreach (int field in fields)
        {
            // Shapes that do not repeat twice on average are no synopsis column, as a column's values.
            (int[] Codes, int Distinct) shapes = SliceSort.CodesOf(arena, node, -field - 1, rows, _shapes);
            if (shapes.Distinct * 2L <= rows)
            {
                entries.Add(-field - 1);
                candidates.Add(shapes);
            }
        }

        for (int p = 0; p < paths.Length; p++)
        {
            // A path whose values repeat twice on average among the rows that hold it, as a column's must;
            // a null codes after every value, so the rows held are those coded below the distinct count. A
            // path left in the residual keys its value counts instead, since its runs could not be read.
            (int[] Codes, int Distinct) values = SliceSort.CodesOfColumn(arena, paths[p], rows);
            long held = 0;
            foreach (int code in values.Codes)
            {
                held += code < values.Distinct ? 1 : 0;
            }

            if (values.Distinct > 0 && values.Distinct * 2L <= held)
            {
                if (_table.Paths[p].Residual)
                {
                    _counted.Add(SynopsisPath.Entry(p));
                    continue;
                }

                entries.Add(SynopsisPath.Entry(p));
                candidates.Add(values);
            }
        }

        int[] greedy = SliceSort.Greedy(candidates, rows, Math.Max(1, rows / OrderSampleRows));
        List<int> order = [];
        List<(int[] Codes, int Distinct)> columns = [];
        foreach (int k in greedy)
        {
            order.Add(entries[k]);
            columns.Add(candidates[k]);
        }

        return (order, columns);
    }

    private static bool IsIdentity(ReadOnlySpan<int> permutation)
    {
        for (int i = 0; i < permutation.Length; i++)
        {
            if (permutation[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A slice's rows as they come and sort, and what its write reads beside them: its parts copied in, then each
    /// entry's codes and each path's column in the order the rows are sorted in.
    /// </summary>
    private sealed class Buffer : IDisposable
    {
        internal ScanContext Context { get; } = new ScanContext([]);

        internal List<int> Parts { get; } = [];

        internal List<int[]> Codes { get; } = [];

        internal List<int> PathColumns { get; } = [];

        /// <summary>Lets the slice's rows go, for the next slice's.</summary>
        internal void Clear()
        {
            Parts.Clear();
            Codes.Clear();
            PathColumns.Clear();
            Context.ResetBatch();
        }

        public void Dispose() => Context.Dispose();
    }
}

/// <summary>
/// A slice sorted, as its write reads it: its rows, each entry of the order's codes over them in the order they are
/// sorted in, values in their order and nulls last, and the column of each of the table's paths in the rows' arena,
/// those the order places and the numeric ones the synopsis sums, -1 for another; valid while the slice is written.
/// </summary>
internal readonly record struct SortedSlice(RecordBatch Batch, IReadOnlyList<int[]> Codes, IReadOnlyList<int> PathColumns);
