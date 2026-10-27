using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Editions;
using Vorticity.IO;
using Vorticity.Indexes;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>The columns written against an object dictionary: their codes in the chunks, their values once.</summary>
public sealed partial class VortexFileWriter
{
    // Per top-level column, its object dictionary, the type of its codes, the segment its values went to, and what the
    // dictionary was priced at against what it wrote, and the arena of the codes' types; null for a file that declares none.
    private ObjectDictionary?[]? _dictionaries;
    private DType[]? _codesTypes;
    private int[]? _dictionarySegments;
    private long[]? _dictionaryPriced;
    private long[]? _dictionaryWritten;
    private DTypeArena? _dictionaryTypes;

    /// <summary>
    /// Writes column <paramref name="field"/> against <paramref name="dictionary"/>, its values once and a code a row,
    /// when that costs fewer bytes than a dictionary a chunk and than no dictionary: the rows of a sorted buffer, a merge's
    /// union of its inputs' dictionaries. Every row the column is given must hold one of the dictionary's values.
    /// </summary>
    /// <param name="field">The top-level column.</param>
    /// <param name="dictionary">Its values, sorted.</param>
    /// <param name="rows">The rows the file will hold.</param>
    /// <param name="rowBytes">The bytes the column's values take over those rows, uncompressed: text's bytes, a number's width a row.</param>
    /// <param name="sorted">Whether the rows come in the order of the column's values, a component of the key: each chunk then holds a run of them.</param>
    /// <returns>Whether the column is written against the dictionary.</returns>
    /// <exception cref="InvalidOperationException">A row was written already.</exception>
    /// <remarks>
    /// <para>
    /// The price is the one the writer pays. The dictionary costs its values, compressed here as the file will hold them,
    /// and its codes packed at their width, <c>128 × w</c> bytes a block of 1 024 rows. A dictionary a chunk costs, a chunk
    /// of <c>r</c> rows drawing from <c>d</c> values, the values it holds, <c>d × (1 − (1 − 1/d)^r)</c> of them at random,
    /// or a run of <c>d / chunks</c> when the rows are sorted, at the ratio the values compressed by, and its codes at
    /// their width; its chunks are as many as hold the column's canonical bytes in the megabyte the writer gives a column.
    /// No dictionary costs the rows at that ratio, or a number's rows packed over its range.
    /// </para>
    /// <para>
    /// Not for a file that targets an edition without the <c>vortex.dict</c> layout, nor one written uncompressed, nor an
    /// append, nor a column of a type <see cref="ObjectDictionary.Holds"/> refuses.
    /// </para>
    /// </remarks>
    internal bool TryDeclareDictionary(int field, ObjectDictionary dictionary, long rows, long rowBytes, bool sorted)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ThrowIfDone();
        if (_acceptedRows != 0 || _rowCount != 0 || _root is { Rows: > 0 })
        {
            throw new InvalidOperationException("A column's object dictionary is declared before its first row.");
        }

        if (!_isTabular || (uint)field >= (uint)_fieldCount || !_compress || _append is not null || rows <= 0
            || !EditionRegistry.Contains(_target, ComponentKind.Layout, "vortex.dict"))
        {
            return false;
        }

        DType type = _schema.GetField(field);
        if (!ObjectDictionary.Holds(type) || dictionary.Kind != type.Kind
            || (type.Kind == DTypeKind.Primitive && dictionary.PType != type.PType) || dictionary.Count == 0)
        {
            return false;
        }

        long values = ValuesBytes(dictionary, type);
        long priced = values + Packed(rows, dictionary.CodeBits);
        if (priced >= ChunksPrice(dictionary, type, values, rows, rowBytes, sorted) || priced >= PlainPrice(dictionary, type, values, rows, rowBytes))
        {
            return false;
        }

