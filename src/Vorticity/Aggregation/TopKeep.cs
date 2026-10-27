using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The best rows of an order met so far, each its value and its place in the source (spec G, 2): at most twice the rows
/// kept, trimmed back to them by a partial sort on the chain (value, place), the value ranked as <see cref="ColumnOrder"/>
/// ranks it, a null last whichever the direction, a tie to the earlier place. Once a trim has set the edge, the last row
/// kept, a row that does not beat it is passed over before it is copied; the edge is one row in an arena of its own.
/// </summary>
/// <remarks>
/// Rows may come in any order of their places: a reader that brings the best granules first fills the top sooner and
/// copies fewer rows. What it holds, its rows' values and places, is reserved under the query's memory.
/// </remarks>
internal sealed class TopKeep : IDisposable
{
    /// <summary>The rows a trim waits for past the kept ones at least, so that a small top is not trimmed at each batch.</summary>
    private const int LeastSlack = 4_096;

    private readonly VortexSession _session;
    private readonly VortexType _type;
    private readonly bool _descending;
    private readonly int _keep;
    private readonly QueryMemory? _memory;
    private readonly DType _dtype;
    private readonly DType _placeType;
    private StructStore _store;
    private CanonicalArena _arena;
    private StructStore? _edgeStore;
    private CanonicalArena? _edgeArena;
    private ColumnOrder? _edge;
    private int _edgeRoot;
    private long _edgePlace;
    private int[] _rows = [];
    private long _held;

