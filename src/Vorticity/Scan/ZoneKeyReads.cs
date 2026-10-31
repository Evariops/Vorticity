using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Scanning;

/// <summary>
/// The top-level columns a scan's consumer reads only through functions of them, a key's truncation of an instant say, and
/// per zone of each whether its zone map sends every such function to one value over its bounds: a split whose zones all
/// do is given each zone's minimum in place of the column, which it then neither reads nor decodes. The functions see the
/// same values either way: each is non-decreasing, so one image at both bounds is its image at every row between them.
/// </summary>
/// <remarks>
/// What a group by on <c>(Endpoint, At.Truncate(Hour))</c> under a filter on <c>Status</c> then reads of <c>At</c>: the
/// zones that straddle an hour, where an object sorted by its emitters and the hour holds an emitter's hour in several
/// zones. The pass names the columns (<see cref="Of"/>), knowing every use of them; a scan leaves out those its own filter
/// reads, and loads the zones of the others once, before its first batch (<see cref="LoadAsync"/>), which the file then
/// keeps for the scans after it (<see cref="TryHeld"/>). The struct at the layout's root gives a split the zones' values
/// (<see cref="TryBuild"/>) and registers none of the column's segments (<see cref="Covers"/>), both asked of the split's
/// rows, so that the two agree.
/// </remarks>
internal sealed class ZoneKeyReads
{
    /// <summary>The asks a file keeps the answer of, past which a new one is answered and not kept.</summary>
    private const int MostHeld = 16;

    private readonly Column?[] _byField;

    private ZoneKeyReads(Column?[] byField) => _byField = byField;

    /// <summary>
    /// The columns every one of <paramref name="uses"/> reads through functions alone, each with those functions, the
    /// columns <paramref name="filter"/> reads left out; null for none. A use of the column itself, of a field in it, or
    /// of a key of a variant leaves the column out.
    /// </summary>
    internal static Request? Of(List<FieldExpr> uses, VortexExpr? filter)
    {
        // Most passes key no column through a function: counted before anything is made, and then made to size, a pass
        // keying one column through one function making four small objects.
        int candidates = 0;
        for (int u = 0; u < uses.Count; u++)
        {
            candidates += uses[u] is FunctionFieldExpr function && ColumnUnder(function) is not null ? 1 : 0;
        }

        if (candidates == 0)
        {
            return null;
        }

        string[] names = new string[candidates];
        FieldExpr[][] functions = new FieldExpr[candidates][];
        int count = 0;
        for (int u = 0; u < uses.Count; u++)
        {
            if (uses[u] is not FunctionFieldExpr function || ColumnUnder(function) is not { } column)
            {
                continue;
            }

            int at = Array.IndexOf(names, column, 0, count);
            if (at < 0)
            {
                names[count] = column;
                functions[count++] = [function];
            }
            else if (!Holds(functions[at], function))
            {
                functions[at] = [.. functions[at], function];
            }
        }

        // A column a use reads whole, or the filter reads, takes no value from its zones.
        int kept = 0;
        for (int c = 0; c < count; c++)
        {
            if (!ReadWhole(uses, names[c]) && (filter is null || !Reads(filter, names[c])))
            {
                names[kept] = names[c];
                functions[kept++] = functions[c];
            }
        }

        if (kept == 0)
        {
            return null;
        }

        if (kept < names.Length)
        {
            Array.Resize(ref names, kept);
            Array.Resize(ref functions, kept);
        }

        return new Request(names, functions);
    }

    /// <summary>The top-level column a function of a function reads, by its name; null for a nested field or another field.</summary>
    private static string? ColumnUnder(FunctionFieldExpr function) =>
        Innermost(function) is ColumnFieldExpr column && column.Segments is null or { Length: 1 } && !column.Path.Contains('.', StringComparison.Ordinal)
            ? column.Path
            : null;

