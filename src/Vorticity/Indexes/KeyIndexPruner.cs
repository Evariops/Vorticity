// The read side of the two locating kinds (docs/10-indexes.md §6.1, §6.2, §6.6): a pruner in the
// scan's block-mask chain, after the zone maps and the Bloom filters.
//
// A LOCATING RUN IS A POSITIVE ANSWER. A Bloom filter says "maybe"; a run lists every key of its
// chunk and where each one is. So for `x = v` every block the run covers is dead unless the run
// places v in it -- the blocks of the postings list, or the blocks of the sorted run's rows -- and a
// run that does not hold v at all kills its whole chunk. The same AND / OR / IN algebra as the
// Bloom pruner combines the answers.
//
// ONLY THE SEGMENTS THAT CAN HOLD THE KEY ARE READ: a run's options give each segment's first and
// last key, so a probe reads the segments whose range covers its key and nothing else, all of them
// in one coalesced read. A run whose table or payloads do not make sense -- the wrong number of
// arrays, a keys array of the wrong type or length -- claims nothing for its blocks.
using System;
using System.Collections.Generic;
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
internal sealed class KeyIndexPruner
{
    private readonly VortexExpr _filter;
    private readonly Dictionary<string, Column> _columns;

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
        CollectEqualities(filter, equalities, matches);
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
                || !TryResolve(file.Schema, entry.ColumnPath, out string path, out DType dtype)
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
                // trigrams the predicates require, folded as the index was.
                literals = [];
                foreach (StringMatchExpr predicate in predicates)
                {
                    foreach (byte[] trigram in Trigrams.Required(predicate, fold))
                    {
                        FilterLiteral literal = FilterLiteral.From(trigram);
                        if (!literals.Contains(literal))
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
                if (KeyRunOptions.TryParseRun(run.OptionBytes, out List<KeySegment> segments)
                    && run.Payload.Count == stride * segments.Count
                    && run.EndBlock <= (ulong)blocks)
                {
                    runs.Add(new Run(run, segments));
                }
            }

            if (runs.Count > 0)
            {
                columns[key] = new Column(entry.Kind == IndexKinds.SortedRuns, layout, storage, runs, literals, blocks, fold);
            }
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
        using SegmentRequestSet requests = new SegmentRequestSet();
        List<(Column Column, Run Run, int Segment, int[] Slots)> wanted = [];
        foreach (Column column in _columns.Values)
        {
            foreach (Run run in column.Runs)
            {
                if (!AnyLive(live, run.Meta))
                {
                    continue;
                }

                for (int segment = 0; segment < run.Segments.Count; segment++)
                {
                    if (!column.SegmentMayHold(run.Segments[segment]))
                    {
                        continue;
                    }

                    int[] slots = new int[column.Stride];
                    for (int array = 0; array < column.Stride; array++)
                    {
                        IndexSegment payload = run.Meta.Payload[(segment * column.Stride) + array];
                        slots[array] = requests.Add(
                            new SegmentSpec(payload.Offset, payload.Length, payload.AlignmentExponent, 0, 0));
                    }

                    wanted.Add((column, run, segment, slots));
                }

                // Every run consulted starts as "proves every key absent"; the lookups below lift
                // that for the blocks that hold a key, and a lookup that fails lifts it whole.
                column.Cover(run.Meta);
            }
        }

        if (requests.Count > 0)
        {
            Segments += requests.Count;
            for (int i = 0; i < requests.Count; i++)
            {
                Bytes += requests.GetSpec(i).Length;
            }

            await file.Segments.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            using ScanContext context = new ScanContext(file, ScanContext.MetadataCapacity);
            foreach ((Column column, Run run, int segment, int[] slots) in wanted)
            {
                context.ResetBatch();
                if (!column.Lookup(context, requests, slots, run, segment, live.BlockRows))
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
                foreach (FilterLiteral value in @in.Values)
                {
                    if (!Absent(@in.Field.Path, value, block))
                    {
                        return false;
                    }
                }

                return @in.Values.Count > 0;

            case StringMatchExpr match when _columns.TryGetValue(TrigramKey(match.Field.Path), out Column? text):
                // Absent when ONE required trigram is: a matching value holds them all.
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

    private static string TrigramKey(string path) => path + "\0ngram3";

    private static void CollectEqualities(
        VortexExpr expr, Dictionary<string, List<FilterLiteral>> into, Dictionary<string, List<StringMatchExpr>> matches)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                CollectEqualities(logical.Left, into, matches);
                CollectEqualities(logical.Right, into, matches);
                break;
            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                Add(into, equal.Field.Path, equal.Value);
                break;
            case InExpr @in:
                foreach (FilterLiteral value in @in.Values)
                {
                    Add(into, @in.Field.Path, value);
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

        static void Add(Dictionary<string, List<FilterLiteral>> into, string path, FilterLiteral value)
        {
            if (!into.TryGetValue(path, out List<FilterLiteral>? list))
            {
                list = [];
                into[path] = list;
            }

            if (!list.Contains(value))
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

    private sealed record Run(IndexRun Meta, List<KeySegment> Segments);

    /// <summary>One column's runs and, per literal, which blocks the runs prove it absent from.</summary>
    private sealed class Column
    {
        private readonly bool _rows;
        private readonly KeyLayout _layout;
        private readonly DType _storage;
        private readonly List<FilterLiteral> _literals;
        private readonly byte[]?[] _keys;
        private readonly byte[]?[] _otherZeros;
        private readonly bool[][] _absent;

        internal Column(
            bool rows, KeyLayout layout, DType storage, List<Run> runs, List<FilterLiteral> literals, int blocks, bool fold)
        {
            Fold = fold;
            _rows = rows;
            _layout = layout;
            _storage = storage;
            Runs = runs;
            _literals = literals;
            _keys = new byte[]?[literals.Count];
            _otherZeros = new byte[]?[literals.Count];
            _absent = new bool[literals.Count][];
            Span<byte> scratch = stackalloc byte[sizeof(ulong)];
            Span<byte> zero = stackalloc byte[sizeof(ulong)];
            for (int i = 0; i < literals.Count; i++)
            {
                _absent[i] = new bool[blocks];
                if (layout.TryEncode(literals[i], scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
                {
                    _keys[i] = key.ToArray();
                    _otherZeros[i] = hasOtherZero ? zero[..layout.Width].ToArray() : null;
                }
            }
        }

        internal List<Run> Runs { get; }

        /// <summary>For a trigram index, whether its trigrams were folded.</summary>
        internal bool Fold { get; }

        internal int Stride => _rows ? KeyRunOptions.SortedStride : KeyRunOptions.PostingsStride;

        /// <summary>Whether any of the column's keys may lie in the segment's range.</summary>
        internal bool SegmentMayHold(KeySegment segment)
        {
            if (segment.Entries == 0)
            {
                return false;
            }

            for (int i = 0; i < _keys.Length; i++)
            {
                if (InRange(segment, _keys[i]) || InRange(segment, _otherZeros[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private bool InRange(KeySegment segment, byte[]? key) =>
            key is not null
            && (_layout.Shape == KeyShape.Bytes || (segment.Min.Length == _layout.Width && segment.Max.Length == _layout.Width))
            && _layout.Compare(segment.Min, key) <= 0
            && _layout.Compare(key, segment.Max) <= 0;

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

        internal bool Absent(FilterLiteral value, int block)
        {
            int i = _literals.IndexOf(value);
            return i >= 0 && (uint)block < (uint)_absent[i].Length && _absent[i][block];
        }

        /// <summary>
        /// Decodes one segment's arrays and marks, for every literal, the blocks that hold it.
        /// </summary>
        /// <returns>Whether the segment made sense.</returns>
        internal bool Lookup(ScanContext context, SegmentRequestSet requests, int[] slots, Run run, int segment, long blockRows)
        {
            ulong expected = run.Segments[segment].Entries;
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
                    int rowsNode = Decode(context, requests.GetBuffer(slots[1]), u32, entries);
                    CanonicalNode rows = context.Canonical.GetNode(rowsNode);
                    if (rows.Kind != CanonicalKind.Primitive || rows.PType != PType.U32 || rows.Length != entries)
                    {
                        return false;
                    }

                    ReadOnlySpan<uint> offsets = rows.Values.Cast<uint>();
                    for (int i = 0; i < _keys.Length; i++)
                    {
                        MarkRows(i, _keys[i], keyNode, offsets, firstBlock, blockRows);
                        MarkRows(i, _otherZeros[i], keyNode, offsets, firstBlock, blockRows);
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
                for (int i = 0; i < _keys.Length; i++)
                {
                    MarkPostings(i, _keys[i], keyNode, starts, blocks, firstBlock);
                    MarkPostings(i, _otherZeros[i], keyNode, starts, blocks, firstBlock);
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
            int literal, byte[]? key, CanonicalNode keys, ReadOnlySpan<uint> starts, ReadOnlySpan<uint> blocks, ulong firstBlock)
        {
            if (key is null)
            {
                return;
            }

            (int lo, int hi) = Range(keys, key);
            for (int k = lo; k < hi; k++)
            {
                for (uint p = starts[k]; p < starts[k + 1]; p++)
                {
                    Present(literal, firstBlock + blocks[(int)p]);
                }
            }
        }

        private void MarkRows(
            int literal, byte[]? key, CanonicalNode keys, ReadOnlySpan<uint> rows, ulong firstBlock, long blockRows)
        {
            if (key is null)
            {
                return;
            }

            (int lo, int hi) = Range(keys, key);
            for (int e = lo; e < hi; e++)
            {
                Present(literal, firstBlock + (ulong)(rows[e] / blockRows));
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

        /// <summary>The entries equal to <paramref name="key"/>: [lower bound, upper bound).</summary>
        private (int Low, int High) Range(CanonicalNode keys, byte[] key)
        {
            int low = 0;
            int high = keys.Length;
            while (low < high)
            {
                int mid = (low + high) >>> 1;
                if (_layout.Compare(KeyAt(keys, mid), key) < 0)
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
                if (_layout.Compare(KeyAt(keys, mid), key) <= 0)
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
