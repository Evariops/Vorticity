using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
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
/// typed column wears one dtype across them; otherwise its chunks are read whole, as before.
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
        VortexFile file, IReadOnlyDictionary<string, string[]> columns, CancellationToken cancellationToken)
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
                || await VariantPathMap.ReadAsync(file, field, wanted, cancellationToken).ConfigureAwait(false) is not { } map)
            {
                continue;
            }

            if (Column(map, wanted, types) is { } read)
            {
                chunks.AddRange(read);
                continue;
            }

            // Keys no chunk shreds lie in the residuals alone: each chunk is read for its residual, found where the
            // map's own keys lie in it.
            if (map.Keys.Count > 0 && !AnyShredded(map, wanted)
                && await VariantPathMap.ReadAsync(file, field, map.Keys, cancellationToken).ConfigureAwait(false) is { } shredded)
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
            for (int i = 0; i < map.Keys.Count; i++)
            {
                if (string.Equals(map.Keys[i], key, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
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
            keys[k] = -1;
            for (int i = 0; i < map.Keys.Count; i++)
            {
                if (string.Equals(map.Keys[i], wanted[k], StringComparison.Ordinal))
                {
                    keys[k] = i;
                }
            }

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
                DType shape = DTypeProtobuf.Read(part.DType, types);
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
        ArrayView view = ArrayView.Root(part.Tree, ref budget);
        return Arrays.Metadata.VariantMetadata.Read(view.Root_.Metadata).ShreddedDType is { } bytes
            ? DTypeProtobuf.Read(bytes, types)
            : null;
    }

    /// <summary>Whether two dtypes are the same shape, names, nesting and nullability alike.</summary>
    private static bool SameShape(DType left, DType right) =>
        string.Equals(DTypeFormatter.Format(left), DTypeFormatter.Format(right), StringComparison.Ordinal);

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

        /// <summary>Registers each key's range.</summary>
        internal void Register(SegmentRequestSet segments)
        {
            foreach (Key key in Keys)
            {
                if (key.Part is { Length: > 0 } part)
                {
                    segments.Add(Spec(part));
                }
            }
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
            for (int k = 0; k < Keys.Length; k++)
            {
                Key key = Keys[k];
                if (key.Part is not { } part)
                {
                    // No row of the chunk holds the key: every row reads null there, and nothing is read.
                    int absent = Nulls(arena, Binary, Rows);
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

                context.Decode.LoadBlob(part.Tree, segment);
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
                    metadata = Nulls(arena, Binary, Rows);
                    values = Nulls(arena, Binary, Rows);
                    typed = context.Decode.DecodeRoot(in root, key.Shape, Rows);
                }

                groups.Span[k] = arena.AddStruct(Groups[k], Rows, Validity.NonNullable, [metadata, values, typed]);
            }

            int tree = arena.AddStruct(Tree, Rows, Validity.NonNullable, groups.Span);
            int none = Nulls(arena, Binary, Rows);
            return arena.AddStruct(variant, Rows, variant.IsNullable ? Validity.AllValid : Validity.NonNullable, [none, none, tree]);
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