    /// <summary>
    /// What <paramref name="file"/> keeps of <paramref name="request"/> under <paramref name="filter"/>, read
    /// <paramref name="byGranules"/> or not: false when no scan of it loaded that yet, which <see cref="LoadAsync"/> then
    /// does. Nothing is made: the scans after the first find the zones' values as they are.
    /// </summary>
    internal static bool TryHeld(VortexFile file, Request request, VortexExpr? filter, bool byGranules, out ZoneKeyReads? reads)
    {
        if (file.HeldZoneKeys is Held[] held)
        {
            ulong unread = Unread(request, filter);
            foreach (Held entry in held)
            {
                if (entry.ByGranules == byGranules && entry.Unread == unread && entry.Request.Matches(request))
                {
                    reads = entry.Reads;
                    return true;
                }
            }
        }

        reads = null;
        return false;
    }

    /// <summary>
    /// The zones of <paramref name="request"/>'s columns in <paramref name="file"/>, those <paramref name="filter"/> reads
    /// left out: for each top-level column of numbers, or of an extension over them, whose zone map is exact somewhere, the
    /// zones that hold no null, nor a NaN, and whose bounds every function sends to one value; a whole chunk of them but where
    /// the scan reads its chunks <paramref name="byGranules"/>. Null when no zone of any column gives a value. The file keeps
    /// the answer, a null one too, for the scans after this one.
    /// </summary>
    internal static async ValueTask<ZoneKeyReads?> LoadAsync(
        VortexFile file, Request request, VortexExpr? filter, bool byGranules, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        if (TryHeld(file, request, filter, byGranules, out ZoneKeyReads? known))
        {
            return known;
        }

        DType schema = file.DType;
        LayoutNode root = file.LayoutTree.Root;
        ulong unread = Unread(request, filter);
        Column?[]? byField = null;
        if (schema.Kind == DTypeKind.Struct && root.Encoding == LayoutEncodingId.Struct && file.RowCount > 0)
        {
            for (int c = 0; c < request.Names.Length; c++)
            {
                string name = request.Names[c];
                int index = schema.IndexOfField(name);
                if (index < 0 || (unread & (1UL << c)) == 0)
                {
                    continue;
                }

                DType field = schema.GetField(index);
                DType storage = field.Kind == DTypeKind.Extension ? field.StorageType : field;
                if (storage.Kind != DTypeKind.Primitive)
                {
                    continue;
                }

                ZonePruner? zones = (await ZonePruningPlan
                    .PlanAsync(file, file.LayoutTree, Expr.IsNotNull(Expr.Field(name)), cancellationToken, steps: null, metrics, indexes: false)
                    .ConfigureAwait(false)).Zones;
                if (zones?.Column(name) is not { HasStatistics: true } map || map.RowCount != file.RowCount)
                {
                    continue;
                }

                long[]? chunks = byGranules ? null : ChunksOf(root.GetChild(index + (schema.IsNullable ? 1 : 0)));
                if (Column.Of(field, storage, map, request.Functions[c], chunks) is { } column)
                {
                    (byField ??= new Column?[schema.FieldCount])[index] = column;
                }
            }
        }

        ZoneKeyReads? reads = byField is null ? null : new ZoneKeyReads(byField);
        Keep(file, new Held(request, byGranules, unread, reads));
        return reads;
    }

    /// <summary>Whether field <paramref name="field"/> of the root takes its zones' values over <paramref name="rows"/>, its segments not read.</summary>
    internal bool Covers(int field, RowRange rows) => At(field) is { } column && column.Covers(rows);

    /// <summary>
    /// Field <paramref name="field"/> of the root over <paramref name="rows"/>, the split's rows, as its zones give it: one
    /// value a zone, the minimum, over the rows of the zone the split wants; false when a zone of them gives none.
    /// </summary>
    internal bool TryBuild(int field, ScanContext context, RowRange rows, int length, out int node)
    {
        if (At(field) is not { } column || !column.Covers(rows))
        {
            node = -1;
            return false;
        }

        node = column.Build(context, rows, length);
        return true;
    }

    private Column? At(int field) => (uint)field < (uint)_byField.Length ? _byField[field] : null;

