using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>Kills the blocks a locating index proves cannot hold an equality's value.</summary>
/// <remarks>
/// It runs in the scan's block-mask chain, after the zone maps and the Bloom filters. Where a Bloom
/// filter says "maybe", a locating run lists every key of its chunk and where each one is, so for
/// <c>x = v</c> every block the run covers is dead unless the run places <c>v</c> in it, and a run
/// that does not hold <c>v</c> at all kills its whole chunk; the conjunctions, disjunctions and
/// <c>IN</c> lists combine as they do for the Bloom pruner. Only the segments whose key range can
/// hold the key are read, all of them in one coalesced read, and a run whose segment table or
/// payload does not make sense claims nothing for its blocks.
/// </remarks>
internal sealed class KeyIndexPruner
{
    private readonly VortexExpr _filter;
    private readonly Dictionary<string, Column> _columns;

    /// <summary>Each <c>IN</c> of the filter, by reference, with what <see cref="Resolve"/> found.</summary>
    private readonly Dictionary<InExpr, (Column? Column, int[] Slots)> _ins =
        new Dictionary<InExpr, (Column? Column, int[] Slots)>(ReferenceEqualityComparer.Instance);

    private KeyIndexPruner(VortexExpr filter, Dictionary<string, Column> columns)
    {
        _filter = filter;
        _columns = columns;
    }

    /// <summary>Segments read to consult the runs.</summary>
    internal int Segments { get; private set; }

    /// <summary>Their bytes.</summary>
    internal long Bytes { get; private set; }

    /// <summary>
    /// The pruner for <paramref name="filter"/>, or <see langword="null"/> when no column it tests
    /// for equality carries a usable locating entry.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="blockRows">The scan's block length; an entry at another length is ignored.</param>
    /// <param name="cancellationToken">Cancels the directory read.</param>
    internal static async ValueTask<KeyIndexPruner?> BuildAsync(
        VortexFile file, VortexExpr filter, long blockRows, CancellationToken cancellationToken)
    {
        // A file without a directory pays nothing, not even the collectors.
        if (!file.HasIndexDirectory)
        {
            return null;
        }

        Dictionary<string, List<FilterLiteral>> equalities = new Dictionary<string, List<FilterLiteral>>(StringComparer.Ordinal);
        Dictionary<string, List<StringMatchExpr>> matches = new Dictionary<string, List<StringMatchExpr>>(StringComparer.Ordinal);
        Dictionary<string, HashSet<FilterLiteral>> seenLiterals =
            new Dictionary<string, HashSet<FilterLiteral>>(StringComparer.Ordinal);
        CollectEqualities(filter, equalities, seenLiterals, matches);
        if (equalities.Count == 0 && matches.Count == 0)
        {
            return null;
        }

        IndexDirectory? directory = await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        int blocks = checked((int)((file.RowCount + blockRows - 1) / blockRows));
        Dictionary<string, Column> columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (IndexEntry entry in directory.Entries)
        {
            int stride = KeyRunOptions.StrideOf(entry.Kind);
            bool trigrams = entry.Kind == IndexKinds.PostingsNgram3;
            if (stride == 0 || entry.BlockLength != (ulong)blockRows
                || !TryResolve(file.DType, entry.ColumnPath, out string path, out DType dtype)
                || !KeyRunOptions.TryParseEntry(entry.Options, out _, out bool fold))
            {
                continue;
            }

            string key = trigrams ? TrigramKey(path) : path;
            List<FilterLiteral>? literals;
            KeyLayout layout;
            DType storage;
            if (trigrams)
            {
                if (!matches.TryGetValue(path, out List<StringMatchExpr>? predicates))
                {
                    continue;
                }

                // The keys a text index holds are trigrams, typed binary; its literals are the
                // trigrams the predicates require, folded as the index was. The list keeps the
                // order the slot arrays are indexed by, so it stays, and the set beside it answers
                // "seen already" in constant time.
                literals = [];
                HashSet<FilterLiteral> seen = [];
                foreach (StringMatchExpr predicate in predicates)
                {
                    foreach (byte[] trigram in Trigrams.Required(predicate, fold))
                    {
                        FilterLiteral literal = FilterLiteral.From(trigram);
                        if (seen.Add(literal))
                        {
                            literals.Add(literal);
                        }
                    }
                }

                layout = new KeyLayout(KeyShape.Bytes, 0, default);
                storage = new DTypeArena().Binary(Nullability.NonNullable);
            }
            else if (!equalities.TryGetValue(path, out literals) || !KeyLayout.TryOf(dtype, out layout))
            {
                continue;
            }
            else
            {
                storage = Storage(dtype);
            }

            if (columns.ContainsKey(key) || literals.Count == 0)
            {
                continue;
            }

            List<Run> runs = [];
            foreach (IndexRun run in entry.Runs)
            {
                if (FenceTable.TryOpen(run, stride, layout, out FenceTable? table, out _)
                    && run.EndBlock <= (ulong)blocks)
                {
                    runs.Add(new Run(run, table!));
                }
            }

            if (runs.Count > 0)
            {
                columns[key] = new Column(entry.Kind == IndexKinds.SortedRuns, layout, storage, runs, literals, blocks, fold);
            }
        }

        // The dictionary probe comes last, and only where no index of runs already answers the
        // column: its claim is per chunk where theirs is per block, and it reads the column's own
        // bytes. Nothing is read here -- the chunks are decoded at the refinement, once the zone
        // maps have had their say.
        foreach (IndexEntry entry in directory.Entries)
        {
            if (entry.Kind != IndexKinds.DictProbe || entry.Runs.Count == 0
                || !TryResolve(file.DType, entry.ColumnPath, out string path, out DType dtype)
                || columns.ContainsKey(path)
                || !equalities.TryGetValue(path, out List<FilterLiteral>? literals) || literals.Count == 0
                || !KeyLayout.TryOf(dtype, out KeyLayout layout))
            {
                continue;
            }

            columns[path] = new Column(rows: false, layout, Storage(dtype), [], literals, blocks, fold: false)
            {
                DictionaryPath = path,
            };
        }

        return columns.Count == 0 ? null : new KeyIndexPruner(filter, columns);
    }

