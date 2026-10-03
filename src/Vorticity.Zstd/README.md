# Vorticity.Zstd

A Zstandard ([RFC 8878](https://www.rfc-editor.org/rfc/rfc8878)) decompressor and compressor in fully
managed C#: no native library, no P/Invoke. It aims to be faster than the platform's
`System.IO.Compression.ZstandardDecoder` and `ZstandardEncoder`, in JIT and in Native AOT.

```csharp
var decoder = new ZstdDecompressor();             // reusable: tables and buffers are kept
OperationStatus status = decoder.Decompress(frame, destination, out int consumed, out int written);

var withDictionary = new ZstdDecompressor(dictionaryBytes);   // zstd-format or raw content
ZstdDecompressor.TryGetFrameContentSize(frame, out ulong size);
```

`Decompress` decodes one whole frame per call (a skippable frame decodes to nothing); a warm decoder
allocates nothing. It is not thread-safe.

```csharp
var compressor = new ZstdCompressor(level: 3);    // reusable: tables and buffers are kept
byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
OperationStatus status = compressor.Compress(content, frame, out int consumed, out int written);
var withDictionary = new ZstdCompressor(level: 3, dictionaryBytes);   // prepared once, for every frame
int size = await compressor.CompressAsync(content, frame, maxDegreeOfParallelism: 8);   // jobs on the thread pool
```

`Compress` writes one whole frame per call, which declares its content size (and, with
`AppendChecksum`, ends with a checksum): the frame libzstd 1.5.7's `ZSTD_compress` writes at the same
level, byte for byte, at every level from -131072 to 22 (libzstd's nine strategies, its pre- and
post-block splitters, and the long-distance matching it turns on at level 22 for sources over
64 MiB). With a dictionary, zstd-format or raw content, the frames are those libzstd writes with the
dictionary prepared at the same level (`ZSTD_createCDict`, then `ZSTD_CCtx_refCDict`, as the
platform's `ZstandardDictionary` does), again byte for byte at every level, long-distance matching
included. A warm compressor allocates nothing. It is not thread-safe.

`CompressAsync` writes the frame libzstd writes with workers (`ZSTD_c_nbWorkers` of 1 or more, zstdmt):
the source cut into jobs of four windows (1 MiB to 1 GiB, each but the first loading the end of the one
before), compressed on the thread pool, up to the degree of parallelism at a time, by compressors the
instance keeps for them. The frame is the same whatever the degree, and up to 512 KiB it is the one
`Compress` writes. Dictionaries and long-distance matching take part as in libzstd (the first job takes
the dictionary; one matcher runs over the whole frame, the jobs in order). It is checked against
libzstd built with threads (`tools/native-ref/build.sh mt`).

## Layout

| path | what |
|---|---|
| `src/Vorticity.Zstd` | the library |
| `tests/Vorticity.Zstd.Tests` | differential tests against the platform's libzstd (decoded content, compressed frames byte for byte), golden files, unit tests, fuzzing |
| `bench/Vorticity.Zstd.Benchmarks` | BenchmarkDotNet |
| `bench/Vorticity.Zstd.Perf` | Native AOT measurement: Vorticity.Zstd, the platform's `ZstandardDecoder`/`ZstandardEncoder` and a fresh libzstd, paired and alternated (`--compress` for compression) |
| `tools/native-ref` | the native reference: libzstd 1.5.7 built with today's compiler, its timing harness, `decodecorpus` |
| `testdata` | the reference frame, zstd's golden files, the decodecorpus corpus |
