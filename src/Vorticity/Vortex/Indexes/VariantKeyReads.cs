using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Indexes;

/// <summary>
/// The chunks of the variant columns a scan reads through some of their shredded keys alone, found by
/// the segment each lies in: per chunk, each key's range and tree from the path map
/// (<see cref="VariantPathMap"/>). A flat layout whose segment is one of them registers the keys'
/// ranges instead of the segment, and decodes each key as a column, never the variant whole.
/// </summary>
/// <remarks>
/// <para>
/// A chunk so read is a variant node of three fields: the rows' metadata and residual, neither read
/// and so null, and a struct with a group per key read. A group is the key's values of other classes,
/// with their metadata, and its typed column, the same dtypes in every chunk whichever form the chunk
/// shredded the key in, so that the chunks of a batch join. A path of one key reads its values from
/// its group (<see cref="VariantFieldExpr"/>).
/// </para>
/// <para>
/// A column is read this way only when every chunk holds every key asked in its map, and the key's
/// typed column wears one dtype across them; otherwise its chunks are read whole.
/// </para>
/// <para>
/// A column asked for keys no chunk shreds has each chunk its map knows read for its residual, where those
/// keys lie and nowhere else: the chunk's segment read and decoded as any, its variant giving the residual
/// alone, the keys it shreds neither decoded nor put back into the rows (<see cref="LeavesUnshredded"/>).
/// A question over such a key of 36 000 log records took 6.6 ms, two thirds of it putting the shredded
/// keys back, against 2.2 so read.
/// </para>
/// </remarks>
internal sealed class VariantKeyReads
{
    private readonly Chunk[] _chunks;
    private readonly byte[][] _residual;

    private VariantKeyReads(Chunk[] chunks, byte[][] residual)
    {
        _chunks = chunks;
        _residual = residual;
    }

    /// <summary>
    /// The reads the scan's filter makes in a pass of its own, before the scan reads the rows it kept: the keys the filter
    /// alone reads of columns the scan reads whole, beside those the scan reads by keys; null when it makes none apart.
    /// </summary>
    internal VariantKeyReads? FilterPass { get; private init; }

    /// <summary>
    /// The reads of a scan that reads <paramref name="columns"/> through their keys and whose filter alone reads
    /// <paramref name="filtered"/>, columns the scan reads whole, through theirs: the scan's own, and its filter's pass
    /// reading both (<see cref="FilterPass"/>); null when neither is read by keys.
    /// </summary>
    internal static async ValueTask<VariantKeyReads?> LoadAsync(
        VortexFile file, IReadOnlyDictionary<string, string[]>? columns, IReadOnlyDictionary<string, string[]>? filtered,
        CancellationToken cancellationToken, QuestionReads? question = null)
    {
        VariantKeyReads? scan = columns is null ? null : await LoadAsync(file, columns, cancellationToken, question).ConfigureAwait(false);
        if (filtered is null || await LoadAsync(file, filtered, cancellationToken, question).ConfigureAwait(false) is not { } alone)
        {
            return scan;
        }

        Chunk[] both = [.. scan?._chunks ?? [], .. alone._chunks];
        Array.Sort(both, static (left, right) => left.Start.CompareTo(right.Start));
        VariantKeyReads pass = new VariantKeyReads(both, [.. scan?._residual ?? [], .. alone._residual]);
        return new VariantKeyReads(scan?._chunks ?? [], scan?._residual ?? []) { FilterPass = pass };
    }

