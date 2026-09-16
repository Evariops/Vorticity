// The writer side of docs/10-indexes.md §7: which index each column gets, what is built, what is
// abandoned and why, and the directory that lists what survived.
//
// ONE OWNER, SO `VortexFileWriter` ONLY CALLS IT. The file writer knows segments and layouts; this
// knows policies, kinds and runs. Every kind a later step adds lands here, with its builder, and
// the file writer's part stays four calls: resolve the policy at open, feed the pass, write the
// runs before the zone maps, write the directory before the footer.
//
// WHAT A POLICY ASKS FOR IS ALWAYS IN THE REPORT. A kind this writer cannot build yet is reported
// ABANDONED with that reason rather than silently skipped or thrown: a caller who asked for an
// index reads what happened to it, and an index is a hint whose absence costs no correctness
// (docs/08-semantics.md §5).
using System;
using System.Collections.Generic;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>Builds the indexes of one file and the directory that lists them.</summary>
internal sealed class IndexWriter
{
    private readonly WritePolicy _policy;
    private readonly bool _isTabular;
    private readonly string[] _paths;
    private readonly IndexPolicy[] _columns;
    private readonly List<IndexEntry> _entries = [];
    private readonly List<IndexWriteReport> _reports = [];

    /// <param name="policy">The policy, already <see cref="WritePolicy.None"/> under <see cref="WriteProfile.Fastest"/>.</param>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="isTabular">Whether the root is a struct whose fields are the columns.</param>
    /// <param name="fieldCount">How many columns.</param>
    internal IndexWriter(WritePolicy policy, DType schema, bool isTabular, int fieldCount)
    {
        _policy = policy;
        _isTabular = isTabular;
        _paths = new string[fieldCount];
        _columns = new IndexPolicy[fieldCount];
        for (int field = 0; field < fieldCount; field++)
        {
            _paths[field] = isTabular ? schema.GetFieldName(field) : string.Empty;
            _columns[field] = policy.Of(_paths[field]);
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

    /// <summary>
    /// Decides every column's indexes once the data is written, from what the columns recorded.
    /// </summary>
    /// <param name="columns">The column writers, in field order.</param>
    /// <param name="chunkRows">Every chunk's row count, in order.</param>
    /// <param name="blockRows">Rows per block: the zone length the runs are counted in.</param>
    internal void Close(IReadOnlyList<ColumnWriter> columns, IReadOnlyList<long> chunkRows, int blockRows)
    {
        for (int field = 0; field < _columns.Length; field++)
        {
            IndexPolicy policy = _columns[field];
            switch (policy.Kind)
            {
                case IndexPolicyKind.None:
                    break;
                case IndexPolicyKind.Auto:
                    DictProbe(field, columns[field], chunkRows, blockRows);
                    break;
                case IndexPolicyKind.Bloom:
                    NotYet(field, IndexKinds.BloomSbbf);
                    break;
                case IndexPolicyKind.NgramBloom:
                    NotYet(field, IndexKinds.BloomNgram3);
                    break;
                case IndexPolicyKind.Postings:
                    NotYet(field, IndexKinds.PostingsBlocks);
                    break;
                case IndexPolicyKind.SortedRuns:
                    NotYet(field, IndexKinds.SortedRuns);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled index policy {policy.Kind}.");
            }
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
            _reports.Add(new IndexWriteReport(
                _paths[field], IndexKinds.DictProbe, IndexOutcome.Abandoned,
                "no chunk of this column is dictionary-encoded", 0, 0, 0));
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
        _reports.Add(new IndexWriteReport(
            _paths[field], kind, IndexOutcome.Abandoned,
            "this writer does not build this kind yet", 0, 0, 0));

    private uint[] ColumnPath(int field) => _isTabular ? [checked((uint)field)] : [];

    /// <summary>The directory's bytes, or <see langword="null"/> when there is nothing to list.</summary>
    /// <param name="rowCount">The file's row count.</param>
    /// <returns>The segment, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A DIRECTORY WITH NO ENTRY IS STILL WRITTEN when the policy asked for something: it carries
    /// the policy, which is what an append reads to index its new blocks the same way, and its
    /// absence would make "asked and abandoned everywhere" indistinguishable from "never asked".
    /// </remarks>
    internal byte[]? Directory(long rowCount) =>
        Enabled
            ? new IndexDirectory((ulong)rowCount, 0, _policy, _entries).ToBytes()
            : null;

    /// <summary>What became of every index the policy asked for.</summary>
    internal IReadOnlyList<IndexWriteReport> Reports => _reports;
}
