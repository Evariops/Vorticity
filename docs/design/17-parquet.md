# Parquet

`Vorticity.Parquet` reads and writes Apache Parquet files. This page does not restate the format:
the standard is the [Apache Parquet format][pf] at release 2.14.0, its metadata serialized with the
[Thrift compact protocol][compact], and the rules below that rest on them link to the text they
rest on. What this page holds is what the standard leaves to an implementation: where the package sits,
how it reads and writes without copying, the kernels it is built on, what it decides where the
standard is silent or inconsistent, and the tests that hold each promise.

**Written before the code.** Each decision here is what the code will be held to; changing one
changes this page first.

**No other Parquet implementation is a reference.** Correctness is anchored in the standard's text
and in oracles this repository owns (§9), speed in baselines it owns (§10). No other
implementation's code, files or behaviour is consulted or compared against, and where the standard
is silent, §3.2 decides from the standard's own text. What is claimed is conformance to the text as
written; interoperability with other readers is not measured.

## 1. Principles

1. **A byte is read where it lies.** A page of a mapped file is decoded in place, and a value the
   format lays out as the arena wants it — plain fixed-width values, plain booleans, the bodies of
   binary values, a dictionary's values — becomes a view, not a copy (§5.5).
2. **Decompressed once, into its final place.** A page's values decompress straight into the buffer
   that becomes the column wherever the page allows it, and a dictionary decompresses and decodes
   once per column chunk, whatever the batches that read it (§5.4).
3. **What is pruned is neither read nor decoded.** Row group statistics, the page index, Bloom
   filters and dictionaries refine one row selection, cheapest first; the pages outside it are not
   requested, and the columns the filter does not name are decoded only for the rows it keeps
   (§5.3).
4. **Encoded forms survive.** A dictionary column reaches an aggregate as codes and values, a long
   run of one code as a run, a batch of one value as a constant (§5.5).
5. **The writer writes for the reader.** Pages cut at the same rows in every column, data pages v2,
   uncompressed pages aligned, a page index and exact statistics always: a file this writer makes is
   read with every view of principle 1 and every pruning of principle 3 (§6.1).
6. **Nothing per value but the work.** No allocation per batch, no dispatch per value, kernels
   generic over the physical type and vectorized with a scalar twin, a footer indexed without an
   allocation per field (§5.2, §7).
7. **Parallel when asked, the same answer at any degree.** Row groups and their parts decode on the
   session's lanes and pages compress on the writer's threads; batches come in row order, and a file
   is the same bytes at every degree (§5.8, §6.4).
8. **Untrusted bytes, bounded work.** Every length, offset, count and width a file gives is checked
   before it is used, against caps that are constants (§8).

| question | decision | § |
|---|---|---|
| where it sits | a satellite of the core, as `Vorticity.Dataset` is: a Parquet file is one more `ScanSource`, so `Scan<TRecord>`, every query and every sink run over it unchanged | 2 |
| what a page decodes into | the core's canonical arena, the nodes a Vortex block decodes into | 5.5 |
| the pages it writes | data pages v2, on a grid of 8 192 rows shared by every column | 6.1 |
| the encodings it writes | chosen per page by exact formulas from one statistics pass, as the core's writer chooses | 6.2 |
| the codecs | ZSTD through `Vorticity.Zstd`; SNAPPY and LZ4_RAW written here; GZIP and BROTLI through the base class library; neither LZ4 nor LZO, whose formats the standard does not give | 6.4, 3.2 |
| the gaps in the standard | sixteen, each decided | 3.2 |

## 2. Where it sits

`src/Vorticity.Parquet` is a satellite of the core, as `Vorticity.Dataset` is: it references
`Vorticity`, which grants it its internals, and through it `Vorticity.Zstd` and `System.IO.Hashing`;
nothing else. It targets `net11.0`, is compatible with trimming and Native AOT, and is marked
`[Experimental("VX0003")]` until its surface settles, as the dataset and the row encoding are.

The core already reads from more than one kind of source. A scan compiles to a `ScanSpec` and asks a
`ScanSource` for its batches, counts, extremes and plan; `Vorticity.Dataset` supplies one over every
object of a version. A Parquet file is one more, `ParquetScanSource`, and everything above that seam
runs over it unchanged — `Scan<TRecord>`, the tool scan, `Where`, `Select`, `GroupBy`, the
aggregates, every sink — as does every consumer of a scan, a Vortex writer among them:

```csharp
await using ParquetFile parquet = await ParquetFile.OpenAsync("hits.parquet");
await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Hit>("hits.vortex");
await writer.WriteAsync(parquet.Scan<Hit>());
await writer.CompleteAsync();
```

| taken from the core | for |
|---|---|
| `VortexSession` | the aligned pool, mapping and the mapped-file cache, `MaxConcurrentReads`, the degree of parallelism |
| `ISegmentSource` and its three sources | reads, coalescing and leases; a mapped file read in place ([03-architecture.md](03-architecture.md) §3.5) |
| the canonical arena | the nodes a page decodes into: `Primitive`, `Decimal`, `Bool`, `VarBinView`, `ListView`, `Struct`, `Extension`, `Constant`, `Dictionary`, `RunEnd` |
| the batch surface | `Columns<TRecord>`, `Column<T>`, `BatchView`, `RecordBatch`, their lifetimes, and `Values` aligned on 64 bytes |
| the engine | filter evaluation, aggregates, group by, projections, ordering, `ScanPlan` and `ScanMetrics` |
| the writer's inputs | `ColumnsBuilder` and `ColumnBuilder<T>`, records, `RecordBatch`, any scan; `CompressionProfile` |
| primitives | `SplitBlockBloom`, which with XXH64 is Parquet's split-block filter bit for bit, and its sizing ([10-indexes.md](10-indexes.md) §4.1); the ALP exponent search; `Int256`; `Crc32` and `XxHash64` of `System.IO.Hashing` |

| added here | holds |
|---|---|
| `Thrift/` | the compact protocol, read in place and written forward |
| `Metadata/` | the footer's index and its lazy views; the schema compiled to leaves and levels |
| `Pages/` | page headers, decompression into place, checksums |
| `Encodings/` | every encoding of the standard, decoded and encoded |
| `Codecs/` | SNAPPY and LZ4_RAW; adapters to `Vorticity.Zstd` and to the base class library's GZIP and Brotli |
| `Levels/` | definition and repetition levels, to and from the arena's validity and list offsets |
| `Reading/` | `ParquetScanSource`: the planner, the pruning cascade, the column cursors |
| `Writing/` | the writer, one column writer per leaf, the page index, the Bloom filters, the footer |

`Thrift/`, `Metadata/`, `Pages/`, `Encodings/` and `Codecs/` work on spans and know nothing of the
arena: they are tested alone, and could become a package of their own without a change.

**Why not a Parquet library of its own**, with its own reader, batches and surface: it would be a
second engine to keep equal to the first — I/O, arenas, filters, aggregates, records — for nothing
the seam does not already give, and Parquet would lose every query.

The surface follows the rules of [14-public-api.md](14-public-api.md). In outline, its names open to
review: `ParquetFile.OpenAsync`, from a path or any `ISegmentSource`, on a session; its `Schema`, a
`VortexSchema`; `RowCount`; `Metadata`, for inspection (row groups, column chunks, encodings,
codecs, statistics, field ids, key-value metadata), each type named as the standard spells it;
`VerifyAsync`, the metadata oracle of §9 over the file, whose findings say what its footer claims
that its rows refute; `Scan<TRecord>()` and `Scan(columns)`. `ParquetFileWriter`, over a path or a `PipeWriter`, typed by a record or a schema,
with `Builder()`, `WriteAsync`, `FlushAsync`, which closes a row group, `CompleteAsync`, which
returns a `ParquetWriteReport`, and `Abandon()`. `ParquetOpenOptions` and `ParquetWriteOptions`.
The session gains `OpenParquetAsync` and `CreateParquetWriter` as extension members. A malformed
file throws `ParquetFormatException`, and an unsupported component `ParquetUnsupportedException`,
which names its kind and its id as the standard spells it. Both derive from `VortexException`, the
base of every exception the library throws on purpose, beside the core's `VortexFormatException`
and `VortexUnsupportedException`, which are sealed and whose messages speak of Vortex components
and editions: a Parquet file makes the promise a Vortex file makes, with its own two types.

## 3. The standard

### 3.1 What is referenced

A source that has releases is pinned to its latest. Moving a pin is reviewed against §3.2 and
against every decision that cites the text it changes.