    /// <param name="session">The session whose pool the rows are held in.</param>
    /// <param name="type">The type of the order's column.</param>
    /// <param name="descending">Whether the largest comes first.</param>
    /// <param name="keep">The rows kept, at least one.</param>
    /// <param name="memory">What the rows held are reserved under; null for none.</param>
    internal TopKeep(VortexSession session, VortexType type, bool descending, int keep, QueryMemory? memory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);
        _session = session;
        _type = type;
        _descending = descending;
        _keep = keep;
        _memory = memory;
        DTypeArena types = new DTypeArena();
        _dtype = VortexTypes.ToDType(VortexSchema.Create([new VortexField("value", type), new VortexField("place", VortexType.Int64)]), types);
        _placeType = VortexTypes.ToDType(VortexType.Int64, types);
        (_store, _arena) = Create();
    }

    /// <summary>
    /// The value of the last row kept, once the top holds every row it keeps: what a reader may push into its filter, a row
    /// past it entering no more. Null before, or when that value is null, or of a type no filter compares.
    /// </summary>
    internal FilterLiteral? Edge =>
        _edge is not null && LiteralReader.TryRead(_edgeArena!, _edgeArena!.GetNode(_edgeRoot).GetFieldIndex(0), 0, out FilterLiteral edge) ? edge : null;

    /// <summary>
    /// Adds the rows of <paramref name="node"/>, the order's column of a batch whose row <c>i</c> lies at place
    /// <paramref name="startRow"/> + <c>i</c>, that <paramref name="selection"/> keeps (every row when empty) and that may
    /// enter the top.
    /// </summary>
    internal void Add(CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> selection, long startRow, CancellationToken cancellationToken)
    {
        Scratch.Grow(ref _rows, rows);
        ColumnOrder? order = _edge is null ? null : ColumnOrder.For(arena, node, _type, _descending);
        int count = 0;
        for (int row = 0; row < rows; row++)
        {
            if (!selection.IsEmpty && ((selection[row >> 6] >> (row & 63)) & 1) == 0)
            {
                continue;
            }

            if (order is not null)
            {
                int against = order.CompareAcross(row, _edge!, 0);
                if (against > 0 || (against == 0 && startRow + row > _edgePlace))
                {
                    continue;
                }
            }

            _rows[count++] = row;
        }

        order?.Release();
        if (count == 0)
        {
            return;
        }

        int values = count == rows ? node : CanonicalFilter.Apply(arena, node, _rows.AsSpan(0, count));
        Buffers.VortexBuffer placesBuffer = arena.AllocateUninitialized(count * sizeof(long), sizeof(long), out Span<byte> placeBytes);
        Span<long> places = MemoryMarshal.Cast<byte, long>(placeBytes);
        for (int i = 0; i < count; i++)
        {
            places[i] = startRow + _rows[i];
        }

        int placeNode = arena.AddPrimitive(_placeType, count, Validity.NonNullable, PType.I64, placesBuffer);
        Span<int> children = [values, placeNode];
        int root = arena.AddStruct(_dtype, count, Validity.NonNullable, children);
        StoreRows.Append(_store, arena, root);
        _store.Commit();
        Reserve();
        if (_store.Rows >= (2 * _keep) + LeastSlack)
        {
            Trim(cancellationToken);
        }
    }

    /// <summary>The places of the rows kept, ascending: the best <c>keep</c> rows met, fewer when fewer were.</summary>
    internal long[] Places(CancellationToken cancellationToken)
    {
        if (_store.Rows > _keep)
        {
            Trim(cancellationToken);
        }

        int rows = _store.Rows;
        _arena.ResetKeepingBlocks();
        int root = _store.Build(_arena, rows);
        long[] places = new long[rows];
        _arena.GetNode(_arena.GetNode(root).GetFieldIndex(1)).Values.Cast<long>()[..rows].CopyTo(places);
        Array.Sort(places);
        return places;
    }

    public void Dispose()
    {
        _store.Release();
        _arena.Reset();
        _edgeStore?.Release();
        _edgeArena?.Reset();
        _edge?.Release();
        _memory?.LetGo(_held);
        _held = 0;
    }

    /// <summary>The store's rows ranked, the best <c>keep</c> kept in a store of their own; the last of them the edge.</summary>
    private void Trim(CancellationToken cancellationToken)
    {
        int rows = _store.Rows;
        _arena.ResetKeepingBlocks();
        int root = _store.Build(_arena, rows);
        CanonicalNode built = _arena.GetNode(root);
        ColumnOrder values = ColumnOrder.For(_arena, built.GetFieldIndex(0), _type, _descending);
        ColumnOrder places = ColumnOrder.For(_arena, built.GetFieldIndex(1), VortexType.Int64, descending: false);
        Scratch.Grow(ref _rows, rows);
        for (int i = 0; i < rows; i++)
        {
            _rows[i] = i;
        }

        int kept = GroupSort.Sort(_rows, rows, new ChainOrder([values, places]), _keep, _memory, cancellationToken);
        values.Release();
        places.Release();
        int gathered = CanonicalFilter.Apply(_arena, root, _rows.AsSpan(0, kept));
        (StructStore store, CanonicalArena arena) = Create();
        StoreRows.Append(store, _arena, gathered);
        store.Commit();
        if (kept == _keep)
        {
            SetEdge(_arena, gathered, kept - 1);
        }

        _store.Release();
        _arena.Reset();
        (_store, _arena) = (store, arena);
        Reserve();
    }

    /// <summary>Row <paramref name="row"/> of <paramref name="root"/>, the last row kept, copied apart as the edge.</summary>
    private void SetEdge(CanonicalArena arena, int root, int row)
    {
        _edge?.Release();
        _edgeStore?.Release();
        _edgeArena?.Reset();
        VortexSessionOptions options = _session.Options;
        _edgeStore = (StructStore)ColumnStores.Create(_dtype, options.EnginePool, options.Extensions);
        _edgeArena = new CanonicalArena(8, options.EnginePool);
        StoreRows.Append(_edgeStore, arena, CanonicalFilter.Apply(arena, root, [row]));
        _edgeStore.Commit();
        _edgeRoot = _edgeStore.Build(_edgeArena, 1);
        CanonicalNode edge = _edgeArena.GetNode(_edgeRoot);
        _edge = ColumnOrder.For(_edgeArena, edge.GetFieldIndex(0), _type, _descending);
        _edgePlace = _edgeArena.GetNode(edge.GetFieldIndex(1)).Values.Cast<long>()[0];
    }

    private (StructStore Store, CanonicalArena Arena) Create()
    {
        VortexSessionOptions options = _session.Options;
        return ((StructStore)ColumnStores.Create(_dtype, options.EnginePool, options.Extensions), new CanonicalArena(64, options.EnginePool));
    }

    /// <summary>What the store holds, reserved under the query's memory: its buffers grown by doubling.</summary>
    private void Reserve()
    {
        long reserved = 2 * _store.CommittedBytes;
        if (reserved > _held)
        {
            _memory?.Hold(reserved - _held, "top rows of an order");
        }
        else
        {
            _memory?.LetGo(_held - reserved);
        }

        _held = reserved;
    }
}
