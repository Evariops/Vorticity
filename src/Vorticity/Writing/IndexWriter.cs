using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

/// <summary>
/// What binds an index fragment to the version of the file it indexes. <c>Hash</c> is the XXH3-128
/// of the file's bytes when the indexer happens to know it; no reader computes it.
/// </summary>
internal sealed record FragmentBinding(long Length, Guid? Identity, string? Token, UInt128? Hash, IReadOnlyList<string> Encodings);

/// <summary>
/// Builds the indexes of one file and the directory that lists them. A kind that cannot be built is
/// reported abandoned with its reason rather than skipped or thrown: an index is a hint whose
/// absence costs no correctness, and a caller who asked for one reads what happened to it.
/// </summary>
internal sealed class IndexWriter : IDisposable
{
    private readonly WritePolicy _policy;
    private readonly bool _isTabular;
    private readonly int _budgetPerMille;
    private readonly string[] _paths;
    private readonly IndexPolicy[] _columns;
    private readonly List<IndexBuilder>[] _builders;
    private readonly string?[] _refusals;
    private readonly long[] _columnBytes;
    private readonly int _fieldCount;
    private readonly IKeyEncoder? _keyEncoder;

    /// <summary>For a composite key's slot, the columns it encodes in key order, each a field-index path.</summary>
    private readonly int[][]?[] _keyFields;

    /// <summary>
    /// For a nested column's slot, its field-index path from the root; <see langword="null"/> when
    /// the policy names no nested column, which is every default write.
    /// </summary>
    private readonly int[]?[]? _nested;
    private CanonicalArena? _keySlices;
    private DTypeArena? _maskTypes;
    private bool _firstBlockClosed;

    /// <summary>
    /// The share of a column's written bytes, in parts per thousand, a Bloom filter may take before
    /// `Auto` gives it up.
    /// </summary>
    internal const int AutoBloomShare = 20;
    private readonly List<IndexEntry> _entries = [];
    private readonly List<IndexWriteReport> _reports = [];

    // Created on the first payload, so a policy of payload-free kinds never makes one.
    private ScanContext? _payloads;
    private DTypeArena? _payloadTypes;
    private long _payloadBytes;

