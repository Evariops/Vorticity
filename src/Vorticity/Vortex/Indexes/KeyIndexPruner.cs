using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.IO;
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

    /// <summary>Each question of the filter a column answers, by reference, with the presence sets it reads.</summary>
    private readonly Dictionary<VortexExpr, Leaf> _leaves =
        new Dictionary<VortexExpr, Leaf>(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// What each filter still in use asks of the columns' locating indexes, made once for the
    /// filter rather than once per pruner: a dataset plans one filter over each of its objects, and
    /// each would encode, index and sort the same literals again. They go with the filter.
    /// </summary>
    private static readonly ConditionalWeakTable<VortexExpr, Prepared> Preparations = [];

    private KeyIndexPruner(VortexExpr filter, Dictionary<string, Column> columns)
    {
        _filter = filter;
        _columns = columns;
        foreach (Column column in columns.Values)
        {
            foreach ((VortexExpr question, (int first, int sets)) in column.Asked.Questions)
            {
                _leaves[question] = new Leaf(column, first, sets);
            }
        }
    }

    /// <summary>Segments read to consult the runs.</summary>
    internal int Segments { get; private set; }

    /// <summary>Whether a column it consults is sorted runs, the entry that also serves as an exact cover.</summary>
    internal bool LocatesRows
    {
        get
        {
            foreach (Column column in _columns.Values)
            {
                if (column.Rows)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Their bytes.</summary>
    internal long Bytes { get; private set; }

    /// <summary>
    /// Whether this pruner alone answers what the filter asks of the column at <paramref name="path"/>: hashed
    /// postings, a declared identifier's, cover every block of the file, and every question of the filter that reads
    /// the column is one they answer, each able to prove absence.
    /// </summary>
    /// <remarks>
    /// The column's zone maps would add nothing then but a false block of the postings now and then, for the bounds of
    /// every block of the file, which values spread over it leave wide: decoded to prune no block.
    /// </remarks>
    internal bool AnswersAlone(string path) =>
        _columns.TryGetValue(path, out Column? column) && column.Hashes && column.CoversAll && AskedOnly(_filter, path, column);

    /// <summary>
    /// Whether every leaf of <paramref name="expr"/> that reads the column at <paramref name="path"/> is a question
    /// <paramref name="column"/> answers, each of its presence sets able to prove absence.
    /// </summary>
    private bool AskedOnly(VortexExpr expr, string path, Column column)
    {
        if (expr is LogicalExpr logical)
        {
            return AskedOnly(logical.Left, path, column) && AskedOnly(logical.Right, path, column);
        }

        if (_leaves.TryGetValue(expr, out Leaf leaf) && ReferenceEquals(leaf.Column, column))
        {
            for (int set = leaf.First; set < leaf.First + leaf.Sets; set++)
            {
                if (!column.Asked.Keyed(set))
                {
                    return false;
                }
            }

            return true;
        }

        Reading reading = new Reading(path);
        return Scanning.ScanBuilder.VisitFields(expr, ref reading);
    }

    /// <summary>A walk over a filter's columns that stops at the column at <c>path</c>, or at a function of it.</summary>
    private readonly struct Reading(string path) : Scanning.ScanBuilder.IFieldVisitor
    {
        public bool Visit(FieldExpr field) => !string.Equals(field.Path, path, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pruner for <paramref name="filter"/>, or <see langword="null"/> when no column it tests
    /// for equality carries a usable locating entry.
    /// </summary>
    /// <param name="file">The file's indexes: the open file, or what a read with no open has of it.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="blockRows">The scan's block length; an entry at another length is ignored.</param>
    /// <param name="cancellationToken">Cancels the directory read.</param>
    internal static async ValueTask<KeyIndexPruner?> BuildAsync(
        IIndexHost file, VortexExpr filter, long blockRows, CancellationToken cancellationToken)
    {
        // A file without a directory pays nothing, not even the collectors.
        if (!file.HasIndexDirectory)
        {
            return null;
        }

        Prepared prepared = Preparations.GetValue(filter, static expr => new Prepared(expr));
        Dictionary<string, List<FilterLiteral>> equalities = prepared.Equalities;
        Dictionary<string, List<StringMatchExpr>> matches = prepared.Matches;
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
        DTypeArena? classTypes = null;
        foreach (IndexEntry entry in directory.Entries)
        {
            int stride = KeyRunOptions.StrideOf(entry.Kind);
            bool trigrams = entry.Kind == IndexKinds.PostingsNgram3;
            bool hashed = entry.Kind == IndexKinds.PostingsHashed;
            if (stride == 0 || entry.BlockLength != (ulong)blockRows
                || !TryResolve(file.DType, entry.ColumnPath, out string path, out DType dtype)
                || !KeyRunOptions.TryParseEntry(entry.Options, out _, out bool fold))
            {
                continue;
            }

            if (entry.VariantPath is not null && !TryResolveVariant(entry, trigrams, ref path, ref dtype, ref classTypes))
            {
                continue;
            }

            string key = trigrams ? TrigramKey(path) : path;
            List<StringMatchExpr>? predicates = null;
            KeyLayout layout;
            DType storage;
            if (trigrams)
            {
                // The keys a text index holds are trigrams, typed binary.
                if (!matches.TryGetValue(path, out predicates))
                {
                    continue;
                }

                layout = new KeyLayout(KeyShape.Bytes, 0, default);
                storage = new DTypeArena().Binary(Nullability.NonNullable);
            }
            else if (!equalities.ContainsKey(path) || !KeyLayout.TryOf(dtype, out layout))
            {
                continue;
            }
            else
            {
                storage = Storage(dtype);
            }

            if (columns.ContainsKey(key))
            {
                continue;
            }

            Asked asked = prepared.For(key, layout, fold, predicates, hashed);
            if (asked.Literals == 0)
            {
                continue;
            }

            List<Run> runs = [];
            foreach (IndexRun run in entry.Runs)
            {
                if (FenceTable.TryOpen(run, stride, asked.Layout, out FenceTable? table, out _)
                    && run.EndBlock <= (ulong)blocks)
                {
                    runs.Add(new Run(run, table!));
                }
            }

            if (runs.Count > 0)
            {
                columns[key] = new Column(
                    hashed ? Placing.Hashes : entry.Kind == IndexKinds.SortedRuns ? Placing.Rows : Placing.Blocks,
                    asked, storage, runs, blocks);
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
                || !equalities.ContainsKey(path)
                || !KeyLayout.TryOf(dtype, out KeyLayout layout))
            {
                continue;
            }

            Asked asked = prepared.For(path, layout, fold: false, predicates: null, hashed: false);
            if (asked.Literals > 0)
            {
                columns[path] = new Column(Placing.Blocks, asked, Storage(dtype), [], blocks)
                {
                    DictionaryPath = path,
                };
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
    /// <param name="file">The file's indexes: the open file, or what a read with no open has of it.</param>
    /// <param name="live">The mask, which only loses blocks.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal ValueTask RefineAsync(IIndexHost file, BlockMask live, CancellationToken cancellationToken) =>
        RefineAsync(file, live, scope: null, cancellationToken);

    /// <summary>
    /// Refines <paramref name="live"/> within <paramref name="scope"/>, the blocks a scan's rows reach, reading the segments
    /// that can hold the filter's keys there: a run or a chunk none of whose live blocks the scope holds is not read, and
    /// its blocks stay live, which the scan never reads. A scan of one part of a file then reads its part's structures,
    /// not the file's.
    /// </summary>
    /// <param name="file">The file's indexes: the open file, or what a read with no open has of it.</param>
    /// <param name="live">The mask, which only loses blocks.</param>
    /// <param name="scope">A mask over the same blocks, live where the scan's rows reach; null for the whole file.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async ValueTask RefineAsync(IIndexHost file, BlockMask live, BlockMask? scope, CancellationToken cancellationToken)
    {
        foreach (Column column in _columns.Values)
        {
            column.Rent();
        }

        try
        {
            await ClaimAsync(file, live, scope, cancellationToken).ConfigureAwait(false);
            Kill(live);
        }
        finally
        {
            foreach (Column column in _columns.Values)
            {
                column.Return();
            }
        }
    }

    /// <summary>Reads the runs and the dictionaries the scope reaches, and records what they claim.</summary>
    private async ValueTask ClaimAsync(IIndexHost file, BlockMask live, BlockMask? scope, CancellationToken cancellationToken)
    {
        using IndexBatches<(Column Column, Run Run, Fence Fence, int Low, int High, int[] Slots)> batches = new();
        foreach (Column column in _columns.Values)
        {
            // A dictionary is read from the chunks of an open file: a read with no open claims nothing by it.
            if (column.DictionaryPath is { } dictionary)
            {
                if (file is VortexFile open)
                {
                    await ProbeAsync(open, column, dictionary, live, scope, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            foreach (Run run in column.Runs)
            {
                if (!AnyLive(live, run.Meta) || (scope is not null && !AnyLive(scope, run.Meta)))
                {
                    continue;
                }

                // The segments that may hold a key, each with its keys: a page that does not read
                // claims nothing for the run, which is then not covered.
                List<(Fence Fence, int Low, int High)> fences;
                int pagesBefore = run.Table.PagesRead;
                try
                {
                    fences = await column.CandidatesAsync(file.IndexSourceOf(run.Meta.Origin), run.Table, cancellationToken).ConfigureAwait(false);
                }
                catch (VortexFormatException)
                {
                    continue;
                }
                finally
                {
                    Segments += run.Table.PagesRead - pagesBefore;
                }

                IndexBatches<(Column Column, Run Run, Fence Fence, int Low, int High, int[] Slots)>.Batch batch = batches.For(run.Meta);
                foreach ((Fence fence, int low, int high) in fences)
                {
                    int[] slots = new int[column.Stride];
                    for (int array = 0; array < column.Stride; array++)
                    {
                        slots[array] = batch.Add(fence.Regions[array]);
                    }

                    batch.Wanted.Add((column, run, fence, low, high, slots));
                }

                // Every run consulted starts as "proves every key absent"; the lookups below lift
                // that for the blocks that hold a key, and a lookup that fails lifts it whole.
                column.Cover(run.Meta);
            }
        }

        foreach ((int origin, IndexBatches<(Column Column, Run Run, Fence Fence, int Low, int High, int[] Slots)>.Batch batch) in batches.ByOrigin)
        {
            SegmentRequestSet requests = batch.Requests;
            if (requests.Count == 0)
            {
                continue;
            }

            // The runs' segments named, which the session's synopsis cache keeps as it keeps the directory.
            ISegmentReader reader = file.IndexSourceOf(origin);
            Segments += Scanning.ScanCounters.Unread(requests, reader, out long bytes);
            Bytes += bytes;
            await reader.ReadRegionsAsync(requests, batch.Regions, cancellationToken).ConfigureAwait(false);
            using ScanContext context = file.CreateIndexContext(origin);
            foreach ((Column column, Run run, Fence fence, int low, int high, int[] slots) in batch.Wanted)
            {
                context.ResetBatch();
                if (!Intact(requests, slots, fence)
                    || !column.Lookup(context, requests, slots, run, fence, low, high, live.BlockRows))
                {
                    column.Uncover(run.Meta);
                }
            }
        }
    }

    /// <summary>Kills the blocks the claims prove the filter false in, a word of blocks at a time.</summary>
    private void Kill(BlockMask live)
    {
        int words = live.Words.Length;
        ulong[] rented = ArrayPool<ulong>.Shared.Rent(words);
        try
        {
            Span<ulong> kept = rented.AsSpan(0, words);
            Absent(_filter, kept);
            for (int i = 0; i < kept.Length; i++)
            {
                kept[i] = ~kept[i];
            }

            live.Keep(kept);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
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

    private static bool AnyLive(BlockMask live, IndexRun run) =>
        live.AnyLiveBlocks((long)run.FirstBlock, (long)run.EndBlock);

    // ------------------------------------------------------------------------------ the proof

    /// <summary>
    /// Writes into <paramref name="absent"/> the blocks the claims prove <paramref name="expr"/>
    /// false in: a question's where one of its presence sets is absent, a conjunction's where
    /// either side is, a disjunction's where both are.
    /// </summary>
    private void Absent(VortexExpr expr, Span<ulong> absent)
    {
        if (expr is LogicalExpr logical)
        {
            Absent(logical.Left, absent);
            ulong[] rented = ArrayPool<ulong>.Shared.Rent(absent.Length);
            try
            {
                Span<ulong> right = rented.AsSpan(0, absent.Length);
                Absent(logical.Right, right);
                for (int i = 0; i < absent.Length; i++)
                {
                    absent[i] = logical.IsAnd ? absent[i] | right[i] : absent[i] & right[i];
                }
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(rented);
            }

            return;
        }

        absent.Clear();
        if (_leaves.TryGetValue(expr, out Leaf leaf))
        {
            for (int set = leaf.First; set < leaf.First + leaf.Sets; set++)
            {
                leaf.Column.OrAbsent(set, absent);
            }
        }
    }

    /// <summary>
    /// Fills a dictionary-backed column's claims: reads the dictionaries of the column's chunks that hold a live block
    /// inside the scope, together, and asks each one which of the filter's literals it holds.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="column">The column, whose claims are empty until this runs.</param>
    /// <param name="path">Its path.</param>
    /// <param name="live">The mask as the earlier structures left it.</param>
    /// <param name="scope">The blocks the scan's rows reach, or null for the whole file.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <remarks>
    /// A chunk that is not a dictionary claims nothing, and neither does a file whose probe leaves blocks uncovered: the
    /// column then proves no literal absent there -- which is what an index that cannot be read must do.
    /// </remarks>
    private async ValueTask ProbeAsync(
        VortexFile file, Column column, string path, BlockMask live, BlockMask? scope, CancellationToken cancellationToken)
    {
        bool Wanted(long start, long rows)
        {
            RowRange chunk = new RowRange(start, start + rows);
            return live.AnyLive(chunk) && (scope is null || scope.AnyLive(chunk));
        }

        // A literal's key, then each float zero's other, which the dictionary may hold instead.
        int literals = column.Asked.Literals;
        byte[]?[] keys = new byte[]?[2 * literals];
        column.Asked.Keys.CopyTo(keys, 0);
        column.Asked.OtherZeros.CopyTo(keys, literals);
        if (await Keys.SortedRunsSource.ClaimDictionariesAsync(file, path, keys, Wanted, cancellationToken).ConfigureAwait(false) is not { } claims)
        {
            return;
        }

        Segments += claims.Read;
        Bytes += claims.Bytes;
        for (int chunk = 0; chunk < claims.Count; chunk++)
        {
            column.Dictionary(claims, chunk, live.BlockRows);
        }
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
            case ComparisonExpr { Op: ComparisonOp.Equal, Field: not Compute.FunctionFieldExpr } equal:
                Add(into, seen, equal.Field.Path, equal.Value);
                break;
            case InExpr { Field: not Compute.FunctionFieldExpr } @in:
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

            // A path's equality is asked of the entry of its read's class, by the read's name.
            case Compute.VariantFilterExpr { Inner: ComparisonExpr { Op: ComparisonOp.Equal, Field: Compute.VariantFieldExpr path } equal }:
                Add(into, seen, path.Key, equal.Value);
                break;
            case Compute.VariantFilterExpr { Inner: InExpr { Field: Compute.VariantFieldExpr path } members }:
                foreach (FilterLiteral value in members.Values)
                {
                    Add(into, seen, path.Key, value);
                }

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

    /// <summary>
    /// The name and the type an index entry of a variant's path answers under: an entry holds the values of one class, so
    /// it answers the read of that class alone, under that read's name, never the variant column's. False for an entry no
    /// predicate of a path asks: a text index's, or one of several keys.
    /// </summary>
    /// <param name="entry">The entry, of a variant's path.</param>
    /// <param name="trigrams">Whether the entry is a text index's.</param>
    /// <param name="path">The variant column's dotted path, replaced by the read's name.</param>
    /// <param name="dtype">The variant column's dtype, replaced by the class's.</param>
    /// <param name="classTypes">The arena the class's dtype is made in, made at the first.</param>
    internal static bool TryResolveVariant(IndexEntry entry, bool trigrams, ref string path, ref DType dtype, ref DTypeArena? classTypes)
    {
        if (trigrams || entry.VariantPath is not [string variantKey] || entry.VariantClass is not { } @class)
        {
            return false;
        }

        path = new Compute.VariantFieldExpr(new ColumnFieldExpr(path), [Compute.VariantStep.Of(variantKey)], Writing.IndexWriter.ReadOf(@class)).Key;
        dtype = Writing.IndexWriter.ClassType(classTypes ??= new DTypeArena(), @class);
        return true;
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

    /// <summary>Where a column's runs place a key.</summary>
    private enum Placing
    {
        /// <summary>At the blocks it occurs in, as postings do.</summary>
        Blocks,

        /// <summary>At its rows, as sorted runs do.</summary>
        Rows,

        /// <summary>At the blocks its hash occurs in, as hashed postings do: a superset, a false block now and then.</summary>
        Hashes,
    }

    /// <summary>A question of the filter a column answers: its presence sets, <paramref name="Sets"/> from <paramref name="First"/>.</summary>
    private readonly record struct Leaf(Column Column, int First, int Sets);

    /// <summary>A descent's comparison: a fence whose last key comes before the key.</summary>
    private readonly struct KeyProbe(KeyLayout layout, byte[] key) : IFenceProbe
    {
        public bool Below(ReadOnlySpan<byte> max) => layout.Compare(max, key) < 0;
    }

    /// <summary>
    /// What one filter asks of the columns' locating indexes, whatever the file: the literals each
    /// column is asked for, and what each column layout makes of them.
    /// </summary>
    private sealed class Prepared
    {
        private readonly VortexExpr _filter;
        private readonly Dictionary<(string Key, KeyLayout Layout, bool Fold, bool Hashed), Asked> _asked = [];

        internal Prepared(VortexExpr filter)
        {
            _filter = filter;
            CollectEqualities(filter, Equalities, new Dictionary<string, HashSet<FilterLiteral>>(StringComparer.Ordinal), Matches);
        }

        /// <summary>Per column path, the literals its equalities and <c>IN</c> lists name, each once.</summary>
        internal Dictionary<string, List<FilterLiteral>> Equalities { get; } = new Dictionary<string, List<FilterLiteral>>(StringComparer.Ordinal);

        /// <summary>Per column path, its text matches.</summary>
        internal Dictionary<string, List<StringMatchExpr>> Matches { get; } = new Dictionary<string, List<StringMatchExpr>>(StringComparer.Ordinal);

        /// <summary>
        /// What the column at <paramref name="key"/> is asked under <paramref name="layout"/>, built by
        /// the first file that needs it. Two lanes may build it at once: both are equal, and one is kept.
        /// </summary>
        /// <param name="key">The column's key: its path, or its trigram key.</param>
        /// <param name="layout">How the column's index orders its keys.</param>
        /// <param name="fold">For a text index, whether its trigrams were folded.</param>
        /// <param name="predicates">For a text index, the matches whose trigrams it is asked for; null for values.</param>
        /// <param name="hashed">Whether the index holds the keys' hashes rather than the keys.</param>
        internal Asked For(string key, KeyLayout layout, bool fold, List<StringMatchExpr>? predicates, bool hashed)
        {
            (string, KeyLayout, bool, bool) id = (key, layout, fold, hashed);
            lock (_asked)
            {
                if (_asked.TryGetValue(id, out Asked? known))
                {
                    return known;
                }
            }

            Asked built = new Asked(_filter, key, layout, fold, predicates is null ? Equalities[key] : TrigramsOf(predicates, fold), hashed);
            lock (_asked)
            {
                return _asked.TryAdd(id, built) ? built : _asked[id];
            }
        }

        /// <summary>
        /// The trigrams the predicates require, folded as the index was, each once: the literals of a
        /// text index, whose keys are trigrams typed binary.
        /// </summary>
        private static List<FilterLiteral> TrigramsOf(List<StringMatchExpr> predicates, bool fold)
        {
            List<FilterLiteral> literals = [];
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

            return literals;
        }
    }

    /// <summary>
    /// What a filter asks of one column's locating index, whatever the file: its literals, their keys
    /// in the column's layout and in the runs' order, and the presence sets each question feeds.
    /// </summary>
    /// <remarks>
    /// Built once per filter and layout and kept with the filter, as the zone maps keep an
    /// <c>IN</c>'s ordered candidates: for a large set, encoding, indexing and sorting it costs more
    /// than the lookups, and a dataset would pay it again at each of its objects. Nothing changes it
    /// once built, so the lanes share it without a lock.
    /// </remarks>
    private sealed class Asked
    {
        /// <summary>Each literal's slot, the first when the filter names one twice.</summary>
        private readonly Dictionary<FilterLiteral, int> _slots;

        // Per literal slot, the presence sets it feeds, a list threaded through two arrays: each
        // slot's first feed, and per feed its set and the slot's next feed.
        private readonly int[] _firstFeed;
        private readonly List<int> _feedSet = [];
        private readonly List<int> _nextFeed = [];

        /// <summary>Per presence set, whether it can prove absence: its question names keys, and every one encodes.</summary>
        private readonly List<bool> _keyed = [];

        /// <remarks>
        /// For hashed postings each key is the XXH3-64 of the literal's bytes in the column's layout,
        /// the bytes the writer hashed, as eight bytes in the order of the runs' keys.
        /// </remarks>
        internal Asked(VortexExpr filter, string key, KeyLayout layout, bool fold, List<FilterLiteral> literals, bool hashed)
        {
            Layout = hashed ? KeyRunOptions.HashedLayout : layout;
            Fold = fold;
            _slots = new Dictionary<FilterLiteral, int>(literals.Count);
            Keys = new byte[]?[literals.Count];
            OtherZeros = new byte[]?[literals.Count];
            _firstFeed = new int[literals.Count];
            _firstFeed.AsSpan().Fill(-1);
            Span<byte> scratch = stackalloc byte[sizeof(ulong)];
            Span<byte> zero = stackalloc byte[sizeof(ulong)];
            for (int i = 0; i < literals.Count; i++)
            {
                _slots.TryAdd(literals[i], i);
                if (layout.TryEncode(literals[i], scratch, out ReadOnlySpan<byte> encoded, zero, out bool hasOtherZero))
                {
                    Keys[i] = hashed ? Hashed(encoded) : encoded.ToArray();
                    OtherZeros[i] = !hasOtherZero ? null : hashed ? Hashed(zero[..layout.Width]) : zero[..layout.Width].ToArray();
                }
            }

            Plan(filter, key);
            FirstFeed = _firstFeed;
            FeedSet = [.. _feedSet];
            NextFeed = [.. _nextFeed];
            Sorted = Sort();
        }

        /// <summary>A key of hashed postings: the hash of a value's bytes, eight bytes little-endian.</summary>
        private static byte[] Hashed(ReadOnlySpan<byte> value)
        {
            byte[] key = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(key, System.IO.Hashing.XxHash3.HashToUInt64(value));
            return key;
        }

        /// <summary>How the index orders its keys.</summary>
        internal KeyLayout Layout { get; }

        /// <summary>For a trigram index, whether its trigrams were folded.</summary>
        internal bool Fold { get; }

        /// <summary>The literals asked for.</summary>
        internal int Literals => Keys.Length;

        /// <summary>Each literal's key, or null when it does not encode exactly.</summary>
        internal byte[]?[] Keys { get; }

        /// <summary>For a float zero, the other zero's key.</summary>
        internal byte[]?[] OtherZeros { get; }

        /// <summary>Per literal, its first feed, or -1.</summary>
        internal int[] FirstFeed { get; }

        /// <summary>Per feed, the presence set it fills.</summary>
        internal int[] FeedSet { get; }

        /// <summary>Per feed, the literal's next feed, or -1.</summary>
        internal int[] NextFeed { get; }

        /// <summary>The presence sets.</summary>
        internal int Sets => _keyed.Count;

        /// <summary>
        /// The keys the filter asks for, in the runs' own order, with their sort keys: the
        /// merge-join's left side.
        /// </summary>
        internal (int Literal, byte[] Key, ulong Sort)[] Sorted { get; }

        /// <summary>Each question of the filter the column answers, by reference: its presence sets, <c>Sets</c> from <c>First</c>.</summary>
        internal Dictionary<VortexExpr, (int First, int Sets)> Questions { get; } =
            new Dictionary<VortexExpr, (int First, int Sets)>(ReferenceEqualityComparer.Instance);

        /// <summary>Whether presence set <paramref name="set"/> can prove absence.</summary>
        internal bool Keyed(int set) => _keyed[set];

        /// <summary>
        /// Records which presence sets each question of the filter on this column reads: an
        /// equality or an <c>IN</c> reads one, fed by its literals; a text match one per trigram it
        /// requires.
        /// </summary>
        private void Plan(VortexExpr expr, string key)
        {
            switch (expr)
            {
                case LogicalExpr logical:
                    Plan(logical.Left, key);
                    Plan(logical.Right, key);
                    break;

                // A function of a column is not the column, whose keys the index holds.
                case ComparisonExpr { Op: ComparisonOp.Equal, Field: not Compute.FunctionFieldExpr } equal when equal.Field.Path == key:
                    Questions[expr] = (Ask(equal.Value), 1);
                    break;

                case InExpr { Field: not Compute.FunctionFieldExpr } @in when @in.Field.Path == key:
                    Questions[expr] = (Ask(@in.Values), 1);
                    break;

                // The fence is the question: the walk over the filter stops at it as at a leaf.
                case Compute.VariantFilterExpr { Inner: ComparisonExpr { Op: ComparisonOp.Equal, Field: Compute.VariantFieldExpr path } equal } when path.Key == key:
                    Questions[expr] = (Ask(equal.Value), 1);
                    break;

                case Compute.VariantFilterExpr { Inner: InExpr { Field: Compute.VariantFieldExpr path } members } when path.Key == key:
                    Questions[expr] = (Ask(members.Values), 1);
                    break;

                case StringMatchExpr match when TrigramKey(match.Field.Path) == key:
                {
                    // Absent as soon as one required trigram is: a matching value holds them all.
                    int first = -1;
                    int count = 0;
                    foreach (byte[] trigram in Trigrams.Required(match, Fold))
                    {
                        int set = Ask(FilterLiteral.From(trigram));
                        first = count++ == 0 ? set : first;
                    }

                    Questions[expr] = (first, count);
                    break;
                }

                default:
                    break;
            }
        }

        /// <summary>A question about one literal: a presence set it feeds.</summary>
        private int Ask(FilterLiteral value)
        {
            int set = _keyed.Count;
            _keyed.Add(Feed(value, set));
            return set;
        }

        /// <summary>
        /// A question about any of <paramref name="values"/>: one presence set they all feed. It
        /// proves absence only where it names a literal and every one of them encodes.
        /// </summary>
        private int Ask(IReadOnlyList<FilterLiteral> values)
        {
            int set = _keyed.Count;
            bool keyed = values.Count > 0;
            for (int i = 0; i < values.Count; i++)
            {
                keyed &= Feed(values[i], set);
            }

            _keyed.Add(keyed);
            return set;
        }

        private bool Feed(FilterLiteral value, int set)
        {
            if (!_slots.TryGetValue(value, out int slot))
            {
                return false;
            }

            // A literal a list names twice feeds its set once.
            int head = _firstFeed[slot];
            if (head < 0 || _feedSet[head] != set)
            {
                _feedSet.Add(set);
                _nextFeed.Add(head);
                _firstFeed[slot] = _feedSet.Count - 1;
            }

            return Keys[slot] is not null;
        }

        /// <summary>The keys in the runs' order, each with its sort key.</summary>
        /// <remarks>
        /// A float's second zero is a key of its own here, pointing at the same literal: two
        /// entries a run may hold separately, one answer to give.
        /// </remarks>
        private (int Literal, byte[] Key, ulong Sort)[] Sort()
        {
            int count = 0;
            for (int i = 0; i < Keys.Length; i++)
            {
                count += (Keys[i] is null ? 0 : 1) + (OtherZeros[i] is null ? 0 : 1);
            }

            (int Literal, byte[] Key, ulong Sort)[] sorted = count == 0 ? [] : new (int, byte[], ulong)[count];
            int at = 0;
            for (int i = 0; i < Keys.Length; i++)
            {
                Add(sorted, ref at, i, Keys[i]);
                Add(sorted, ref at, i, OtherZeros[i]);
            }

            SpanSort.Sort(sorted.AsSpan(), new ByKey(Layout));
            return sorted;

            void Add((int Literal, byte[] Key, ulong Sort)[] into, ref int next, int literal, byte[]? key)
            {
                if (key is not null)
                {
                    into[next++] = (literal, key, Layout.Shape == KeyShape.Bytes ? 0 : Layout.SortKey(key));
                }
            }
        }

        /// <summary>Keys in the runs' order: their bytes for a bytes key, their sort key otherwise.</summary>
        private readonly struct ByKey : IComparer<(int Literal, byte[] Key, ulong Sort)>
        {
            private readonly KeyLayout _layout;

            internal ByKey(KeyLayout layout) => _layout = layout;

            public int Compare((int Literal, byte[] Key, ulong Sort) a, (int Literal, byte[] Key, ulong Sort) b) =>
                _layout.Shape == KeyShape.Bytes ? _layout.Compare(a.Key, b.Key) : a.Sort.CompareTo(b.Sort);
        }
    }

    /// <summary>
    /// One column's runs in one file and what they claim: the blocks they cover, and per question of
    /// the filter the blocks where one of its keys was seen. A covered block holds none of a
    /// question's keys but those its presence set records, so the question is false there.
    /// </summary>
    private sealed class Column
    {
        private readonly bool _rows;
        private readonly bool _hashes;
        private readonly KeyLayout _layout;

        /// <summary>The dtypes a segment's arrays decode as: its keys, and its offsets, block lists or rows.</summary>
        private readonly DType _keyType;
        private readonly DType _u32;
        private readonly DType _u64;

        /// <summary>The file's blocks, and the words of a bit per block.</summary>
        private readonly int _blocks;
        private readonly int _words;

        // While a refinement runs: the covered blocks, then each presence set, a bit per block.
        private ulong[]? _bits;

        /// <summary>One segment's matches: per literal, the entries equal to its key.</summary>
        private readonly List<(int Literal, int Low, int High)> _matches = [];

        /// <summary>Whether the runs place each key at its rows, as sorted runs do, rather than at its blocks.</summary>
        internal bool Rows => _rows;

        internal Column(Placing placing, Asked asked, DType storage, List<Run> runs, int blocks)
        {
            Asked = asked;
            _rows = placing == Placing.Rows;
            _hashes = placing == Placing.Hashes;
            _layout = asked.Layout;
            DTypeArena types = new DTypeArena();
            _keyType = storage.Kind == DTypeKind.Primitive
                ? types.Primitive(storage.PType, Nullability.NonNullable)
                : storage.Kind == DTypeKind.Utf8
                    ? types.Utf8(Nullability.NonNullable)
                    : types.Binary(Nullability.NonNullable);
            _u32 = types.Primitive(PType.U32, Nullability.NonNullable);
            _u64 = types.Primitive(PType.U64, Nullability.NonNullable);
            Runs = runs;
            _blocks = blocks;
            _words = (blocks + 63) >> 6;
            CoversAll = Covers(runs, blocks);
        }

        /// <summary>Whether the runs place each key at the blocks its hash occurs in, as hashed postings do.</summary>
        internal bool Hashes => _hashes;

        /// <summary>Whether the runs cover every block of the file, so that their claims speak for each one.</summary>
        internal bool CoversAll { get; }

        /// <summary>What the filter asks of the column, shared with every file it is asked of.</summary>
        internal Asked Asked { get; }

        internal List<Run> Runs { get; }

        /// <summary>
        /// The column, when its claims come from the chunks' dictionaries rather than from an
        /// index's runs; null for every other kind.
        /// </summary>
        internal string? DictionaryPath { get; init; }

        internal int Stride => _hashes
            ? KeyRunOptions.HashedStride
            : _rows ? KeyRunOptions.SortedStride : KeyRunOptions.PostingsStride;

        /// <summary>
        /// Whether <paramref name="runs"/>, in the order of their blocks as a writer lists them, leave none of the
        /// file's <paramref name="blocks"/> uncovered; runs in another order are taken for a gap.
        /// </summary>
        private static bool Covers(List<Run> runs, int blocks)
        {
            ulong next = 0;
            foreach (Run run in runs)
            {
                if (run.Meta.FirstBlock > next)
                {
                    return false;
                }

                next = Math.Max(next, run.Meta.EndBlock);
            }

            return runs.Count > 0 && next >= (ulong)blocks;
        }

        /// <summary>
        /// The segments of <paramref name="table"/> whose range may hold one of the column's keys,
        /// each once, in segment order, with the keys of its range: <c>[Low, High)</c> of the keys
        /// in the runs' order.
        /// </summary>
        /// <remarks>
        /// The keys and the segments are walked together, as two sorted lists are merged, so that
        /// each segment is asked about its own keys only. A key past the segment at hand is reached
        /// by steps to the next segments while they cost less than a descent, which compares about
        /// log₂ of the segments, and by a descent past that: keys thick in the run cost a step each,
        /// keys far apart a descent each. A run in pages is only descended, since a step could read
        /// a page no key needs, which a descent never does.
        /// </remarks>
        internal async ValueTask<List<(Fence Fence, int Low, int High)>> CandidatesAsync(
            ISegmentReader source, FenceTable table, CancellationToken cancellationToken)
        {
            List<(Fence Fence, int Low, int High)> found = [];
            (int Literal, byte[] Key, ulong Sort)[] sorted = Asked.Sorted;
            long count = table.SegmentCount;
            int reach = table.Paged ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)count);
            int next = 0;
            long segment = 0;
            while (next < sorted.Length && segment < count)
            {
                // The first segment from here whose last key does not come before the next key.
                byte[] key = sorted[next].Key;
                Fence fence = default;
                bool reached = false;
                for (int step = 0; step < reach && segment < count; step++, segment++)
                {
                    fence = await table.GetAsync(source, segment, cancellationToken).ConfigureAwait(false);
                    if (fence.Bounds.Entries > 0 && _layout.Compare(fence.Bounds.Max, key) >= 0)
                    {
                        reached = true;
                        break;
                    }
                }

                if (!reached)
                {
                    long bound = await table.LowerBoundAsync(source, new KeyProbe(_layout, key), cancellationToken).ConfigureAwait(false);
                    segment = Math.Max(segment, bound);
                    if (segment >= count)
                    {
                        break;
                    }

                    fence = await table.GetAsync(source, segment, cancellationToken).ConfigureAwait(false);
                }

                // Its keys: from the first that does not come before its first key, up to its last.
                // A key that falls between two segments is in neither, and is passed over here.
                int low = Seek(sorted, next, fence.Bounds.Min, past: false);
                int high = Seek(sorted, low, fence.Bounds.Max, past: true);
                if (high > low)
                {
                    found.Add((fence, low, high));
                }

                // A key equal to the segment's last may open the next segment too.
                next = Seek(sorted, low, fence.Bounds.Max, past: false);
                segment++;
            }

            return found;
        }

        /// <summary>
        /// The first of <paramref name="sorted"/> from <paramref name="from"/> that does not come
        /// before <paramref name="bound"/>, or that comes after it when <paramref name="past"/>:
        /// strides that double, then a binary search in the last, so that a short way costs a few
        /// comparisons and a long one its logarithm.
        /// </summary>
        private int Seek((int Literal, byte[] Key, ulong Sort)[] sorted, int from, byte[] bound, bool past)
        {
            ulong sort = _layout.Shape == KeyShape.Bytes ? 0 : _layout.SortKey(bound);
            int low = from;
            int high = from;
            int stride = 1;
            while (high < sorted.Length && Before(sorted[high], bound, sort, past))
            {
                low = high + 1;
                high += stride;
                stride <<= 1;
            }

            high = Math.Min(high, sorted.Length);
            while (low < high)
            {
                int mid = (low + high) >>> 1;
                if (Before(sorted[mid], bound, sort, past))
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        /// <summary>Whether a key comes before <paramref name="bound"/>, or does not pass it when <paramref name="past"/>.</summary>
        private bool Before((int Literal, byte[] Key, ulong Sort) pair, byte[] bound, ulong sort, bool past)
        {
            int sign = _layout.Shape == KeyShape.Bytes ? _layout.Compare(pair.Key, bound) : pair.Sort.CompareTo(sort);
            return past ? sign <= 0 : sign < 0;
        }

        /// <summary>
        /// Claims one chunk's blocks from its dictionary: a literal the chunk's values do not hold
        /// is absent from every block the chunk covers whole.
        /// </summary>
        /// <param name="claims">What the column's dictionaries the probe read hold: each literal's key, then each float zero's other.</param>
        /// <param name="chunk">The chunk, under the claims' count.</param>
        /// <param name="blockRows">Rows per block.</param>
        /// <remarks>
        /// Only the blocks the chunk covers whole are claimed. A dictionary says what its own rows
        /// hold and nothing about the rows around them, so a block a chunk shares with its
        /// neighbour is left live -- the writer aligns chunks to blocks, so this costs nothing
        /// there and stays right on a file whose chunks are not aligned.
        /// </remarks>
        internal void Dictionary(Keys.DictionaryClaims claims, int chunk, long blockRows)
        {
            (long firstRow, long rows) = claims.Extent(chunk);
            long last = firstRow + rows;
            ulong first = (ulong)((firstRow + blockRows - 1) / blockRows);

            // The chunk's last block counts as whole when the file holds no row past it: the tail
            // of the file's last block is not another chunk's, it is nothing at all.
            ulong end = (ulong)((last + blockRows - 1) / blockRows >= _blocks ? _blocks : last / blockRows);
            if (end <= first)
            {
                return;
            }

            Fill(Covered, first, end, true);

            // The two zeros of a float are one literal, and a dictionary may hold either.
            int literals = Asked.Literals;
            foreach (int key in claims.HeldBy(chunk))
            {
                int literal = key < literals ? key : key - literals;
                for (int feed = Asked.FirstFeed[literal]; feed >= 0; feed = Asked.NextFeed[feed])
                {
                    Fill(Set(Asked.FeedSet[feed]), first, end, true);
                }
            }
        }

        /// <summary>Claims every block of the run: none holds a key but where a lookup finds one.</summary>
        internal void Cover(IndexRun run) => Fill(Covered, run.FirstBlock, run.EndBlock, true);

        /// <summary>Lifts the run's claim: a lookup in it failed.</summary>
        internal void Uncover(IndexRun run) => Fill(Covered, run.FirstBlock, run.EndBlock, false);

        /// <summary>Takes the bits a refinement fills, no block claimed.</summary>
        internal void Rent()
        {
            int length = (1 + Asked.Sets) * _words;
            _bits = ArrayPool<ulong>.Shared.Rent(length);
            _bits.AsSpan(0, length).Clear();
        }

        /// <summary>Gives the bits back once the refinement has killed its blocks.</summary>
        internal void Return()
        {
            if (_bits is not null)
            {
                ArrayPool<ulong>.Shared.Return(_bits);
                _bits = null;
            }
        }

        /// <summary>
        /// Adds to <paramref name="absent"/> the covered blocks where presence set
        /// <paramref name="set"/> saw none of its keys, when the set can prove absence.
        /// </summary>
        internal void OrAbsent(int set, Span<ulong> absent)
        {
            if (!Asked.Keyed(set))
            {
                return;
            }

            ReadOnlySpan<ulong> covered = Covered;
            ReadOnlySpan<ulong> present = Set(set);
            int words = Math.Min(absent.Length, _words);
            for (int i = 0; i < words; i++)
            {
                absent[i] |= covered[i] & ~present[i];
            }
        }

        private Span<ulong> Covered => _bits.AsSpan(0, _words);

        private Span<ulong> Set(int set) => _bits.AsSpan((1 + set) * _words, _words);

        /// <summary>Sets or clears the bits of blocks [<paramref name="first"/>, <paramref name="end"/>).</summary>
        private static void Fill(Span<ulong> bits, ulong first, ulong end, bool value) =>
            BitmapKernels.FillRange(MemoryMarshal.AsBytes(bits), (int)first, (int)(end - first), value);

        /// <summary>
        /// Decodes one segment's arrays and marks, for every literal, the blocks that hold it.
        /// </summary>
        /// <returns>Whether the segment made sense.</returns>
        internal bool Lookup(ScanContext context, SegmentRequestSet requests, int[] slots, Run run, Fence fence, int low, int high, long blockRows)
        {
            ulong expected = fence.Bounds.Entries;
            if (expected > int.MaxValue)
            {
                return false;
            }

            int entries = (int)expected;
            try
            {
                if (_hashes)
                {
                    return LookupHashes(context, requests, slots, run, fence, low, high, entries);
                }

                int keys = Decode(context, requests.GetBuffer(slots[0]), _keyType, entries);
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
                    int rowsNode = Decode(context, requests.GetBuffer(slots[1]), wide ? _u64 : _u32, entries);
                    CanonicalNode rows = context.Canonical.GetNode(rowsNode);
                    if (rows.Kind != CanonicalKind.Primitive || rows.PType != width || rows.Length != entries)
                    {
                        return false;
                    }

                    foreach ((int literal, int first, int end) in Match(keyNode, low, high))
                    {
                        if (wide)
                        {
                            MarkRows(literal, first, end, rows.Values.Cast<ulong>(), firstBlock, blockRows);
                        }
                        else
                        {
                            MarkRows(literal, first, end, rows.Values.Cast<uint>(), firstBlock, blockRows);
                        }
                    }

                    return true;
                }

                int offsetsNode = Decode(context, requests.GetBuffer(slots[1]), _u32, entries + 1);
                CanonicalNode offsetArray = context.Canonical.GetNode(offsetsNode);
                if (offsetArray.Kind != CanonicalKind.Primitive || offsetArray.PType != PType.U32 || offsetArray.Length != entries + 1)
                {
                    return false;
                }

                ReadOnlySpan<uint> starts = offsetArray.Values.Cast<uint>();
                int listLength = checked((int)starts[entries]);
                int listsNode = Decode(context, requests.GetBuffer(slots[2]), _u32, listLength);
                CanonicalNode lists = context.Canonical.GetNode(listsNode);
                if (lists.Kind != CanonicalKind.Primitive || lists.PType != PType.U32 || lists.Length != listLength)
                {
                    return false;
                }

                ReadOnlySpan<uint> blocks = lists.Values.Cast<uint>();
                foreach ((int literal, int first, int end) in Match(keyNode, low, high))
                {
                    MarkPostings(literal, first, end, starts, blocks, firstBlock);
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

        /// <summary>
        /// Decodes one segment of hashed postings and marks, for every literal, the blocks of the
        /// pairs whose cut hash is the literal's: the gaps summed from the segment's first hash, cut
        /// as the run's pair count says, walked beside the literals in hash order.
        /// </summary>
        /// <returns>Whether the segment made sense.</returns>
        private bool LookupHashes(ScanContext context, SegmentRequestSet requests, int[] slots, Run run, Fence fence, int low, int high, int entries)
        {
            if (fence.Bounds.Min.Length != sizeof(ulong))
            {
                return false;
            }

            CanonicalNode gapArray = context.Canonical.GetNode(Decode(context, requests.GetBuffer(slots[0]), _u32, entries));
            if (gapArray.Kind != CanonicalKind.Primitive || gapArray.PType != PType.U32 || gapArray.Length != entries)
            {
                return false;
            }

            CanonicalNode blockArray = context.Canonical.GetNode(Decode(context, requests.GetBuffer(slots[1]), _u32, entries));
            if (blockArray.Kind != CanonicalKind.Primitive || blockArray.PType != PType.U32 || blockArray.Length != entries)
            {
                return false;
            }

            ReadOnlySpan<uint> gaps = gapArray.Values.Cast<uint>();
            ReadOnlySpan<uint> blocks = blockArray.Values.Cast<uint>();
            ReadOnlySpan<(int Literal, byte[] Key, ulong Sort)> wanted = Asked.Sorted.AsSpan(low, high - low);
            int shift = KeyRunOptions.HashShift(run.Meta.EntryCount);
            ulong firstBlock = run.Meta.FirstBlock;
            ulong cut = BinaryPrimitives.ReadUInt64LittleEndian(fence.Bounds.Min) >> shift;
            int pair = 0;
            for (int entry = 0; entry < entries && pair < wanted.Length; entry++)
            {
                cut += gaps[entry];
                while (pair < wanted.Length && (wanted[pair].Sort >> shift) < cut)
                {
                    pair++;
                }

                // Every literal cut to this entry's hash: the one asked, or one that shares its bits.
                for (int next = pair; next < wanted.Length && (wanted[next].Sort >> shift) == cut; next++)
                {
                    Present(wanted[next].Literal, firstBlock + blocks[entry]);
                }
            }

            return true;
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
            // The literal's sets are shared by a float's two zeros; a bad block id claims nothing.
            if (block >= (ulong)_blocks)
            {
                return;
            }

            ulong[] bits = _bits!;
            int word = (int)(block >> 6);
            ulong bit = 1UL << (int)(block & 63);
            int[] next = Asked.NextFeed;
            int[] sets = Asked.FeedSet;
            for (int feed = Asked.FirstFeed[literal]; feed >= 0; feed = next[feed])
            {
                bits[((1 + sets[feed]) * _words) + word] |= bit;
            }
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
        /// single integer comparison instead of a span walk. The keys are those of the segment's
        /// range only, so a large set costs each segment its own share of it.
        /// </remarks>
        /// <param name="keys">The segment's decoded keys.</param>
        /// <param name="from">The first of the sorted keys in the segment's range.</param>
        /// <param name="to">The one past the last.</param>
        private List<(int Literal, int Low, int High)> Match(CanonicalNode keys, int from, int to)
        {
            _matches.Clear();
            ReadOnlySpan<(int Literal, byte[] Key, ulong Sort)> sorted = Asked.Sorted.AsSpan(from, to - from);
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