        _dictionaries ??= new ObjectDictionary?[_fieldCount];
        _codesTypes ??= new DType[_fieldCount];
        _dictionaryPriced ??= new long[_fieldCount];
        _dictionaryWritten ??= new long[_fieldCount];
        _dictionaries[field] = dictionary;
        _codesTypes[field] = (_dictionaryTypes ??= new DTypeArena()).Primitive(dictionary.CodesPType, type.Nullability);

        // A nullable column's codes carry its validity too, a bit a row, as every plan of it would: no part of the choice.
        _dictionaryPriced[field] = priced + (type.IsNullable ? (rows + 7) / 8 : 0);
        _columns[field].Code();
        return true;
    }

    /// <summary>The bytes <paramref name="rows"/> codes of <paramref name="bits"/> bits take packed: <c>128 × bits</c> a block of 1 024.</summary>
    private static long Packed(long rows, int bits) => (rows + 1023) / 1024 * 128 * bits;

    /// <summary>The bits a code of <paramref name="values"/> values takes.</summary>
    private static int Bits(double values) => values <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)Math.Ceiling(values) - 1);

    /// <summary>What dictionaries a chunk would cost the column, by the model <see cref="TryDeclareDictionary"/> gives.</summary>
    private static long ChunksPrice(ObjectDictionary dictionary, DType type, long values, long rows, long rowBytes, bool sorted)
    {
        long canonical = rowBytes + (type.Kind == DTypeKind.Primitive ? 0 : 16 * rows);
        long chunks = Math.Max(1, (canonical + AutomaticColumnBytes - 1) / AutomaticColumnBytes);
        double perChunk = (double)rows / chunks;
        double d = dictionary.Count;
        double held = sorted ? Math.Min(d, (d / chunks) + 1) : d * (1 - Math.Pow(1 - (1 / d), perChunk));
        return (long)(chunks * ((values * held / d) + Packed((long)Math.Ceiling(perChunk), Bits(held))));
    }

    /// <summary>What the column would cost with no dictionary, by the model <see cref="TryDeclareDictionary"/> gives.</summary>
    private static long PlainPrice(ObjectDictionary dictionary, DType type, long values, long rows, long rowBytes)
    {
        if (type.Kind != DTypeKind.Primitive)
        {
            return (long)((double)rowBytes * values / Math.Max(dictionary.ValueBytes, 1));
        }

        // A number packed over the range from the dictionary's least value to its greatest, which are its first and last.
        int width = dictionary.PType.ByteWidth();
        ulong least = Read(dictionary.Value(0), width);
        ulong greatest = Read(dictionary.Value(dictionary.Count - 1), width);
        ulong range = greatest - least;
        if (width == 4)
        {
            range = (uint)range;
        }

        return Packed(rows, range == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(range));

        static ulong Read(ReadOnlySpan<byte> bytes, int width) =>
            width == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    /// <summary>The bytes the dictionary's values take compressed, as the file will hold them: written here and let go.</summary>
    private long ValuesBytes(ObjectDictionary dictionary, DType type)
    {
        CanonicalArena arena = Scratch();
        EncodingDictionary encodings = new EncodingDictionary(ComponentKind.Array, _target);
        string[] known = new string[_arrayEncodings.Count];
        for (int i = 0; i < known.Length; i++)
        {
            known[i] = _arrayEncodings.Ids[i];
        }

        encodings.Reset(known);
        try
        {
            using ArrayBlobWriter.BlobLease blob = ArrayBlobWriter.Write(Blobs, arena, dictionary.ValuesNode(arena, type), encodings, _compress);
            return blob.Length;
        }
        finally
        {
            arena.Reset();
        }
    }

    /// <summary>
    /// The codes of column <paramref name="field"/>'s chunk <paramref name="node"/>, in <paramref name="arena"/>, when the
    /// column is written against an object dictionary; the chunk itself otherwise.
    /// </summary>
    private int Coded(int field, CanonicalArena arena, int node) =>
        _dictionaries?[field] is { } dictionary ? dictionary.Code(arena, node, _codesTypes![field]) : node;

    /// <summary>Whether column <paramref name="field"/> is written against an object dictionary.</summary>
    private bool Codes(int field) => _dictionaries?[field] is not null;

    /// <summary>The type column <paramref name="field"/>'s chunks hold: its codes' for a column written against an object dictionary.</summary>
    private DType ChunkType(int field) =>
        _dictionaries?[field] is not null ? _codesTypes![field] : _isTabular ? _schema.GetField(field) : _schema;

    /// <summary>Writes each object dictionary's values as a segment of their own, right after the data.</summary>
    private async ValueTask WriteDictionariesAsync(CancellationToken cancellationToken)
    {
        if (_dictionaries is not { } dictionaries)
        {
            return;
        }

        _dictionarySegments = new int[_fieldCount];
        for (int field = 0; field < dictionaries.Length; field++)
        {
            _dictionarySegments[field] = -1;
            if (dictionaries[field] is not { } dictionary)
            {
                continue;
            }

            CanonicalArena arena = Scratch();
            try
            {
                using ArrayBlobWriter.BlobLease blob = ArrayBlobWriter.Write(
                    Blobs, arena, dictionary.ValuesNode(arena, _schema.GetField(field)), _arrayEncodings, _compress);
                _dictionaryWritten![field] += blob.Length;
                _dictionarySegments[field] = await WriteSegmentAsync(blob, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                arena.Reset();
            }
        }
    }

    /// <summary>
    /// What column <paramref name="field"/>'s object dictionary was priced at and what it wrote, its values and its codes'
    /// chunks; null for a column written without one.
    /// </summary>
    internal (long Priced, long Written)? DictionaryBytes(int field) =>
        _dictionaries?[field] is not null ? (_dictionaryPriced![field], _dictionaryWritten![field]) : null;

    /// <summary>
    /// Where each column's object dictionary lies, by the column's place, null for a column without one, which a reader of
    /// its chunks without the footer gathers their codes through; empty when no column has one.
    /// </summary>
    private ColumnDictionary?[] Dictionaries()
    {
        if (_dictionaries is not { } dictionaries || _dictionarySegments is not { } segments)
        {
            return [];
        }

        ColumnDictionary?[] placed = new ColumnDictionary?[_fieldCount];
        for (int field = 0; field < placed.Length; field++)
        {
            if (dictionaries[field] is { } dictionary && segments[field] >= 0)
            {
                SegmentSpec spec = _segments[segments[field]];
                placed[field] = new ColumnDictionary(
                    spec.Offset, spec.Length, spec.AlignmentExponent, dictionary.Count, dictionary.CodesPType, Ordered: true);
            }
        }

        return placed;
    }

    /// <summary>
    /// Column <paramref name="field"/>'s <c>vortex.dict</c> layout over its codes' layout <paramref name="codes"/>: child 0
    /// the values, one flat segment, child 1 the codes, the reverse of the array's order.
    /// </summary>
    private int DictLayout(FlatBufferBuilder builder, int field, int codes, ushort flat)
    {
        ObjectDictionary dictionary = _dictionaries![field]!;
        ushort dict = _layoutEncodings.Intern("vortex.dict");
        ProtoWriter writer = new ProtoWriter();
        try
        {
            DictLayoutMetadata metadata = new DictLayoutMetadata(dictionary.CodesPType, _schema.GetField(field).IsNullable, dictionary.AllReferenced);
            DictLayoutMetadata.Write(ref writer, in metadata);
            Span<uint> segment = stackalloc uint[1];
            segment[0] = (uint)_dictionarySegments![field];
            int values = LayoutWriter.Write(builder, flat, (ulong)dictionary.Count, default, [], segment);
            Span<int> children = stackalloc int[2];
            children[0] = values;
            children[1] = codes;
            return LayoutWriter.Write(builder, dict, (ulong)_rowCount, writer.WrittenSpan, children, []);
        }
        finally
        {
            writer.Dispose();
        }
    }
}
