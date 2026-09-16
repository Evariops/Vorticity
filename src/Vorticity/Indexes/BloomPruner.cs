// The read side of `vorticity.bloom.sbbf.v1` (docs/10-indexes.md §5.1): a pruner in the scan's
// block-mask chain (docs/11-write-strategy.md §6.1), after the zone maps.
//
// ONLY EQUALITY PROVES ANYTHING. `x = v` kills a block whose filter does not hold v; `x IN (...)`
// one that holds none of them; an AND kills what any conjunct kills, an OR what every arm kills.
// Nothing else -- `!=`, an ordering, a string match, a NOT -- claims anything.
//
// THE LITERAL IS HASHED AS THE COLUMN STORES IT, and only when that is exact. The kernels compare in
// three domains -- i64, u64, f64 -- so `x = 5` on an i32 column is the four bytes of 5, and `x = 5.0`
// on an f32 column is the four bytes of 5.0f. A literal that does not convert exactly makes no
// claim, rather than a proof built on a rounding: past 2^53 several integers share a double, and
// hashing one of them would lose the others. A float zero asks for BOTH zeros, since the filter
// stores bit patterns and the scan's equality is IEEE; a NaN asks for nothing.
//
// COARSEST FIRST, AND A FINER LEVEL ONLY WHERE IT CAN STILL KILL (10 §4.3): the file filter, then
// the generations of the blocks still live, then those blocks' own filters, each level one
// coalesced read. A payload that is not what its entry says -- the wrong length, the wrong type --
// makes no claim for the blocks it covers: a lying index may cost pruning, never rows.
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

/// <summary>Kills the blocks a column's Bloom filters prove cannot hold an equality's value.</summary>
internal sealed class BloomPruner
{
    private readonly VortexExpr _filter;
    private readonly Dictionary<string, Column> _columns;

    private BloomPruner(VortexExpr filter, Dictionary<string, Column> columns)
    {
        _filter = filter;
        _columns = columns;
    }

    /// <summary>What consulting the filters cost: segments and bytes read.</summary>
    internal int Segments { get; private set; }

    /// <summary>The bytes of those segments.</summary>
    internal long Bytes { get; private set; }

    /// <summary>
    /// The pruner for <paramref name="filter"/>, or <see langword="null"/> when no column it tests
    /// for equality carries a usable Bloom entry.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The scan's predicate.</param>
    /// <param name="blockRows">The scan's block length; an entry at another length is ignored.</param>
    /// <param name="cancellationToken">Cancels the directory read.</param>
    internal static async ValueTask<BloomPruner?> BuildAsync(
        VortexFile file, VortexExpr filter, long blockRows, CancellationToken cancellationToken)
    {
        // A FILE WITHOUT A DIRECTORY PAYS NOTHING, not even the collectors: the read-path
        // allocation ceilings hold a filtered scan to the byte, and every file written before
        // step 12 is such a file.
        if (!file.HasIndexDirectory)
        {
            return null;
        }

        HashSet<string> equalities = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> matches = new HashSet<string>(StringComparer.Ordinal);
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

        DType schema = file.Schema;
        Dictionary<string, Column> columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (IndexEntry entry in directory.Entries)
        {
            bool trigrams = entry.Kind == IndexKinds.BloomNgram3;
            if ((!trigrams && entry.Kind != IndexKinds.BloomSbbf) || entry.BlockLength != (ulong)blockRows
                || !TryResolve(schema, entry.ColumnPath, out string path, out DType dtype)
                || !(trigrams ? matches : equalities).Contains(path)
                || !BloomIndexOptions.TryParse(entry.Options, out BloomIndexOptions? options))
            {
                continue;
            }

            string key = trigrams ? TrigramKey(path) : path;
            if (!columns.TryGetValue(key, out Column? column))
            {
                column = new Column(dtype, trigrams, options!.CaseInsensitive);
                columns[key] = column;
            }

            column.Levels.Add(new Level(entry, options!));
        }

        if (columns.Count == 0)
        {
            return null;
        }

        foreach (Column column in columns.Values)
        {
            // File, then generations, then blocks.
            column.Levels.Sort((a, b) => b.Options.Level.CompareTo(a.Options.Level));
        }

        return new BloomPruner(filter, columns);
    }