    /// <summary>
    /// The variant columns, by name, that a reader of <paramref name="uses"/> and of the filter
    /// <paramref name="filter"/> reads through top-level keys alone, with those keys; null for none.
    /// A column any other use names, whole or by a deeper path, is read whole.
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]>? Of(IEnumerable<FieldExpr> uses, VortexExpr? filter)
    {
        Dictionary<string, HashSet<string>>? keys = null;
        HashSet<string>? whole = null;
        List<FieldExpr> all = [.. uses];
        if (filter is not null)
        {
            Scanning.ScanBuilder.FieldsOf(filter, all);
        }

        foreach (FieldExpr use in all)
        {
            if (use is VariantFieldExpr { Steps: [{ IsKey: true } step] } path && path.Column.Segments is null or { Length: 1 } && !path.Column.Path.Contains('.', StringComparison.Ordinal))
            {
                keys ??= new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                if (!keys.TryGetValue(path.Column.Path, out HashSet<string>? named))
                {
                    keys[path.Column.Path] = named = new HashSet<string>(StringComparer.Ordinal);
                }

                named.Add(step.Key!);
            }
            else
            {
                string column = use is VariantFieldExpr deep ? deep.Column.Path : use.Segments is { Length: > 0 } segments ? segments[0] : use.Path.Split('.')[0];
                (whole ??= new HashSet<string>(StringComparer.Ordinal)).Add(column);
            }
        }

        if (keys is null)
        {
            return null;
        }

        Dictionary<string, string[]> read = new(StringComparer.Ordinal);
        foreach ((string column, HashSet<string> named) in keys)
        {
            if (whole?.Contains(column) != true)
            {
                read[column] = [.. named];
            }
        }

        return read.Count == 0 ? null : read;
    }

    /// <summary>
    /// Whether the path maps of <paramref name="file"/> let a reader read every key of <paramref name="columns"/> by key in
    /// every chunk, as it reads an object a job shredded those keys in: what an object an append shredded them in holds.
    /// </summary>
    internal static async ValueTask<bool> ShredsEveryAsync(
        VortexFile file, IReadOnlyDictionary<string, string[]> columns, CancellationToken cancellationToken)
    {
        DType schema = file.DType;
        if (schema.Kind != DTypeKind.Struct || !file.HasIndexDirectory || columns.Count == 0)
        {
            return false;
        }

        DTypeArena types = new DTypeArena();
        foreach ((string name, string[] wanted) in columns)
        {
            int field = schema.IndexOfField(name);
            if (field < 0 || schema.GetField(field).Kind != DTypeKind.Variant
                || await VariantPathMap.ReadAsync(file, field, wanted, cancellationToken).ConfigureAwait(false) is not { } map
                || Column(map, wanted, types) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The chunks of <paramref name="columns"/> in <paramref name="file"/> that its path maps let a
    /// reader read by keys; null when none does.
    /// </summary>
    internal static async ValueTask<VariantKeyReads?> LoadAsync(
        VortexFile file, IReadOnlyDictionary<string, string[]> columns, CancellationToken cancellationToken, QuestionReads? question = null)
    {
        // A question's scans of an object read the same columns by the same keys: the reads its first made.
        if (question is null)
        {
            return await ReadAsync(file, columns, question: null, cancellationToken).ConfigureAwait(false);
        }

        if (question.TryGetKeyReads(file, columns, out VariantKeyReads? kept))
        {
            return kept;
        }

        VariantKeyReads? read = await ReadAsync(file, columns, question, cancellationToken).ConfigureAwait(false);
        question.KeepKeyReads(file, columns, read);
        return read;
    }

    /// <summary>The reads as <see cref="LoadAsync(VortexFile, IReadOnlyDictionary{string, string[]}, CancellationToken, QuestionReads?)"/> says, made from the file's path maps.</summary>
    private static async ValueTask<VariantKeyReads?> ReadAsync(
        VortexFile file, IReadOnlyDictionary<string, string[]> columns, QuestionReads? question, CancellationToken cancellationToken)
    {
        DType schema = file.DType;
        if (schema.Kind != DTypeKind.Struct || !file.HasIndexDirectory)
        {
            return null;
        }

        List<Chunk> chunks = [];
        List<byte[]> residual = [];
        DTypeArena types = new DTypeArena();
        foreach ((string name, string[] wanted) in columns)
        {
            int field = schema.IndexOfField(name);
            if (field < 0 || schema.GetField(field).Kind != DTypeKind.Variant
                || await VariantPathMap.ReadAsync(file, field, wanted, cancellationToken, question).ConfigureAwait(false) is not { } map)
            {
                continue;
            }

            if (Column(map, wanted, types) is { } read)
            {
                chunks.AddRange(read);
                long keyed = 0;
                foreach (Chunk chunk in read)
                {
                    keyed += chunk.Rows;
                }

                // The chunks that shred none of these keys hold them in their residuals alone: read for their residuals,
                // the keys they shred not put back, where read whole each rebuilt every row's variant. Sought only where
                // the chunks that shred them leave rows, a column whose every chunk shreds one walking no layout.
                if (keyed < file.RowCount && Laid(file, field, Shredding(map, wanted)) is { Count: > 0 } others)
                {
                    chunks.AddRange(others);
                    foreach (string key in wanted)
                    {
                        residual.Add(System.Text.Encoding.UTF8.GetBytes(key));
                    }
                }

                continue;
            }

            // Keys no chunk shreds lie in the residuals alone: each chunk is read for its residual, found where the
            // map's own keys lie in it.
            if (map.Chunks.Count > 0 && !AnyShredded(map, wanted)
                && await VariantPathMap.ReadAsync(file, field, wanted: null, cancellationToken, question).ConfigureAwait(false) is { } shredded)
            {
                chunks.AddRange(Residuals(shredded));
                foreach (string key in wanted)
                {
                    residual.Add(System.Text.Encoding.UTF8.GetBytes(key));
                }
            }
        }

        if (chunks.Count == 0)
        {
            return null;
        }

        chunks.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return new VariantKeyReads([.. chunks], [.. residual]);
    }

    /// <summary>
    /// Whether the chunk whose shredded tree is <paramref name="shredded"/> may give its residual alone: the tree shreds
    /// no key a column of the scan reads from residuals, or is no object's, whose keys then all lie in the residual.
    /// Asked by the decoder of the chunk's variant, which holds the tree, so that a map wrong about a chunk costs the
    /// keys put back, never a value.
    /// </summary>
    internal bool LeavesUnshredded(DType shredded)
    {
        if (_residual.Length == 0)
        {
            return false;
        }

        if (shredded.Kind != DTypeKind.Struct)
        {
            return true;
        }

        for (int f = 0; f < shredded.FieldCount; f++)
        {
            ReadOnlySpan<byte> name = shredded.GetFieldNameUtf8(f);
            foreach (byte[] key in _residual)
            {
                if (name.SequenceEqual(key))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a chunk of <paramref name="map"/> shreds one of <paramref name="wanted"/>.</summary>
    private static bool AnyShredded(VariantPathMap map, string[] wanted)
    {
        foreach (string key in wanted)
        {
            if (map.IndexOf(key) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first rows of the chunks of <paramref name="map"/> that shred one of <paramref name="wanted"/>.</summary>
    private static HashSet<long> Shredding(VariantPathMap map, string[] wanted)
    {
        HashSet<long> rows = [];
        foreach (string key in wanted)
        {
            if (map.IndexOf(key) is int at and >= 0)
            {
                foreach (PathChunk part in map.Chunks[at])
                {
                    rows.Add(part.FirstRow);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// The chunks of the column at top-level field <paramref name="field"/> but those whose first rows <paramref name="apart"/>
    /// holds, each read for its residual: found by the segment the file's layout lays it in, which the layout tree holds
    /// read, and of its rows; none when the column is laid out otherwise than in chunks of one segment each.
    /// </summary>
    private static List<Chunk> Laid(VortexFile file, int field, HashSet<long> apart)
    {
        List<Chunk> chunks = [];
        try
        {
            ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
            (List<(Layouts.LayoutNode Flat, long Start)> laid, _) = VortexFileWriter.AppendPlan.ColumnChunks(file.LayoutTree, field);
            foreach ((Layouts.LayoutNode flat, long start) in laid)
            {
                if (!apart.Contains(start) && flat.Segments is [uint segment] && segment < (uint)specs.Length)
                {
                    chunks.Add(Chunk.ResidualOf(specs[(int)segment].Offset, checked((int)flat.RowCount)));
                }
            }
        }
        catch (VortexUnsupportedException)
        {
            return [];
        }

        return chunks;
    }

    /// <summary>
    /// The chunks of the column <paramref name="map"/> describes, each read for its residual: found by where its first
    /// shredded key's range starts, which lies in its segment, and of its rows.
    /// </summary>
    private static List<Chunk> Residuals(VariantPathMap map)
    {
        SortedDictionary<long, (ulong Start, long Rows)> found = [];
        foreach (PathChunk[] parts in map.Chunks)
        {
            foreach (PathChunk part in parts)
            {
                if (part.Length == 0)
                {
                    continue;
                }

                found[part.FirstRow] = found.TryGetValue(part.FirstRow, out (ulong Start, long Rows) seen)
                    ? (Math.Min(seen.Start, part.Offset), seen.Rows)
                    : (part.Offset, part.Rows);
            }
        }

        List<Chunk> chunks = new List<Chunk>(found.Count);
        foreach ((ulong start, long rows) in found.Values)
        {
            chunks.Add(Chunk.ResidualOf(start, checked((int)rows)));
        }

        return chunks;
    }

    /// <summary>The chunk whose keys lie in the segment at <paramref name="offset"/>, <paramref name="length"/> bytes long; null for none.</summary>
    internal Chunk? Find(ulong offset, uint length)
    {
        int low = 0;
        int high = _chunks.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (_chunks[middle].Start < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < _chunks.Length && _chunks[low].Start < offset + length ? _chunks[low] : null;
    }

    /// <summary>
    /// The column's chunks that shred a key wanted, each key's typed column of one dtype across them; null
    /// when the keys' chunks disagree. A chunk shreds only the keys its rows hold, so a key it lacks reads
    /// as null in it, and a chunk that holds none of the keys is not listed, and reads whole.
    /// </summary>
    private static List<Chunk>? Column(VariantPathMap map, string[] wanted, DTypeArena types)
    {
        int[] keys = new int[wanted.Length];
        for (int k = 0; k < wanted.Length; k++)
        {
            keys[k] = map.IndexOf(wanted[k]);
            if (keys[k] < 0)
            {
                return null;
            }
        }

        // The chunks by their first row, each its rows, from whichever key it shreds.
        SortedDictionary<long, long> rowsOf = [];
        foreach (int key in keys)
        {
            foreach (PathChunk part in map.Chunks[key])
            {
                if (rowsOf.TryGetValue(part.FirstRow, out long rows) && rows != part.Rows)
                {
                    return null;
                }

                rowsOf[part.FirstRow] = part.Rows;
            }
        }

        long[] firsts = [.. rowsOf.Keys];
        DType binary = types.Binary(Nullability.Nullable);
        Key?[][] perChunk = new Key?[firsts.Length][];
        for (int c = 0; c < firsts.Length; c++)
        {
            perChunk[c] = new Key?[wanted.Length];
        }

        DType[] groups = new DType[wanted.Length];
        for (int k = 0; k < wanted.Length; k++)
        {
            DType? typed = null;
            foreach (PathChunk part in map.Chunks[keys[k]])
            {
                DType shape = DTypeProtobuf.Read(part.DType.Span, types);
                DType own = shape.Kind == DTypeKind.Variant ? NestedTyped(part, types) ?? default : shape;
                if (own.IsDefault || (typed is { } known && !SameShape(known, own)))
                {
                    return null;
                }

                typed ??= own;
                perChunk[Array.BinarySearch(firsts, part.FirstRow)][k] = new Key(wanted[k], part, shape, own);
            }

            if (typed is null)
            {
                return null;
            }

            groups[k] = types.Struct(["metadata", "value", "typed_value"], [binary, binary, typed.Value], Nullability.NonNullable);
            for (int c = 0; c < firsts.Length; c++)
            {
                perChunk[c][k] ??= new Key(wanted[k], null, typed.Value, typed.Value);
            }
        }

        DType tree = types.Struct(wanted, groups, Nullability.NonNullable);
        List<Chunk> chunks = new List<Chunk>(firsts.Length);
        for (int c = 0; c < firsts.Length; c++)
        {
            ulong start = ulong.MaxValue;
            foreach (Key? key in perChunk[c])
            {
                if (key!.Part is { Length: > 0 } part)
                {
                    start = Math.Min(start, part.Offset);
                }
            }

            if (start != ulong.MaxValue)
            {
                chunks.Add(new Chunk(start, checked((int)rowsOf[firsts[c]]), [.. perChunk[c]!], groups, tree, binary));
            }
        }

        return chunks;
    }

    /// <summary>The typed column's dtype of a key a chunk kept as a variant: the one its own metadata names.</summary>
    private static DType? NestedTyped(PathChunk part, DTypeArena types)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        ArrayView view = ArrayView.Root(part.Tree.Span, ref budget);
        return Arrays.Metadata.VariantMetadata.Read(view.Root_.Metadata).ShreddedDType is { } bytes
            ? DTypeProtobuf.Read(bytes, types)
            : null;
    }

    /// <summary>
    /// Whether two dtypes are the same shape, names, nesting and nullability alike: their structural equality, an index's
    /// in the one arena that interns both, where both were formatted as texts at every chunk of every scan.
    /// </summary>
    private static bool SameShape(DType left, DType right) => left.Equals(right);

    /// <summary>
    /// One key of a chunk: its name, its range and tree, the dtype the chunk shredded it as, and its typed
    /// column's; no range for a key the chunk does not shred, since none of its rows holds it.
    /// </summary>
    internal sealed record Key(string Name, PathChunk? Part, DType Shape, DType Typed);

    /// <summary>
    /// One chunk read by keys: where its first key's range starts, its rows, its keys, each key's
    /// group dtype, the struct of them, and the dtype of a column of variant binaries.
    /// </summary>
    internal sealed record Chunk(ulong Start, int Rows, Key[] Keys, DType[] Groups, DType Tree, DType Binary)
    {
        /// <summary>
        /// Whether the chunk is read for keys its shredding does not hold: its segment read and decoded as any, its
        /// variant giving its residual alone; it has no keys of its own then.
        /// </summary>
        internal bool Residual { get; init; }

        /// <summary>A chunk of <paramref name="rows"/> rows, its first shredded key's range at <paramref name="start"/>, read for its residual.</summary>
        internal static Chunk ResidualOf(ulong start, int rows) => new Chunk(start, rows, [], [], default, default) { Residual = true };

        /// <summary>
        /// Registers each key's range, or, where the split reads the chunk's live granules apart, the granules of its rows
        /// <paramref name="rows"/> alone (<see cref="GranulePlans.TryRegisterKey"/>, spec E, 4.1), the chunk of segment offset
        /// <paramref name="chunk"/> giving the marks of a key's text; every range whole in a chunk
        /// that kept a key's values of other classes beside its typed column, which a split does not decode apart.
        /// </summary>
        internal void Register(SegmentRequestSet segments, RowRange rows, ulong chunk)
        {
            bool apart = segments.Granules is not null && Typed;
            foreach (Key key in Keys)
            {
                if (key.Part is { Length: > 0 } part && (!apart || !segments.Granules!.TryRegisterKey(part, key.Shape, rows, segments, chunk)))
                {
                    segments.Add(Spec(part));
                }
            }
        }

        /// <summary>Whether every key the chunk shreds is its typed column alone, with no values of other classes beside it.</summary>
        private bool Typed => Array.TrueForAll(Keys, static key => key.Part is null || key.Shape.Kind != DTypeKind.Variant);

        /// <summary>
        /// The answer of <paramref name="predicate"/> on key <paramref name="name"/> read as <paramref name="read"/>, over the
        /// chunk's rows, by the encoding of the key's typed column, as a column's encoding answers (spec E, 4.2): a row without
        /// the key unknown, the key reading null there. False when the chunk keeps values of other classes beside the typed
        /// column, when the typed column is not of the class read, when the split reads the key by its granules
        /// (<paramref name="plans"/>, over <paramref name="rows"/>), or when the encoding declines.
        /// </summary>
        internal bool TryAnswer(
            ScanContext context, string name, Compute.VariantRead read, Compute.EncodedPredicate predicate, GranulePlans? plans, RowRange rows,
            ref Compute.PredicateBits bits)
        {
            foreach (Key key in Keys)
            {
                if (!string.Equals(key.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (key.Part is not { } part)
                {
                    bits.FillUnknown();
                    return true;
                }

                if (key.Shape.Kind == DTypeKind.Variant || !Reads(read, key.Typed) || (plans is not null && part.Length > 0 && plans.Find(part.Offset, rows) is not null))
                {
                    return false;
                }

                Buffers.VortexBuffer segment = part.Length > 0 ? context.Segments.GetBuffer(context.Segments.Add(Spec(part))) : default;
                context.Decode.LoadBlob(part.Tree.Span, segment);
                ArrayNode root = context.Nodes.Root;
                ArrayDecoder decoder = ArrayDecoderTable.Require(context, root.Encoding, root.EncodingSpecIndex);
                return decoder.EvaluatesWithoutFullDecode && decoder.TryEvaluate(context.Decode, in root, key.Shape, Rows, predicate, ref bits);
            }

            return false;
        }

        /// <summary>Whether a typed column of <paramref name="typed"/> holds the values a read as <paramref name="read"/> takes.</summary>
        private static bool Reads(Compute.VariantRead read, DType typed) => read switch
        {
            Compute.VariantRead.AsString => typed.Kind == DTypeKind.Utf8,
            Compute.VariantRead.AsInt64 => typed.Kind == DTypeKind.Primitive && typed.PType.IsInteger(),
            _ => false,
        };

        /// <summary>
        /// Rows <c>[start, start + count)</c> of the chunk as a variant node of <paramref name="variant"/>'s dtype, each key
        /// decoded from the granules <paramref name="plans"/> read of it for them, or, read whole, from its range; -1 when no
        /// key was read by its granules, the chunk's decode, retained for all its splits, then serving. With
        /// <paramref name="unplanned"/>, a part of a chunk whatever read its keys: each key's rows decoded from its whole
        /// range, a key that keeps values of other classes beside its typed column too; -1 when a key's encoding decodes no
        /// range of its rows, which the chunk's whole decode then serves.
        /// </summary>
        /// <remarks>
        /// A question that reads one granule of each chunk decoded the whole of each chunk for it, retained for splits that
        /// never came: the hosts' 5xx by service read 59 chunks of some 216,000 rows a key, 49 of them at each question,
        /// 8,192 rows of each, and spent a third of its time expanding their runs (2026-11-02). A key's run ends and values,
        /// which every range reads whole, are decoded once a chunk for every range (<see cref="ArrayDecodeContext.DecodeWholeChild"/>).
        /// </remarks>
        internal int DecodeRange(ScanContext context, DType variant, uint segment, int start, int count, GranulePlans? plans, bool unplanned = false)
        {
            RowRange rows = new RowRange(start, start + count);
            if (!unplanned)
            {
                bool planned = false;
                foreach (Key key in Keys)
                {
                    if (key.Part is { Length: > 0 } part)
                    {
                        planned |= plans?.Find(part.Offset, rows) is not null;
                    }
                }

                if (!planned || !Typed)
                {
                    return -1;
                }
            }

            CanonicalArena arena = context.Canonical;
            Span<int> stack = stackalloc int[16];
            using Scratch<int> groups = new(Keys.Length, stack);
            int none = -1;
            for (int k = 0; k < Keys.Length; k++)
            {
                Key key = Keys[k];
                if (key.Part is not { } part)
                {
                    int absent = None();
                    groups.Span[k] = arena.AddStruct(Groups[k], count, Validity.NonNullable, [absent, absent, arena.AddNull(key.Typed, count)]);
                    continue;
                }

                Buffers.VortexBuffer region = default;
                ReadOnlyMemory<byte> tree = part.Tree;
                GranulePlans.Plan? plan = part.Length > 0 ? plans?.Find(part.Offset, rows) : null;
                if (plan is not null)
                {
                    tree = plan.Tree;
                    region = Region(context, part, plan);
                }
                else if (part.Length > 0)
                {
                    region = context.Segments.GetBuffer(context.Segments.Add(Spec(part)));
                }

                context.Decode.LoadBlob(tree.Span, region);
                ArrayNode root = context.Nodes.Root;

                // A key read from its whole range, under a scope of its own: the children every range reads whole, a run
                // end's ends and values, are retained for the scan's other parts of the chunk, which decompressed them again,
                // a frame in two of a question of the services under 5xx over the last quarter of an hour (2026-11-02). A
                // key read by its granules decodes from a region of their ranges alone, which no other part shares.
                uint? scope = plan is null && part.Length > 0 && KeyScope(segment, k) is uint own ? own : null;
                uint? outer = scope is uint opened ? context.Decode.BeginNodeCheckScope(opened) : null;
                int metadata;
                int values;
                int typed;
                try
                {
                    if (key.Shape.Kind == DTypeKind.Variant)
                    {
                        // The key's own variant, a range of its core of the values of other classes and of its typed column.
                        if (!context.Decode.ChildDecodesRange(in root, 0) || !context.Decode.ChildDecodesRange(in root, 1))
                        {
                            return -1;
                        }

                        int core = context.Decode.DecodeChildRange(in root, 0, key.Shape, Rows, start, count);
                        CanonicalNode storage = arena.GetNode(core);
                        metadata = storage.GetFieldIndex(0);
                        values = storage.GetFieldIndex(1);
                        typed = context.Decode.DecodeChildRange(in root, 1, key.Typed, Rows, start, count);
                    }
                    else
                    {
                        if (unplanned && !context.Decode.DecodesRange(in root))
                        {
                            return -1;
                        }

                        metadata = None();
                        values = metadata;
                        typed = context.Decode.DecodeRootRange(in root, key.Shape, Rows, start, count, keepEncoding: false);
                    }
                }
                finally
                {
                    if (scope is not null)
                    {
                        context.Decode.EndNodeCheckScope(outer);
                    }
                }

                groups.Span[k] = arena.AddStruct(Groups[k], count, Validity.NonNullable, [metadata, values, typed]);
            }

            int struct_ = arena.AddStruct(Tree, count, Validity.NonNullable, groups.Span);
            int nulls = None();
            return arena.AddStruct(variant, count, variant.IsNullable ? Validity.AllValid : Validity.NonNullable, [nulls, nulls, struct_]);

            int None() => none >= 0 ? none : none = Nulls(arena, Binary, count);
        }

        /// <summary>
        /// The check scope of key <paramref name="key"/> of the chunk whose layout segment is <paramref name="segment"/>: a
        /// chunk's keys share its segment and each decodes its own blob, whose children a scope of the segment alone would
        /// mix; above every segment number a scope otherwise takes. Null past the numbers it holds.
        /// </summary>
        private static uint? KeyScope(uint segment, int key) => segment < (1u << 24) && key < 64 ? (1u << 30) | (segment << 6) | (uint)key : null;

        /// <summary>
        /// The region of the key's buffers, the ranges <paramref name="plan"/> read laid at their places, every other byte left as the
        /// arena gives it, which no decoder reads (<see cref="Layouts.FlatLayoutReader"/>'s granules lay theirs alike).
        /// </summary>
        private static Buffers.VortexBuffer Region(ScanContext context, PathChunk part, GranulePlans.Plan plan)
        {
            SegmentRequestSet segments = context.Segments;
            Buffers.VortexBuffer region = context.Canonical.AllocateUninitialized(checked((int)plan.RegionLength), 1 << plan.Alignment, out Span<byte> bytes);
            foreach ((long start, long end) in plan.Ranges)
            {
                int slot = segments.Add(new SegmentSpec(part.Offset + (ulong)start, checked((uint)(end - start)), 0, 0, 0));
                segments.GetBuffer(slot).Span[..(int)(end - start)].CopyTo(bytes[(int)start..]);
            }

            return region;
        }

        /// <summary>
        /// The chunk's rows as a variant node of <paramref name="variant"/>'s dtype in the context's
        /// current arena: the metadata and the residual null, and a group per key, each decoded from
        /// its own range.
        /// </summary>
        internal int Decode(ScanContext context, DType variant)
        {
            CanonicalArena arena = context.Canonical;
            Span<int> stack = stackalloc int[16];
            using Scratch<int> groups = new(Keys.Length, stack);

            // One column of nulls serves every place the chunk holds none, where one a place took 16 bytes a row each:
            // 16 MB a key of a chunk of a million rows, three or four times over.
            int none = -1;
            for (int k = 0; k < Keys.Length; k++)
            {
                Key key = Keys[k];
                if (key.Part is not { } part)
                {
                    // No row of the chunk holds the key: every row reads null there, and nothing is read.
                    int absent = None();
                    groups.Span[k] = arena.AddStruct(Groups[k], Rows, Validity.NonNullable, [absent, absent, arena.AddNull(key.Typed, Rows)]);
                    continue;
                }

                Buffers.VortexBuffer segment = default;
                if (part.Length > 0)
                {
                    SegmentRequestSet segments = context.Segments;
                    int slot = segments.Add(Spec(part));
                    segment = segments.GetBuffer(slot);
                    if (context.IsRetaining)
                    {
                        context.NoteSegment(segments.GetOwner(slot));
                    }
                }

                context.Decode.LoadBlob(part.Tree.Span, segment);
                ArrayNode root = context.Nodes.Root;
                int metadata;
                int values;
                int typed;
                if (key.Shape.Kind == DTypeKind.Variant)
                {
                    // The key's own variant: its core of the values of other classes, and its typed column.
                    int core = context.Decode.DecodeChild(in root, 0, key.Shape, Rows);
                    CanonicalNode storage = arena.GetNode(core);
                    metadata = storage.GetFieldIndex(0);
                    values = storage.GetFieldIndex(1);
                    typed = context.Decode.DecodeChild(in root, 1, key.Typed, Rows);
                }
                else
                {
                    metadata = None();
                    values = metadata;
                    typed = context.Decode.DecodeRoot(in root, key.Shape, Rows);
                }

                groups.Span[k] = arena.AddStruct(Groups[k], Rows, Validity.NonNullable, [metadata, values, typed]);
            }

            int tree = arena.AddStruct(Tree, Rows, Validity.NonNullable, groups.Span);
            int nulls = None();
            return arena.AddStruct(variant, Rows, variant.IsNullable ? Validity.AllValid : Validity.NonNullable, [nulls, nulls, tree]);

            int None() => none >= 0 ? none : none = Nulls(arena, Binary, Rows);
        }

        private static SegmentSpec Spec(PathChunk part) => new SegmentSpec(part.Offset, part.Length, part.AlignmentExponent, 0, 0);

        /// <summary>A column of <paramref name="rows"/> nulls of <paramref name="dtype"/>, a binary.</summary>
        private static int Nulls(CanonicalArena arena, DType dtype, int rows)
        {
            Buffers.VortexBuffer views = arena.Allocate(Math.Max(rows * 16, 1), 16, out _).Slice(0, rows * 16);
            return arena.AddVarBinView(dtype, rows, Validity.AllInvalid, views, []);
        }
    }
}