    /// <summary>
    /// The budget is a share per mille of the data bytes. Rows per block is 0 when the caller's
    /// batches are the blocks, and a row span past <c>wideRowsAbove</c> writes rows at 64 bits.
    /// </summary>
    internal IndexWriter(
        WritePolicy policy, DType schema, bool isTabular, int fieldCount, int budgetPerMille = 100, int blockRows = 0,
        IKeyEncoder? keyEncoder = null, string? scratchDirectory = null, long scratchMemoryBytes = DefaultScratchMemoryBytes,
        long wideRowsAbove = uint.MaxValue)
    {
        _policy = policy;
        _isTabular = isTabular;
        _budgetPerMille = budgetPerMille;
        _fieldCount = fieldCount;
        _keyEncoder = keyEncoder;

        // A composite key is one more column after the real ones, and a nested column one more after
        // the keys, so every loop over the builders serves them with no second path; only the feed
        // differs. An override naming nothing in the schema takes a slot too, so the report says so.
        List<string>? nested = Unmatched(policy, schema, isTabular);
        int keys = policy.Keys.Count;
        int total = fieldCount + keys + (nested?.Count ?? 0);
        _paths = new string[total];
        _columns = new IndexPolicy[total];
        _builders = new List<IndexBuilder>[total];
        _refusals = new string?[total];
        _columnBytes = new long[total];
        _keyFields = new int[][]?[total];
        for (int n = 0; n < (nested?.Count ?? 0); n++)
        {
            int slot = fieldCount + keys + n;
            string path = nested![n];
            IndexPolicy column = policy.Of(path);
            _paths[slot] = path;
            _columns[slot] = column;
            _builders[slot] = [];
            if (!TryResolve(schema, isTabular, path, out int[] chain, out DType leaf, out string? missing))
            {
                _refusals[slot] = missing;
                continue;
            }

            (_nested ??= new int[]?[total])[slot] = chain;
            _refusals[slot] = column.Kind == IndexPolicyKind.Auto
                ? "Auto chooses among the top-level columns; a nested column is indexed when its override names a kind"
                : Explicit(column, leaf, _builders[slot]);
        }

        for (int k = 0; k < keys; k++)
        {
            int field = fieldCount + k;
            CompositeKeyPolicy key = policy.Keys[k];
            _paths[field] = "(" + string.Join(", ", key.Paths) + ")";
            _columns[field] = key.Policy;
            _builders[field] = [];
            _refusals[field] = Composite(key, schema, isTabular, keyEncoder, out _keyFields[field]);
            if (_refusals[field] is null)
            {
                _builders[field].Add(new KeyIndexBuilder(
                    rows: true, new KeyLayout(KeyShape.Bytes, 0, default), utf8: false, key.Policy.SegmentEntries));
            }
        }

        for (int field = 0; field < fieldCount; field++)
        {
            _paths[field] = isTabular ? schema.GetFieldName(field) : string.Empty;
            IndexPolicy column = policy.Of(_paths[field]);
            _columns[field] = column;
            DType dtype = isTabular ? schema.GetField(field) : schema;
            List<IndexBuilder> builders = [];
            _builders[field] = builders;
            string? reason = null;
            switch (column.Kind)
            {
                case IndexPolicyKind.Auto:
                    // Every cheap builder that applies starts at block 0; a dtype one does not apply
                    // to is not a candidate, and is not reported as refused. Sorted runs cost a sort
                    // per chunk and postings an intern per row, so neither is ever Auto's.
                    if (BloomBuilder.Supports(dtype, out _))
                    {
                        builders.Add(new BloomBuilder(IndexPolicy.Bloom())
                        {
                            AutoShare = AutoBloomShare,
                            BlockRows = blockRows,
                        });
                    }

                    break;
                default:
                    reason = Explicit(column, dtype, builders);
                    break;
            }

            _refusals[field] = reason;
        }

        // The locating builders share one scratch, made only when there is one such builder.
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                if (builder is KeyIndexBuilder locating)
                {
                    _scratch ??= new RunScratch(scratchMemoryBytes, scratchDirectory);
                    locating.Scratch = _scratch;
                    locating.BlockRows = blockRows;
                    locating.WideRowsAbove = wideRowsAbove;
                }
            }
        }
    }

    private readonly RunScratch? _scratch;

    /// <summary>The default memory the chunk runs may hold before they move to a file.</summary>
    internal const long DefaultScratchMemoryBytes = 64L << 20;

    /// <summary>The scratch the locating builders lay their chunk runs in, when there is one.</summary>
    internal RunScratch? Scratch => _scratch;

    /// <summary>The builder a policy names for a column, or why there is none.</summary>
    private static string? Explicit(IndexPolicy column, DType dtype, List<IndexBuilder> builders)
    {
        string? reason = null;
        switch (column.Kind)
        {
            case IndexPolicyKind.Bloom:
                if (BloomBuilder.Supports(dtype, out reason))
                {
                    builders.Add(new BloomBuilder(column));
                }

                break;
            case IndexPolicyKind.NgramBloom:
                if (BloomBuilder.Supports(dtype, trigrams: true, out reason))
                {
                    builders.Add(new BloomBuilder(column));
                }

                break;
            case IndexPolicyKind.NgramPostings:
                if (BloomBuilder.Supports(dtype, trigrams: true, out reason))
                {
                    builders.Add(KeyIndexBuilder.ForTrigrams(column.CaseInsensitive, column.SegmentEntries));
                }

                break;
            case IndexPolicyKind.Postings or IndexPolicyKind.SortedRuns:
                if (KeyIndexBuilder.Supports(dtype, out KeyLayout layout, out bool utf8, out reason))
                {
                    builders.Add(new KeyIndexBuilder(
                        column.Kind == IndexPolicyKind.SortedRuns, layout, utf8, column.SegmentEntries));
                }

                break;
            default:
                break;
        }

        return reason;
    }

    /// <summary>
    /// The override paths that are not a top-level column, in ordinal order so that one policy
    /// always lays its slots out the same way; null when there is none.
    /// </summary>
    private static List<string>? Unmatched(WritePolicy policy, DType schema, bool isTabular)
    {
        List<string>? unmatched = null;
        foreach (string path in policy.Columns.Keys)
        {
            bool column = isTabular ? schema.IndexOfField(path) >= 0 : path.Length == 0;
            if (!column)
            {
                (unmatched ??= []).Add(path);
            }
        }

        unmatched?.Sort(StringComparer.Ordinal);
        return unmatched;
    }

    /// <summary>
    /// A column path as field indices from the root, through structs and the extensions around
    /// them, and the dtype it ends on; or why it names nothing. The path is <c>.</c>-separated, as
    /// the scan spells it.
    /// </summary>
    internal static bool TryResolve(
        DType schema, bool isTabular, string path, out int[] chain, out DType leaf, out string? reason)
    {
        leaf = schema;
        if (!isTabular)
        {
            chain = [];
            reason = $"'{path}' names no column: the file's root is its only column";
            return false;
        }

        // A top-level name that holds a dot is that column, as the scan reads it.
        int top = schema.IndexOfField(path);
        if (top >= 0)
        {
            chain = [top];
            leaf = schema.GetField(top);
            reason = null;
            return true;
        }

        string[] names = path.Split('.');
        chain = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            while (leaf.Kind == DTypeKind.Extension)
            {
                leaf = leaf.StorageType;
            }

            int field = leaf.Kind == DTypeKind.Struct ? leaf.IndexOfField(names[i]) : -1;
            if (field < 0)
            {
                reason = $"'{path}' names no column of the schema";
                return false;
            }

            chain[i] = field;
            leaf = leaf.GetField(field);
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Whether a policy can ask for anything at all: a default of <see cref="IndexPolicyKind.None"/>
    /// with no override is the one that cannot, and the writer then builds no index writer.
    /// </summary>
    internal static bool Asks(WritePolicy policy) =>
        policy.Default.Kind != IndexPolicyKind.None || policy.Columns.Count > 0 || policy.Keys.Count > 0;

    /// <summary>Why a composite key cannot be built, or null; and the columns it encodes, as field-index paths.</summary>
    private static string? Composite(
        CompositeKeyPolicy key, DType schema, bool isTabular, IKeyEncoder? encoder, out int[][]? fields)
    {
        fields = null;
        if (!isTabular)
        {
            return "a composite key needs a file whose root is a struct of columns";
        }

        if (encoder is null)
        {
            return "no key encoder: set VortexWriteOptions.KeyEncoder, which the Vorticity.RowEncoding package provides (RowKeyEncoder)";
        }

        int[][] resolved = new int[key.Paths.Count][];
        for (int i = 0; i < resolved.Length; i++)
        {
            if (!TryResolve(schema, isTabular, key.Paths[i], out resolved[i], out _, out string? reason))
            {
                return reason;
            }
        }

        fields = resolved;
        return null;
    }

    /// <summary>
    /// Feeds every composite key the same rows: the key columns cut to the range -- records, not
    /// bytes -- encoded together, and every row whose tuple holds a null left out.
    /// </summary>
    internal void AccumulateKeys(CanonicalArena arena, ReadOnlySpan<int> fieldNodes, int start, int count)
    {
        for (int field = _fieldCount; field < _builders.Length; field++)
        {
            if (_keyFields[field] is not { } fields
                || _builders[field].Count == 0 || _builders[field][0] is not KeyIndexBuilder builder)
            {
                continue;
            }

            if (builder.Abandoned is not null || count <= 0)
            {
                builder.Skip(count);
                continue;
            }

            CanonicalArena slices = _keySlices ??= new CanonicalArena();
            slices.Reset();
            int[] columns = new int[fields.Length];
            bool[] include = new bool[count];
            include.AsSpan().Fill(true);
            bool masked = true;
            for (int c = 0; c < fields.Length; c++)
            {
                int leaf = Descend(arena, fieldNodes, fields[c], start, include, ref masked);
                if (leaf < 0)
                {
                    builder.Abandon($"a batch has no column at '{_paths[field]}''s path");
                    builder.Skip(count);
                    break;
                }

                columns[c] = Layouts.CanonicalSlice.SliceAcross(arena, slices, leaf, start, count);
                ValidityMask validity = ValidityMask.From(slices, slices.GetNode(columns[c]).Validity);
                if (validity.AllValid)
                {
                    continue;
                }

                for (int row = 0; row < count; row++)
                {
                    include[row] &= validity.IsValid(row);
                }
            }

            if (builder.Abandoned is not null)
            {
                continue;
            }

            try
            {
                using IEncodedKeys keys = _keyEncoder!.Encode(slices, columns);
                builder.AccumulateEncoded(keys, include);
            }
            catch (Exception e) when (e is VortexUnsupportedException or ArgumentException)
            {
                builder.Abandon($"the key encoder refused the key: {e.Message}");
                builder.Skip(count);
            }
        }
    }

    /// <summary>
    /// Feeds every nested column its rows: its node, reached from its top-level column, as it
    /// stands when no parent is null, and otherwise re-published over the range with the parents'
    /// nulls folded into its validity -- so a builder reads a nested column as it reads any other.
    /// </summary>
    internal void AccumulateNested(CanonicalArena arena, ReadOnlySpan<int> fieldNodes, int start, int count)
    {
        if (_nested is not { } nested || count <= 0)
        {
            return;
        }

        for (int field = 0; field < nested.Length; field++)
        {
            if (nested[field] is not { } chain || _builders[field].Count == 0)
            {
                continue;
            }

            bool[] include = ArrayPool<bool>.Shared.Rent(count);
            try
            {
                bool masked = false;
                int leaf = Descend(arena, fieldNodes, chain, start, include.AsSpan(0, count), ref masked);
                if (leaf < 0)
                {
                    foreach (IndexBuilder builder in _builders[field])
                    {
                        builder.Abandon($"a batch has no column at '{_paths[field]}'");
                    }

                    continue;
                }

                CanonicalArena source = arena;
                int from = start;
                if (masked)
                {
                    source = _keySlices ??= new CanonicalArena();
                    leaf = Masked(arena, leaf, start, include.AsSpan(0, count));
                    from = 0;
                }

                foreach (IndexBuilder builder in _builders[field])
                {
                    builder.Accumulate(source, leaf, from, count);
                }
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(include);
            }
        }
    }

    /// <summary>
    /// The node a field-index path ends on in a batch, with the rows its parents null out cleared
    /// in <paramref name="include"/>; -1 when the batch does not have the path. The first parent
    /// with a null fills <c>include</c> and sets <c>masked</c>, which says whether it holds
    /// anything yet.
    /// </summary>
    private static int Descend(
        CanonicalArena arena, ReadOnlySpan<int> fieldNodes, int[] chain, int start, Span<bool> include, ref bool masked)
    {
        int node = fieldNodes[chain[0]];
        for (int d = 1; d < chain.Length; d++)
        {
            CanonicalNode parent = arena.GetNode(node);
            Exclude(arena, parent.Validity, start, include, ref masked);
            while (parent.Kind == CanonicalKind.Extension)
            {
                parent = arena.GetNode(parent.StorageIndex);
                Exclude(arena, parent.Validity, start, include, ref masked);
            }

            if (parent.Kind != CanonicalKind.Struct || chain[d] >= parent.FieldCount)
            {
                return -1;
            }

            node = parent.GetFieldIndex(chain[d]);
        }

        return node;
    }

    private static void Exclude(CanonicalArena arena, Validity validity, int start, Span<bool> include, ref bool masked)
    {
        ValidityMask mask = ValidityMask.From(arena, validity);
        if (mask.AllValid)
        {
            return;
        }

        if (!masked)
        {
            include.Fill(true);
            masked = true;
        }

        for (int row = 0; row < include.Length; row++)
        {
            include[row] &= mask.IsValid(start + row);
        }
    }

    /// <summary>
    /// Rows <c>[start, start + include.Length)</c> of <paramref name="leaf"/>, in the scratch arena,
    /// valid where the leaf is and <paramref name="include"/> says so.
    /// </summary>
    private int Masked(CanonicalArena arena, int leaf, int start, ReadOnlySpan<bool> include)
    {
        int count = include.Length;
        CanonicalArena slices = _keySlices!;
        slices.Reset();
        int sliced = Layouts.CanonicalSlice.SliceAcross(arena, slices, leaf, start, count);
        CanonicalNode outer = slices.GetNode(sliced);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = slices.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(slices, node.Validity);
        ValidityMask wrapper = ValidityMask.From(slices, outer.Validity);
        Buffers.VortexBuffer bits = slices.Allocate(Math.Max((count + 7) / 8, 1), 1, out Span<byte> raw);
        raw.Clear();
        for (int row = 0; row < count; row++)
        {
            if (include[row] && own.IsValid(row) && wrapper.IsValid(row))
            {
                raw[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        DTypeArena types = _maskTypes ??= new DTypeArena();
        Validity validity = Validity.Bitmap(
            slices.AddBool(types.Bool(Nullability.NonNullable), count, Validity.NonNullable, bits, 0));
        DType dtype = node.DType;
        return node.Kind switch
        {
            CanonicalKind.Primitive => slices.AddPrimitive(dtype, count, validity, node.PType, node.Values),
            CanonicalKind.Decimal => slices.AddDecimal(
                dtype, count, validity, node.Storage, node.Precision, node.Scale, node.Values),
            CanonicalKind.Bool => slices.AddBool(dtype, count, validity, node.Bits, node.BitOffset),
            CanonicalKind.Constant => slices.AddConstant(dtype, count, validity, node.ConstantElement),
            CanonicalKind.VarBinView => MaskedViews(slices, node, dtype, count, validity),

            // A list under a null parent names no element: the same offsets and elements, under the
            // folded validity.
            CanonicalKind.ListView => slices.AddListView(
                dtype, count, validity, node.ElementsIndex, node.Offsets, node.OffsetPType, node.Sizes, node.SizePType),
            CanonicalKind.FixedSizeList => slices.AddFixedSizeList(
                dtype, count, validity, node.ElementsIndex, node.FixedSize),

            // No builder keys the other kinds, and each says so when it is fed one.
            _ => sliced,
        };
    }

    private static int MaskedViews(CanonicalArena slices, CanonicalNode node, DType dtype, int count, Validity validity)
    {
        Buffers.VortexBuffer[] data = new Buffers.VortexBuffer[node.DataBufferCount];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = node.GetDataBuffer(i);
        }

        return slices.AddVarBinView(dtype, count, validity, node.Views, data);
    }

    // Both created by the call that fills them: a plain write allocates neither.
    private List<IndexEntry>? _prior;
    private ulong _previousEof;

    /// <summary>
    /// Continues an existing file's indexes: every builder starts at <paramref name="boundary"/>,
    /// and the old entries' runs that end at or before it are listed again beside the new ones. A
    /// run reaching past it covered rows the append writes again -- a re-opened chunk, a short last
    /// block -- and is dropped; a dictionary probe is recomputed from the chunks' schemes.
    /// The absorbed entries are the old ones whose tail of runs was read back to be merged into the
    /// append's run, and the writer disposes the scratch they lie in.
    /// </summary>
    internal void Continue(
        IReadOnlyList<IndexEntry> entries, int boundary, long row, long previousEof,
        List<AbsorbedEntry>? absorbed = null, RunScratch? absorbedScratch = null)
    {
        _previousEof = (ulong)previousEof;
        _adopted = absorbedScratch;
        Begin(boundary, row);

        foreach (IndexEntry entry in entries)
        {
            if (entry.Kind == IndexKinds.DictProbe || Kept(entry, boundary) is not { } kept)
            {
                continue;
            }

            // A tail read back is merged only by the builder that continues the entry -- same kind,
            // column and options. Without one, the runs stay listed.
            int count = 0;
            if (absorbed?.Find(a => Same(a.Entry, entry)) is { } tail && Continuing(entry) is { } builder)
            {
                builder.Absorbed = tail.Runs;
                builder.AbsorbedScratch = absorbedScratch;
                count = tail.Count;
            }

            (_prior ??= []).Add(kept);
            (_priorAbsorbed ??= []).Add(count);
        }
    }

    /// <summary>
    /// Numbers the first block and row this pass is about to be fed, so its runs say which blocks of
    /// the file they cover. Separate from <see cref="Continue"/> because indexing a block range
    /// wants only this half: <see cref="Continue"/> also drops every old run reaching past its
    /// boundary, which is right for a suffix and wrong for a range.
    /// </summary>
    internal void Begin(int firstBlock, long firstRow)
    {
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                builder.Start(firstBlock, firstRow);
            }
        }
    }

    /// <summary>The scratch an append handed over with the runs it read back.</summary>
    private RunScratch? _adopted;

    /// <summary>Per entry of <see cref="_prior"/>, how many of its last runs its builder merges.</summary>
    private List<int>? _priorAbsorbed;

    private static bool Same(IndexEntry a, IndexEntry b) =>
        a.Kind == b.Kind && SamePath(a.ColumnPath, b.ColumnPath) && a.Options.AsSpan().SequenceEqual(b.Options);

    /// <summary>The locating builder whose entry would continue <paramref name="old"/>, or null.</summary>
    private KeyIndexBuilder? Continuing(IndexEntry old)
    {
        for (int field = 0; field < _builders.Length; field++)
        {
            if (!SamePath(ColumnPath(field), old.ColumnPath))
            {
                continue;
            }

            foreach (IndexBuilder builder in _builders[field])
            {
                if (builder is KeyIndexBuilder keys && keys.Kind == old.Kind
                    && ExpectedOptions(field, keys).AsSpan().SequenceEqual(old.Options))
                {
                    return keys;
                }
            }
        }

        return null;
    }

    /// <summary>The options a locating entry of this slot is written with.</summary>
    private byte[] ExpectedOptions(int field, KeyIndexBuilder keys) =>
        KeyRunOptions.Entry(
            keys.SegmentEntries, keys.CaseInsensitive, KeyColumns(field),
            _keyFields[field] is null ? null : _keyEncoder!.Format);

    /// <summary>
    /// An old entry restricted to the runs that end by <paramref name="boundary"/>, or null. A
    /// filter tree is cut rather than dropped: its nodes over the rewritten blocks only say "maybe"
    /// more often, and the shorter range leaves those blocks to the append's own tree.
    /// </summary>
    private static IndexEntry? Kept(IndexEntry entry, int boundary)
    {
        bool bloom = IsBloom(entry.Kind);
        if (bloom && !BloomIndexOptions.TryParse(entry.Options, out _))
        {
            return null;
        }

        List<IndexRun> runs = [];
        foreach (IndexRun run in entry.Runs)
        {
            if (run.EndBlock <= (ulong)boundary)
            {
                runs.Add(run);
            }
            else if (bloom && run.FirstBlock < (ulong)boundary)
            {
                runs.Add(run with { BlockCount = checked((uint)((ulong)boundary - run.FirstBlock)) });
            }
        }

        return runs.Count == 0 ? null : entry with { Runs = runs };
    }

    private static bool IsBloom(string kind) => kind is IndexKinds.BloomSbbf or IndexKinds.BloomNgram3;

    private List<IndexEntry>? _preserved;

    /// <summary>
    /// For an index built after the fact: the old directory's entries, listed again unless a new
    /// entry indexes the same column with the same kind -- the new one covers every block and
    /// replaces it.
    /// The previous end of file is the old file's length when the new runs are appended to it, and
    /// 0 for a fragment.
    /// </summary>
    internal void Preserve(IReadOnlyList<IndexEntry> entries, long previousEof)
    {
        (_preserved ??= []).AddRange(entries);
        _previousEof = (ulong)previousEof;
    }

    /// <summary>The new entries with the old ones laid before them, entry by entry.</summary>
    private List<IndexEntry> Merged()
    {
        if (_preserved is { Count: > 0 } preserved)
        {
            List<IndexEntry> all = [.. _entries];
            foreach (IndexEntry old in preserved)
            {
                if (!_entries.Exists(e => e.Kind == old.Kind && SamePath(e.ColumnPath, old.ColumnPath)))
                {
                    all.Add(old);
                }
            }

            return all;
        }

        if (_prior is not { Count: > 0 } prior)
        {
            return _entries;
        }

        List<IndexEntry> merged = [.. _entries];
        for (int p = 0; p < prior.Count; p++)
        {
            IndexEntry old = prior[p];
            int match = merged.FindIndex(e => Continues(old, e));
            if (match < 0)
            {
                // No new entry, so nothing merged the runs read back: they stay listed.
                merged.Add(old);
                continue;
            }

            // The new entry's first run covers the tail its builder read back and merged.
            int absorbed = _priorAbsorbed?[p] ?? 0;
            if (absorbed > 0)
            {
                List<IndexRun> remaining = [.. old.Runs];
                remaining.RemoveRange(remaining.Count - absorbed, absorbed);
                old = old with { Runs = remaining };
            }

            IndexEntry current = merged[match];
            merged[match] = current with { Runs = [.. old.Runs, .. current.Runs] };
        }

        return merged;
    }

    private static bool SamePath(IReadOnlyList<uint> a, IReadOnlyList<uint> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a new entry continues an old one: same kind, column, block length and options. No
    /// entry's options hold anything that grows with the file, so the bytes decide.
    /// </summary>
    private static bool Continues(IndexEntry old, IndexEntry current) =>
        old.Kind == current.Kind
        && SamePath(old.ColumnPath, current.ColumnPath)
        && old.BlockLength == current.BlockLength
        && old.Options.AsSpan().SequenceEqual(current.Options);

    /// <summary>Whether any column asked for anything: a file with no request carries no directory.</summary>
    internal bool Enabled
    {
        get
        {
            foreach (IndexPolicy column in _columns)
            {
                if (column.Kind != IndexPolicyKind.None)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether a column feeds a streaming builder, so the ingest has something to call.</summary>
    internal bool Streams
    {
        get
        {
            foreach (List<IndexBuilder> builders in _builders)
            {
                if (builders.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal void Accumulate(int field, CanonicalArena arena, int nodeIndex, int start, int count)
    {
        foreach (IndexBuilder builder in _builders[field])
        {
            builder.Accumulate(arena, nodeIndex, start, count);
        }
    }

    /// <summary>Seals every builder's open block, telling them once what the statistics say of it.</summary>
    internal void CloseBlock(IReadOnlyList<ColumnWriter> columns)
    {
        for (int field = 0; field < _builders.Length; field++)
        {
            foreach (IndexBuilder builder in _builders[field])
            {
                if (!_firstBlockClosed && field < columns.Count && columns[field].Blocks.Count > 0)
                {
                    builder.FirstBlock(columns[field].Blocks[^1].IsSorted);
                }

                builder.CloseBlock();
            }
        }

        _firstBlockClosed = true;
    }

    /// <summary>Counts a chunk's data bytes toward its column, for `Auto`'s shares.</summary>
    internal void AddColumnBytes(int field, long bytes) => _columnBytes[field] += bytes;

    internal void CloseChunk(int firstBlock, int blocks, long firstRow, long rows)
    {
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                builder.CloseChunk(firstBlock, blocks, firstRow, rows);
            }
        }
    }

    /// <summary>
    /// `Auto`'s verdict, run before the payloads a chunk closed are written, so a builder given up
    /// on here leaves nothing in the file.
    /// </summary>
    internal void Judge()
    {
        for (int field = 0; field < _builders.Length; field++)
        {
            ColumnFacts facts = new ColumnFacts(_columnBytes[field]);
            foreach (IndexBuilder builder in _builders[field])
            {
                builder.Judge(facts);
            }
        }
    }

    /// <summary>
    /// Settles the budget over the whole file, before the last payloads are written.
    /// </summary>
    /// <param name="dataBytes">The file's data bytes, the index regions excluded.</param>
    /// <remarks>
    /// <see cref="Close"/> asks the same question and used to be the only one asking it, which was
    /// one flush too late: everything it abandoned had already been written. It also asked only
    /// once a payload had been placed, so a file whose indexes all fit between two chunks was never
    /// judged at all while it was being written. Asked here, over the living bytes rather than the
    /// placed ones, the verdict lands before the bytes do.
    /// </remarks>
    internal void SettleBudget(long dataBytes)
    {
        if (dataBytes >= BudgetFloor && LivingBytes > 0 && OverBudget(dataBytes))
        {
            AbandonForBudget(dataBytes);
        }
    }

    /// <summary>Closes what the end of the data closes: partial generations, file-level filters.</summary>
    internal void EndOfData()
    {
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                builder.EndOfData();
            }
        }
    }

    internal bool HasPending
    {
        get
        {
            foreach (List<IndexBuilder> builders in _builders)
            {
                foreach (IndexBuilder builder in builders)
                {
                    if (builder.Pending.Count > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Whether the payloads queued so far may be written now, the budget having been asked first.
    /// </summary>
    /// <param name="position">Where the sink stands, so the data written so far can be derived.</param>
    /// <returns><see langword="false"/> when nothing is to be written between chunks this time.</returns>
    /// <remarks>
    /// <para>
    /// Asked before a byte goes out, where the budget used to be asked after. The order is the
    /// whole difference: an index the budget refuses used to be abandoned having already written
    /// itself, and what it wrote stayed in the file -- up to a mebibyte of it, because that is how
    /// much data has to arrive before the share of it means anything.
    /// </para>
    /// <para>
    /// Below that threshold the payloads are held rather than judged. Judging them there would
    /// refuse an index that a full file would have afforded, which is what the threshold has always
    /// been for; holding them costs the memory of an index over one mebibyte of data, and costs it
    /// only until the data arrives. At or above it the verdict is the same one
    /// <see cref="Placed"/> reached a moment too late.
    /// </para>
    /// </remarks>
    internal bool TryOpenFlush(long position)
    {
        long dataBytes = position - FileBytes;
        if (dataBytes < BudgetFloor)
        {
            return false;
        }

        if (!OverBudget(dataBytes))
        {
            return true;
        }

        AbandonForBudget(dataBytes);

        // A required builder keeps its payloads, and they are still owed their write.
        return HasPending;
    }

    /// <summary>
    /// Serializes the next waiting payload as an array blob, or reports there is none. The caller
    /// disposes <paramref name="blob"/> and hands <paramref name="payload"/> back to
    /// <see cref="Placed"/> once it is written.
    /// </summary>
    internal bool TryTakePayload(
        EncodingDictionary encodings, out ArrayBlobWriter.BlobLease blob, out PendingPayload? payload)
    {
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                if (!builder.Pending.TryDequeue(out payload))
                {
                    continue;
                }

                if (_payloads is null)
                {
                    _payloads = new ScanContext([]);
                    _payloadTypes = new DTypeArena();
                }

                CanonicalArena arena = _payloads.Canonical;
                int node = payload.Build(arena, _payloadTypes!);
                payload.DType = DTypeFlatBuffers.Serialize(arena.GetNode(node).DType);
                blob = ArrayBlobWriter.Write(arena, node, encodings, payload.Compress);
                return true;
            }
        }

        blob = default;
        payload = null;
        return false;
    }

    /// <summary>Every byte the payloads took in the file, their alignment padding included.</summary>
    internal long FileBytes { get; private set; }

    /// <summary>
    /// Records where a payload landed, and checks the file's index budget. The file bytes include
    /// the write's padding.
    /// </summary>
    internal void Placed(PendingPayload payload, IndexSegment segment, long fileBytes, long position)
    {
        payload.Segment = segment;
        payload.Owner?.Placed(payload, segment.Length);
        _payloadBytes += segment.Length;
        FileBytes += fileBytes;
        _payloads!.Canonical.Reset();

        // The budget is a share of the data, and a share of a few kilobytes says nothing: it is
        // enforced once the data passes a mebibyte, and again at the end over the whole file.
        long dataBytes = position - FileBytes;
        if (dataBytes >= BudgetFloor && OverBudget(dataBytes))
        {
            AbandonForBudget(dataBytes);
        }
    }

    /// <summary>
    /// The data a file must hold before its index budget means anything: a share of a few kilobytes
    /// says nothing, and refusing an index on one would refuse it on a file that could afford it.
    /// </summary>
    private const long BudgetFloor = 1L << 20;

    /// <summary>
    /// The bytes of the indexes still alive. What an abandoned builder already wrote is dead weight
    /// the file carries either way, and must not condemn the builders that survived it.
    /// </summary>
    private long LivingBytes
    {
        get
        {
            long bytes = 0;
            foreach (List<IndexBuilder> builders in _builders)
            {
                foreach (IndexBuilder builder in builders)
                {
                    bytes += builder.Abandoned is null ? builder.Bytes : 0;
                }
            }

            return bytes;
        }
    }

    private bool OverBudget(long dataBytes) => LivingBytes * 1000 > dataBytes * _budgetPerMille;

    /// <summary>Abandons every index the caller did not mark required.</summary>
    private void AbandonForBudget(long dataBytes)
    {
        long living = LivingBytes;
        for (int field = 0; field < _builders.Length; field++)
        {
            if (_columns[field].Required)
            {
                continue;
            }

            foreach (IndexBuilder builder in _builders[field])
            {
                builder.Abandon(
                    $"the file's indexes reached {living} bytes against {dataBytes} bytes of data, " +
                    $"over the budget of {_budgetPerMille}‰ (VortexWriteOptions.IndexBudgetPerMille)");
            }
        }
    }

    /// <summary>
    /// Decides every column's indexes once the data is written. Rows per block is the zone length
    /// the runs are counted in, and the budget is a share of the data bytes.
    /// </summary>
    internal void Close(
        IReadOnlyList<ColumnWriter> columns, IReadOnlyList<long> chunkRows, int blockRows, long dataBytes = 0)
    {
        if (_payloadBytes > 0 && OverBudget(dataBytes))
        {
            AbandonForBudget(dataBytes);
        }

        for (int field = 0; field < _columns.Length; field++)
        {
            IndexPolicy policy = _columns[field];
            List<IndexBuilder> builders = _builders[field];
            IndexBuilder? only = builders.Count > 0 ? builders[0] : null;
            switch (policy.Kind)
            {
                case IndexPolicyKind.None:
                    break;
                case IndexPolicyKind.Auto when field >= _fieldCount:
                    // An override naming no column, or a nested one, under Auto: said, not skipped.
                    Abandoned(field, "auto", _refusals[field] ?? "Auto chooses among the top-level columns");
                    break;
                case IndexPolicyKind.Auto:
                    DictProbe(field, columns[field], chunkRows, blockRows);
                    foreach (IndexBuilder builder in builders)
                    {
                        if (builder is BloomBuilder bloom)
                        {
                            Bloom(field, bloom.Kind, bloom, blockRows);
                        }
                        else if (builder is KeyIndexBuilder keys)
                        {
                            Locating(field, keys.Kind, keys, blockRows);
                        }
                    }

                    break;
                case IndexPolicyKind.Bloom:
                    Bloom(field, IndexKinds.BloomSbbf, only as BloomBuilder, blockRows);
                    break;
                case IndexPolicyKind.NgramBloom:
                    Bloom(field, IndexKinds.BloomNgram3, only as BloomBuilder, blockRows);
                    break;
                case IndexPolicyKind.Postings:
                    Locating(field, IndexKinds.PostingsBlocks, only as KeyIndexBuilder, blockRows);
                    break;
                case IndexPolicyKind.SortedRuns:
                    Locating(field, IndexKinds.SortedRuns, only as KeyIndexBuilder, blockRows);
                    break;
                case IndexPolicyKind.NgramPostings:
                    Locating(field, IndexKinds.PostingsNgram3, only as KeyIndexBuilder, blockRows);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled index policy {policy.Kind}.");
            }
        }
    }

    private void Abandoned(int field, string kind, string reason) =>
        _reports.Add(new IndexWriteReport(_paths[field], kind, IndexOutcome.Abandoned, reason, 0, 0, 0));

    /// <summary>
    /// A Bloom entry: one per resolution the builder kept, each listing the runs whose payload is
    /// written.
    /// </summary>
    private void Bloom(int field, string kind, BloomBuilder? bloom, int blockRows)
    {
        if (bloom is null || bloom.Abandoned is not null)
        {
            Abandoned(field, kind, bloom?.Abandoned ?? _refusals[field] ?? "no builder ran");
            return;
        }

        IndexPolicy policy = bloom.Policy;

        // One entry, one run, one root: the tree names every other region itself.
        if (bloom.Tree is not { Payload: { Segment: { } root, DType: { } dtype } } tree)
        {
            Abandoned(field, kind, "the column wrote no block");
            return;
        }

        if (bloom.Leaves == 0 && tree.Nodes == 0)
        {
            Abandoned(
                field, kind,
                $"no block holds the {policy.MinDistinct} distinct values the policy asks before a filter pays");
            return;
        }

        BloomIndexOptions options = new BloomIndexOptions(
            policy.FalsePositivePpm, policy.Hash, BloomBuilder.MaxBlocksOf(policy), policy.MinDistinct,
            policy.Kind == IndexPolicyKind.NgramBloom && policy.CaseInsensitive,
            policy.Resolutions >= 3 ? BloomBuilder.FileMaxBlocks : BloomBuilder.MaxBlocksOf(policy));
        IndexRun run = new IndexRun(
            (ulong)tree.FirstBlock, checked((uint)tree.Leaves), [root], [dtype], 0, BloomTreeRun.Options(tree.RootWords));
        _entries.Add(new IndexEntry(kind, ColumnPath(field), (ulong)Math.Max(blockRows, 1), options.ToBytes(), [run]));

        string? fileNote = policy.Resolutions >= 3 ? bloom.FileAbandoned : null;
        _reports.Add(new IndexWriteReport(
            _paths[field], kind, IndexOutcome.Built,
            fileNote is null ? null : "built without its file-level filter: " + fileNote,
            bloom.WrittenBytes, tree.Nodes, 1));
    }

    /// <summary>
    /// A locating entry: one entry, one run per chunk, its payloads `stride` per segment.
    /// </summary>
    private void Locating(int field, string kind, KeyIndexBuilder? keys, int blockRows)
    {
        if (keys is null || keys.Abandoned is not null)
        {
            Abandoned(field, kind, keys?.Abandoned ?? _refusals[field] ?? "no builder ran");
            return;
        }

        List<IndexRun> runs = [];
        List<PagedRun>? paged = null;
        int stride = KeyRunOptions.StrideOf(kind);
        long bytes = 0;
        foreach (KeyRun run in keys.Runs)
        {
            List<IndexSegment> segments = [];
            List<byte[]> dtypes = [];
            foreach (PendingPayload payload in run.Payloads)
            {
                if (payload.Segment is not { } segment)
                {
                    // A run whose payloads did not all go out is not listed.
                    segments.Clear();
                    break;
                }

                segments.Add(segment);
                dtypes.Add(payload.DType!);
                bytes += segment.Length;
            }

            if (segments.Count != run.Payloads.Count)
            {
                continue;
            }

            if (Fences.Pages(run.Segments.Count) && Paged(run, stride, segments, dtypes, runs) is { } pages)
            {
                (paged ??= []).Add(pages);
                continue;
            }

            runs.Add(new IndexRun(
                (ulong)run.FirstBlock, checked((uint)run.BlockCount), segments, dtypes,
                (ulong)run.Entries, KeyRunOptions.Run(run.Segments)));
        }

        if (runs.Count == 0)
        {
            Abandoned(field, kind, "the column wrote no chunk");
            return;
        }

        _entries.Add(new IndexEntry(
            kind, ColumnPath(field), (ulong)Math.Max(blockRows, 1), ExpectedOptions(field, keys), runs));
        _reports.Add(new IndexWriteReport(_paths[field], kind, IndexOutcome.Built, null, bytes, 0, runs.Count));
        foreach (PagedRun pages in paged ?? [])
        {
            pages.Report = _reports.Count - 1;
            (_paged ??= []).Add(pages);
        }
    }

    /// <summary>When a run's table goes to fence pages; lowered only by the tests that page a short run.</summary>
    internal FenceShape Fences { get; init; } = FenceShape.Default;

    /// <summary>The runs whose tables go to pages, in the order the pages are written.</summary>
    private List<PagedRun>? _paged;

    /// <summary>The first of <see cref="_paged"/> whose root is not known yet.</summary>
    private int _pagedNext;

    /// <summary>A run listed once its fence pages are written.</summary>
    private sealed class PagedRun(FenceTreeWriter tree, List<IndexRun> runs, IndexRun placeholder, byte[][] dtypes)
    {
        internal FenceTreeWriter Tree { get; } = tree;

        /// <summary>The entry's runs, where <see cref="Placeholder"/> holds the run's place.</summary>
        internal List<IndexRun> Runs { get; } = runs;

        internal IndexRun Placeholder { get; } = placeholder;

        /// <summary>The dtype of each array of a segment.</summary>
        internal byte[][] DTypes { get; } = dtypes;

        /// <summary>Which report's bytes the pages add to.</summary>
        internal int Report { get; set; }
    }

    /// <summary>
    /// A long run's pages to come, its place held in <paramref name="runs"/>; null when its segments'
    /// arrays do not share one dtype each, and the run keeps its table inline.
    /// </summary>
    private PagedRun? Paged(KeyRun run, int stride, List<IndexSegment> segments, List<byte[]> dtypes, List<IndexRun> runs)
    {
        if (stride == 0 || segments.Count != stride * run.Segments.Count)
        {
            return null;
        }

        for (int i = stride; i < dtypes.Count; i++)
        {
            if (!dtypes[i].AsSpan().SequenceEqual(dtypes[i % stride]))
            {
                return null;
            }
        }

        List<IndexSegment[]> regions = new List<IndexSegment[]>(run.Segments.Count);
        for (int s = 0; s < run.Segments.Count; s++)
        {
            regions.Add(segments.GetRange(s * stride, stride).ToArray());
        }

        // The run holds its place with no payload; the directory refuses to run before the pages
        // are placed, so no such run is ever listed.
        IndexRun placeholder = new IndexRun(
            (ulong)run.FirstBlock, checked((uint)run.BlockCount), [], [], (ulong)run.Entries);
        PagedRun pages = new PagedRun(
            new FenceTreeWriter(Fences, run.Segments, regions), runs, placeholder, dtypes.GetRange(0, stride).ToArray());
        runs.Add(placeholder);
        return pages;
    }

    /// <summary>
    /// Writes every fence page, one level after the other, and lists each paged run once its root
    /// is known. Called after <see cref="Close"/>, before the directory.
    /// </summary>
    internal async ValueTask WriteFencePagesAsync(ISegmentSink sink, CancellationToken cancellationToken)
    {
        while (_paged is not null && _pagedNext < _paged.Count)
        {
            PagedRun pages = _paged[_pagedNext];
            while (pages.Tree.TryTake(out byte[] page))
            {
                long offset = sink.Position;
                await sink.WriteAsync(page, cancellationToken).ConfigureAwait(false);
                pages.Tree.Placed(IndexSegment.Of(offset, page, 0));
            }

            FencePage root = pages.Tree.Root!;
            IndexSegment[] children = new IndexSegment[root.Count];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = root.Regions[i][0];
            }

            int slot = pages.Runs.FindIndex(r => ReferenceEquals(r, pages.Placeholder));
            pages.Runs[slot] = pages.Placeholder with
            {
                Payload = children,
                Options = KeyRunOptions.PagedRun(root, pages.DTypes),
            };
            IndexWriteReport report = _reports[pages.Report];
            _reports[pages.Report] = report with { Bytes = report.Bytes + pages.Tree.Bytes };
            _pagedNext++;
        }
    }

    /// <summary>
    /// The dictionary probe: no payload, one run per maximal range of consecutive
    /// dictionary-encoded chunks. The run is the claim, so a chunk that is not a dictionary lies
    /// between two runs and the reader gets no claim for it.
    /// </summary>
    private void DictProbe(int field, ColumnWriter column, IReadOnlyList<long> chunkRows, int blockRows)
    {
        List<IndexRun> runs = [];
        long first = -1;
        long end = -1;
        int block = 0;
        for (int chunk = 0; chunk < chunkRows.Count; chunk++)
        {
            int start = block;
            block += VortexFileWriter.ChunkBlocks(chunkRows[chunk], blockRows);
            if (column.SchemeAt(start) != ColumnScheme.Dict)
            {
                Flush(runs, first, end);
                first = -1;
                continue;
            }

            if (first < 0)
            {
                first = start;
            }

            end = block;
        }

        Flush(runs, first, end);
        if (runs.Count == 0)
        {
            Abandoned(field, IndexKinds.DictProbe, "no chunk of this column is dictionary-encoded");
            return;
        }

        _entries.Add(new IndexEntry(
            IndexKinds.DictProbe, ColumnPath(field), (ulong)Math.Max(blockRows, 1), [], runs));
        _reports.Add(new IndexWriteReport(
            _paths[field], IndexKinds.DictProbe, IndexOutcome.Built, null, 0, 0, runs.Count));

        static void Flush(List<IndexRun> runs, long first, long end)
        {
            if (first >= 0)
            {
                runs.Add(new IndexRun((ulong)first, checked((uint)(end - first)), [], []));
            }
        }
    }

    /// <summary>
    /// A column's path: its field for a top-level column, its fields for a nested one; empty for the
    /// root and for a composite key, whose columns are in its options.
    /// </summary>
    private uint[] ColumnPath(int field) =>
        _nested?[field] is { } chain ? Unsigned(chain)
        : _isTabular && field < _fieldCount ? [checked((uint)field)]
        : [];

    /// <summary>A composite key's columns, as the entry options carry them; null for a real column.</summary>
    private List<uint[]>? KeyColumns(int field) =>
        _keyFields[field] is { } fields ? [.. Array.ConvertAll(fields, Unsigned)] : null;

    private static uint[] Unsigned(int[] chain) => Array.ConvertAll(chain, f => checked((uint)f));

    /// <summary>
    /// The directory's bytes, or null when there is nothing to list. A directory with no entry is
    /// still written when the policy was the caller's own, since it carries that policy and an
    /// append reads it to index its new blocks the same way; under the default policy it would say
    /// nothing an append would not assume from its absence.
    /// A fragment passes what binds it to the indexed file, along with the payloads' encoding table.
    /// </summary>
    internal byte[]? Directory(long rowCount, FragmentBinding? fragment = null)
    {
        if (!Enabled && _preserved is not { Count: > 0 })
        {
            return null;
        }

        if (_paged is not null && _pagedNext < _paged.Count)
        {
            throw new InvalidOperationException("The fence pages are written before the directory that names them.");
        }

        bool defaultPolicy = _policy.Default == IndexPolicy.Auto && _policy.Columns.Count == 0;
        List<IndexEntry> entries = Merged();
        return entries.Count == 0 && defaultPolicy && _previousEof == 0 && fragment is null
            ? null
            : new IndexDirectory((ulong)rowCount, _previousEof, _policy, entries)
            {
                BudgetPerMille = _budgetPerMille,
                FileLength = (ulong)(fragment?.Length ?? 0),
                FileIdentity = fragment?.Identity,
                FileToken = fragment?.Token,
                FileHash = fragment?.Hash,
                ArrayEncodings = fragment?.Encodings,
            }.ToBytes();
    }

    /// <summary>What became of every index the policy asked for.</summary>
    internal IReadOnlyList<IndexWriteReport> Reports => _reports;

    public void Dispose()
    {
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                builder.Dispose();
            }
        }

        _payloads?.Dispose();
        _payloads = null;
        _keySlices?.Reset();
        _keySlices = null;
        _scratch?.Dispose();
        _adopted?.Dispose();
        _adopted = null;
    }
}