    /// <summary>
    /// Whether the file-level filters leave room for a match (10 §5.4): the multi-file question,
    /// answered with one read per filtered column and no zone map.
    /// </summary>
    /// <param name="file">The open file.</param>
    /// <param name="filter">The predicate.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns><see langword="false"/> only when a file-level filter proves no row can match.</returns>
    internal static async ValueTask<bool> FileMayMatchAsync(
        VortexFile file, VortexExpr filter, CancellationToken cancellationToken)
    {
        long blockRows = Scan.SplitPlan.NaturalBatchRows(file.LayoutTree);
        if (file.RowCount <= 0 || blockRows <= 0)
        {
            return true;
        }

        BloomPruner? pruner = await BuildAsync(file, filter, blockRows, cancellationToken).ConfigureAwait(false);
        if (pruner is null)
        {
            return true;
        }

        BlockMask live = new BlockMask(file.RowCount, blockRows);
        await pruner.RefineAsync(file, live, cancellationToken, BloomLevel.File).ConfigureAwait(false);
        return !live.IsEmpty;
    }

    /// <summary>Refines <paramref name="live"/>, reading the filters it needs level by level.</summary>
    /// <param name="file">The open file.</param>
    /// <param name="live">The mask, which only loses blocks.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <param name="finest">The finest level to consult.</param>
    internal async ValueTask RefineAsync(
        VortexFile file, BlockMask live, CancellationToken cancellationToken, BloomLevel finest = BloomLevel.Block)
    {
        for (int pass = (int)BloomLevel.File; pass >= (int)finest && !live.IsEmpty; pass--)
        {
            BloomLevel level = (BloomLevel)pass;
            using SegmentRequestSet requests = new SegmentRequestSet();
            List<(Level Level, int Run, int Slot)> wanted = [];
            foreach (Column column in _columns.Values)
            {
                foreach (Level candidate in column.Levels)
                {
                    if (candidate.Options.Level != level)
                    {
                        continue;
                    }

                    IReadOnlyList<IndexRun> runs = candidate.Entry.Runs;
                    for (int run = 0; run < runs.Count; run++)
                    {
                        if (candidate.Loaded.ContainsKey(run) || !AnyLive(live, runs[run]))
                        {
                            continue;
                        }

                        IndexSegment segment = runs[run].Payload[0];
                        int slot = requests.Add(new SegmentSpec(
                            segment.Offset, segment.Length, segment.AlignmentExponent, 0, 0));
                        wanted.Add((candidate, run, slot));
                    }
                }
            }

            if (wanted.Count > 0)
            {
                Segments += requests.Count;
                for (int i = 0; i < requests.Count; i++)
                {
                    Bytes += requests.GetSpec(i).Length;
                }

                await file.IndexSource.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
                using ScanContext context = file.CreateIndexContext();
                foreach ((Level candidate, int run, int slot) in wanted)
                {
                    candidate.Loaded[run] = Decode(context, requests.GetBuffer(slot), candidate, run);
                }
            }

            for (int block = 0; block < live.BlockCount; block++)
            {
                if (live.IsLive(block) && ProvesAbsent(_filter, block, level))
                {
                    live.Kill(block);
                }
            }
        }
    }