| source | pin | governs |
|---|---|---|
| [Apache Parquet format][pf] | 2.14.0, commit `04d56f291f` | everything below: [README.md][readme] (layout, levels, data pages, checksums), [parquet.thrift][thrift] (every structure), [Encodings.md][enc], [AlpEncoding.md][alp], [Compression.md][comp], [LogicalTypes.md][lt], [PageIndex.md][pi], [BloomFilter.md][bloom], [Encryption.md][crypt], [VariantEncoding.md][var], [VariantShredding.md][shred], [Geospatial.md][geo], [BinaryProtocolExtensions.md][ext], and [CONTRIBUTING.md][contrib] for what a new feature may break |
| [Thrift compact protocol][compact] | Thrift 0.25.0, commit `27e8a425ff` | the bytes of every structure of `parquet.thrift` |
| [RFC 1952][rfc1952], [RFC 7932][rfc7932], [RFC 8878][rfc8878] | — | GZIP, BROTLI, ZSTD |
| [Snappy format][snappy] | snappy 1.3.1 | SNAPPY |
| [LZ4 block format][lz4] | lz4 1.10.0 | LZ4_RAW |
| [xxHash specification][xxh] | 0.1.1 | the Bloom filter's XXH64 |
| IEEE 754-2008 §5.10 and §5.12.2 | — | total order of floats; the correctly rounded constants of ALP |
| NIST SP 800-38D and 800-38A | — | AES-GCM and AES-CTR of modular encryption |
| Melnik et al., *Dremel*, VLDB 2010; Afroozeh, Kuffo and Boncz, *ALP*, SIGMOD 2024 | — | levels; ALP's parameter search |

### 3.2 Where the standard is silent or inconsistent

Each is a place where a plausible guess reads wrong values rather than failing. Each is decided
once, from the standard's own text, and held by a test.

