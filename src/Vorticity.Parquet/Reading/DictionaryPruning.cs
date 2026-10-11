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
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// The row groups a scan's filter cannot select, proven by the dictionaries of the columns it
/// reads: a chunk every data page of which is codes holds no value but its dictionary's entries,
/// over which each part of the filter that reads that column alone is evaluated by the engine's own
/// evaluator, once per distinct value rather than once per row.
/// </summary>
/// <remarks>
/// <para>
/// A part's evaluation says whether it can come out true on a row of the chunk, and whether it can
/// come out false. A null row adds neither, since every predicate but a null check is unknown on
/// one, and a part that checks for null is left open over a chunk that may hold one. The parts are
/// combined as the filter combines its rows, soundly for three-valued logic: an AND can be true only
/// where both its sides can, and false where either can; an OR the other way around; a NOT swaps
/// the two. A group whose filter cannot come out true is not read.
/// </para>
/// <para>
/// A chunk's pages are all codes by its <c>encoding_stats</c>, or, without them, by its encodings,
/// when they are the first version's dictionary encoding and the levels' alone, as the standard's
/// reference reader decides. The dictionary page is read on its own, from its offset to the first
/// data page's, and again with its chunk when the group is read after all, which then steps over it:
/// the dictionary decoded to prune is handed to the scan's reader of the column with the bytes it
/// was decoded from. One past 8 MiB is not read to prune.
/// </para>
/// </remarks>
internal sealed class DictionaryPruning : IDisposable
{
    /// <summary>The largest dictionary page read to prune.</summary>
    private const int MaximumBytes = 8 << 20;

    /// <summary>The encodings of a chunk of codes that has no <c>encoding_stats</c>: the first version's dictionary encoding and the levels'.</summary>
    private const uint CodesAndLevels =
        (1u << (int)ParquetEncoding.PlainDictionary) | (1u << (int)ParquetEncoding.Rle) | (1u << (int)ParquetEncoding.BitPacked);

    private readonly FilterColumns _filter;
    private readonly Part _plan;

    /// <summary>Per filter column, whether a part reads it alone.</summary>
    private readonly bool[] _alone;

    /// <summary>Per filter column a part reads alone, the reader of its dictionaries, made at the first; null for the others.</summary>
    private readonly ColumnChunkReader?[] _readers;

    /// <summary>Per filter column, the slot of its dictionary page in the group's request, or -1.</summary>
    private readonly int[] _slots;

    /// <summary>Per filter column, the node of the group's dictionary, or -1 when it has none to prune by.</summary>
    private readonly int[] _nodes;

    /// <summary>Per filter column, whether the group's chunk may hold a null.</summary>
    private readonly bool[] _nulls;

    /// <summary>
    /// Per filter column, a reference to the bytes of the dictionary the group just kept was pruned
    /// by, until the scan's reader of the column takes them with it, or the next group comes.
    /// </summary>
    private readonly SegmentOwner?[] _bytes;

    /// <summary>
    /// The scan's context, whose arena the dictionaries are decoded into between two batches, or null
    /// for one of this pruning's own, made at the first dictionary.
    /// </summary>
    private readonly ScanContext? _shared;
    private ScanContext? _owned;
    private CanonicalArena? _arena;
    private readonly SegmentRequestSet _requests = new();
    private byte[] _states = [];

    private DictionaryPruning(FilterColumns filter, Part plan, bool[] alone, ScanContext? context)
    {
        _filter = filter;
        _shared = context;
        _plan = plan;
        _alone = alone;
        _readers = new ColumnChunkReader?[alone.Length];
        _slots = new int[alone.Length];
        _nodes = new int[alone.Length];
        _nulls = new bool[alone.Length];
        _bytes = new SegmentOwner?[alone.Length];
    }

    /// <summary>
    /// The pruning of <paramref name="filter"/>'s row groups by their dictionaries; null when no part
    /// of it reads one of its columns alone. A scan gives its <paramref name="context"/>, whose arena
    /// holds nothing while the next row group is chosen; without one, the pruning makes its own.
    /// </summary>
    internal static DictionaryPruning? For(FilterColumns? filter, ScanContext? context = null)
    {
        if (filter is null)
        {
            return null;
        }

        bool[] alone = new bool[filter.Columns.Length];
        Part plan = Plan(filter.Filter, filter, alone, new HashSet<string>(StringComparer.Ordinal));
        return Array.IndexOf(alone, true) < 0 ? null : new DictionaryPruning(filter, plan, alone, context);
    }

