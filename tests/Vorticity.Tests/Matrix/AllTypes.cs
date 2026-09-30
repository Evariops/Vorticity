using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;

namespace Vorticity.Tests.Matrix;

/// <summary>An enum over a byte, which a record writes as its underlying integer.</summary>
public enum Status : byte
{
    Draft,
    Open,
    Closed,
    Archived,
}

/// <summary>The nested record of <see cref="AllTypes"/>.</summary>
[VortexRecord]
public partial record struct Inner(int A, string? B);

/// <summary>
/// One member per .NET type a record maps, nullable and not, and one decimal per storage width:
/// the schema every encoding of the matrix is written with.
/// </summary>
[VortexRecord]
public partial record struct AllTypes(
    bool Bool,
    bool? BoolN,
    sbyte I8,
    sbyte? I8N,
    short I16,
    short? I16N,
    int I32,
    int? I32N,
    long I64,
    long? I64N,
    byte U8,
    byte? U8N,
    ushort U16,
    ushort? U16N,
    uint U32,
    uint? U32N,
    ulong U64,
    ulong? U64N,
    Half F16,
    Half? F16N,
    float F32,
    float? F32N,
    double F64,
    double? F64N,
    [VortexColumn(Precision = 2, Scale = 1)] decimal Dec8,
    [VortexColumn(Precision = 4, Scale = 2)] decimal Dec16,
    [VortexColumn(Precision = 9, Scale = 2)] decimal Dec32,
    [VortexColumn(Precision = 18, Scale = 4)] decimal Dec64,
    decimal Dec128,
    decimal? Dec128N,
    [VortexColumn(Precision = 38, Scale = 6)] VortexDecimal Wide128,
    [VortexColumn(Precision = 76, Scale = 10)] VortexDecimal Wide256,
    [VortexColumn(Precision = 76, Scale = 10)] VortexDecimal? Wide256N,
    string Text,
    string? TextN,
    ReadOnlyMemory<byte> Bytes,
    ReadOnlyMemory<byte>? BytesN,
    DateOnly Date,
    DateOnly? DateN,
    TimeOnly Time,
    TimeOnly? TimeN,
    DateTime Stamp,
    DateTime? StampN,
    [VortexColumn(Unit = TimeUnit.Nanoseconds)] DateTime StampNs,
    [VortexColumn(Unit = TimeUnit.Milliseconds, TimeZone = "UTC")] DateTime StampMsUtc,
    [VortexColumn(TimeZone = "Europe/Paris")] DateTimeOffset Zoned,
    [VortexColumn(Unit = TimeUnit.Seconds, TimeZone = "UTC")] DateTimeOffset? ZonedN,
    Guid Uuid,
    Guid? UuidN,
    Status State,
    Status? StateN,
    ReadOnlyMemory<int> Ints,
    ReadOnlyMemory<string> Texts,
    ReadOnlyMemory<long?>? LongsN,
    Inner Nested,
    Inner? NestedN,
    char Char,
    char? CharN,
    nint NInt,
    nuint NUInt,
    TimeSpan Span,
    TimeSpan? SpanN,
    Int128 I128,
    Int128? I128N,
    UInt128 U128,
    UInt128? U128N,
    [VortexColumn(Precision = 39)] Int128 I128Wide,
    [VortexColumn(Precision = 39)] UInt128 U128Wide,
    BigInteger Big,
    BigInteger? BigN,
    int[] IntArray,
    int[]? IntArrayN,
    List<string> TextList,
    List<string?>? TextListN,
    byte[] Blob,
    byte[]? BlobN,
    Memory<byte> BlobMemory,
    ImmutableArray<long> LongArray,
    IReadOnlyList<double?> Doubles,
    double[][] Jagged,
    List<int[]> ListOfArrays,
    Memory<short> Shorts,
    IEnumerable<Guid> Guids,
    List<Inner> Items,
    Inner[]? ItemsN,
    IReadOnlyList<Inner> ItemSequence,
    Dictionary<string, int> Scores,
    IReadOnlyDictionary<int, double?>? Weights,
    IDictionary<Guid, Status> Statuses);