    private static DType Storage(DType dtype)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        return dtype;
    }

    /// <summary>Refines <paramref name="live"/>, reading the segments that can hold the filter's keys.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="live">The mask, which only loses blocks.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask RefineAsync(VortexFile file, BlockMask live, CancellationToken cancellationToken)
    {
        using IndexBatches<(Column Column, Run Run, Fence Fence, int[] Slots)> batches = new();
        foreach (Column column in _columns.Values)
        {
            if (column.DictionaryPath is { } dictionary)
            {
                await ProbeAsync(file, column, dictionary, live, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (Run run in column.Runs)
            {
                if (!AnyLive(live, run.Meta))
                {
                    continue;
                }

                // The segments that may hold a key, found by a descent per key: a page that does
                // not read claims nothing for the run, which is then not covered.
                List<Fence> fences;
                int pagesBefore = run.Table.PagesRead;
                try
                {
                    fences = await column.CandidatesAsync(file.IndexSourceOf(run.Meta), run.Table, cancellationToken).ConfigureAwait(false);
                }
                catch (VortexFormatException)
                {
                    continue;
                }
                finally
                {
                    Segments += run.Table.PagesRead - pagesBefore;
                }

                IndexBatches<(Column Column, Run Run, Fence Fence, int[] Slots)>.Batch batch = batches.For(run.Meta);
                foreach (Fence fence in fences)
                {
                    int[] slots = new int[column.Stride];
                    for (int array = 0; array < column.Stride; array++)
                    {
                        IndexSegment payload = fence.Regions[array];
                        slots[array] = batch.Requests.Add(
                            new SegmentSpec(payload.Offset, payload.Length, payload.AlignmentExponent, 0, 0));
                    }

                    batch.Wanted.Add((column, run, fence, slots));
                }

                // Every run consulted starts as "proves every key absent"; the lookups below lift
                // that for the blocks that hold a key, and a lookup that fails lifts it whole.
                column.Cover(run.Meta);
            }
        }

        foreach ((int origin, IndexBatches<(Column Column, Run Run, Fence Fence, int[] Slots)>.Batch batch) in batches.ByOrigin)
        {
            SegmentRequestSet requests = batch.Requests;
            if (requests.Count == 0)
            {
                continue;
            }

            Segments += requests.Count;
            for (int i = 0; i < requests.Count; i++)
            {
                Bytes += requests.GetSpec(i).Length;
            }

            await file.IndexSourceOf(origin).ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            using ScanContext context = file.CreateIndexContext(origin);
            foreach ((Column column, Run run, Fence fence, int[] slots) in batch.Wanted)
            {
                context.ResetBatch();
                if (!Intact(requests, slots, fence)
                    || !column.Lookup(context, requests, slots, run, fence, live.BlockRows))
                {
                    column.Uncover(run.Meta);
                }
            }
        }

        for (int block = 0; block < live.BlockCount; block++)
        {
            if (live.IsLive(block) && ProvesAbsent(_filter, block))
            {
                live.Kill(block);
            }
        }
    }

    /// <summary>
    /// Whether the regions a lookup read are the ones written; a lookup over torn bytes would
    /// prove a key absent from blocks that hold it.
    /// </summary>
    private static bool Intact(SegmentRequestSet requests, int[] slots, Fence fence)
    {
        for (int array = 0; array < slots.Length; array++)
        {
            if (!fence.Regions[array].Holds(requests.GetBuffer(slots[array]).Span))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyLive(BlockMask live, IndexRun run)
    {
        long end = Math.Min((long)run.EndBlock, live.BlockCount);
        for (long block = (long)run.FirstBlock; block < end; block++)
        {
            if (live.IsLive((int)block))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------------------ the proof

    private bool ProvesAbsent(VortexExpr expr, int block)
    {
        switch (expr)
        {
            case LogicalExpr { IsAnd: true } and:
                return ProvesAbsent(and.Left, block) || ProvesAbsent(and.Right, block);

            case LogicalExpr or:
                return ProvesAbsent(or.Left, block) && ProvesAbsent(or.Right, block);

            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                return Absent(equal.Field.Path, equal.Value, block);

            case InExpr @in:
            {
                // Resolved once, asked per block: "is every one of these literals absent from this
                // block" is asked for each of the file's blocks against the same expression, so the
                // literals' slots are found once and each block then costs one array read.
                (Column? column, int[] slots) = Resolve(@in);
                if (column is null || slots.Length == 0)
                {
                    return false;
                }

                foreach (int slot in slots)
                {
                    if (slot < 0 || !column.AbsentAt(slot, block))
                    {
                        return false;
                    }
                }

                return true;
            }

            case StringMatchExpr match when _columns.TryGetValue(TrigramKey(match.Field.Path), out Column? text):
                // Absent as soon as one required trigram is: a matching value holds them all.
                foreach (byte[] trigram in Trigrams.Required(match, text.Fold))
                {
                    if (text.Absent(FilterLiteral.From(trigram), block))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private bool Absent(string path, FilterLiteral value, int block) =>
        _columns.TryGetValue(path, out Column? column) && column.Absent(value, block);

    /// <summary>
    /// Fills a dictionary-backed column's claims: opens the column's chunks' dictionaries and asks
    /// each one whether it holds the filter's literals.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="column">The column, whose claims are empty until this runs.</param>
    /// <param name="path">Its path.</param>
    /// <param name="live">The mask as the earlier structures left it.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// A source that refuses claims nothing: a chunk the entry claims and that is not a dictionary
    /// makes the source refuse whole, and the column then proves no literal absent anywhere --
    /// which is what an index that cannot be read must do.
    /// </remarks>
    private async ValueTask ProbeAsync(
        VortexFile file, Column column, string path, BlockMask live, CancellationToken cancellationToken)
    {
        (Keys.SortedRunsSource? source, _) = await Keys.SortedRunsSource
            .OpenAsync(file, path, KeySourceKind.Dictionary, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return;
        }

        await using (source.ConfigureAwait(false))
        {
            Segments += source.Dictionaries;
            Bytes += source.DictionaryBytes;
            for (int chunk = 0; chunk < source.Dictionaries; chunk++)
            {
                column.Dictionary(source, chunk, live.BlockRows);
            }
        }
    }

    /// <summary>The column an <c>IN</c> asks about and its literals' slots in it, found once.</summary>
    /// <param name="expr">The expression, the same instance at every block.</param>
    private (Column? Column, int[] Slots) Resolve(InExpr expr)
    {
        if (_ins.TryGetValue(expr, out (Column? Column, int[] Slots) found))
        {
            return found;
        }

        if (!_columns.TryGetValue(expr.Field.Path, out Column? column))
        {
            found = (null, []);
        }
        else
        {
            int[] slots = new int[expr.Values.Count];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = column.SlotOf(expr.Values[i]);
            }

            found = (column, slots);
        }

        _ins[expr] = found;
        return found;
    }

    private static string TrigramKey(string path) => path + "\0ngram3";

    private static void CollectEqualities(
        VortexExpr expr,
        Dictionary<string, List<FilterLiteral>> into,
        Dictionary<string, HashSet<FilterLiteral>> seen,
        Dictionary<string, List<StringMatchExpr>> matches)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                CollectEqualities(logical.Left, into, seen, matches);
                CollectEqualities(logical.Right, into, seen, matches);
                break;
            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                Add(into, seen, equal.Field.Path, equal.Value);
                break;
            case InExpr @in:
                foreach (FilterLiteral value in @in.Values)
                {
                    Add(into, seen, @in.Field.Path, value);
                }

                break;
            case StringMatchExpr match:
                if (!matches.TryGetValue(match.Field.Path, out List<StringMatchExpr>? list))
                {
                    list = [];
                    matches[match.Field.Path] = list;
                }

                list.Add(match);
                break;
            default:
                break;
        }

        // The list keeps the order the slot arrays are indexed by; the set answers whether a
        // literal is already in it. Asking the list instead would cost a comparison per literal
        // already found, which a long `IN` turns into a quadratic cost before a single block is
        // read.
        static void Add(
            Dictionary<string, List<FilterLiteral>> into,
            Dictionary<string, HashSet<FilterLiteral>> seen,
            string path,
            FilterLiteral value)
        {
            if (!into.TryGetValue(path, out List<FilterLiteral>? list))
            {
                list = [];
                into[path] = list;
                seen[path] = [];
            }

            if (seen[path].Add(value))
            {
                list.Add(value);
            }
        }
    }

    /// <summary>The dotted path and dtype an entry's field indices name, or false when they name nothing.</summary>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="fields">The entry's column path.</param>
    /// <param name="path">The dotted path.</param>
    /// <param name="dtype">The column's dtype.</param>
    /// <returns>Whether the indices name a column.</returns>
    internal static bool TryResolve(DType schema, IReadOnlyList<uint> fields, out string path, out DType dtype)
    {
        path = string.Empty;
        dtype = schema;
        if (fields.Count == 0)
        {
            return false;
        }

        List<string> names = [];
        foreach (uint field in fields)
        {
            if (dtype.Kind != DTypeKind.Struct || field >= (uint)dtype.FieldCount)
            {
                return false;
            }

            names.Add(dtype.GetFieldName((int)field));
            dtype = dtype.GetField((int)field);
        }

        path = string.Join('.', names);
        return true;
    }

    // ------------------------------------------------------------------------------ state

    private sealed record Run(IndexRun Meta, FenceTable Table);

    /// <summary>A descent's comparison: a fence whose last key comes before the key.</summary>
    private readonly struct KeyProbe(KeyLayout layout, byte[] key) : IFenceProbe
    {
        public bool Below(ReadOnlySpan<byte> max) => layout.Compare(max, key) < 0;
    }

    /// <summary>One column's runs and, per literal, which blocks the runs prove it absent from.</summary>
    private sealed class Column
    {
        private readonly bool _rows;
        private readonly KeyLayout _layout;
        private readonly DType _storage;
        private readonly List<FilterLiteral> _literals;

        /// <summary>Each literal's slot, the first when the filter names one twice.</summary>
        private readonly Dictionary<FilterLiteral, int> _slots;
        private readonly byte[]?[] _keys;
        private readonly byte[]?[] _otherZeros;
        private readonly bool[][] _absent;

        /// <summary>
        /// The keys the filter asks for, in the runs' own order, with their sort keys: the
        /// merge-join's left side. Built at the first segment.
        /// </summary>
        private (int Literal, byte[] Key, ulong Sort)[]? _sorted;

        /// <summary>One segment's matches: per literal, the entries equal to its key.</summary>
        private readonly List<(int Literal, int Low, int High)> _matches = [];

        internal Column(
            bool rows, KeyLayout layout, DType storage, List<Run> runs, List<FilterLiteral> literals, int blocks, bool fold)
        {
            Fold = fold;
            _rows = rows;
            _layout = layout;
            _storage = storage;
            Runs = runs;
            _literals = literals;
            _slots = new Dictionary<FilterLiteral, int>(literals.Count);
            _keys = new byte[]?[literals.Count];
            _otherZeros = new byte[]?[literals.Count];
            _absent = new bool[literals.Count][];
            Span<byte> scratch = stackalloc byte[sizeof(ulong)];
            Span<byte> zero = stackalloc byte[sizeof(ulong)];
            for (int i = 0; i < literals.Count; i++)
            {
                _slots.TryAdd(literals[i], i);
                _absent[i] = new bool[blocks];
                if (layout.TryEncode(literals[i], scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
                {
                    _keys[i] = key.ToArray();
                    _otherZeros[i] = hasOtherZero ? zero[..layout.Width].ToArray() : null;
                }
            }
        }

        internal List<Run> Runs { get; }

        /// <summary>
        /// The column, when its claims come from the chunks' dictionaries rather than from an
        /// index's runs; null for every other kind.
        /// </summary>
        internal string? DictionaryPath { get; init; }

        /// <summary>For a trigram index, whether its trigrams were folded.</summary>
        internal bool Fold { get; }

        internal int Stride => _rows ? KeyRunOptions.SortedStride : KeyRunOptions.PostingsStride;

        /// <summary>
        /// The segments of <paramref name="table"/> whose range may hold one of the column's keys,
        /// each once, in segment order: per key, a descent to the first segment whose last key does
        /// not come before it, and the segments after it whose first key does not pass it.
        /// </summary>
        internal async ValueTask<List<Fence>> CandidatesAsync(
            ISegmentSource source, FenceTable table, CancellationToken cancellationToken)
        {
            SortedDictionary<long, Fence> found = [];
            for (int i = 0; i < _keys.Length; i++)
            {
                foreach (byte[]? key in (byte[]?[])[_keys[i], _otherZeros[i]])
                {
                    if (key is null)
                    {
                        continue;
                    }

                    long s = await table.LowerBoundAsync(source, new KeyProbe(_layout, key), cancellationToken).ConfigureAwait(false);
                    for (; s < table.SegmentCount; s++)
                    {
                        Fence fence = await table.GetAsync(source, s, cancellationToken).ConfigureAwait(false);
                        if (fence.Bounds.Entries == 0)
                        {
                            continue;
                        }

                        if (_layout.Compare(fence.Bounds.Min, key) > 0)
                        {
                            break;
                        }

                        found.TryAdd(s, fence);

                        // A segment that ends past the key is the last that can hold it: the next
                        // fence, perhaps on another page, is not read.
                        if (_layout.Compare(fence.Bounds.Max, key) > 0)
                        {
                            break;
                        }
                    }
                }
            }

            return [.. found.Values];
        }

        /// <summary>
        /// Claims one chunk's blocks from its dictionary: a literal the chunk's values do not hold
        /// is absent from every block the chunk covers whole.
        /// </summary>
        /// <param name="source">The column's dictionary source, open over the claimed chunks.</param>
        /// <param name="chunk">The chunk, under the source's count.</param>
        /// <param name="blockRows">Rows per block.</param>
        /// <remarks>
        /// Only the blocks the chunk covers whole are claimed. A dictionary says what its own rows
        /// hold and nothing about the rows around them, so a block a chunk shares with its
        /// neighbour is left live -- the writer aligns chunks to blocks, so this costs nothing
        /// there and stays right on a file whose chunks are not aligned.
        /// </remarks>
        internal void Dictionary(Keys.SortedRunsSource source, int chunk, long blockRows)
        {
            (long firstRow, long rows) = source.DictionaryExtent(chunk);
            int count = _absent.Length == 0 ? 0 : _absent[0].Length;
            long last = firstRow + rows;
            ulong first = (ulong)((firstRow + blockRows - 1) / blockRows);

            // The chunk's last block counts as whole when the file holds no row past it: the tail
            // of the file's last block is not another chunk's, it is nothing at all.
            ulong end = (ulong)((last + blockRows - 1) / blockRows >= count ? count : last / blockRows);
            if (end <= first)
            {
                return;
            }

            for (int i = 0; i < _keys.Length; i++)
            {
                if (_keys[i] is not { } key)
                {
                    continue;
                }

                // The two zeros of a float are one literal, and a dictionary may hold either.
                bool holds = source.DictionaryHolds(chunk, key)
                    || (_otherZeros[i] is { } other && source.DictionaryHolds(chunk, other));
                if (holds)
                {
                    continue;
                }

                for (ulong block = first; block < end && block < (ulong)_absent[i].Length; block++)
                {
                    _absent[i][block] = true;
                }
            }
        }

        /// <summary>Marks every block of the run absent for every encodable literal.</summary>
        internal void Cover(IndexRun run)
        {
            for (int i = 0; i < _keys.Length; i++)
            {
                if (_keys[i] is null)
                {
                    continue;
                }

                for (ulong block = run.FirstBlock; block < run.EndBlock; block++)
                {
                    _absent[i][block] = true;
                }
            }
        }

        /// <summary>Lifts the run's claim: a lookup in it failed.</summary>
        internal void Uncover(IndexRun run)
        {
            for (int i = 0; i < _keys.Length; i++)
            {
                for (ulong block = run.FirstBlock; block < run.EndBlock; block++)
                {
                    _absent[i][block] = false;
                }
            }
        }

        /// <summary>Whether the runs prove <paramref name="value"/> absent from <paramref name="block"/>.</summary>
        /// <remarks>
        /// This is asked once per literal per block, so the literal's slot is found through a map
        /// and not by walking the list: a long `IN` over a file with many blocks would otherwise
        /// spend more on literal comparisons than the scan it guards spends on reading.
        /// </remarks>
        /// <param name="value">The literal.</param>
        /// <param name="block">The block.</param>
        internal bool Absent(FilterLiteral value, int block) =>
            _slots.TryGetValue(value, out int i) && AbsentAt(i, block);

        /// <summary>The slot of <paramref name="value"/>, or −1 when the filter never named it.</summary>
        /// <param name="value">The literal.</param>
        internal int SlotOf(FilterLiteral value) => _slots.TryGetValue(value, out int i) ? i : -1;

        /// <summary>Whether the runs prove slot <paramref name="slot"/>'s key absent from the block.</summary>
        /// <param name="slot">The literal's slot.</param>
        /// <param name="block">The block.</param>
        internal bool AbsentAt(int slot, int block) =>
            (uint)block < (uint)_absent[slot].Length && _absent[slot][block];

        /// <summary>
        /// Decodes one segment's arrays and marks, for every literal, the blocks that hold it.
        /// </summary>
        /// <returns>Whether the segment made sense.</returns>
        internal bool Lookup(ScanContext context, SegmentRequestSet requests, int[] slots, Run run, Fence fence, long blockRows)
        {
            ulong expected = fence.Bounds.Entries;
            if (expected > int.MaxValue)
            {
                return false;
            }

            int entries = (int)expected;
            try
            {
                DTypeArena types = new DTypeArena();
                DType keyType = _storage.Kind == DTypeKind.Primitive
                    ? types.Primitive(_storage.PType, Nullability.NonNullable)
                    : _storage.Kind == DTypeKind.Utf8
                        ? types.Utf8(Nullability.NonNullable)
                        : types.Binary(Nullability.NonNullable);
                DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);

                int keys = Decode(context, requests.GetBuffer(slots[0]), keyType, entries);
                CanonicalNode keyNode = context.Canonical.GetNode(keys);
                if (!Fits(keyNode, entries))
                {
                    return false;
                }

                ulong firstBlock = run.Meta.FirstBlock;
                if (_rows)
                {
                    // A run spanning 2³² rows or more writes them at 64 bits; its dtype says so.
                    bool wide = run.Table.WideRows;
                    PType width = wide ? PType.U64 : PType.U32;
                    int rowsNode = Decode(
                        context, requests.GetBuffer(slots[1]), types.Primitive(width, Nullability.NonNullable), entries);
                    CanonicalNode rows = context.Canonical.GetNode(rowsNode);
                    if (rows.Kind != CanonicalKind.Primitive || rows.PType != width || rows.Length != entries)
                    {
                        return false;
                    }

                    foreach ((int literal, int low, int high) in Match(keyNode))
                    {
                        if (wide)
                        {
                            MarkRows(literal, low, high, rows.Values.Cast<ulong>(), firstBlock, blockRows);
                        }
                        else
                        {
                            MarkRows(literal, low, high, rows.Values.Cast<uint>(), firstBlock, blockRows);
                        }
                    }

                    return true;
                }

                int offsetsNode = Decode(context, requests.GetBuffer(slots[1]), u32, entries + 1);
                CanonicalNode offsetArray = context.Canonical.GetNode(offsetsNode);
                if (offsetArray.Kind != CanonicalKind.Primitive || offsetArray.PType != PType.U32 || offsetArray.Length != entries + 1)
                {
                    return false;
                }

                ReadOnlySpan<uint> starts = offsetArray.Values.Cast<uint>();
                int listLength = checked((int)starts[entries]);
                int listsNode = Decode(context, requests.GetBuffer(slots[2]), u32, listLength);
                CanonicalNode lists = context.Canonical.GetNode(listsNode);
                if (lists.Kind != CanonicalKind.Primitive || lists.PType != PType.U32 || lists.Length != listLength)
                {
                    return false;
                }

                ReadOnlySpan<uint> blocks = lists.Values.Cast<uint>();
                foreach ((int literal, int low, int high) in Match(keyNode))
                {
                    MarkPostings(literal, low, high, starts, blocks, firstBlock);
                }

                return true;
            }
            catch (VortexFormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private bool Fits(CanonicalNode keys, int entries) =>
            keys.Length == entries
            && (_layout.Shape == KeyShape.Bytes
                ? keys.Kind == CanonicalKind.VarBinView
                : keys.Kind == CanonicalKind.Primitive && keys.PType == _layout.PType);

        private void MarkPostings(
            int literal, int lo, int hi, ReadOnlySpan<uint> starts, ReadOnlySpan<uint> blocks, ulong firstBlock)
        {
            for (int k = lo; k < hi; k++)
            {
                for (uint p = starts[k]; p < starts[k + 1]; p++)
                {
                    Present(literal, firstBlock + blocks[(int)p]);
                }
            }
        }

        private void MarkRows(
            int literal, int lo, int hi, ReadOnlySpan<uint> rows, ulong firstBlock, long blockRows)
        {
            for (int e = lo; e < hi; e++)
            {
                Present(literal, firstBlock + (ulong)(rows[e] / blockRows));
            }
        }

        private void MarkRows(
            int literal, int lo, int hi, ReadOnlySpan<ulong> rows, ulong firstBlock, long blockRows)
        {
            for (int e = lo; e < hi; e++)
            {
                Present(literal, firstBlock + (rows[e] / (ulong)blockRows));
            }
        }

        private void Present(int literal, ulong block)
        {
            // The literal's own absent array is shared by the two zeros; a bad block id claims nothing.
            if (block < (ulong)_absent[literal].Length)
            {
                _absent[literal][block] = false;
            }
        }

        /// <summary>The filter's keys in the runs' order, each with its sort key.</summary>
        /// <remarks>
        /// A float's second zero is a key of its own here, pointing at the same literal: two
        /// entries a run may hold separately, one answer to give.
        /// </remarks>
        private (int Literal, byte[] Key, ulong Sort)[] Sorted()
        {
            if (_sorted is { } built)
            {
                return built;
            }

            List<(int Literal, byte[] Key, ulong Sort)> pairs = [];
            for (int i = 0; i < _keys.Length; i++)
            {
                foreach (byte[]? key in (byte[]?[])[_keys[i], _otherZeros[i]])
                {
                    if (key is not null)
                    {
                        pairs.Add((i, key, _layout.Shape == KeyShape.Bytes ? 0 : _layout.SortKey(key)));
                    }
                }
            }

            pairs.Sort((a, b) => _layout.Shape == KeyShape.Bytes
                ? _layout.Compare(a.Key, b.Key)
                : a.Sort.CompareTo(b.Sort));
            _sorted = [.. pairs];
            return _sorted;
        }

        /// <summary>
        /// The entries of one segment that hold the filter's keys, as ranges per literal.
        /// </summary>
        /// <remarks>
        /// Two shapes, and the comparison counts choose between them: a binary search per key costs
        /// <c>2·keys·log₂(entries)</c> comparisons, while walking the segment against the sorted
        /// keys costs <c>entries + keys</c>, so few keys favour the searches and many the walk.
        /// Both compare sort keys rather than bytes — a fixed-width key is one unsigned integer in
        /// the run's own order (<see cref="KeyLayout.SortKey"/>) — which makes a comparison a
        /// single integer comparison instead of a span walk.
        /// </remarks>
        /// <param name="keys">The segment's decoded keys.</param>
        private List<(int Literal, int Low, int High)> Match(CanonicalNode keys)
        {
            _matches.Clear();
            (int Literal, byte[] Key, ulong Sort)[] sorted = Sorted();
            int entries = keys.Length;
            if (sorted.Length == 0 || entries == 0)
            {
                return _matches;
            }

            long searches = 2L * sorted.Length * (64 - BitOperations.LeadingZeroCount((ulong)entries));
            if (searches <= entries + sorted.Length)
            {
                foreach ((int literal, byte[] key, ulong _) in sorted)
                {
                    (int low, int high) = Range(keys, key);
                    if (high > low)
                    {
                        _matches.Add((literal, low, high));
                    }
                }

                return _matches;
            }

            int entry = 0;
            int pair = 0;
            while (entry < entries && pair < sorted.Length)
            {
                int sign = CompareAt(keys, entry, sorted[pair]);
                if (sign < 0)
                {
                    entry++;
                    continue;
                }

                if (sign > 0)
                {
                    pair++;
                    continue;
                }

                int low = entry;
                while (entry < entries && CompareAt(keys, entry, sorted[pair]) == 0)
                {
                    entry++;
                }

                _matches.Add((sorted[pair].Literal, low, entry));

                // The keys the filter asks for may repeat -- an `IN` list is the caller's, and a
                // float's two zeros are two keys of two literals -- so the next pair is offered the
                // same entries rather than the ones after them.
                entry = low;
                pair++;
            }

            return _matches;
        }

        /// <summary>Entry <paramref name="index"/> against a key, in the run's order.</summary>
        private int CompareAt(CanonicalNode keys, int index, (int Literal, byte[] Key, ulong Sort) pair) =>
            _layout.Shape == KeyShape.Bytes
                ? _layout.Compare(KeyAt(keys, index), pair.Key)
                : _layout.SortKey(KeyAt(keys, index)).CompareTo(pair.Sort);

        /// <summary>The entries equal to <paramref name="key"/>: [lower bound, upper bound).</summary>
        private (int Low, int High) Range(CanonicalNode keys, byte[] key)
        {
            (int Literal, byte[] Key, ulong Sort) wanted =
                (0, key, _layout.Shape == KeyShape.Bytes ? 0 : _layout.SortKey(key));
            int low = 0;
            int high = keys.Length;
            while (low < high)
            {
                int mid = (low + high) >>> 1;
                if (CompareAt(keys, mid, wanted) < 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            int start = low;
            high = keys.Length;
            while (low < high)
            {
                int mid = (low + high) >>> 1;
                if (CompareAt(keys, mid, wanted) <= 0)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return (start, low);
        }

        private ReadOnlySpan<byte> KeyAt(CanonicalNode keys, int index) =>
            _layout.Shape == KeyShape.Bytes
                ? LiteralReader.ViewAt(keys, index)
                : keys.Values.Span.Slice(index * _layout.Width, _layout.Width);

        private static int Decode(ScanContext context, VortexBuffer segment, DType dtype, int length)
        {
            context.Decode.LoadBlob(segment);
            ArrayNode root = context.Nodes.Root;
            return context.Decode.DecodeRoot(in root, dtype, length);
        }
    }
}
