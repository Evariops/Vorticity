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
/// </remarks>
internal sealed class SortedSlices : IDisposable
{
    private readonly SliceTable _table;
    private readonly int _instant;
    private readonly DType _schema;
    private readonly IReadOnlyDictionary<int, Types.Variants.VariantShapes>? _shapes;
    private readonly ScanContext _context = new ScanContext([]);
    private readonly List<int> _parts = [];
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

    /// <summary>The slices, sorted in the order the first slice settled: the table's, the shapes placed in it.</summary>
    internal SliceTable Table => _order is { } order && !order.SequenceEqual(_table.Order) ? new SliceTable(_table.Unit, _table.Slices, order) : _table;

    /// <summary>Takes the next rows in time order, writing each slice they complete through <paramref name="write"/>.</summary>
    internal async ValueTask AppendAsync(RecordBatch rows, Func<RecordBatch, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
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
            _parts.Add(_context.Canonical.CopyFrom(part.Arena, part.RootIndex));
            _held += take;
            at += take;
            if (_held == _table.Slices[_slice].Rows)
            {
                await FlushAsync(write, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether every slice of the table was written.</summary>
    internal bool Complete => _slice == _table.Slices.Count && _held == 0;

    public void Dispose() => _context.Dispose();

    /// <summary>Sorts the slice held and writes it, then lets its rows go.</summary>
    private async ValueTask FlushAsync(Func<RecordBatch, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        CanonicalArena arena = _context.Canonical;
        int rows = checked((int)_held);
        int whole = _parts.Count == 1 ? _parts[0] : CanonicalConcat.Concat(_context.Decode, _schema, rows, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_parts));
        int sorted = Sort(arena, whole, rows);
        using (RecordBatch batch = new RecordBatch(arena, sorted, 0))
        {
            await write(batch, cancellationToken).ConfigureAwait(false);
        }

        _parts.Clear();
        _held = 0;
        _slice++;
        _context.ResetBatch();
    }

    /// <summary>
    /// The slice's rows sorted by its synopsis columns, rows of equal values in the order they came,
    /// which is the instant's: the rows arrive in time order.
    /// </summary>
    private int Sort(CanonicalArena arena, int node, int rows)
    {
        List<(int[] Codes, int Distinct)> columns;
        _sortedCodes.Clear();
        if (_order is null && (_shapes is not null || _table.Order.Count > 1))
        {
            // The first slice settles the object's order, and sorts by the codes it measured it with.
            (_order, columns) = Place(arena, node, rows);
        }
        else
        {
            _order ??= [.. _table.Order];
            if (_order.Count == 0)
            {
                return node;
            }

            columns = new List<(int[], int)>(_order.Count);
            foreach (int entry in _order)
            {
                columns.Add(SliceSort.CodesOf(arena, node, entry, rows, _shapes));
            }
        }

        int[] permutation = SliceSort.Permutation(columns, rows);
        bool identity = IsIdentity(permutation);

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

            _sortedCodes.Add(codes);
        }

        return identity ? node : CanonicalFilter.Apply(arena, node, permutation);
    }

    /// <summary>
    /// The codes of each entry of <see cref="Order"/> over the slice being written, in the order its rows are
    /// sorted in, values in their order and nulls last; valid while the slice is written.
    /// </summary>
    internal IReadOnlyList<int[]> SortedCodes => _sortedCodes;

    private readonly List<int[]> _sortedCodes = [];
    private int[] _gathered = [];

    /// <summary>
    /// The rows of a slice the order is measured on, at most: on mixes of columns independent, correlated,
    /// nested and shaped, a sample of one row in 64 of a million gives the order every row gives, in 3 to
    /// 7 ms where every row takes 160 to 430 (`vortex-queries --micro runsorder`). Provisional until real
    /// mixes of logs confirm it.
    /// </summary>
    private const int OrderSampleRows = 16_384;

    /// <summary>
    /// The object's order and each entry's codes over this slice: the table's columns and the shapes that
    /// repeat, ordered greedily by the runs each adds (<see cref="SliceSort.Greedy"/>), measured on a sample.
    /// </summary>
    private (List<int> Order, List<(int[] Codes, int Distinct)> Columns) Place(CanonicalArena arena, int node, int rows)
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
}