    /// <summary>Keeps <paramref name="entry"/> beside what <paramref name="file"/> holds, the array replaced whole; past <see cref="MostHeld"/>, not kept.</summary>
    private static void Keep(VortexFile file, Held entry)
    {
        while (true)
        {
            Held[]? seen = file.HeldZoneKeys as Held[];
            if (seen is { Length: >= MostHeld })
            {
                return;
            }

            Held[] next = seen is null ? [entry] : [.. seen, entry];
            if (file.ExchangeHeldZoneKeys(next, seen))
            {
                return;
            }
        }
    }

    /// <summary>The columns of <paramref name="request"/> the filter does not read, a bit each by their place; at most 64.</summary>
    private static ulong Unread(Request request, VortexExpr? filter)
    {
        ulong unread = 0;
        for (int c = 0; c < request.Names.Length && c < 64; c++)
        {
            if (filter is null || !Reads(filter, request.Names[c]))
            {
                unread |= 1UL << c;
            }
        }

        return unread;
    }

    /// <summary>Whether <paramref name="filter"/> reads column <paramref name="name"/>, walked without a list.</summary>
    private static bool Reads(VortexExpr filter, string name)
    {
        Seeker seeker = new Seeker(name);
        return !ScanBuilder.VisitFields(filter, ref seeker);
    }

    /// <summary>Whether <paramref name="field"/> reads column <paramref name="name"/>: its first name, or the first of its path.</summary>
    private static bool Names(FieldExpr field, string name)
    {
        if (field is VariantFieldExpr variant)
        {
            field = variant.Column;
        }

        if (field.Segments is { Length: > 0 } segments)
        {
            return string.Equals(segments[0], name, StringComparison.Ordinal);
        }

        string path = field.Path;
        return path.StartsWith(name, StringComparison.Ordinal) && (path.Length == name.Length || path[name.Length] == '.');
    }

