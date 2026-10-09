namespace Vorticity.Parquet.Metadata;

/// <summary>The physical types of <c>parquet.thrift</c>'s <c>Type</c>, with its numbers.</summary>
internal enum PhysicalType
{
    Boolean = 0,
    Int32 = 1,
    Int64 = 2,
    Int96 = 3,
    Float = 4,
    Double = 5,
    ByteArray = 6,
    FixedLenByteArray = 7,
}

/// <summary>The deprecated annotations of <c>parquet.thrift</c>'s <c>ConvertedType</c>, with their numbers.</summary>
internal enum ConvertedType
{
    Utf8 = 0,
    Map = 1,
    MapKeyValue = 2,
    List = 3,
    Enum = 4,
    Decimal = 5,
    Date = 6,
    TimeMillis = 7,
    TimeMicros = 8,
    TimestampMillis = 9,
    TimestampMicros = 10,
    UInt8 = 11,
    UInt16 = 12,
    UInt32 = 13,
    UInt64 = 14,
    Int8 = 15,
    Int16 = 16,
    Int32 = 17,
    Int64 = 18,
    Json = 19,
    Bson = 20,
    Interval = 21,
}

/// <summary><c>FieldRepetitionType</c>.</summary>
internal enum FieldRepetition
{
    Required = 0,
    Optional = 1,
    Repeated = 2,
}

/// <summary>The page encodings of <c>parquet.thrift</c>'s <c>Encoding</c>, with their numbers; 1 was never used.</summary>
internal enum ParquetEncoding
{
    Plain = 0,
    PlainDictionary = 2,
    Rle = 3,
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    RleDictionary = 8,
    ByteStreamSplit = 9,
    Alp = 10,
}

/// <summary><c>CompressionCodec</c>, with its numbers.</summary>
internal enum CompressionCodec
{
    Uncompressed = 0,
    Snappy = 1,
    Gzip = 2,
    Lzo = 3,
    Brotli = 4,
    Lz4 = 5,
    Zstd = 6,
    Lz4Raw = 7,
}

/// <summary><c>PageType</c>, with its numbers.</summary>
internal enum PageType
{
    DataPage = 0,
    IndexPage = 1,
    DictionaryPage = 2,
    DataPageV2 = 3,
}

/// <summary><c>BoundaryOrder</c> of a column index.</summary>
internal enum BoundaryOrder
{
    Unordered = 0,
    Ascending = 1,
    Descending = 2,
}

/// <summary>The members of the <c>LogicalType</c> union, by their field ids; 9 is reserved for an interval.</summary>
internal enum LogicalTypeKind : byte
{
    None = 0,
    String = 1,
    Map = 2,
    List = 3,
    Enum = 4,
    Decimal = 5,
    Date = 6,
    Time = 7,
    Timestamp = 8,
    Integer = 10,
    Unknown = 11,
    Json = 12,
    Bson = 13,
    Uuid = 14,
    Float16 = 15,
    Variant = 16,
    Geometry = 17,
    Geography = 18,
    File = 19,

    /// <summary>A member this build does not know, or one on a physical type it does not allow: ignored with the column's order.</summary>
    Unrecognized = 255,
}

/// <summary>The unit of a <c>TIME</c> or <c>TIMESTAMP</c>, the members of <c>TimeUnit</c>.</summary>
internal enum TimeUnit : byte
{
    Millis = 1,
    Micros = 2,
    Nanos = 3,
}

/// <summary>The members of the <c>ColumnOrder</c> union, by their field ids.</summary>
internal enum ColumnOrderKind : byte
{
    /// <summary>The footer carries no column orders: <c>min_value</c> and <c>max_value</c> mean nothing.</summary>
    Undefined = 0,

    TypeDefined = 1,
    Ieee754TotalOrder = 2,
    Int96Timestamp = 3,

    /// <summary>A member this build does not know: the column's statistics are ignored.</summary>
    Unrecognized = 255,
}
