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
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Writing;

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
    internal IndexWriter(
        WritePolicy policy, DType schema, bool isTabular, int fieldCount, int budgetPerMille = 100, int blockRows = 0)
    {
        _policy = policy;
        _isTabular = isTabular;
        _budgetPerMille = budgetPerMille;
        _paths = new string[fieldCount];
        _columns = new IndexPolicy[fieldCount];
        _builders = new List<IndexBuilder>[fieldCount];
        _refusals = new string?[fieldCount];
        _columnBytes = new long[fieldCount];
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
                    break;
            }

            _refusals[field] = reason;
        }
    }

    /// <summary>
    /// Whether a policy can ask for anything at all: a default of <see cref="IndexPolicyKind.None"/>
    /// with no override is the one that cannot, and the writer then builds no index writer.
    /// </summary>
    /// <param name="policy">The policy.</param>
    internal static bool Asks(WritePolicy policy) =>
        policy.Default.Kind != IndexPolicyKind.None || policy.Columns.Count > 0;

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
        ulong blockLength = (ulong)Math.Max(blockRows, 1);
        long bytes = 0;

        // Block level: a run only where at least one of its blocks has a filter.
        List<IndexRun> blockRuns = [];
        foreach (BloomRun run in bloom.Runs)
        {
            if (run.Blocks is { Segment: { } segment } payload)
            {
                blockRuns.Add(Run(run.FirstBlock, run.BlockCount, payload));
                bytes += segment.Length;
            }
        }

        if (blockRuns.Count > 0)
        {
            _entries.Add(new IndexEntry(
                kind, ColumnPath(field), blockLength,
                BloomOptions(policy, BloomLevel.Block, [.. bloom.BlockFilterBlocks]), blockRuns));
        }

        // Generation level: one count per listed run.
        List<IndexRun> generationRuns = [];
        List<int> generationCounts = [];
        foreach (BloomRun run in bloom.Runs)
        {
            if (run.Generation is { Segment: { } segment } payload)
            {
                generationRuns.Add(Run(run.FirstBlock, run.BlockCount, payload));
                generationCounts.Add(run.GenerationBlocks);
                bytes += segment.Length;
            }
        }

        if (generationRuns.Count > 0)
        {
            _entries.Add(new IndexEntry(
                kind, ColumnPath(field), blockLength,
                BloomOptions(policy, BloomLevel.Generation, [.. generationCounts]), generationRuns));
        }

        // File level.
        int files = 0;
        if (bloom.File is { Generation: { Segment: { } fileSegment } filePayload } file)
        {
            _entries.Add(new IndexEntry(
                kind, ColumnPath(field), blockLength,
                BloomOptions(policy, BloomLevel.File, [file.GenerationBlocks]),
                [Run(file.FirstBlock, file.BlockCount, filePayload)]));
            bytes += fileSegment.Length;
            files = 1;
        }

        if (blockRuns.Count == 0 && generationRuns.Count == 0 && files == 0)
        {
            Abandoned(
                field, kind,
                $"no block holds the {policy.MinDistinct} distinct values the policy asks before a filter pays");
            return;
        }

        string? fileNote = policy.Resolutions >= 3 ? bloom.FileAbandoned : null;
        _reports.Add(new IndexWriteReport(
            _paths[field], kind, IndexOutcome.Built,
            fileNote is null ? null : "built without its file-level filter: " + fileNote,
            bytes, generationRuns.Count + files, blockRuns.Count));
    }

    private static IndexRun Run(int firstBlock, int blockCount, PendingPayload payload) =>
        new IndexRun((ulong)firstBlock, checked((uint)blockCount), [payload.Segment!.Value], [payload.DType!]);

    private static byte[] BloomOptions(IndexPolicy policy, BloomLevel level, int[] counts) =>
        new BloomIndexOptions(
            level, policy.FalsePositivePpm, policy.Hash,
            level == BloomLevel.File ? BloomBuilder.FileMaxBlocks : policy.MaxBlocks,
            counts, BloomBuilder.GenerationBlocks, policy.MinDistinct,
            policy.Kind == IndexPolicyKind.NgramBloom && policy.CaseInsensitive).ToBytes();

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

            if (segments.Count == run.Payloads.Count)
            {
                runs.Add(new IndexRun(
                    (ulong)run.FirstBlock, checked((uint)run.BlockCount), segments, dtypes,
                    (ulong)run.Entries, KeyRunOptions.Run(run.Segments)));
            }
        }

        if (runs.Count == 0)
        {
            Abandoned(field, kind, "the column wrote no chunk");
            return;
        }

        _entries.Add(new IndexEntry(
            kind, ColumnPath(field), (ulong)Math.Max(blockRows, 1),
            KeyRunOptions.Entry(keys.SegmentEntries, keys.CaseInsensitive), runs));
        _reports.Add(new IndexWriteReport(_paths[field], kind, IndexOutcome.Built, null, bytes, 0, runs.Count));
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

    private uint[] ColumnPath(int field) => _isTabular ? [checked((uint)field)] : [];

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
    internal byte[]? Directory(long rowCount)
    {
        if (!Enabled)
        {
            return null;
        }

        bool defaultPolicy = _policy.Default == IndexPolicy.Auto && _policy.Columns.Count == 0;
        return _entries.Count == 0 && defaultPolicy
            ? null
            : new IndexDirectory((ulong)rowCount, 0, _policy, _entries).ToBytes();
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
    }
}