    /// <summary>Whether a use other than a function of it reads column <paramref name="name"/>.</summary>
    private static bool ReadWhole(List<FieldExpr> uses, string name)
    {
        for (int u = 0; u < uses.Count; u++)
        {
            if (uses[u] is not FunctionFieldExpr && Names(uses[u], name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="functions"/> holds the same functions of the same column as <paramref name="function"/>.</summary>
    private static bool Holds(FieldExpr[] functions, FieldExpr function)
    {
        for (int f = 0; f < functions.Length; f++)
        {
            if (Same(functions[f], function))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether two functions of a column are the same functions of the same column, innermost last.</summary>
    private static bool Same(FieldExpr left, FieldExpr right)
    {
        while (left is FunctionFieldExpr outer && right is FunctionFieldExpr other)
        {
            if (!string.Equals(outer.Function.Text, other.Function.Text, StringComparison.Ordinal) || outer.Function.Storage != other.Function.Storage)
            {
                return false;
            }

            left = outer.Column;
            right = other.Column;
        }

        return left is ColumnFieldExpr && right is ColumnFieldExpr && string.Equals(left.Path, right.Path, StringComparison.Ordinal);
    }

    /// <summary>The rows each chunk of a column's layout holds, as boundaries: one chunk for a layout that has none.</summary>
    private static long[] ChunksOf(LayoutNode node)
    {
        while (node.Encoding == LayoutEncodingId.Zoned && node.ChildCount > 0)
        {
            node = node.GetChild(0);
        }

        return node.Encoding == LayoutEncodingId.Chunked ? node.ChunkOffsets.ToArray() : [0, node.RowCount];
    }

    /// <summary>The column under a function of a function of it.</summary>
    private static FieldExpr Innermost(FunctionFieldExpr function)
    {
        FieldExpr inner = function.Column;
        while (inner is FunctionFieldExpr next)
        {
            inner = next.Column;
        }

        return inner;
    }

    /// <summary>The columns a pass reads through functions alone, each with its functions, by their places.</summary>
    internal sealed class Request(string[] names, FieldExpr[][] functions)
    {
        internal string[] Names { get; } = names;

        internal FieldExpr[][] Functions { get; } = functions;

        /// <summary>Whether <paramref name="other"/> names the same columns through the same functions, in the same order.</summary>
        internal bool Matches(Request other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (other.Names.Length != Names.Length)
            {
                return false;
            }

            for (int c = 0; c < Names.Length; c++)
            {
                if (!string.Equals(Names[c], other.Names[c], StringComparison.Ordinal) || Functions[c].Length != other.Functions[c].Length)
                {
                    return false;
                }

                for (int f = 0; f < Functions[c].Length; f++)
                {
                    if (!Same(Functions[c][f], other.Functions[c][f]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>One ask a file keeps the answer of: the columns, how the scan reads its chunks, those its filter leaves unread, and what the zones give.</summary>
    private sealed record Held(Request Request, bool ByGranules, ulong Unread, ZoneKeyReads? Reads);

    /// <summary>A walk of a filter's columns that stops at the first that reads a named column.</summary>
    private readonly struct Seeker(string name) : ScanBuilder.IFieldVisitor
    {
        public bool Visit(FieldExpr field) => !Names(field, name);
    }

    /// <summary>One column's zones: which give one value through its functions, and the value each gives.</summary>
    private sealed class Column
    {
        /// <summary>The zones of a split whose runs are found on the stack: a split is a zone or a few.</summary>
        private const int StackZones = 64;

        private readonly DType _dtype;
        private readonly DType _storage;
        private readonly PType _ptype;
        private readonly int _width;
        private readonly long _zoneLength;
        private readonly long _rows;
        private readonly ulong[] _settled;
        private readonly byte[] _values;

        private Column(DType dtype, DType storage, long zoneLength, long rows, ulong[] settled, byte[] values)
        {
            _dtype = dtype;
            _storage = storage;
            _ptype = storage.PType;
            _width = _ptype.ByteWidth();
            _zoneLength = zoneLength;
            _rows = rows;
            _settled = settled;
            _values = values;
        }

        /// <summary>
        /// The column whose zones are <paramref name="map"/>, or null when none of them gives one value. Under
        /// <paramref name="chunks"/>, the column's chunks read whole, a zone gives its value only when every zone of its
        /// chunk does: a chunk that one split reads is read whole, and the splits between that took their zones' values
        /// would leave it to be read again by the next.
        /// </summary>
        internal static Column? Of(DType dtype, DType storage, ZoneColumn map, FieldExpr[] functions, long[]? chunks)
        {
            int zones = map.ZoneCount;
            int width = storage.PType.ByteWidth();
            ulong[] settled = new ulong[(zones + 63) / 64];
            byte[] values = new byte[zones * width];
            for (int zone = 0; zone < zones; zone++)
            {
                ZoneBounds bounds = map.Bounds(zone);
                if (!bounds.HasNullCount || bounds.NullCount != 0 || !bounds.IsExact || !bounds.HasMin || !bounds.HasMax
                    || (storage.PType.IsFloat() && (!bounds.HasNanCount || bounds.NanCount != 0))
                    || !Array.TrueForAll(functions, function =>
                        ZoneSettling.TryMap(function, bounds.Min, out FilterLiteral low)
                        && ZoneSettling.TryMap(function, bounds.Max, out FilterLiteral high)
                        && ZoneSettling.Same(low, high)))
                {
                    continue;
                }

                ZoneMapWriter.Write(values.AsSpan(zone * width, width), storage.PType, bounds.Min);
                settled[zone >> 6] |= 1UL << (zone & 63);
            }

            Column column = new Column(dtype, storage, map.ZoneLength, map.RowCount, settled, values);
            if (chunks is not null)
            {
                for (int chunk = 0; chunk + 1 < chunks.Length; chunk++)
                {
                    RowRange rows = new RowRange(chunks[chunk], chunks[chunk + 1]);
                    if (!rows.IsEmpty && !column.Covers(rows))
                    {
                        column.Unsettle(rows);
                    }
                }
            }

            return Array.Exists(settled, word => word != 0) ? column : null;
        }

        /// <summary>The zones <paramref name="rows"/> holds a row of, given no value.</summary>
        private void Unsettle(RowRange rows)
        {
            int last = (int)((rows.End - 1) / _zoneLength);
            for (int zone = (int)(rows.Start / _zoneLength); zone <= last; zone++)
            {
                _settled[zone >> 6] &= ~(1UL << (zone & 63));
            }
        }

        /// <summary>Whether every zone <paramref name="rows"/> holds a row of gives one value.</summary>
        internal bool Covers(RowRange rows)
        {
            if (rows.IsEmpty || rows.End > _rows)
            {
                return false;
            }

            int last = (int)((rows.End - 1) / _zoneLength);
            for (int zone = (int)(rows.Start / _zoneLength); zone <= last; zone++)
            {
                if ((_settled[zone >> 6] & (1UL << (zone & 63))) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The column over the split's rows: a constant of one zone's value, or runs of each zone's over the rows the split
        /// wants of it, its selection's when one is in force.
        /// </summary>
        internal int Build(ScanContext context, RowRange rows, int length)
        {
            CanonicalArena arena = context.Canonical;
            int first = (int)(rows.Start / _zoneLength);
            int last = (int)((rows.End - 1) / _zoneLength);
            Validity valid = _storage.IsNullable ? Validity.AllValid : Validity.NonNullable;
            int node;
            if (first == last || length == 0)
            {
                node = arena.AddConstant(_storage, length, valid, _values.AsSpan(first * _width, _width));
            }
            else
            {
                // A run a zone the split wants a row of, each ending where the rows wanted of its zone do.
                int zones = last - first + 1;
                VortexBuffer endsBuffer = arena.AllocateUninitialized(zones * sizeof(uint), sizeof(uint), out Span<byte> endBytes);
                Span<uint> ends = MemoryMarshal.Cast<byte, uint>(endBytes);
                int[]? rented = zones <= StackZones ? null : System.Buffers.ArrayPool<int>.Shared.Rent(zones);
                Span<int> kept = rented is null ? stackalloc int[StackZones] : rented;
                try
                {
                    int runs = Ends(context, rows, first, ends, kept);
                    if (runs == 1)
                    {
                        node = arena.AddConstant(_storage, length, valid, _values.AsSpan(kept[0] * _width, _width));
                    }
                    else
                    {
                        VortexBuffer valuesBuffer = arena.AllocateUninitialized(runs * _width, 64, out Span<byte> values);
                        for (int run = 0; run < runs; run++)
                        {
                            _values.AsSpan(kept[run] * _width, _width).CopyTo(values[(run * _width)..]);
                        }

                        int byRun = arena.AddPrimitive(_storage, runs, valid, _ptype, valuesBuffer);
                        node = arena.AddRunEnd(_storage, length, valid, endsBuffer.Slice(0, runs * sizeof(uint)), byRun);
                    }
                }
                finally
                {
                    if (rented is not null)
                    {
                        System.Buffers.ArrayPool<int>.Shared.Return(rented);
                    }
                }
            }

            return _dtype.Kind == DTypeKind.Extension ? arena.AddExtension(_dtype, length, node) : node;
        }

        /// <summary>
        /// The end of each run, in the rows the split delivers, and the zone it holds, a zone of no wanted row given none:
        /// the zones' bounds over the rows, or over the selection's rows, which ascend. Returns the runs.
        /// </summary>
        private int Ends(ScanContext context, RowRange rows, int first, Span<uint> ends, Span<int> zones)
        {
            int runs = 0;
            if (!context.HasSelection)
            {
                for (int zone = first; zone * _zoneLength < rows.End; zone++)
                {
                    zones[runs] = zone;
                    ends[runs++] = (uint)(Math.Min((zone + 1) * _zoneLength, rows.End) - rows.Start);
                }

                return runs;
            }

            long origin = context.SelectionBase;
            ReadOnlySpan<int> selected = context.Selection;
            for (int i = 0; i < selected.Length; i++)
            {
                int zone = (int)((origin + selected[i]) / _zoneLength);
                if (runs > 0 && zones[runs - 1] == zone)
                {
                    ends[runs - 1]++;
                    continue;
                }

                zones[runs] = zone;
                ends[runs] = runs == 0 ? 1u : ends[runs - 1] + 1;
                runs++;
            }

            return runs;
        }
    }
}