    /// <summary>
    /// Whether the dictionaries of row group <paramref name="group"/> prove the filter selects none
    /// of its rows, the dictionary pages of the columns its parts read alone read in one request.
    /// </summary>
    internal async ValueTask<bool> RulesOutAsync(int group, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        ParquetFile file = _filter.File;
        ParquetFooter footer = file.Footer;
        int asked = 0;
        long bytes = 0;
        Drop();
        _requests.Release();
        for (int i = 0; i < _readers.Length; i++)
        {
            _slots[i] = -1;
            _nodes[i] = -1;
            if (!_alone[i])
            {
                continue;
            }

            ColumnChunkMetadata chunk = footer.Chunk(group, _filter.Columns[i]);
            long start = chunk.DictionaryPageOffset;
            long length = chunk.DataPageOffset - start;
            if (!OnlyCodes(chunk) || chunk.IsEncrypted || chunk.HasFilePath || start <= 0 || length > MaximumBytes || !file.Holds(start, length))
            {
                continue;
            }

            _slots[i] = _requests.Add(new SegmentSpec((ulong)start, (uint)length, 0, 0, 0));
            _nulls[i] = file.Compiled.Columns[_filter.Columns[i]].MaxDefinitionLevel > 0 && !(chunk.Statistics.HasNullCount && chunk.Statistics.NullCount == 0);
            bytes += length;
            asked++;
        }

        if (asked == 0)
        {
            return false;
        }

        ScanCounters.Note(metrics, asked, bytes);
        await file.Reader.ReadManyAsync(_requests, cancellationToken).ConfigureAwait(false);
        ScanContext context = _shared ?? (_owned ??= new ScanContext([], new VortexReadOptions { MaxDecompressedBytes = file.Options.MaxDecompressedBytes }));
        context.ResetBatch();
        _arena = context.Canonical;
        bool kept = false;
        try
        {
            for (int i = 0; i < _readers.Length; i++)
            {
                if (_slots[i] >= 0)
                {
                    _nodes[i] = Reader(i).ReadDictionary(context, _requests.GetBuffer(_slots[i]), footer.Chunk(group, _filter.Columns[i]).Codec);
                }
            }

            kept = Truth(_plan).True;
            return !kept;
        }
        finally
        {
            // A group kept is read next: its dictionaries wait for the scan's readers, their bytes
            // held past the request's release.
            for (int i = 0; i < _readers.Length; i++)
            {
                if (kept && _slots[i] >= 0)
                {
                    _bytes[i] = _requests.GetOwner(_slots[i]).Retain();
                }
                else
                {
                    _readers[i]?.Release();
                }
            }

            context.ResetBatch();
            _requests.Release();
        }
    }

    /// <summary>
    /// Hands the dictionary of leaf <paramref name="leaf"/> by which the group just kept was pruned to
    /// <paramref name="reader"/>, started on the group's chunk of it: false where the pruning holds none.
    /// </summary>
    internal bool Hand(int leaf, ColumnChunkReader reader)
    {
        int i = Array.IndexOf(_filter.Columns, leaf);
        if (i < 0 || _bytes[i] is not { } bytes || _readers[i] is not { } pruning || !pruning.HandDictionary(reader, bytes))
        {
            return false;
        }

        _bytes[i] = null;
        return true;
    }

    /// <summary>Gives back the dictionaries no reader took, and the bytes they were decoded from.</summary>
    private void Drop()
    {
        for (int i = 0; i < _readers.Length; i++)
        {
            _readers[i]?.Release();
            _bytes[i]?.Release();
            _bytes[i] = null;
        }
    }

    public void Dispose()
    {
        Drop();
        foreach (ColumnChunkReader? reader in _readers)
        {
            reader?.Dispose();
        }

        _requests.Release();
        _requests.Dispose();
        _owned?.Dispose();
    }

    /// <summary>The reader of filter column <paramref name="i"/>'s dictionaries.</summary>
    private ColumnChunkReader Reader(int i)
    {
        if (_readers[i] is { } reader)
        {
            return reader;
        }

        ParquetFile file = _filter.File;
        ParquetColumn column = file.Compiled.Columns[_filter.Columns[i]];
        DTypeArena types = new();
        return _readers[i] = new ColumnChunkReader(
            column, VortexTypes.ToDType(column.Type, types), types.Bool(Nullability.NonNullable), file.Session.Options.EnginePool, file.Options.MaxDecompressedBytes)
        {
            VerifyChecksums = file.Options.VerifyChecksums,
            Counters = file.Counters,
        };
    }

