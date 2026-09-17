// The writer side of docs/10-indexes.md §7: which index each column gets, what is built, what is
// abandoned and why, and the directory that lists what survived.
//
// ONE OWNER, SO `VortexFileWriter` ONLY CALLS IT. The file writer knows segments and layouts; this
// knows policies, kinds and runs. Every kind lands here with its builder, and the file writer's
// part stays a handful of calls: feed the ingest, close the blocks and the chunks, write what is
// pending between chunks, and write the directory before the footer.
//
// WHAT A POLICY ASKS FOR IS ALWAYS IN THE REPORT. A kind this writer cannot build yet is reported
// ABANDONED with that reason rather than silently skipped or thrown: a caller who asked for an
// index reads what happened to it, and an index is a hint whose absence costs no correctness
// (docs/08-semantics.md §5).
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

/// <summary>What binds a sidecar to the version of the file it indexes (docs/13-dataset.md §7).</summary>
/// <param name="Length">The file's length.</param>
/// <param name="Identity">Its identity, when it has one.</param>
/// <param name="Token">The store's token for it, when the store gives one.</param>
/// <param name="Hash">The XXH3-128 of its bytes.</param>
/// <param name="Encodings">The encodings the sidecar's payloads name.</param>
internal sealed record SidecarBinding(long Length, Guid? Identity, string? Token, UInt128 Hash, IReadOnlyList<string> Encodings);