    /// <summary>
    /// The run's filter words, copied out of the segment, or <see langword="null"/> when the payload
    /// is not the u32 array of the length its entry declares.
    /// </summary>
    private static uint[]? Decode(ScanContext context, VortexBuffer segment, Level level, int run)
    {
        long expected = level.ExpectedWords(run);
        if (expected <= 0 || expected > int.MaxValue)
        {
            return null;
        }

        try
        {
            context.ResetBatch();
            context.Decode.LoadBlob(segment);
            DTypeArena types = new DTypeArena();
            DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
            ArrayNode root = context.Nodes.Root;
            int node = context.Decode.DecodeRoot(in root, u32, (int)expected);
            CanonicalNode array = context.Canonical.GetNode(node);
            if (array.Kind != CanonicalKind.Primitive || array.PType != PType.U32 || array.Length != expected)
            {
                return null;
            }

            return array.Values.Cast<uint>()[..(int)expected].ToArray();
        }
        catch (VortexFormatException)
        {
            return null;
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

    private bool ProvesAbsent(VortexExpr expr, int block, BloomLevel level)
    {
        switch (expr)
        {
            case LogicalExpr { IsAnd: true } and:
                return ProvesAbsent(and.Left, block, level) || ProvesAbsent(and.Right, block, level);

            case LogicalExpr or:
                return ProvesAbsent(or.Left, block, level) && ProvesAbsent(or.Right, block, level);

            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                return Absent(equal.Field.Path, equal.Value, block, level);

            case StringMatchExpr match:
                return AbsentTrigrams(match, block, level);

            case InExpr @in:
                foreach (FilterLiteral value in @in.Values)
                {
                    if (!Absent(@in.Field.Path, value, block, level))
                    {
                        return false;
                    }
                }

                return @in.Values.Count > 0;

            default:
                return false;
        }
    }

    private bool Absent(string path, FilterLiteral value, int block, BloomLevel level)
    {
        if (!_columns.TryGetValue(path, out Column? column))
        {
            return false;
        }

        foreach (Level candidate in column.Levels)
        {
            if (candidate.Options.Level != level || !candidate.TryFilter(block, out ReadOnlySpan<uint> words))
            {
                continue;
            }

            if (!column.Hashes(value, candidate.Options.Hash, out ulong first, out ulong second, out bool two))
            {
                return false;
            }

            bool held = SplitBlockBloom.Contains(words, first) || (two && SplitBlockBloom.Contains(words, second));
            if (!held)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A string predicate is absent from a block when one trigram it requires is absent from the
    /// block's trigram filter (10 §5.2); a predicate that requires none claims nothing.
    /// </summary>
    private bool AbsentTrigrams(StringMatchExpr match, int block, BloomLevel level)
    {
        if (!_columns.TryGetValue(TrigramKey(match.Field.Path), out Column? column))
        {
            return false;
        }

        foreach (Level candidate in column.Levels)
        {
            if (candidate.Options.Level != level || !candidate.TryFilter(block, out ReadOnlySpan<uint> words))
            {
                continue;
            }

            foreach (byte[] trigram in column.Required(match))
            {
                if (!SplitBlockBloom.Contains(words, SplitBlockBloom.Hash(trigram, candidate.Options.Hash)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The trigram entries of a column live beside its value entries, under their own key.</summary>
    private static string TrigramKey(string path) => path + "\0ngram3";

    private static void CollectEqualities(VortexExpr expr, HashSet<string> equalities, HashSet<string> matches)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                CollectEqualities(logical.Left, equalities, matches);
                CollectEqualities(logical.Right, equalities, matches);
                break;
            case ComparisonExpr { Op: ComparisonOp.Equal } equal:
                equalities.Add(equal.Field.Path);
                break;
            case InExpr @in:
                equalities.Add(@in.Field.Path);
                break;
            case StringMatchExpr match:
                matches.Add(match.Field.Path);
                break;
            default:
                break;
        }
    }

    private static bool TryResolve(DType schema, IReadOnlyList<uint> fields, out string path, out DType dtype)
    {
        path = string.Empty;
        dtype = schema;
        if (fields.Count == 0)
        {
            // The root column of a non-struct file, which a filter cannot name.
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

    /// <summary>One column's entries and how its values hash.</summary>
    /// <remarks>
    /// The literal's bytes come from <see cref="KeyLayout.TryEncode"/>, the one conversion every
    /// value index shares; a decimal has no layout, so a decimal filter claims nothing here.
    /// </remarks>
    private sealed class Column
    {
        private readonly bool _keyed;
        private readonly KeyLayout _layout;
        private readonly bool _fold;
        private readonly Dictionary<StringMatchExpr, List<byte[]>> _required = new(ReferenceEqualityComparer.Instance);

        internal Column(DType dtype, bool trigrams, bool fold)
        {
            _keyed = !trigrams && KeyLayout.TryOf(dtype, out _layout);
            _fold = fold;
        }

        internal List<Level> Levels { get; } = [];

        /// <summary>The trigrams a predicate requires, folded as this column's filters were, computed once.</summary>
        internal List<byte[]> Required(StringMatchExpr match)
        {
            if (!_required.TryGetValue(match, out List<byte[]>? trigrams))
            {
                trigrams = Trigrams.Required(match, _fold);
                _required[match] = trigrams;
            }

            return trigrams;
        }

        /// <summary>
        /// The hash (or the two, for a float zero) of <paramref name="value"/> as this column stores
        /// it; <see langword="false"/> when the conversion is not exact and nothing can be claimed.
        /// </summary>
        internal bool Hashes(FilterLiteral value, BloomHash hash, out ulong first, out ulong second, out bool two)
        {
            first = 0;
            second = 0;
            two = false;
            Span<byte> scratch = stackalloc byte[sizeof(ulong)];
            Span<byte> zero = stackalloc byte[sizeof(ulong)];
            if (!_keyed || !_layout.TryEncode(value, scratch, out ReadOnlySpan<byte> key, zero, out bool hasOtherZero))
            {
                return false;
            }

            first = SplitBlockBloom.Hash(key, hash);
            if (hasOtherZero)
            {
                second = SplitBlockBloom.Hash(zero[.._layout.Width], hash);
                two = true;
            }

            return true;
        }
    }

    /// <summary>One entry: its options, its runs, and the filters read so far.</summary>
    private sealed class Level(IndexEntry entry, BloomIndexOptions options)
    {
        private long[]? _blockOffsets;

        internal IndexEntry Entry { get; } = entry;

        internal BloomIndexOptions Options { get; } = options;

        /// <summary>Run index to its words; <see langword="null"/> for a payload that made no sense.</summary>
        internal Dictionary<int, uint[]?> Loaded { get; } = [];

        /// <summary>The words the run's payload must hold.</summary>
        internal long ExpectedWords(int run)
        {
            IndexRun meta = Entry.Runs[run];
            int[] counts = Options.FilterBlocks;
            long blocks = 0;
            if (Options.Level == BloomLevel.Block)
            {
                for (ulong block = meta.FirstBlock; block < meta.EndBlock; block++)
                {
                    if (block >= (ulong)counts.Length)
                    {
                        return -1;
                    }

                    blocks += counts[block];
                }
            }
            else
            {
                if (run >= counts.Length)
                {
                    return -1;
                }

                blocks = counts[run];
            }

            return blocks * SplitBlockBloom.WordsPerBlock;
        }

        /// <summary>The filter that covers <paramref name="block"/> at this level, when one was loaded.</summary>
        internal bool TryFilter(int block, out ReadOnlySpan<uint> words)
        {
            words = default;
            int run = RunOf(block);
            if (run < 0 || !Loaded.TryGetValue(run, out uint[]? all) || all is null)
            {
                return false;
            }

            if (Options.Level != BloomLevel.Block)
            {
                words = all;
                return all.Length > 0;
            }

            int[] counts = Options.FilterBlocks;
            if (counts[block] == 0)
            {
                return false;
            }

            _blockOffsets ??= Offsets(counts);
            long start = (_blockOffsets[block] - _blockOffsets[(int)Entry.Runs[run].FirstBlock]) * SplitBlockBloom.WordsPerBlock;
            words = all.AsSpan((int)start, counts[block] * SplitBlockBloom.WordsPerBlock);
            return true;
        }

        private static long[] Offsets(int[] counts)
        {
            long[] offsets = new long[counts.Length + 1];
            for (int i = 0; i < counts.Length; i++)
            {
                offsets[i + 1] = offsets[i] + counts[i];
            }

            return offsets;
        }

        private int RunOf(int block)
        {
            IReadOnlyList<IndexRun> runs = Entry.Runs;
            int low = 0;
            int high = runs.Count - 1;
            while (low <= high)
            {
                int mid = (low + high) >>> 1;
                IndexRun run = runs[mid];
                if ((ulong)block < run.FirstBlock)
                {
                    high = mid - 1;
                }
                else if ((ulong)block >= run.EndBlock)
                {
                    low = mid + 1;
                }
                else
                {
                    return mid;
                }
            }

            return -1;
        }
    }
}
