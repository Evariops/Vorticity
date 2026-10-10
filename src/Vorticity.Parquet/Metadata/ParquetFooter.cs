using System;
using System.Text;
using System.Threading;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>Where a row group lies in the footer, and the counts the plan needs before its columns.</summary>
internal struct RowGroupEntry
{
    /// <summary>The position of the <c>columns</c> list's header in the footer.</summary>
    internal int ColumnsStart;

    internal int ColumnCount;
    internal long RowCount;

    /// <summary>The first row of the row group in the file.</summary>
    internal long FirstRow;

    internal long TotalByteSize;

    /// <summary>The compressed bytes of every chunk, or -1 when the footer does not say.</summary>
    internal long TotalCompressedSize;

    /// <summary>The row group's ordinal, or -1.</summary>
    internal int Ordinal;

    internal ByteRange SortingColumns;
}

/// <summary>
/// A file's <c>FileMetaData</c>, indexed rather than materialized: one pass reads the version, the
/// schema, the row counts and where each row group lies; a row group's column chunks are indexed the
/// first time a plan needs them, and a chunk's metadata is decoded from its place when asked for.
/// </summary>
/// <remarks>
/// What the open allocates is proportional to the schema and the row groups, never to their product.
/// The footer's bytes are kept, since every range this type hands out points into them. The type is
/// thread-safe: the only state it builds after the open, a row group's chunk positions, is published
/// once.
/// </remarks>
internal sealed class ParquetFooter
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly int[]?[] _chunkStarts;

    private ParquetFooter(ReadOnlyMemory<byte> bytes, int rowGroups)
    {
        _bytes = bytes;
        _chunkStarts = new int[]?[rowGroups];
        _decrypted = new ReadOnlyMemory<byte>?[]?[rowGroups];
    }

    /// <summary>The footer's bytes, which every <see cref="ByteRange"/> of this footer points into.</summary>
    internal ReadOnlySpan<byte> Bytes => _bytes.Span;

    internal int Version { get; private set; }

    internal SchemaElement[] Schema { get; private set; } = [];

    internal long RowCount { get; private set; }

    internal RowGroupEntry[] RowGroups { get; private set; } = [];

    /// <summary>The key-value pairs, a key's range and its value's range (absent for a key without a value).</summary>
    internal (ByteRange Key, ByteRange Value)[] KeyValues { get; private set; } = [];

    internal ByteRange CreatedBy { get; private set; } = ByteRange.None;

    /// <summary>The order of each leaf, in schema order; empty when the footer carries none.</summary>
    internal ColumnOrderKind[] ColumnOrders { get; private set; } = [];

    /// <summary>Whether the footer declares an encryption algorithm, a plaintext footer of an encrypted file.</summary>
    internal bool IsEncrypted => Algorithm is not null;

    /// <summary>A plaintext footer's encryption algorithm, with the metadata of its signing key; null for a file in plaintext, or one whose footer is encrypted.</summary>
    internal Encryption.FileCrypto? Algorithm { get; private set; }

    /// <summary>The bytes the <c>FileMetaData</c> takes, past which a plaintext footer holds its signature.</summary>
    internal int StructLength { get; private set; }

    /// <summary>What decrypts the chunks' encrypted metadata, set once the open has the keys.</summary>
    internal Encryption.FileDecryptor? Decryptor { get; set; }

    /// <summary>Per row group, the chunks' encrypted metadata decrypted, each the first time it is asked for.</summary>
    private readonly ReadOnlyMemory<byte>?[]?[] _decrypted;

    /// <summary>Reads a <c>FileMetaData</c>.</summary>
    internal static ParquetFooter Read(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        ThriftCompactReader reader = new(span);
        int found = 0;
        int version = 0;
        long rowCount = 0;
        SchemaElement[] schema = [];
        RowGroupEntry[] rowGroups = [];
        (ByteRange, ByteRange)[] keyValues = [];
        ColumnOrderKind[] orders = [];
        ByteRange createdBy = ByteRange.None;
        Encryption.FileCrypto? algorithm = null;
        byte[] signingKey = [];
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    version = reader.ReadI32();
                    found |= 1;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    schema = ReadSchema(ref reader);
                    found |= 2;
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I64);
                    rowCount = reader.ReadI64();
                    found |= 4;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.List);
                    rowGroups = ReadRowGroups(ref reader);
                    found |= 8;
                    break;
                case 5 when type == ThriftType.List:
                    keyValues = ReadKeyValues(ref reader);
                    break;
                case 6 when type == ThriftType.Binary:
                    int length = reader.ReadBinary().Length;
                    createdBy = new ByteRange(reader.Position - length, length);
                    break;
                case 7 when type == ThriftType.List:
                    orders = ReadColumnOrders(ref reader);
                    break;
                case 8 when type == ThriftType.Struct:
                    algorithm = Encryption.FileCrypto.ReadAlgorithm(ref reader);
                    break;
                case 9 when type == ThriftType.Binary:
                    signingKey = reader.ReadBinary().ToArray();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        int structLength = reader.Position;
        if (algorithm is not null)
        {
            algorithm.KeyMetadata = signingKey;
        }

        if (found != 15)
        {
            ParquetThrow.Format("The footer lacks its version, schema, row count or row groups.");
        }

        if (version is not (1 or 2))
        {
            ParquetThrow.Unsupported(version.ToString(System.Globalization.CultureInfo.InvariantCulture), ParquetComponentKind.Version,
                "The standard reserves every version but 1 and 2, which are read alike.");
        }

        long rows = 0;
        for (int i = 0; i < rowGroups.Length; i++)
        {
            if (rowGroups[i].RowCount < 0)
            {
                ParquetThrow.Format($"Row group {i} declares {rowGroups[i].RowCount} rows.");
            }

            rowGroups[i].FirstRow = rows;
            rows += rowGroups[i].RowCount;
        }

        // The rows are the row groups': some writers left the file's own count at 0, and no read
        // depends on it.
        rowCount = rows;

        ParquetFooter footer = new(bytes, rowGroups.Length)
        {
            Version = version,
            Schema = schema,
            RowCount = rowCount,
            RowGroups = rowGroups,
            KeyValues = keyValues,
            CreatedBy = createdBy,
            ColumnOrders = orders,
            Algorithm = algorithm,
            StructLength = structLength,
        };
        return footer;
    }

    /// <summary>The value of the key-value pair named <paramref name="key"/>, or null.</summary>
    internal string? Value(string key)
    {
        ReadOnlySpan<byte> bytes = Bytes;
        foreach ((ByteRange name, ByteRange value) in KeyValues)
        {
            if (value.IsPresent && Encoding.UTF8.GetString(name.Of(bytes)) == key)
            {
                return Encoding.UTF8.GetString(value.Of(bytes));
            }
        }

        return null;
    }

    /// <summary>Where each column chunk of <paramref name="rowGroup"/> starts in the footer, indexed once and kept.</summary>
    internal int[] ChunkStarts(int rowGroup)
    {
        int[]? starts = Volatile.Read(ref _chunkStarts[rowGroup]);
        if (starts is not null)
        {
            return starts;
        }

        ref readonly RowGroupEntry entry = ref RowGroups[rowGroup];
        ThriftCompactReader reader = new(Bytes.Slice(entry.ColumnsStart));
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.Struct);
        starts = new int[count];
        for (int i = 0; i < count; i++)
        {
            starts[i] = entry.ColumnsStart + reader.Position;
            reader.Skip(ThriftType.Struct);
        }

        return Interlocked.CompareExchange(ref _chunkStarts[rowGroup], starts, null) ?? starts;
    }

    /// <summary>The metadata of column <paramref name="column"/>'s chunk in row group <paramref name="rowGroup"/>.</summary>
    /// <remarks>
    /// A chunk whose metadata is encrypted with its column's key is read from the plaintext of it,
    /// decrypted the first time and kept, where the caller gave the key; else it is
    /// <see cref="ColumnChunkMetadata.Hidden"/>, its plaintext footer's copy stripped of its statistics
    /// or absent.
    /// </remarks>
    internal ColumnChunkMetadata Chunk(int rowGroup, int column)
    {
        ColumnChunkMetadata chunk = ColumnChunkMetadata.Read(_bytes, ChunkStarts(rowGroup)[column]);
        if (!chunk.EncryptedMetadata.IsPresent || Decryptor is not { } decryptor)
        {
            return chunk;
        }

        ReadOnlyMemory<byte>?[] decrypted = Volatile.Read(ref _decrypted[rowGroup])
            ?? Interlocked.CompareExchange(ref _decrypted[rowGroup], new ReadOnlyMemory<byte>?[RowGroups[rowGroup].ColumnCount], null)
            ?? _decrypted[rowGroup]!;
        if (decrypted[column] is not { } plaintext)
        {
            byte[]? key = decryptor.ColumnKey(ColumnPaths[column], chunk.KeyMetadata.Of(Bytes), chunk.Crypto == ChunkCrypto.FooterKey);
            if (key is null)
            {
                return chunk;
            }

            plaintext = decryptor.Decrypt(key, chunk.EncryptedMetadata.Of(Bytes), Encryption.ModuleType.ColumnMetaData, Ordinal(rowGroup), column);
            decrypted[column] = plaintext;
        }

        chunk.Decrypted(plaintext);
        return chunk;
    }

    /// <summary>The ordinal of row group <paramref name="rowGroup"/> in its modules' AAD: its own where the footer gives it.</summary>
    internal int Ordinal(int rowGroup) => RowGroups[rowGroup].Ordinal >= 0 ? RowGroups[rowGroup].Ordinal : rowGroup;

    /// <summary>The leaves' paths, their names joined by dots, in their order: what a column's key is asked for by; set at the open.</summary>
    internal string[] ColumnPaths { get; set; } = [];

    private static SchemaElement[] ReadSchema(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.Struct);
        if (count == 0)
        {
            ParquetThrow.Format("The schema has no root.");
        }

        SchemaElement[] schema = new SchemaElement[count];
        for (int i = 0; i < count; i++)
        {
            schema[i] = SchemaElement.Read(ref reader);
        }

        return schema;
    }

    private static RowGroupEntry[] ReadRowGroups(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.Struct);
        RowGroupEntry[] groups = new RowGroupEntry[count];
        for (int i = 0; i < count; i++)
        {
            ref RowGroupEntry entry = ref groups[i];
            entry.ColumnsStart = -1;
            entry.TotalCompressedSize = -1;
            entry.Ordinal = -1;
            entry.SortingColumns = ByteRange.None;
            int found = 0;
            short saved = reader.EnterStruct();
            while (reader.ReadFieldHeader(out ThriftType type, out short id))
            {
                switch (id)
                {
                    case 1:
                        ThriftCompactReader.Expect(type, ThriftType.List);
                        entry.ColumnsStart = reader.Position;
                        int columns = reader.ReadListHeader(out ThriftType chunk);
                        ThriftCompactReader.Expect(chunk, ThriftType.Struct);
                        for (int c = 0; c < columns; c++)
                        {
                            reader.Skip(ThriftType.Struct);
                        }

                        entry.ColumnCount = columns;
                        found |= 1;
                        break;
                    case 2:
                        ThriftCompactReader.Expect(type, ThriftType.I64);
                        entry.TotalByteSize = reader.ReadI64();
                        found |= 2;
                        break;
                    case 3:
                        ThriftCompactReader.Expect(type, ThriftType.I64);
                        entry.RowCount = reader.ReadI64();
                        found |= 4;
                        break;
                    case 4 when type == ThriftType.List:
                        int start = reader.Position;
                        reader.Skip(type);
                        entry.SortingColumns = new ByteRange(start, reader.Position - start);
                        break;
                    case 6 when type == ThriftType.I64:
                        entry.TotalCompressedSize = reader.ReadI64();
                        break;
                    case 7 when type == ThriftType.I16:
                        entry.Ordinal = reader.ReadI16();
                        break;
                    default:
                        reader.Skip(type);
                        break;
                }
            }

            reader.ExitStruct(saved);
            if (found != 7)
            {
                ParquetThrow.Format("A row group lacks its columns, its size or its row count.");
            }

            if (entry.RowCount < 0)
            {
                ParquetThrow.Format("A row group declares a negative row count.");
            }
        }

        return groups;
    }

    private static (ByteRange, ByteRange)[] ReadKeyValues(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.Struct);
        (ByteRange, ByteRange)[] pairs = new (ByteRange, ByteRange)[count];
        for (int i = 0; i < count; i++)
        {
            ByteRange key = ByteRange.None, value = ByteRange.None;
            short saved = reader.EnterStruct();
            while (reader.ReadFieldHeader(out ThriftType type, out short id))
            {
                if (id is 1 or 2 && type == ThriftType.Binary)
                {
                    int length = reader.ReadBinary().Length;
                    ByteRange range = new(reader.Position - length, length);
                    if (id == 1)
                    {
                        key = range;
                    }
                    else
                    {
                        value = range;
                    }
                }
                else
                {
                    reader.Skip(type);
                }
            }

            reader.ExitStruct(saved);
            if (!key.IsPresent)
            {
                ParquetThrow.Format("A key-value pair has no key.");
            }

            pairs[i] = (key, value);
        }

        return pairs;
    }

    private static ColumnOrderKind[] ReadColumnOrders(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(out ThriftType element);
        ThriftCompactReader.Expect(element, ThriftType.Struct);
        ColumnOrderKind[] orders = new ColumnOrderKind[count];
        for (int i = 0; i < count; i++)
        {
            ColumnOrderKind order = ColumnOrderKind.Unrecognized;
            short saved = reader.EnterStruct();
            while (reader.ReadFieldHeader(out ThriftType type, out short id))
            {
                if (id is >= 1 and <= 3 && type == ThriftType.Struct)
                {
                    order = (ColumnOrderKind)id;
                }

                reader.Skip(type);
            }

            reader.ExitStruct(saved);
            orders[i] = order;
        }

        return orders;
    }
}