/// <summary>Builds the indexes of one file and the directory that lists them.</summary>
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
    /// `Auto`'s share of a column's bytes a Bloom filter may take (10 §5.5: "a filter whose
    /// projected bytes exceed 2 % of the column's written bytes").
    /// </summary>
    internal const int AutoBloomShare = 20;
    private readonly List<IndexEntry> _entries = [];
    private readonly List<IndexWriteReport> _reports = [];

    // The payloads' own arena and dtypes, created on the first payload: a policy that builds only
    // payload-free kinds never makes one.
    private ScanContext? _payloads;
    private DTypeArena? _payloadTypes;
    private long _payloadBytes;

    /// <param name="policy">The policy, already <see cref="WritePolicy.None"/> under <see cref="WriteProfile.Fastest"/>.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="isTabular">Whether the root is a struct whose fields are the columns.</param>
    /// <param name="fieldCount">How many columns.</param>
    /// <param name="budgetPerMille">The share of the data bytes all indexes together may take.</param>
    /// <param name="blockRows">Rows per block, 0 when the caller's batches are the blocks.</param>
    /// <param name="keyEncoder">The encoder of the policy's composite keys, or null.</param>
    /// <param name="scratchDirectory">Where the locating builders' chunk runs spill, or null for the system's.</param>
    /// <param name="scratchMemoryBytes">What those runs may hold in memory before they spill.</param>
    /// <param name="wideRowsAbove">The row span above which a sorted run writes its rows at 64 bits.</param>
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

        // A COMPOSITE KEY IS ONE MORE COLUMN, after the real ones: every loop over the builders --
        // blocks, chunks, the budget, the payloads, the close -- serves it with no second path, and
        // only the feed differs, because its rows are several columns encoded together.
        //
        // SO IS A NESTED COLUMN AN OVERRIDE NAMES, after the keys (10 §4.1: `column_path` resolves
        // to a leaf column). It is fed from its top-level column's node, descended through the
        // structs, and a row one of its parents nulls out is no entry. An override naming nothing
        // in the schema takes a slot too, so that the report says so rather than skip it.
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
                    // EVERY CHEAP BUILDER THAT APPLIES STARTS AT BLOCK 0 (10 §5.5); a dtype one does
                    // not apply to is not a candidate, and is not reported as refused. Sorted runs are
                    // not cheap -- a sort per chunk -- and are never Auto's.
                    //
                    // POSTINGS ARE NOT AUTO'S, AND THE REASON IS A MEASUREMENT. 10 §5.5 counts them
                    // among the cheap builders because they would come "from the tables we already
                    // build"; the writer's distinct table lives by plan memory and cannot feed them,
                    // so here they cost an intern per row of their own -- +7 % on `table_mixed` on
                    // top of the Bloom filter's +7 %, past the +10 % 11 §5.3 allows. They stay one
                    // `IndexPolicy.Postings` away.
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

        // THE LOCATING BUILDERS SHARE ONE SCRATCH (13 §6.1): their chunk runs wait there for the
        // merge, in memory up to its budget and in a file beyond. A write with no locating index
        // makes none.
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

    /// <summary>The builder a policy names for a column of <paramref name="dtype"/>, or why there is none.</summary>
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
    /// The override paths that are not a top-level column, in ordinal order so that one policy lays
    /// its slots out one way; <see langword="null"/> when there is none.
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
    /// them, and the dtype it ends on; or why it names nothing.
    /// </summary>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="isTabular">Whether the root is a struct of columns.</param>
    /// <param name="path">The path, <c>.</c>-separated as the scan spells it.</param>
    /// <param name="chain">The field indices.</param>
    /// <param name="leaf">The dtype the path ends on.</param>
    /// <param name="reason">Why the path names nothing.</param>
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
    /// <param name="policy">The policy.</param>
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
    /// <param name="arena">The batch's arena.</param>
    /// <param name="fieldNodes">Every real column's node in it, in field order.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
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
    /// <param name="arena">The batch's arena.</param>
    /// <param name="fieldNodes">Every real column's node in it, in field order.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
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
    /// in <paramref name="include"/>; -1 when the batch does not have the path.
    /// </summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="fieldNodes">Every real column's node in it.</param>
    /// <param name="chain">The path.</param>
    /// <param name="start">The first row.</param>
    /// <param name="include">Per row of the range, whether it may be an entry.</param>
    /// <param name="masked">
    /// Whether <paramref name="include"/> holds anything yet: filled with <see langword="true"/> by
    /// the first parent that has a null.
    /// </param>
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

            // A list under a null parent names no element (10 §5.1, step 28b): the same offsets and
            // elements, under the folded validity.
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

    // ------------------------------------------------------------------------------ an append

    // Both created by the call that fills them: a plain write allocates neither.
    private List<IndexEntry>? _prior;
    private ulong _previousEof;

    /// <summary>
    /// Continues an existing file's indexes (docs/11 §3.8, docs/10-indexes.md §8): every builder
    /// starts at <paramref name="boundary"/>, and the old entries' runs that end at or before it
    /// are listed again beside the new ones. A run reaching past it covered rows the append writes
    /// again -- a re-opened chunk, a short last block -- and is dropped; a dictionary probe is
    /// recomputed from the chunks' schemes.
    /// </summary>
    /// <param name="entries">The old directory's entries.</param>
    /// <param name="boundary">The first block the append writes.</param>
    /// <param name="row">Its first row.</param>
    /// <param name="previousEof">The old file's length, which the new directory records.</param>
    /// <param name="absorbed">
    /// The old entries whose tail of runs was read back to be merged into the append's run
    /// (13 §6.1: at most K runs per entry); null when none was.
    /// </param>
    /// <param name="absorbedScratch">Where those runs lie; the writer disposes it.</param>
    internal void Continue(
        IReadOnlyList<IndexEntry> entries, int boundary, long row, long previousEof,
        List<AbsorbedEntry>? absorbed = null, RunScratch? absorbedScratch = null)
    {
        _previousEof = (ulong)previousEof;
        _adopted = absorbedScratch;
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                builder.Start(boundary, row);
            }
        }

        foreach (IndexEntry entry in entries)
        {
            if (entry.Kind == IndexKinds.DictProbe || Kept(entry, boundary) is not { } kept)
            {
                continue;
            }

            // A TAIL READ BACK IS MERGED ONLY BY THE BUILDER THAT CONTINUES THE ENTRY: same kind,
            // column and options, the very test `Merged` applies. Without one, the runs stay listed.
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

    /// <summary>An old entry restricted to the runs that end by <paramref name="boundary"/>, or null.</summary>
    /// <remarks>
    /// A FILTER TREE IS CUT, NOT DROPPED (13 §6.2): its nodes over the blocks the append writes again
    /// hold values those blocks no longer have, which only makes them say "maybe" more often, and the
    /// run's shorter range keeps every probe of those blocks for the append's own tree. A Bloom entry
    /// this reader cannot parse -- version 1 -- is dropped, as the reader ignores it.
    /// </remarks>
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
    /// For an index built after the fact (docs/10-indexes.md §8): the old directory's entries,
    /// listed again unless a new entry indexes the same column with the same kind -- the new one
    /// covers every block and replaces it.
    /// </summary>
    /// <param name="entries">The old entries.</param>
    /// <param name="previousEof">The old file's length, when the new runs are appended to it; 0 for a sidecar.</param>
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
    /// Whether a new entry continues an old one: same kind, column, block length and options. A
    /// Bloom entry's options hold nothing that grows with the file since step 24, so the bytes decide.
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

    // ------------------------------------------------------------------------------ the stream

    /// <summary>Feeds one column's rows to its builder.</summary>
    /// <param name="field">The column.</param>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="nodeIndex">The column's node in it.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">How many rows.</param>
    internal void Accumulate(int field, CanonicalArena arena, int nodeIndex, int start, int count)
    {
        foreach (IndexBuilder builder in _builders[field])
        {
            builder.Accumulate(arena, nodeIndex, start, count);
        }
    }

    /// <summary>
    /// Seals the open block of every builder; the first time, tells them what the statistics say
    /// of it.
    /// </summary>
    /// <param name="columns">The column writers, whose last closed block is the one sealed here.</param>
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
    /// <param name="field">The column.</param>
    /// <param name="bytes">The segment's length.</param>
    internal void AddColumnBytes(int field, long bytes) => _columnBytes[field] += bytes;

    /// <summary>A chunk went out; each builder closes its run.</summary>
    /// <param name="firstBlock">Its first block.</param>
    /// <param name="blocks">Its blocks.</param>
    /// <param name="firstRow">Its first row.</param>
    /// <param name="rows">Its rows.</param>
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
    /// `Auto`'s verdict, before the payloads a chunk or the end of the data closed are written:
    /// a Bloom filter is uncompressed, so its estimate is its size, and a builder given up on here
    /// leaves nothing in the file.
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

    /// <summary>Whether a payload is waiting to be written.</summary>
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
    /// Serializes the next waiting payload as an array blob (10 §4.2), or reports there is none.
    /// </summary>
    /// <param name="encodings">The file's array-encoding dictionary.</param>
    /// <param name="blob">The blob to write; the caller disposes it.</param>
    /// <param name="payload">What to tell <see cref="Placed"/> once it is written.</param>
    /// <returns>Whether a payload was produced.</returns>
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

    /// <summary>Records where a payload landed, and checks the file's index budget.</summary>
    /// <param name="payload">What <see cref="TryTakePayload"/> said.</param>
    /// <param name="segment">Where it was written.</param>
    /// <param name="fileBytes">The bytes the write took, padding included.</param>
    /// <param name="position">The sink's position after the write.</param>
    internal void Placed(PendingPayload payload, IndexSegment segment, long fileBytes, long position)
    {
        payload.Segment = segment;
        payload.Owner?.Placed(payload, segment.Length);
        _payloadBytes += segment.Length;
        FileBytes += fileBytes;
        _payloads!.Canonical.Reset();

        // THE BUDGET IS A SHARE OF THE DATA, and a share of a few kilobytes says nothing: it is
        // enforced once the data passes a mebibyte, and again at the end over the whole file.
        long dataBytes = position - FileBytes;
        if (dataBytes >= 1L << 20 && OverBudget(dataBytes))
        {
            AbandonForBudget(dataBytes);
        }
    }

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

    private void AbandonForBudget(long dataBytes)
    {
        long living = LivingBytes;
        foreach (List<IndexBuilder> builders in _builders)
        {
            foreach (IndexBuilder builder in builders)
            {
                builder.Abandon(
                    $"the file's indexes reached {living} bytes against {dataBytes} bytes of data, " +
                    $"over the budget of {_budgetPerMille}‰ (VortexWriteOptions.IndexBudgetPerMille)");
            }
        }
    }

    // ------------------------------------------------------------------------------ the close

    /// <summary>
    /// Decides every column's indexes once the data is written, from what the columns recorded.
    /// </summary>
    /// <param name="columns">The column writers, in field order.</param>
    /// <param name="chunkRows">Every chunk's row count, in order.</param>
    /// <param name="blockRows">Rows per block: the zone length the runs are counted in.</param>
    /// <param name="dataBytes">The file's data bytes, which the budget is a share of.</param>
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
                    // An override that names no column, or a nested one, under Auto: said, not skipped.
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
    /// <c>vorticity.bloom.sbbf.v1</c> and <c>vorticity.bloom.ngram3.v1</c>: one entry per
    /// resolution the builder kept, each listing the runs whose payload is written.
    /// </summary>
    private void Bloom(int field, string kind, BloomBuilder? bloom, int blockRows)
    {
        if (bloom is null || bloom.Abandoned is not null)
        {
            Abandoned(field, kind, bloom?.Abandoned ?? _refusals[field] ?? "no builder ran");
            return;
        }

        IndexPolicy policy = bloom.Policy;

        // ONE ENTRY, ONE RUN, ONE ROOT (13 §6.2): the tree names every other region itself.
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
    /// <c>vorticity.postings.blocks.v1</c> and <c>vorticity.sorted.runs.v1</c>: one entry, one run
    /// per chunk, its payloads `stride` per segment.
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

    // ------------------------------------------------------------------------------ fence pages

    /// <summary>13 §6.3's bounds, lowered only by the tests that page a short run.</summary>
    internal FenceShape Fences { get; init; } = FenceShape.Default;

    /// <summary>The runs whose tables go to pages, in the order the pages are written; null until one does.</summary>
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

        /// <summary>The entry's report, whose bytes the pages add to.</summary>
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

        // The run holds its place with no payload, which no directory lists: `Directory` refuses to
        // run before the pages are placed.
        IndexRun placeholder = new IndexRun(
            (ulong)run.FirstBlock, checked((uint)run.BlockCount), [], [], (ulong)run.Entries);
        PagedRun pages = new PagedRun(
            new FenceTreeWriter(Fences, run.Segments, regions), runs, placeholder, dtypes.GetRange(0, stride).ToArray());
        runs.Add(placeholder);
        return pages;
    }

    /// <summary>
    /// Writes every fence page (13 §6.3), one level after the other, and lists each paged run once
    /// its root is known. Called after <see cref="Close"/>, before the directory.
    /// </summary>
    /// <param name="sink">The sink the payloads went to.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
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
    /// <c>vorticity.dict.probe.v1</c> (docs/10-indexes.md §5.3): no payload, one run per
    /// maximal range of consecutive dictionary-encoded chunks.
    /// </summary>
    /// <remarks>
    /// A RUN IS THE CLAIM, so a chunk that is NOT a dictionary lies between two runs and the reader
    /// gets no claim for it -- "a block that no run covers is simply live" (§4.1). Consecutive
    /// dictionary chunks are merged into one run because a run carries no payload here and the
    /// directory has no reason to spend a message per chunk.
    /// </remarks>
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

    private void NotYet(int field, string kind) =>
        Abandoned(field, kind, "this writer does not build this kind yet");

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

    /// <summary>The directory's bytes, or <see langword="null"/> when there is nothing to list.</summary>
    /// <param name="rowCount">The file's row count.</param>
    /// <returns>The segment, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A DIRECTORY WITH NO ENTRY IS STILL WRITTEN when the policy was the caller's own: it carries
    /// that policy, which is what an append reads to index its new blocks the same way. Under the
    /// default policy an empty directory says nothing an append would not assume from its absence,
    /// and since `Auto` became the default it would have cost every file of a sorted or numeric
    /// schema a hundred and fifty bytes for that nothing.
    /// </remarks>
    /// <param name="sidecar">For a sidecar: what binds it to the indexed file, and the payloads' encoding table.</param>
    internal byte[]? Directory(long rowCount, SidecarBinding? sidecar = null)
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
        return entries.Count == 0 && defaultPolicy && _previousEof == 0 && sidecar is null
            ? null
            : new IndexDirectory((ulong)rowCount, _previousEof, _policy, entries)
            {
                BudgetPerMille = _budgetPerMille,
                FileLength = (ulong)(sidecar?.Length ?? 0),
                FileIdentity = sidecar?.Identity,
                FileToken = sidecar?.Token,
                FileHash = sidecar?.Hash,
                ArrayEncodings = sidecar?.Encodings,
            }.ToBytes();
    }

    /// <summary>What became of every index the policy asked for.</summary>
    internal IReadOnlyList<IndexWriteReport> Reports => _reports;

    /// <inheritdoc/>
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