| # | the standard | left open | decision |
|---|---|---|---|
| 1 | INT96 is ordered by [its last 4 bytes, the "days", then its first 8, the "nanos"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L1143-L1153), and [PLAIN stores it as 12 bytes](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encodings.md#L63) | the epoch of the days and the meaning of the instant | read as its 12 bytes, an extension `parquet.int96` over `FixedSizeList<u8, 12>`, whose accessors split the two fields the ordering names; no conversion to a timestamp, since no epoch is given. Never written. Its statistics are used only under `INT96_TIMESTAMP_ORDER` |
| 2 | an RLE run's value takes ["round-up-to-next-byte(bit-width)"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encodings.md#L112) bytes | their order | little-endian, as every multi-byte value of PLAIN; a width over 32 bits is malformed |
| 3 | a DELTA_BINARY_PACKED miniblock is ["a list of bit-packed ints"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encodings.md#L235-L236) | the order of the bits | least significant bit first, the order of the RLE/bit-packing hybrid, which the section names as the similar encoding and ALP names for its own packing |
| 4 | the Bloom filter hashes ["a column value using plain encoding"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L798-L805) | whether a BYTE_ARRAY's length prefix is hashed, and what one BOOLEAN's plain encoding is | the value's bytes without the prefix, as statistics encode a value ["except that variable-length byte arrays do not include a length prefix"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L303-L304); no filter is written or probed for BOOLEAN. A float is hashed by its bits: a probe for zero probes both zeros, and NaN is never probed. These are the core's own filter's rules ([10-indexes.md](10-indexes.md) §4.1) |
| 5 | ALP's decode is normative, [two multiplications by correctly rounded constants](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/AlpEncoding.md#L256-L265) | in which precision a FLOAT vector multiplies, and how its integer becomes a float | binary32 throughout: the integer converted with round-to-nearest-even, then two binary32 multiplications by binary32 constants, never fused; DOUBLE the same in binary64. The writer checks every value against this decode. The constants are held by a test against exact rational rounding, never trusted to a compiler's parsing of a literal |
| 6 | a column chunk names its leaf by ["Path in schema"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L916-L917) | whether the root's name is part of it | the names from the root's child to the leaf; a chunk is matched to its leaf by position, which [the order of `RowGroup.columns`](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L1050-L1053) fixes, and a path that disagrees refuses the file |
| 7 | an extension's header is [`08 FF FF 01`](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/BinaryProtocolExtensions.md#L37-L43), field 32767 as a plain ULEB128, while the compact protocol [zigzags a long-form field id](https://github.com/apache/thrift/blob/27e8a425ffb498e190df3a12e239326bf5ba9ed6/doc/specs/thrift-compact-protocol.md#L207-L216), under which those bytes are field −16384 | which is meant | both read as an unknown binary field and are skipped; the writer writes the protocol's form, `08 FE FF 03`, for the one extension it emits (§6.5) |
| 8 | LZ4 has ["an additional undocumented framing scheme"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Compression.md#L78-L87); LZO is ["based on or interoperable with the LZO compression library"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Compression.md#L65-L68) | the bytes of either | neither is read nor written; a page in either refuses, naming the codec |
| 9 | ["no agreed upon consensus"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L1408-L1416) on what version 2 of a file is | — | write 1; read 1 and 2 alike; any other version is unsupported |
| 10 | a chunk's ["Number of values in this column"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L922-L923) beside a page's ["including NULLs"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L700-L707) | what is counted in a nested column | level entries, nulls and empty lists included; the pages' counts sum to the chunk's |
| 11 | `IndexPageHeader` is [a `TODO`](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L722-L724) | the index page | skipped, as is every page type the reader does not know |
| 12 | a boolean element of a list is [1 for true and 2 for false](https://github.com/apache/thrift/blob/27e8a425ffb498e190df3a12e239326bf5ba9ed6/doc/specs/thrift-compact-protocol.md#L125-L127) | any other byte | 1 is true, 2 and 0 are false, any other value is malformed |
| 13 | a value past an `INT(8)` or `INT(16)` annotation [is undefined](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/LogicalTypes.md#L113-L116) | — | refused as malformed when it is narrowed; never written |
| 14 | DECIMAL's order is given [for fixed lengths](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/LogicalTypes.md#L244-L251) | a BYTE_ARRAY decimal's | compared sign-extended to the longer of the two lengths |
| 15 | a truncated bound ["must still be valid values within the column's logical type"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L295-L301) | how to cut a STRING | at a code point boundary, an upper bound raising its last code point past the surrogates, so that both stay UTF-8; a bound that cannot be raised is written whole |
| 16 | a page's ordinal in the encryption AAD is ["2-byte short"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encryption.md#L254-L259) | a chunk of more than 32 767 pages | its low 16 bits, little-endian, on read; the writer encrypts no column chunk of more than 32 767 pages |
| 17 | a field both shredded and in a partially shredded object's `value` makes reads that ["may be inconsistent"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md#L171-L173) | which one a read returns | the shredded one, whether or not the row defines it: the `value`'s field of a shredded name is dropped, as a field the typed columns hold may not be in `value` |
| 18 | an array's elements ["must be present"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md#L145-L147) | an element both of whose columns are null | the variant null, as where a value is required at the top |
| 19 | a variant group holds a `value` that ["must be annotated"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantEncoding.md#L50) required or optional, and a shredded field's group [is required](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md#L193) | a group without its `value`, a field's group that is optional | read: a column that is not there is null in every row, and a null field's group is a missing field |
| 20 | a shredded decimal's [physical type](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md#L94-L96) names decimal4, decimal8 or decimal16, and so does [its precision](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantEncoding.md#L444-L446) | a DECIMAL(5, 2) stored in INT64, which the two tables place apart | the precision's: the variant's width is the one the encoding's decimal table gives, and the value is the same either way |
| 21 | a CRS may be [`projjson:` and a key](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Geospatial.md#L52) of the file's key-value metadata | what a type read from the file and written elsewhere names | the PROJJSON the key holds, which the type then carries itself; a key the file does not have leaves the CRS as written |
| 22 | a GEOGRAPHY's box [bounds its values on the sphere](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Geospatial.md#L107-L115), its edges geodesics | a writer's box, and a reader's check of one | the writer gives a box only where no value has an edge, the box of its vertices then exact; the verifier holds a box to the vertices alone, within 1e-9 degrees, as a box worked out on the sphere takes its vertices through unit vectors and back, a few units in the last place off |
| 23 | the types are ["from all instances"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Geospatial.md#L132-L151) | whether a collection's parts count | a value's own type alone, as GeoParquet's `geometry_types` lists them; a value that is not ISO WKB of the table's seven types, EWKB's flags among them, leaves the chunk without geospatial statistics |

Two more are not gaps but choices the standard leaves to a writer, and are §6's: a page whose
values do not shrink is stored uncompressed in a compressed chunk, which
[`is_compressed`](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L774-L779)
allows in a v2 page; and a ZSTD page may hold several frames, which the reader accepts and the
writer never makes, as GZIP pages [may hold several members](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Compression.md#L61-L63).

## 4. Types

### 4.1 Reading

A `LogicalType` wins over a `ConvertedType`, and a file with only the latter reads through the
standard's backward-compatibility tables. A logical type the reader does not know, or one on a
physical type it does not allow, is ignored with the column's order, and the column reads as its
physical type ([LogicalTypes.md][lt], *Unsupported Logical Types*).

| Parquet | dtype | from the page |
|---|---|---|
| BOOLEAN | `Bool` | a view of a plain page: its bits are a validity-style bitmap already |
| INT32, INT64 | `i32`, `i64` | a view |
| INT32 with `INT(8)` or `INT(16)`, signed or not | `i8`, `i16`, `u8`, `u16` | narrowed, checked (§3.2 #13) |
| INT32 with unsigned `INT(32)`, INT64 with unsigned `INT(64)` | `u32`, `u64` | a view |
| INT96 | `parquet.int96` over `FixedSizeList<u8, 12>` | a view (§3.2 #1) |
| FLOAT, DOUBLE | `f32`, `f64` | a view |
| FIXED_LEN_BYTE_ARRAY(2) with `FLOAT16` | `f16` | a view: the same little-endian bytes |
| BYTE_ARRAY | `Binary` | views over the page |
| BYTE_ARRAY with `STRING`, `ENUM`, `JSON` | `Utf8` | views over the page, validated |
| BYTE_ARRAY with `BSON` | `Binary` | views over the page |
| FIXED_LEN_BYTE_ARRAY(n) | `FixedSizeList<u8, n>` | a view; binds as `ReadOnlyMemory<byte>` |
| FIXED_LEN_BYTE_ARRAY(16) with `UUID` | `vortex.uuid` | a view: both are the big-endian bytes |
| FIXED_LEN_BYTE_ARRAY(12) with `INTERVAL` | `parquet.interval` over `FixedSizeList<u8, 12>` | a view |
| `DECIMAL` on INT32, INT64 | `Decimal(p, s)` over `i32`, `i64` | a view |
| `DECIMAL` on FIXED_LEN_BYTE_ARRAY, BYTE_ARRAY | `Decimal(p, s)` over the width its precision selects ([07-dotnet-mapping.md](07-dotnet-mapping.md) §2) | reversed to little-endian and sign-extended; above 76 digits, unsupported |
| `DATE` | `vortex.date` over `i32` | a view |
| `TIME` | `vortex.time` over `i32` or `i64`, its unit | a view |
| `TIMESTAMP` | `vortex.timestamp`, its unit, zone `UTC` when adjusted to UTC and none otherwise | a view |
| `UNKNOWN` | `Null` | nothing |
| a group | `Struct`, its validity from the definition levels | levels |
| `LIST`, and a repeated field outside `LIST` and `MAP` | `List` of the element, the latter a required list of required elements | levels |
| `MAP` | `Map`, a list view of key-value entries as the core holds one | levels |
| `VARIANT` | `Variant`: the core's struct of the `metadata` and `value` binaries, under the variant dtype | the metadata a view; the value a view where nothing is shredded, else rebuilt (§5.6) |
| `GEOMETRY`, `GEOGRAPHY` | `parquet.geometry`, `parquet.geography` over `Binary`: its WKB, the extension's metadata the CRS's UTF-8, a GEOGRAPHY's after a byte of its edge algorithm (§3.2 #21) | views over the page |
| `FILE` | `parquet.file` over the struct of its fields, where each is one the standard names, of its type, and optional; else the struct alone, the annotation dropped | levels |

A file this writer made carries its Vortex schema in its key-value metadata (§6.1). Top-level column
by top-level column, where that schema agrees with the Parquet schema, it restores what Parquet
cannot say: a timestamp's zone, which Parquet keeps as UTC, and an extension Parquet has no
annotation for, over the type the column reads as. A column that disagrees reads as Parquet says, and
a schema this build cannot read, or of other columns, is ignored. A fixed-size list of other than
bytes reads back as the list Parquet holds. The pairs are `ParquetFile.KeyValueMetadata`; field ids
are kept and shown by `Metadata`.

### 4.2 Writing

| dtype | Parquet |
|---|---|
| `Bool` | BOOLEAN |
| `i8`, `i16`, `i32`; `u8`, `u16`, `u32` | INT32 with `INT(8, 16, 32)`, signed or not |
| `i64`; `u64` | INT64; INT64 with unsigned `INT(64)` |
| `f16`, `f32`, `f64` | FIXED_LEN_BYTE_ARRAY(2) with `FLOAT16`, FLOAT, DOUBLE |
| `Decimal(p, s)` | INT32 up to 9 digits, INT64 up to 18, then FIXED_LEN_BYTE_ARRAY of the fewest bytes, big-endian |
| `Utf8`; `Binary` | BYTE_ARRAY with `STRING`; BYTE_ARRAY |
| `FixedSizeList<u8, n>` of non-null bytes | FIXED_LEN_BYTE_ARRAY(n) |
| `vortex.uuid`, `vortex.date`, `vortex.time`, `vortex.timestamp` | `UUID`, `DATE`, `TIME`, `TIMESTAMP` adjusted to UTC when the dtype has a zone |
| `Struct`; `List`, other fixed-size lists; the map | a group; the three-level `LIST`; the three-level `MAP` |
| `Variant` | a group annotated `VARIANT(1)` of a required BYTE_ARRAY `metadata` and a required BYTE_ARRAY `value`: the unshredded form |
| `Null` | INT32 with `UNKNOWN` |
| `parquet.interval` | FIXED_LEN_BYTE_ARRAY(12) with the `INTERVAL` converted type |
| `parquet.geometry`, `parquet.geography` | BYTE_ARRAY with `GEOMETRY` or `GEOGRAPHY`, its CRS and edge algorithm; no bounds, and the chunk's `GeospatialStatistics` (§6.1) |
| `parquet.file` over a struct of the fields `FILE` names | a group annotated `FILE` of them, each optional; any other field is refused |
| `parquet.int96` | refused: INT96 is deprecated |
| a union | unsupported |

Every annotation is written as a `LogicalType` and, where one corresponds, as its `ConvertedType`
too, as the standard requires of a writer; a local `TIME` or `TIMESTAMP` takes the legacy
annotation as the standard's forward-compatibility tables say. `INTERVAL` is the one annotation
written as a `ConvertedType` alone: its `LogicalType` is reserved and has no definition.

## 5. Reading

### 5.1 The open

The open reads the tail as the core's open does: the last 8 KiB of a local file positionally, else
64 KiB, or the whole file when it is smaller. The last eight bytes give the footer's length and the
magic; `PARE` is an encrypted footer (§5.10). A footer the tail does not cover costs one more read,
of exactly its bytes. The magic at offset 0, the same as the last, is checked when the read already
covers it or the file is mapped, and not at the price of a request.

The footer is then **indexed, not materialized**. One pass over `FileMetaData` compiles the schema —
leaves, paths, levels, physical and logical types, column orders — and records where each row group
begins and its row count; it allocates in proportion to the schema and the row groups, never to their
product. A row group's column chunks are indexed the first time a scan needs that row group: one walk
over its columns, recording where each `ColumnChunk` and `ColumnMetaData` begins, published once on
the file, which is thread-safe as a `VortexFile` is. Statistics, encodings, offsets and the page
index's locations are decoded from those positions when a plan asks for them. A file of a thousand
columns and a thousand row groups opens without decoding a million column chunks. The footer's bytes
are not copied either: they are read where the open's read put them, a mapped file's in the mapping,
and held until the file closes, as the core holds a Vortex file's tail, so that nothing an open
allocates grows with the footer.

### 5.2 Thrift, in place

The reader is a `ref struct` over the footer's bytes. A field header decodes in its short or long
form; `i16`, `i32` and `i64` are zigzag varints of at most 3, 5 and 10 bytes, longer or overflowing
ones refused;
`binary` and `string` are slices of the input, never copies; a list's size is bounded by the bytes
that remain, since no element takes less than one, which is what keeps a forged size from sizing an
allocation. Unknown fields are skipped by their wire type, with an explicit stack of at most 64
levels rather than recursion; field 32767, the standard's extension slot, is one of them. A known
field with an unexpected wire type, a missing required field and a union with two members are
malformed; an unknown value of `Encoding`, `CompressionCodec` or `PageType` is kept as a number and
refused only by the page that needs it; an unknown `ConvertedType` or `LogicalType` member drops the
annotation.

The writer writes fields in ascending order, in the short form whenever the delta allows, into the
sink's memory.

### 5.3 The plan: pruning and I/O

A split is a row group, or, when the row group is larger than the scan's split target and has an
offset index, a range of its rows cut at page boundaries, so that a file of one row group still
spreads over the lanes. Each split's rows are refined into a **row selection** by the structures, in
the core's order — cheapest first, stopping when nothing is left:

1. **Row group statistics**, already in the footer.
2. **The page index**: `ColumnIndex` and `OffsetIndex` of the predicate's columns, one coalesced
   read for every chunk the scan keeps, and most often inside the tail the open read. Pages whose
   bounds rule the predicate out drop their rows from the selection.
3. **Bloom filters**, for equality and `In` only: one read per chunk and column.
4. **The dictionary**, when the chunk's `encoding_stats` show every data page dictionary-encoded,
   or, without them, its encodings are the first version's dictionary encoding and the levels'
   alone: each part of the predicate that reads that column alone is evaluated by the core's
   evaluator over the dictionary's entries, once per distinct value, for whether it can come out
   true on a row and whether it can come out false; the parts combine as the predicate does, an AND
   true only where both sides can be, an OR false only where both can be, a NOT swapping the two,
   and a null check stays open over a chunk that may hold a null. A row group the predicate cannot
   select is dropped. The dictionary page is read on its own, up to 8 MiB, and again with its chunk
   when the group is read after all, but decoded once: the dictionary the pruning decoded passes to
   the scan's reader of the column, with a reference to the bytes it was decoded from, and the
   reader steps over the chunk's dictionary page by its header.

A statistic prunes only where the standard makes it a bound:

| the column | min and max are used |
|---|---|
| a column order this build knows, on a logical type it knows | `min_value` and `max_value` as bounds, exact only when `is_min_value_exact` or `is_max_value_exact` says so |
| no `column_orders` in the footer | not at all: the standard leaves their meaning undefined |
| the legacy `min` and `max` | only without `min_value` and `max_value`, and on a signed order: INT32 and INT64 without an unsigned annotation, FLOAT and DOUBLE |
| FLOAT, DOUBLE or FLOAT16 under the type order | a NaN bound is ignored; a minimum of +0 admits −0 and a maximum of −0 admits +0; NaN is known absent only from a `nan_count` of 0 |
| FLOAT, DOUBLE or FLOAT16 under the IEEE 754 total order | as bounds in that order; a NaN bound says that every non-null value it covers is NaN |
| INT96 | only under `INT96_TIMESTAMP_ORDER` |
| `INTERVAL`, `GEOMETRY`, `GEOGRAPHY`, `LIST`, `MAP`, `VARIANT`, `FILE`, an unknown logical type | never |
| `null_count` | only when present: an absent count is not 0 |

Then the I/O. Per row group, every range the selection needs is registered in one batch read of the
`ISegmentSource`, which coalesces it. A source that reads in place, a mapped file, is asked for the
chunks whole, since a page costs nothing until it is touched. Any other is asked, of a chunk whose
offset index tiles it, for what precedes its first data page, the dictionary page, and the pages the
selection needs, those that touch as one range; the offset indexes the page index did not read are
read first, in one request, and a chunk without one, or whose index does not place its pages as they
lie, is read whole. The column reader then steps over a page it never read by its place in the
index, never by its header, and a page of an indexed chunk starts a row, as the standard requires.
Each range is requested at most once per scan, and the plan counts the same ranges in the same
units as the execution, so the core's gates hold unchanged ([14-public-api.md](14-public-api.md) §9).

From a source that does not read in place, a row group whose chunks hold more than 4 MiB is read in
windows of batches, so that its first batch waits for its own pages and not its group's: each
window the pages its batches need that the windows before it did not read, the first with what
precedes each chunk's first page, and twice as many batches as the window before it from one until
a window holds 4 MiB, as many after. A page goes with the window of its first row. A window past the
first goes back as soon as every column reader is past it, its next byte and every page it holds
beyond the window's end, so that a group holds a few windows of its reads rather than all of them,
in blocks the pool keeps from one window to the next.
The next window is read while one is decoded, and given to the column readers before the batch
that needs it, or before a skip that ends inside one of its pages. Their offset indexes are read
for it, in the one request a sparse read makes, and a group whose chunks lack them, or an encrypted
one, is read in one request. The plan cuts the same windows. Over positional reads of a file of
four million rows, 39 MB in one row group, the first batch takes 268 µs where a read of the group in
one request took 6.08 ms, and the whole scan 4.51 ms, its reads under its decoding, where windows
held to the group's end took 6.06; groups sixteen times smaller, each read whole, give their first
batch in 328 µs.

The
predicate's columns are decoded first for each batch; the other columns are decoded only for the
rows the filter keeps — a skipped run of a page is stepped over, not decoded, where its encoding
lets it be: plain values by arithmetic, RLE runs by their lengths, whole pages by the offset index.

### 5.4 Pages: decompression into place

A page header is parsed from the lease, and its `crc` checked when
`ParquetOpenOptions.VerifyChecksums` asks for it: a check touches every byte a view would not, so it
is opt-in, as the core's `VerifyStatistics` is. `uncompressed_page_size` is held to the
decompression cap before anything is allocated, and the decompressed size must equal it exactly.

Where the bytes go is decided by the page:

| page | where its values decompress |
|---|---|
| uncompressed, on a mapped file | nowhere: read in place |
| v2, plain fixed-width, no null, inside the batch | straight into the column's buffer, at the batch's offset |
| v2, any other | into a page buffer the decode reads, and the data buffer of the views it makes |
| v1 | the whole page into a page buffer: its levels sit inside the compressed bytes |
| the dictionary page | once per column chunk, into a buffer the split keeps |

A v2 page's levels are never decompressed: the standard keeps them outside the compressed section.
Every buffer comes from the session's pool, 64-byte aligned, with 64 bytes of slack past its end, so
a kernel may load a whole vector past the last value. A page read in place from a mapped file has
slack of its own, since a valid file holds its footer and eight more bytes after its last page; a
page that ends less than 64 bytes before the end of the file is decoded on the kernels' exact path.

### 5.5 Decoding into the arena

A page decodes into the canonical nodes a Vortex block decodes into, so that what follows the decode
cannot tell the two formats apart:

| encoding | node | what is done per value |
|---|---|---|
| PLAIN, fixed width | `Primitive`, `Decimal`, an extension's storage, `FixedSizeList` | nothing: a view, when the page has no null; else an expand to the rows' slots |
| PLAIN, BOOLEAN | `Bool` | nothing: a view at a bit offset, when the page has no null |
| PLAIN, BYTE_ARRAY | `VarBinView` over the page | a 16-byte view per value; values of up to 12 bytes inlined in it, longer ones left in the page |
| RLE, BOOLEAN | `Bool` | runs expanded to bits |
| RLE_DICTIONARY, PLAIN_DICTIONARY | `Dictionary`: the codes, over the chunk's values node | codes unpacked; the values decoded once per chunk and shared by every batch |
| DELTA_BINARY_PACKED | `Primitive` | unpack, add, prefix sum |
| DELTA_LENGTH_BYTE_ARRAY | `VarBinView` over the page | views from the lengths' prefix sum; the bodies are contiguous and stay in the page |
| DELTA_BYTE_ARRAY | `VarBinView` over a heap the decode writes | each value rebuilt from the previous one's prefix |
| BYTE_STREAM_SPLIT | `Primitive`, `FixedSizeList` | the streams interleaved |
| ALP | `Primitive` | unpack, add, two multiplications, exceptions patched |

**Encoded delivery**, when the scan asks for it as the core's aggregates do: a dictionary column is
delivered as `Dictionary` and never gathered, so a group by groups by code and never touches a
string; a page whose codes are a few long runs as `RunEnd`; a batch that is one code and no null as
`Constant`. Otherwise the core canonicalizes on access, as it does for a Vortex file.

**Alignment.** `Values` is 64-byte aligned by contract; a view of a page is only as aligned as the
page, and the core copies an unaligned one once, on its first read. Every page this writer stores
uncompressed starts its values on a 64-byte boundary of the file (§6.5), so on its files `Values` is
the mapping itself.

**Text.** `STRING`, `ENUM` and `JSON` values are validated as UTF-8 when decoded: a dictionary's
values once per chunk, a delta-length or delta heap once whole plus a check that no value starts
inside a sequence, plain values one by one.

### 5.6 Levels and nesting

Each leaf's maximum definition and repetition levels, and the level at which each of its ancestors
becomes defined and repeats, are compiled at the open.

- **A flat required column** has no levels to read.
- **A flat optional column**, maximum definition level 1, gets its validity straight from the
  RLE/bit-packing hybrid: a bit-packed run of width 1 is already a least-significant-bit-first
  bitmap, copied with a shift; an RLE run fills whole words; the non-null count is a population
  count.
- **A nested column** is assembled in one pass over its repetition and definition levels: list
  offsets and sizes per repeated ancestor, validity per optional one, the leaf's values dense. The
  structure an ancestor shares between several leaves is built from the first projected leaf and
  checked against each other leaf's levels by a vector comparison; a disagreement refuses the file,
  since list offsets are what memory safety rests on.
- Legacy two-level lists and unannotated repeated fields read by the five backward-compatibility
  rules of [LogicalTypes.md][lt], and `MAP_KEY_VALUE` as `MAP`.

A batch of a nested column ends on a row: where the repetition level is 0.

**A variant** is assembled as the struct of its group's fields, at any depth, and read as the
core's variant: the `metadata` column as it is, and the `value` rebuilt from the columns shredded
out of it as [`construct_variant`](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md#L289-L343) builds it. The core does the rebuilding
(`ShreddedVariant`, beside its Parquet variant encoding), from the group's type and canonical nodes
alone, so that the Vortex `vortex.parquet.variant` encoding's shredded child can take the same path.

- **Nothing shredded**, a group of a required `value`, costs nothing: the `value` node is the
  variant's, its views over the page. A million rows of 45 bytes read in 14 to 19 ms.
- **A row a typed column leaves null** takes its `value` as it is: its 16-byte view is copied, its
  bytes are not.
- **A row a typed column defines** is written into a scratch the plan keeps from batch to batch,
  which the arena takes in one copy. An object takes its fields in name order, the unsigned order of
  their UTF-8 bytes: the shredded fields, sorted once at the open of the scan, merged with those of a
  partially shredded row's `value`. Field ids are the row's metadata's, looked up again only when a
  row's metadata differs from the last row's: a view equal to the last row's, sixteen bytes, is
  enough, which a metadata column of one dictionary value always is.
- **Counts, ids and offsets take the fewest bytes** that hold them, the encoding the standard's test
  files carry, so that a rebuilt value is compared byte for byte with theirs. An object or array of
  leaves knows every field's size before it writes one, and writes its header first at its final
  widths; one with a nested object or array reserves four bytes an offset and moves its values back
  once, by the size of the header it did not need.
- **Measured**: a million rows of an object of three INT32 fields read in 46 to 51 ms, against 20
  to 25 ms for the same columns as a plain struct; with a third of the rows partially shredded, a
  string field in their `value`, 67 to 72 ms against 24 to 30. A column at a time would leave the
  row loop for one over each field's column, which this has not needed yet.
- **Refused**: a primitive or array whose `value` and `typed_value` are both non-null, a `value`
  that is not an object beside a typed object, an object field a row's metadata does not name, and
  an object whose fields are out of name order, as malformed; a typed column of a type the
  [shredding table](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md#L85-L107) does not have, as unsupported when the variant is read, not when
  the file opens.

### 5.7 Batches

A batch holds the same rows in every column, as the core requires, 8 192 by default. On a file this
writer made, every column's pages lie on that grid (§6.1), so a batch is one page of each column and
each of its buffers is the page's own: the mapping for a plain page stored uncompressed, the
decompression's destination otherwise. On any other file, a batch's rows are cut on the same grid; a
page is decoded whole, and a batch it holds whole reads its slots, views and validity in place, sliced
at the batch's rows, so that a page of a million rows, as other writers cut them by bytes, is decoded
once and copied never. Decoding a dictionary page's rows batch by batch instead, straight into each
batch's buffers, was measured slower: on the January 2023 yellow taxi trips, read a column at a time,
333 ms against 248, a narrow column's whole-page gather being one vectorized pass where a batch's pays
its allocation and its spread each time. A column whose values for one batch come from two pages
copies them once into one buffer, since a column's values are one span.

A batch is borrowed, valid until the next `MoveNextAsync`; its buffers belong to the batch's arena or
to the split's context, which holds the dictionaries and the page buffers views point into. An owned
`RecordBatch` copies once, as for a Vortex file.

From a source that does not read in place, the next row group is chosen and read while one is
decoded: its page index, Bloom filters and dictionaries consulted, its chunks or its first window
read, on a thread of the pool, since a positional read the system's cache serves completes before it
returns and would otherwise run on the scan's own. The dictionaries it decodes to prune go into a
context of the pruning's own, never the batch's arena. A scan that takes a number of rows reads no
group ahead, and a disposed scan cancels what it reads ahead. Over positional reads of sixteen row
groups of a quarter million rows, a scan goes from 7.05 ms to 4.14.

### 5.8 Parallelism

The degree is the session's or the scan's, 1 unless set ([09-contracts.md](09-contracts.md) §2).
Splits decode side by side, each on a context and arenas of its own, nothing shared, and batches are
delivered in row order by the core's machinery; a large row group is cut into splits at its page
boundaries so that it spreads. Within a split, the pages of a column are decompressed ahead of their
decode within the read-ahead window. The answers are the same bits at every degree.

### 5.9 Answers from the footer

A count without a filter is `FileMetaData.num_rows`, with no read after the open. With a filter, a
row group the statistics prove entirely inside the predicate — exact bounds, no null — counts its
`num_rows` without a decode, one proven outside counts nothing, and only the others are read. A
minimum or a maximum comes from the statistics when every row group's bound is exact, and from the
data otherwise.

### 5.10 Encrypted files

A file of the standard's [modular encryption](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encryption.md) is read with the keys
`ParquetOpenOptions.Decryption` gives: a column's from `ColumnKeys` by its dotted path, the footer's
from `FooterKey`, else what `KeyResolver` makes of the key metadata the file stores. `AES_GCM_V1`
decrypts every module through .NET's `AesGcm`; `AES_GCM_CTR_V1`'s pages are in counter mode, which
.NET does not have: a run of 256 counter blocks is encrypted in one `Aes.EncryptEcb`, which AES-NI
pipelines, and the 4 KiB of keystream XORed in a vector at a time.

- **An encrypted footer**, `PARE`, is its `FileCryptoMetaData` then its module, decrypted once at the
  open into bytes the file holds for its life: the one copy of the footer an open makes.
- **A plaintext footer** of an encrypted file reads without a key, as a reader older than encryption
  does: its plaintext columns read as any file's, and an encrypted one is refused until its key is
  given. Given keys, the open holds the footer to its signature, the 28 bytes past its
  `FileMetaData`, unless `VerifyFooterSignature` is off.
- **A column's metadata** encrypted under its own key is decrypted the first time a plan asks for the
  chunk, and kept: its statistics then prune as a plaintext chunk's do. Without its key, the chunk's
  plaintext copy, stripped of its statistics, places it, and a scan of it is refused by name.
- **A page** is two modules, its header and its body. The header is decrypted to be read, the body
  into a block of the pool when the page is decoded: one copy, as a decompression is, after which a
  compressed page decompresses as any. A page's `compressed_page_size` is [its module's](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L837-L838),
  length to tag, its plaintext that less 32 bytes, or 16 in counter mode. A module's AAD carries the
  page's ordinal among the chunk's data pages, which follows the pages' places: a header a skip
  looks at and leaves is the same page when it is read.
- **Refused**: a module that fails its authentication, as malformed, since the file was altered or
  the key or the AAD prefix is not its own; a footer, a column or an AAD prefix the file needs and the
  caller does not give, as unsupported, naming it.
- **Pruning**: an encrypted chunk's column index, offset index and Bloom filter are modules of
  their own, decrypted where its key is given, and prune as a plaintext chunk's do. The chunk is
  still read whole, never sparsely: a page skipped by its place, unread, would leave the next one's
  ordinal unknown to its module's AAD.
- **Not yet**: the suite's file whose keys are wrapped by a KMS, in key material outside it, needs a
  resolver that unwraps them, which is the caller's.

The suite's encrypted files, 128-bit and 256-bit keys, every footer mode, both algorithms, a prefix
stored and one supplied, read as the rows of the C++ writer's test generator, every one.

### 5.11 Ordered reads

A scan in the order of a column, `OrderBy` or the pass of a group by on it, reads a file that lies
in that order as it lies, with no sort. The file lies in it when every row group with rows declares
the column first among its [`sorting_columns`](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L1061-L1064),
in the scan's direction; its statistics give its minimum and maximum, no null and, for a float, no
NaN; and each row group's first bound, its minimum ascending or its maximum descending, is at or
past the last group's other bound, so that the groups follow one another. A bound cut short is
still a bound. The plan's order says `SortedColumn` where the file streams and `InMemory` where it
is sorted, and an ordered read is never cut into pieces read side by side.

- **A null** in the column sorts the file: a scan places nulls last, which a file may have first,
  and a row group's statistics count its nulls without saying where they lie.
- **The other way**, an order backwards on a file sorted forwards, is a sort too: a row group read
  backwards would need its rows reversed, held whole.
- **A group by** on the column streams, each group out once the scan reads past it: the source
  says the order comes on asking (`OrdersOnAsking`), which the core asks of a dataset's clustering
  key.
- **A declaration** is believed, as statistics are (§8); the verifier holds a row group's rows to
  it. One malformed orders nothing.

Measured on four million rows in sixteen row groups, on a key of one to five rows each: ordering
by the key and summing a column takes 28.6 ms streamed against 234 ms sorted, allocating 46 KB
against 16 MB; a group by on the key, 1.4 million groups, takes 91 ms streamed against 115 ms by
hash, allocating 1.5 MB against 340 MB.

## 6. Writing

### 6.1 The files it writes

| | |
|---|---|
| version and magic | 1 and `PAR1` |
| row groups | whole blocks, `RowGroupRows` 1 048 576 by default; a row group closes sooner when its buffered pages reach `RowGroupBytes`, 256 MiB, and at `FlushAsync` |
| pages | data pages v2, or v1 when `DataPageVersion.V1` asks for them, rows never split in either; a page is one block of `BlockRows`, 8 192 counted from the file's first row in every column, or a power-of-two fraction of one, down to 1 024 rows, when a block's page would pass `PageBytes`, 1 MiB, each range halved while it passes; below that, a page is cut by bytes and leaves the grid for that column alone. A page the dictionary codes stays whole, its codes far smaller than its values |
| dictionary | at most one page per column chunk, first, as the standard requires |
| page index | an `OffsetIndex` for every column chunk; a `ColumnIndex` wherever bounds are defined |
| statistics | `min_value` and `max_value` with their exactness, `null_count` always, `nan_count` for floats; byte arrays bounded at `StatisticsBoundBytes`, 64 (§3.2 #15); neither the legacy `min` and `max` nor statistics in page headers, which readers of the page index ignore ([PageIndex.md][pi]) |
| column orders | `TYPE_ORDER`; `IEEE_754_TOTAL_ORDER` for FLOAT, DOUBLE and FLOAT16, as the standard recommends |
| size statistics | the byte arrays' unencoded bytes and the level histograms, per chunk and per page |
| geospatial statistics | a GEOMETRY's box of every vertex, NaN skipped, and its values' types; a GEOGRAPHY's types, and its box where no value has an edge; neither where a value is not ISO WKB (§3.2 #22, #23). Vertices of little-endian XY points are bounded two coordinates to a vector |
| key-value metadata | `vorticity.schema`: the Vortex dtype the file was written from, its FlatBuffers bytes in base64 (§4.1), and the caller's own pairs |
| `created_by` | `Vorticity.Parquet version <version> (build <commit>)`, the form the standard asks for |
| Bloom filters | by `BloomFilters`, off by default: per column, a false-positive rate. A value is inserted as its page closes, into a filter sized by the core's sizing for the most values the chunk can hold, a page of dictionary codes by the dictionary's entries as the chunk closes; the filter is then folded, bit for bit the filter built smaller, to the size of the dictionary's exact count plus every other value counted as one, at most 1 MiB. Written after the row group's chunks, the standard's other place for them, so that a writer holds no filter past its row group |
| checksums | a `crc` on every page when `WriteChecksums` asks for it, off by default |
| never written | INT96, PLAIN_DICTIONARY, BIT_PACKED, LZ4, LZO, a `ConvertedType` without its `LogicalType` but `INTERVAL`'s, two-level lists, data pages v1 unless `DataPageVersion.V1` is asked for |

**Why one grid in every column.** The standard lets each column cut its pages anywhere. Cutting them
at the same rows makes a batch one page per column, a decompression the column's own buffer, an
`OffsetIndex` the same in every column, and the page index a mask of blocks as the core's zone maps
are; a column of wide values pays with more pages, a narrow one with smaller ones.

**Why v2.** Its levels are outside the compressed bytes, so a reader decompresses values straight
into place and counts nulls without decompressing; it never splits a row; and `is_compressed` lets a
page that does not shrink stay as it is. A v1 page offers none of the three.

The file is a function of the sequence of batches and the options: no seed, no sample, the same bytes
at every degree.

### 6.2 One pass per block

The writer is the core's ([11-write-strategy.md](11-write-strategy.md)) with Parquet's wire forms: a
value is read twice and copied never between its arrival and its page. The statistics pass reads each
block once, on the caller's memory, and keeps what the bounds need — minimum and maximum, NaN skipped
and the two zeros told apart by their bits, null and NaN counts, level histograms, byte totals — and
what the choice needs: distinct values through the chunk's table, runs, deltas and their widths per
miniblock of 32, common prefixes between neighbouring byte arrays. Every candidate's cost is then an
exact formula of those statistics, the encoder's own planning, so that a cost is the bytes the encoder
writes; a trial runs only where no formula exists, as BYTE_STREAM_SPLIT does, whose worth is what a
codec makes of it. Where the encoding is the likelier outcome, the price is the write itself: a page of
integers is written as DELTA_BINARY_PACKED, and a page's dictionary codes as their runs, into room for
the worst case, and dropped when they do not pay, so that nothing priced is walked twice.

A column chunk keeps one distinct table, codes assigned as rows arrive. While the dictionary holds —
it wins on the formulas and its page stays under `DictionaryPageBytes`, 1 MiB — the chunk's pages are
RLE_DICTIONARY; once it stops holding, the rest of the chunk's pages take the best other encoding,
and the dictionary page holds the values coded so far.

**Why no plan memory.** The core remembers a column's plan from one chunk to the next; this writer
re-weighs each row group. What a row group re-weighs is the dictionary on its first page and a trial
of one page, about one page in 128 of a row group of 1 048 576 rows, and every other price is the
write itself. Rewriting ClickBench's first file, a row group of 105 columns, choosing every page's
encoding takes 178 ms of 2 081, nearly all of it the encoding a remembered plan would run as well.

### 6.3 Encodings

| column | candidates |
|---|---|
| BOOLEAN | PLAIN; RLE when runs make it smaller |
| INT32, INT64 | PLAIN, RLE_DICTIONARY, DELTA_BINARY_PACKED; BYTE_STREAM_SPLIT under a codec, by trial |
| FLOAT, DOUBLE | PLAIN, RLE_DICTIONARY; BYTE_STREAM_SPLIT under a codec, by trial; ALP when `Alp` is enabled, by the same trial, which then weighs all three on the chunk's first PLAIN page |
| BYTE_ARRAY | PLAIN, RLE_DICTIONARY, DELTA_LENGTH_BYTE_ARRAY, DELTA_BYTE_ARRAY |
| FIXED_LEN_BYTE_ARRAY | PLAIN, RLE_DICTIONARY, DELTA_BYTE_ARRAY; BYTE_STREAM_SPLIT under a codec, by trial |
| levels | the RLE/bit-packing hybrid, the only encoding v2 allows |

ALP stays behind an option while the standard marks it Preview, as the standard recommends of a
writer; a hint pins it without one. A page takes one pair of exponents, the core's search over a
sample of its values, and vectors of 1 024. On a million prices of two decimals ALP stores 3.03 MB
where PLAIN under ZSTD stores 4.34, on as many coordinates of six 2.75 MB where it stores 6.59, and
both scan five times as fast, an ALP page that ZSTD does not shrink staying as it is; a column of few
distinct values keeps its dictionary, which the taxi file's every double does.

`CompressionProfile` changes the arithmetic, not the pass: `Auto` weighs bytes against decode speed,
an encoding taken when it saves an eighth; `Smallest` weighs every candidate by the bytes it
stores once compressed, in a trial of each chunk's first PLAIN page at the chunk's codec and level,
whose winner the chunk's later PLAIN pages take, a few compressions a chunk; `Fastest` writes PLAIN
and dictionaries, `None` writes PLAIN. `Hints` pins a column to an encoding by path, as the core's
hints pin a scheme: on every page, under every profile, unpriced, a dictionary while it stays within
its bound; an encoding the column's type does not take is refused when the writer is created.

### 6.4 Compression

A column chunk has one codec, ZSTD at level 3 under `Auto`, LZ4_RAW under `Fastest`, ZSTD at level
19 under `Smallest`, none under `None`; a column may name its own codec and level in
`ColumnCompression`, the columns of one ZSTD level sharing their compressors. A v2 page whose values do not
shrink by an eighth is stored with `is_compressed` false, so its reader skips the codec; a dictionary
page, which has no such flag, takes the chunk's codec. ZSTD goes through `Vorticity.Zstd`, one frame
per page. SNAPPY and LZ4_RAW are this package's own, each a valid stream of its format by its own
deterministic algorithm, with no claim to match another compressor's bytes. GZIP and BROTLI go
through `System.IO.Compression`; its GZIP stream allocates per page, which the allocation gates name
as the one exception.

Pages are compressed on the writer's threads at a degree above one, each into a buffer of its own,
and assembled in order: the bytes do not depend on the degree.

### 6.5 Pages out without copies

A page is a header, then, for v2, its levels, then its values; the pieces live in pooled buffers
until their row group is complete, since a column chunk must be contiguous and every column of the
row group arrives together. Over a path, the row group goes out in one vectored positional write,
`RandomAccess.WriteAsync` with the list of buffers: a page is never copied once it is compressed.
Over a caller's `PipeWriter`, it is copied once, into the pipe's memory.

**Uncompressed pages are aligned.** When a page's values are stored uncompressed, its header ends
with field 32767, the extension slot the standard reserves in every structure
([BinaryProtocolExtensions.md][ext]), carrying a 16-byte identifier of this writer, as the standard
requires of an extension, and padding sized so that the values start on a 64-byte boundary of the
file. Every reader skips the field. A chunk's place in the file is known only as its row group closes,
after its pages were compressed, so its pages are laid out from a boundary of their own, and the
header of its first data page, written last, takes the padding that brings that boundary onto the
file's; a dictionary page, written at the close, is aligned where it lies. It costs 21 to 84 bytes
per uncompressed page and buys a reader that maps the file its values in place, aligned as the
core's `Values` must be, which an unaligned page has copied at its first read: summing a PLAIN key
of a million rows through `Values` takes 294 µs aligned, 529 µs unaligned, and 312 µs from a Vortex
file. `AlignUncompressedPages` turns it off.

### 6.6 Row groups, memory and order on disk

A writer holds the open block of each column, each chunk's distinct table while it runs, the
compressed pages of the row group being written, which `RowGroupBytes` bounds, and a Bloom filter of
at most 1 MiB per column that asked for one; nothing per row. The file is laid out as: the magic; the
row groups, each its column chunks in schema order, each its dictionary page then its data pages,
then the row group's Bloom filters; every `ColumnIndex`, then every `OffsetIndex`, by row group then
column, so that a reader's index reads coalesce; the footer, its length and the magic. `Abandon()` gives the file up as the core's writer does: a new file is deleted,
a caller's pipe completed with an error.

### 6.7 Encrypted files

`ParquetWriteOptions.Encryption` writes a file of the standard's modular encryption: its footer
encrypted, `PARE` at both ends, or in plaintext and signed, `PAR1`; every column under the footer's
key, or the columns `ColumnKeys` names under their own, the others in plaintext; `AES_GCM_V1`, or
`AES_GCM_CTR_V1` whose pages are in counter mode.

- **Modules** each take a fresh random nonce, and their AAD the file's prefix, eight random bytes of
  its own, the module's type and its row group's, column's and data page's ordinals: no module of
  one file authenticates in another, nor in another place of the same file, under the same key.
- **A page** is a header module then a body module, the body the page's levels and values, which
  its header's `compressed_page_size` counts as the module's. A page is encrypted as it closes,
  its first header at the chunk's close as a plaintext one is; the 64-byte alignment of uncompressed
  pages is off, the module's length and nonce coming first.
- **A column's metadata** is a module of its own where its own key encrypts it, and wherever the
  footer is in plaintext, where the footer's copy is stripped of its statistics, sizes and box. Its
  column index, offset index and Bloom filter are modules too.
- **The footer** encrypted is its `FileCryptoMetaData`, then its module; in plaintext, it names the
  algorithm and the signing key's metadata, and 28 bytes past it sign it.

Read back, the five arrangements the tests write hold the rows their plaintext twin holds, prune as
it does, statistics, page index and Bloom filters alike, and are refused without their keys, under
another prefix, or with a byte altered.

### 6.8 Sorted files

`ParquetWriteOptions.SortingColumns` declares the columns the rows are sorted on, the first of most
precedence, in every row group's `sorting_columns`: flat columns of integers, decimals, booleans,
text, bytes or times, each ascending or descending, its nulls first or last. A float is refused:
its order has a NaN and two zeros to place, which the standard leaves to each writer.

The writer holds the rows it is given to the declaration before any column takes them. A first key
of integers with no null, the common declaration, is walked a register at a time by the core's pair
kernel, the one the Vortex writer's `is_sorted` statistic uses, strictly where keys follow it so
that its ties are handed to them; any other batch a pair at a time through the core's column
orders. A batch's first row is held to the last batch's last, kept as literals. A row out of order
refuses its batch whole, naming its place, and the file goes on from the rows it holds. Writing
four million rows sorted on an integer key takes 264 ms with the declaration and 264 ms without.

## 7. Kernels

Each is generic over its physical type and specialized by the compiler, vectorized with `Vector128`
everywhere and wider vectors where the hardware has them, and has a scalar twin that the suite runs
again with hardware intrinsics disabled and compares bit for bit
([03-architecture.md](03-architecture.md) §1).

| kernel | serves | vector form |
|---|---|---|
| unpack, least significant bit first, widths 0 to 64 | hybrid runs, dictionary codes, delta miniblocks, ALP | AVX-512 VBMI: a byte permute gathers the bytes each value spans, then shift and mask, a register of values a step; elsewhere a byte shuffle (`PSHUFB`, `TBL`) and a variable shift; one table per width, chosen once per run |
| pack | every bit-packed output | a word at a time: one store per 32 or 64 bits filled, the bits past it carried into the next |
| RLE runs | levels, codes, booleans | broadcast stores; to encode, runs found where neighbours differ, a vector of pairs compared at a time, 32 levels or 8 codes |
| levels to validity | maximum definition level 1 | bit-packed runs copied as bitmaps with a shift, RLE runs filled by words, population count |
| levels to offsets and validity | nesting | comparison masks per level, positions compressed out of them, prefix sums |
| expand | dense values to the rows' slots | the core's: a word of 64 rows at a time, all valid a copy, all null nothing, a mixed one by `VPEXPAND` under AVX-512, a branch-free scatter otherwise |
| delta decode | DELTA_BINARY_PACKED, the lengths of the delta byte arrays | unpack, add the minimum delta, an in-register prefix sum with a carried lane, wrapping |
| delta encode | the same | deltas by a shifted subtraction, minimum and width by reductions and a leading-zero count |
| stream split | BYTE_STREAM_SPLIT | 16 values a step for 2, 4 and 8 streams: byte interleaves (`PUNPCK`, `ZIP`) to decode, even bytes pulled from odd ones (`PACKUSWB` over a mask or a shift, `UZP`) to encode, at 25 GB/s and more either way |
| ALP | FLOAT, DOUBLE | unpack, add the frame, convert (`VCVTQQ2PD` under AVX-512DQ), two multiplications never fused, exceptions patched |
| plain byte arrays | PLAIN BYTE_ARRAY | serial, since each length gives the next value's place; unrolled, values inlined into views by 16-byte loads within the slack |
| big-endian decimals | DECIMAL on fixed and variable byte arrays | in 16 bytes, a value a register: a byte shuffle that reverses its bytes and repeats its first, whose sign a comparison spreads over the bytes past it; in 4 or 8, and a length-prefixed value in 16, a big-endian load and an arithmetic shift. A page with no null widens into its slots, nothing copied after: a column of four million decimal(28, 4) values reads in 3.24 ms, against 43.3 a byte at a time |
| checked narrowing | `INT(8)`, `INT(16)` | the core's kernels: the page's extremes against the annotation's range, two accumulators a register, then truncation by vector narrowing: four million values in 0.62 ms against 2.2 |
| dictionary gather | materializing a dictionary column | gathers of 4 and 8 bytes, views by pairs of 8 |
| UTF-8 | text | `System.Text.Unicode.Utf8.IsValid` |
| SNAPPY, LZ4_RAW | the codecs | copies as overlapping 16-byte stores within the slack, short match offsets by pattern shuffles; encoders by a hash table, greedy, LZ4's end-of-block rules kept |
| checksums, hashes | CRC32, XXH64, the split-block filter | `System.IO.Hashing`; the core's `SplitBlockBloom` |

**What the core's ALP brings, and what it does not.** The exponent and factor search and the
rounding are the paper's, shared with `vortex.alp`. The layout, the frame of reference and the
packing order are Parquet's, and so are the constants, which §3.2 #5 holds to exact rounding.

## 8. Untrusted input

A file is untrusted input, under the core's threat model ([09-contracts.md](09-contracts.md) §4):
malformed bytes produce `ParquetFormatException` or `ParquetUnsupportedException` and nothing else;
well-formed bytes that lie may produce wrong answers. The fields fall into the core's three classes
([08-semantics.md](08-semantics.md) §5):

| class | Parquet fields | handling |
|---|---|---|
| I: memory depends on it | footer length; every offset and size; Thrift lengths and list sizes; schema `num_children`; page sizes, decompressed sizes; RLE run lengths; bit widths; dictionary codes; DELTA headers; byte array lengths; ALP headers, offsets, exception positions; BYTE_STREAM_SPLIT lengths; levels against their maxima; nested structure shared between leaves | checked always, before use |
| II: only correctness depends on it | statistics, the page index's bounds, Bloom filters, `encoding_stats`, a dictionary's `is_sorted`, `sorting_columns` | believed; checked by `VerifyStatistics` |
| III: hints | `total_byte_size`, `total_uncompressed_size`, `distinct_count`, size statistics | never a correctness input |

| cap | value |
|---|---|
| footer | `MaxFooterBytes`, 256 MiB, and inside the file |
| Thrift nesting, schema depth | 64 |
| a Thrift list | no more elements than bytes left |
| a page decompressed | the core's decompression cap, 256 MiB by default |
| an RLE or bit-packed run | no more values than the page has left |
| a dictionary code | below the dictionary's size |
| a DELTA_BINARY_PACKED block | a multiple of 128 values, miniblocks of a multiple of 32, widths within the type, its value count the page's |
| an ALP page | vectors of 2^3 to 2^15, exponents up to 10 and 18, factors up to the exponent, widths up to 32 and 64, offsets inside the page |

The fuzzer of [04-conformance.md](04-conformance.md) §5 has a Parquet target, `--parquet`: it walks
a file's footer and each page header the footer places as Thrift compact, notes where every varint
lies and how long it is, and rewrites one in place at its own length to an edge, zero, one, one
off, twice, the most its bytes hold, so that the structure stays well formed and the value is the
one a bounds check exists for; beside it, the footer's length, the first bytes of a page's body
where an encoding's header lies, a page's decompressed size at its most, truncation, a word in the
data, and the bit flip as the control. Each mutation is read whole mapped, then by positional reads,
which cut groups into windows and read ahead, and verified against itself; only
`ParquetFormatException` and `ParquetUnsupportedException` may escape, within five seconds. Its
seeds are the standard's suite, its encrypted files read with the keys its README publishes, and
this writer's files under every page version, codec and forced encoding (`FuzzSeeds`, where
`VX_FUZZ_SEEDS` names the directory). 300 000 mutations of the suite and 100 000 of this writer's
files read or fail cleanly; a third of the suite's read to the end.

## 9. Correctness without an external reference

| layer | question | holds |
|---|---|---|
| the standard's examples | do we write and read the bytes the standard's own examples show? | the varint `DF 89 03` of the compact protocol; the hybrid's and the legacy packing's 0 to 7 at width 3; both DELTA_BINARY_PACKED examples' arithmetic; `Hello`, `World`, `Foobar`, `ABCDEF`; `axis`, `axle`, `babble`, `babyhood`; the stream-split example; ALP's 31-byte vector. Each test cites its line |
| transcriptions | does the fast path compute what the standard says? | one deliberately plain decoder and encoder per encoding and codec, written from the standard alone, value by value, and never changed to agree with the fast path, which must match it bit for bit on random inputs at every width and at the edges: 0, 1, 7, 8, 9, 31 to 33, 127 to 129, 1 023 to 1 025, 8 191 to 8 193 values |
| round trips | does what we write read back? | every physical and logical type, nullability, nesting to depth four, every encoding forced, every codec, v1 and v2, page and row group sizes swept; floats compared by their bits |
| metamorphic | does an option change a value? | the same rows under every writer option read back identical; pruning on and off, every degree, every split target return the same rows; every degree writes the same bytes |
| Vortex as oracle | do two formats agree on the same rows? | rows written as Vortex, whose writer the Rust reference cross-checks, and as Parquet read back equal; a Parquet file rewritten as Vortex and back keeps every value |
| the file's own metadata | does a file's content agree with what it says of itself? | for any Parquet file, ours or found: exact statistics recomputed, size statistics' byte totals and level histograms recomputed, the page index's null pages, null counts, bounds and boundary order, row and value counts summed, checksums when present, no false negative in a Bloom filter, a sorted dictionary or sorting columns when claimed. A tool runs it on any file, which is how files from the real world are read without a reference |
| real files | do files of other writers, at scale, read as they say? | the January 2023 yellow taxi trips, Arrow C++ 8.0, 3.07 million rows in one row group, and ClickBench's first part of hits, a million rows of 105 columns: each held to its own metadata, and the same rows, hashed, mapped and by positional reads; the trips written again by this writer sorted on their pickup time, held to their declaration and read in that order as they lie, the same rows as the original's sort. `RealDataTests`, where `VORTICITY_REAL_DATA` names the data disk |
| fuzzing | does malformed input escape a clean failure? | §8 |

## 10. The performance contract

| promise | gate |
|---|---|
| nothing allocated per batch | allocations counted, warm, across full scans of a row group of 64 batches and of one of 16, which cost the same within 256 bytes, for every encoding the writer makes, pages v1 and v2, and every codec but GZIP |
| a plain page without nulls is its column | on a mapped file of this writer, every `Values` of an uncompressed plain page lies inside the mapping; a v2 compressed plain page decompresses into the column's buffer, and the count of batches copied out of the pages they span stays at 0, where pages of three rows read in batches of four count theirs |
| a page is decompressed once per scan | the file's pages walked by their headers: data and dictionary pages decoded equal theirs, decompressions equal those that go through the codec, under ZSTD, Snappy and none, pages v1 and v2, mapped and read; a dictionary decoded to prune is not decoded again |
| a pruned page is neither read nor decoded | `Requests` and `BytesRequested` equal the plan's; pages decoded equal the pages of the batches the page index leaves, of every column, nested ones among them |
| a range is requested at most once per scan | `Requests` equals the plan's distinct ranges |
| what the footer answers reads nothing more | no request after the open for a count, and for a minimum or a maximum under exact statistics |
| the open is the schema and the row groups, not their product | the opens of footers of 50 and 200 columns by 50 and 200 row groups: the widest costs what the two mixed ones do less the smallest, within 2 KiB, where a cost per chunk would leave 22 500 chunks over |
| the first batch waits for its pages, not its row group | from a source that does not read in place, the first batch of a group sixteen times another's asks for no more bytes than the smaller group whole, and less than an eighth of its own: 377 KB of 15.7 MB against 984 KB; `ParquetWindowBenchmarks` times both, 268 µs against 328 |
| no dispatch per value | `PerRowDispatchTests` counts the package's calls to the per-value readers beside the core's, at the same ceilings |
| every kernel has a scalar twin | the package's suite, run whole by `tests/scalar-pass.sh` with hardware intrinsics disabled |
| Native AOT | `pqdump`, the inspection tool, built on the public surface alone and published ahead of time, opens, scans and verifies every file of the standard's test suite at a pinned commit; its surface is on record beside the core's, rendered the same way |
| a file is the same bytes at every degree | written at degrees 1, 2, 4 and 8 and compared |
| the writer allocates per file, not per row | warm, a file of four times the batches costs at most 288 bytes a page more, its bounds and its place kept for the indexes (251 measured), and a schema four times as wide at most 7 KiB a column more (6.6 measured), the columns PLAIN: a dictionary's table rents from the shared array pool, whose capacity follows the machine's cores |

The ratchets are counted as the core's are ([05-benchmarks.md](05-benchmarks.md) §5): a ceiling only
comes down. Speed is measured against baselines this repository owns:

- **each kernel** against its scalar twin, and a copy kernel against `memcpy` of its output, on
  BenchmarkDotNet;
- **the format's cost in one engine**: the core report's actions — open, scan, project, the narrow and
  wide filters, take, write — on the same table written once as Parquet and once as Vortex by this
  repository's writers, read by the same engine; the ratio informs and gates nothing;
- **regressions**, as the ratio of two kept runners across commits (`bench/runners.sh`);
- **real files**, read from the data disk under §9's metadata oracle: throughput, allocations, and the
  oracle's verdict.

## 11. Phases

1. **Read.** The compact protocol, the open, the schema with nesting; PLAIN, the hybrid, both
   dictionaries, the three delta encodings, BYTE_STREAM_SPLIT, and BIT_PACKED for old files;
   UNCOMPRESSED, SNAPPY, GZIP, ZSTD, LZ4_RAW and BROTLI; pages v1 and v2; pruning by statistics, page
   index, Bloom filter and dictionary; `ParquetScanSource` and the type mapping; checksums on request.
2. **Write.** §6, but ALP and encryption.
3. **The rest of the standard.** ALP in both directions, the writer's behind its option while the
   standard says Preview; `VARIANT`, read unshredded and shredded and written unshredded, through
   the core's Parquet variant encoding; `GEOMETRY` and `GEOGRAPHY` with their bounding-box
   statistics, written and verified, which no filter prunes by until the core's expressions have a
   spatial predicate; `FILE`, read and written as the struct of its fields, its references left to
   the caller to resolve; modular encryption, `AES_GCM_V1` through `AesGcm` and `AES_GCM_CTR_V1`
   with AES in counter mode over `Aes.EncryptEcb`, keys from a resolver the caller gives, read
   (§5.10) and written (§6.7); ordered reads on declared `sorting_columns` (§5.11), which the
   writer declares and holds its rows to (§6.8).

LZ4 and LZO are in no phase: the standard gives neither format (§3.2 #8).

## 12. Deliberately not done

| approach | why not |
|---|---|
| a Parquet library with an engine of its own | a second engine to keep equal to the core's, while the core's `ScanSource` already takes another kind of file |
| depending on a Parquet or Arrow library | the founding constraint ([03-architecture.md](03-architecture.md) §1) |
| reading column data from another file through `file_path` | the standard says such use ["is not considered part of the Parquet specification"](https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift#L992-L1010) |
| `_metadata` summary files | never specified, for the same reason |
| statistics in page headers | readers of the page index do not use them |
| data pages v1 by default | levels inside the compressed bytes and rows split across pages: no decompression into place, no common grid |
| checksums verified by default | a check reads every byte a view would not |
| a footer in another encoding | not in the standard; the extensions page shows one only as an example |
| matching another compressor's bytes | a codec's format admits many valid streams; ours are deterministic, which is what a test needs |

[pf]: https://github.com/apache/parquet-format/tree/04d56f291ff963e98bc37ab8100e2fc133ff583c
[readme]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/README.md
[thrift]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/src/main/thrift/parquet.thrift
[enc]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encodings.md
[alp]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/AlpEncoding.md
[comp]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Compression.md
[lt]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/LogicalTypes.md
[pi]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/PageIndex.md
[bloom]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/BloomFilter.md
[crypt]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Encryption.md
[var]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantEncoding.md
[shred]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/VariantShredding.md
[geo]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/Geospatial.md
[ext]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/BinaryProtocolExtensions.md
[contrib]: https://github.com/apache/parquet-format/blob/04d56f291ff963e98bc37ab8100e2fc133ff583c/CONTRIBUTING.md
[compact]: https://github.com/apache/thrift/blob/27e8a425ffb498e190df3a12e239326bf5ba9ed6/doc/specs/thrift-compact-protocol.md
[rfc1952]: https://www.rfc-editor.org/rfc/rfc1952
[rfc7932]: https://www.rfc-editor.org/rfc/rfc7932
[rfc8878]: https://www.rfc-editor.org/rfc/rfc8878
[snappy]: https://github.com/google/snappy/blob/1.3.1/format_description.txt
[lz4]: https://github.com/lz4/lz4/blob/v1.10.0/doc/lz4_Block_format.md
[xxh]: https://github.com/Cyan4973/xxHash/blob/v0.7.0/doc/xxhash_spec.md