    /// <summary>
    /// Whether every data page of the chunk is dictionary codes: its <c>encoding_stats</c> say so,
    /// or, without them, its encodings are the first version's dictionary encoding and the levels'.
    /// </summary>
    private static bool OnlyCodes(in ColumnChunkMetadata chunk) =>
        chunk.HasEncodingStats
            ? chunk.AllDataPagesDictionary
            : !chunk.HasUnknownEncoding
                && (chunk.Encodings & (1u << (int)ParquetEncoding.PlainDictionary)) != 0
                && (chunk.Encodings & ~CodesAndLevels) == 0;

    /// <summary>
    /// The parts of <paramref name="expr"/>: each largest part that reads one of the filter's columns
    /// alone a leaf, marked in <paramref name="alone"/>; the rest as the filter combines them.
    /// </summary>
    private static Part Plan(VortexExpr expr, FilterColumns filter, bool[] alone, HashSet<string> paths)
    {
        paths.Clear();
        expr.CollectFields(paths);
        if (paths.Count == 1 && expr.Kind != ExprKind.ListContains)
        {
            using HashSet<string>.Enumerator only = paths.GetEnumerator();
            only.MoveNext();
            for (int column = 0; column < filter.Fields.Length; column++)
            {
                if (string.Equals(filter.Fields[column].Path, only.Current, StringComparison.Ordinal))
                {
                    alone[column] = true;
                    return new Leaf(column, new FilterEvaluator(expr), ChecksNull(expr));
                }
            }
        }

        switch (expr)
        {
            case LogicalExpr logical:
                Part left = Plan(logical.Left, filter, alone, paths);
                Part right = Plan(logical.Right, filter, alone, paths);
                return logical.IsAnd ? new Both(left, right) : new Either(left, right);
            case NotExpr not:
                return new Negation(Plan(not.Operand, filter, alone, paths));
            default:
                return Open.Instance;
        }
    }

    private static bool ChecksNull(VortexExpr expr) => expr switch
    {
        NullCheckExpr => true,
        LogicalExpr logical => ChecksNull(logical.Left) || ChecksNull(logical.Right),
        NotExpr not => ChecksNull(not.Operand),
        _ => false,
    };

    /// <summary>Whether <paramref name="part"/> can come out true on a row of the group, and whether it can come out false.</summary>
    private (bool True, bool False) Truth(Part part)
    {
        switch (part)
        {
            case Leaf leaf:
                int node = _nodes[leaf.Column];
                if (node < 0 || (leaf.ChecksNull && _nulls[leaf.Column]))
                {
                    return (true, true);
                }

                CanonicalArena arena = _arena!;
                int rows = arena.GetNode(node).Length;
                if (_states.Length < rows)
                {
                    _states = new byte[Math.Max(rows, _states.Length * 2)];
                }

                Span<byte> states = _states.AsSpan(0, rows);
                leaf.Evaluator.EvaluateColumn(arena, node, rows, states);
                return (states.Contains(Trilean.True), states.Contains(Trilean.False));
            case Both both:
                (bool leftTrue, bool leftFalse) = Truth(both.Left);
                (bool rightTrue, bool rightFalse) = Truth(both.Right);
                return (leftTrue && rightTrue, leftFalse || rightFalse);
            case Either either:
                (bool firstTrue, bool firstFalse) = Truth(either.Left);
                (bool secondTrue, bool secondFalse) = Truth(either.Right);
                return (firstTrue || secondTrue, firstFalse && secondFalse);
            case Negation negation:
                (bool operandTrue, bool operandFalse) = Truth(negation.Operand);
                return (operandFalse, operandTrue);
            default:
                return (true, true);
        }
    }

    private abstract class Part;

    /// <summary>A part that reads one of the filter's columns alone, evaluated over its dictionary.</summary>
    private sealed class Leaf(int column, FilterEvaluator evaluator, bool checksNull) : Part
    {
        internal int Column { get; } = column;

        internal FilterEvaluator Evaluator { get; } = evaluator;

        internal bool ChecksNull { get; } = checksNull;
    }

    private sealed class Both(Part left, Part right) : Part
    {
        internal Part Left { get; } = left;

        internal Part Right { get; } = right;
    }

    private sealed class Either(Part left, Part right) : Part
    {
        internal Part Left { get; } = left;

        internal Part Right { get; } = right;
    }

    private sealed class Negation(Part operand) : Part
    {
        internal Part Operand { get; } = operand;
    }

    /// <summary>A part no dictionary answers: it may come out either way.</summary>
    private sealed class Open : Part
    {
        internal static readonly Open Instance = new();
    }
}
