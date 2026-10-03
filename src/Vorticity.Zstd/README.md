# Vorticity.Zstd

Zstandard ([RFC 8878](https://www.rfc-editor.org/rfc/rfc8878)) compression and decompression for .NET,
in fully managed C#.

- **No native code.** No bundled library, no P/Invoke: Vorticity.Zstd runs wherever .NET runs, and it is
  trimming- and Native AOT-compatible.
- **The same frames as libzstd.** At every compression level, Vorticity.Zstd writes exactly the bytes that
  libzstd 1.5.7 writes. That holds with dictionaries, with long-distance matching and in multithreaded
  compression.
- **Fast.** Decompression is 1.10–1.21× as fast as libzstd on the Silesia corpus. Compression of large
  inputs is within a few percent of libzstd. See [Performance](#performance).
- **Allocation-free.** A warm compressor or decompressor allocates nothing.

Vorticity.Zstd is an independent implementation of the format. It is not affiliated with the zstd project or
with Meta.

## Requirements

Vorticity.Zstd targets .NET 11 (`net11.0`). Its only dependency is `System.IO.Hashing`, for the XXH64 frame
checksums. No NuGet package has been published yet: reference `src/Vorticity.Zstd/Vorticity.Zstd.csproj` directly.

## Usage

### Decompression

```csharp
using System.Buffers;
using Vorticity.Zstd;

var decompressor = new ZstdDecompressor();   // reuse it: it keeps its tables and buffers between frames

if (ZstdDecompressor.TryGetFrameContentSize(frame, out ulong size))
{
    byte[] content = new byte[size];
    OperationStatus status = decompressor.Decompress(frame, content, out int consumed, out int written);
}
```

`Decompress` decodes one whole frame per call:

| status | meaning |
|---|---|
| `Done` | The frame was decoded. `consumed` is its size, so the next frame starts there. |
| `NeedMoreData` | The source ends inside the frame. |
| `DestinationTooSmall` | The content does not fit in the destination. |
| `InvalidData` | The source is not a valid frame. |

After `NeedMoreData` or `DestinationTooSmall`, nothing has been consumed: call again with the whole
frame or a larger destination. Checksums are verified when a frame has one. A skippable frame decodes to nothing.

### Compression

```csharp
var compressor = new ZstdCompressor(level: 3);   // from ZstdCompressor.MinLevel (-131072) to MaxLevel (22)

byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
compressor.Compress(content, frame, out _, out int length);   // the frame is frame[..length]
```

`Compress` writes one whole frame per call. The frame always declares its content size. Set
`AppendChecksum = true` to end each frame with a checksum of its content. Level 0 selects
`ZstdCompressor.DefaultLevel`, which is 3.

### Dictionaries

```csharp
byte[] dictionary = File.ReadAllBytes("records.dict");   // trained by `zstd --train`, or any raw content

var compressor = new ZstdCompressor(level: 3, dictionary);
var decompressor = new ZstdDecompressor(dictionary);
```

The dictionary is prepared once, in the constructor, and then used for every frame. Vorticity.Zstd does not
train dictionaries: use `zstd --train`, or `ZstandardDictionary.Train` from `System.IO.Compression`.

### Parallel compression

```csharp
int length = await compressor.CompressAsync(content, frame, maxDegreeOfParallelism: 8);
```

`CompressAsync` cuts the source into jobs and compresses them on the thread pool. It writes the same
frame as libzstd with worker threads (`ZSTD_c_nbWorkers`), so the frame does not depend on the degree of
parallelism. Up to 512 KiB, it writes the same frame as `Compress`.

### Threading

Instances are not thread-safe: use one per thread, or one per concurrent operation.

## Compatibility with libzstd

**Decompression** follows libzstd's decoding and validation, so the two accept and reject the same
frames. Vorticity.Zstd additionally enforces RFC 8878's limit on the size of a block in every case; libzstd
enforces it only partly in one-shot decoding.

**Compression** reproduces libzstd 1.5.7's `ZSTD_compress` byte for byte, at every level from -131072
to 22. This covers:

- the nine strategies, from `fast` to `btultra2`;
- the pre- and post-block splitters;
- the long-distance matching that libzstd turns on at level 22 for sources over 64 MiB.

With a dictionary, Vorticity.Zstd writes the frames libzstd writes when the dictionary is prepared at the same
level (`ZSTD_createCDict`, then `ZSTD_CCtx_refCDict`). That is also how the `ZstandardDictionary` of
`System.IO.Compression` uses it. `CompressAsync` reproduces libzstd's multithreaded compression (zstdmt),
including dictionaries and long-distance matching.

**Limitations**

- There is no streaming API: a frame is compressed or decompressed in one call, from and into memory.
- Sources and frames must fit in a span, so they are limited to 2 GiB.
- Compression takes a level, a dictionary and the checksum option. libzstd's advanced parameters, such
  as the window size or the strategy, are not exposed.

## Performance

| speedup over libzstd 1.5.7 | Vorticity.Zstd | .NET 11 `System.IO.Compression` |
|:--|--:|--:|
| decompression, silesia.tar | 1.10–1.21× | 0.82–0.86× |
| decompression, small records | 1.16–1.34× | 0.92–0.95× |
| decompression, small records with a dictionary | 0.56–0.62× | 0.75–0.80× |
| compression, silesia.tar | 0.94–1.08× | 0.97–1.01× |
| compression, small records | 0.88–1.01× | 0.90–0.99× |
| compression, small records with a dictionary | 0.87–1.27× | 0.96–1.00× |

The ranges span compression levels 1 to 19. Vorticity.Zstd decodes small frames that use a dictionary more
slowly than libzstd, at every level.

The measurements use the data of zstd's own benchmarks, which `bench/zstd-corpus.sh` downloads:

- **silesia.tar**: the [Silesia corpus](https://sun.aei.polsl.pl//~sdeor/index.php?page=silesia), which
  the benchmarks in zstd's README use. It is taken as one tar of 211,948,032 bytes, compressed into one
  frame.
- **github**: the 500 JSON records of GitHub users from zstd's regression tests, 407,963 bytes in all.
  Each record is its own frame, and all frames go through one compressor or decompressor.
- **github + dict**: the same records with `github.dict`, the 110 KiB dictionary that zstd's tests use
  with them.

Three implementations are compared:

- libzstd 1.5.7 compiled at `-O3` with Apple clang 21. This is the reference.
- `ZstandardEncoder` and `ZstandardDecoder` from .NET 11, which wrap the libzstd that the runtime ships.
- Vorticity.Zstd, compiled with Native AOT.

The machine is an Apple M4 Pro running macOS 26.7, on one thread. The three implementations run in one
process and take turns, and each figure is the median of its rounds. MB/s are millions of bytes of
uncompressed data per second. At every level the three write identical frames, which the benchmark
checks, so one ratio holds for all of them. Decompression decodes libzstd's frames at each level.

**Compression ratio**

| level | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar | 2.896 | 3.205 | 3.378 | 3.518 | 3.588 | 3.674 | 3.834 | 4.011 |
| github | 2.866 | 2.992 | 3.019 | 3.019 | 3.019 | 3.070 | 3.063 | 3.070 |
| github + dict | 9.886 | 9.922 | 10.527 | 10.524 | 10.344 | 10.225 | 10.764 | 10.760 |

**Compression, MB/s**

| corpus | implementation | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar | libzstd 1.5.7 (native) | 777 | 459 | 239 | 150 | 115 | 21.9 | 9.89 | 4.74 |
|  | .NET 11 (System.IO.Compression) | 768 | 454 | 239 | 150 | 114 | 22.0 | 9.86 | 4.61 |
|  | Vorticity.Zstd (Native AOT) | 768 | 465 | 232 | 146 | 109 | 23.6 | 10.3 | 4.72 |
| github | libzstd 1.5.7 (native) | 316 | 303 | 208 | 201 | 154 | 39.4 | 15.4 | 12.5 |
|  | .NET 11 (System.IO.Compression) | 302 | 289 | 199 | 194 | 145 | 38.9 | 13.9 | 11.5 |
|  | Vorticity.Zstd (Native AOT) | 294 | 265 | 182 | 176 | 143 | 39.6 | 14.2 | 11.3 |
| github + dict | libzstd 1.5.7 (native) | 1187 | 1135 | 440 | 230 | 141 | 68.7 | 6.11 | 5.95 |
|  | .NET 11 (System.IO.Compression) | 1141 | 1092 | 438 | 227 | 139 | 68.9 | 5.90 | 5.72 |
|  | Vorticity.Zstd (Native AOT) | 1151 | 1078 | 397 | 201 | 130 | 86.6 | 7.74 | 7.41 |

**Compression, speedup over libzstd 1.5.7 (native)**

| corpus | implementation | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar | .NET 11 (System.IO.Compression) | 0.99× | 0.99× | 1.00× | 1.00× | 0.99× | 1.01× | 1.00× | 0.97× |
|  | Vorticity.Zstd (Native AOT) | 0.99× | 1.01× | 0.97× | 0.97× | 0.94× | 1.08× | 1.04× | 1.00× |
| github | .NET 11 (System.IO.Compression) | 0.96× | 0.95× | 0.96× | 0.96× | 0.94× | 0.99× | 0.90× | 0.92× |
|  | Vorticity.Zstd (Native AOT) | 0.93× | 0.88× | 0.88× | 0.88× | 0.93× | 1.01× | 0.92× | 0.91× |
| github + dict | .NET 11 (System.IO.Compression) | 0.96× | 0.96× | 1.00× | 0.99× | 0.98× | 1.00× | 0.96× | 0.96× |
|  | Vorticity.Zstd (Native AOT) | 0.97× | 0.95× | 0.90× | 0.87× | 0.92× | 1.26× | 1.27× | 1.24× |

**Decompression, MB/s**

| corpus | implementation | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar | libzstd 1.5.7 (native) | 2148 | 2006 | 1995 | 2170 | 2231 | 2314 | 2227 | 2030 |
|  | .NET 11 (System.IO.Compression) | 1837 | 1669 | 1641 | 1810 | 1863 | 1936 | 1851 | 1682 |
|  | Vorticity.Zstd (Native AOT) | 2371 | 2401 | 2413 | 2601 | 2634 | 2700 | 2538 | 2432 |
| github | libzstd 1.5.7 (native) | 760 | 521 | 529 | 525 | 528 | 535 | 539 | 528 |
|  | .NET 11 (System.IO.Compression) | 697 | 493 | 498 | 496 | 499 | 503 | 504 | 497 |
|  | Vorticity.Zstd (Native AOT) | 1022 | 613 | 615 | 618 | 621 | 628 | 633 | 625 |
| github + dict | libzstd 1.5.7 (native) | 3582 | 3716 | 3961 | 3175 | 3063 | 3240 | 3867 | 3900 |
|  | .NET 11 (System.IO.Compression) | 2777 | 2857 | 2954 | 2504 | 2453 | 2566 | 2929 | 2973 |
|  | Vorticity.Zstd (Native AOT) | 2128 | 2149 | 2227 | 1969 | 1913 | 1971 | 2200 | 2226 |

**Decompression, speedup over libzstd 1.5.7 (native)**

| corpus | implementation | 1 | 3 | 5 | 7 | 9 | 13 | 16 | 19 |
|:--|:--|--:|--:|--:|--:|--:|--:|--:|--:|
| silesia.tar | .NET 11 (System.IO.Compression) | 0.86× | 0.83× | 0.82× | 0.83× | 0.83× | 0.84× | 0.83× | 0.83× |
|  | Vorticity.Zstd (Native AOT) | 1.10× | 1.20× | 1.21× | 1.20× | 1.18× | 1.17× | 1.14× | 1.20× |
| github | .NET 11 (System.IO.Compression) | 0.92× | 0.95× | 0.94× | 0.94× | 0.95× | 0.94× | 0.94× | 0.94× |
|  | Vorticity.Zstd (Native AOT) | 1.34× | 1.18× | 1.16× | 1.18× | 1.18× | 1.17× | 1.17× | 1.18× |
| github + dict | .NET 11 (System.IO.Compression) | 0.78× | 0.77× | 0.75× | 0.79× | 0.80× | 0.79× | 0.76× | 0.76× |
|  | Vorticity.Zstd (Native AOT) | 0.59× | 0.58× | 0.56× | 0.62× | 0.62× | 0.61× | 0.57× | 0.57× |

To reproduce the measurements (macOS, .NET 11 SDK):

```sh
tools/native-ref/build.sh && bench/zstd-corpus.sh
dotnet publish bench/Vorticity.Zstd.Perf -c Release -o artifacts/perf
artifacts/perf/Vorticity.Zstd.Perf --corpus silesia,github,github-dict --results corpus.tsv --markdown corpus.md
```

## Testing

Correctness is checked against libzstd itself:

- **Differential tests** against the libzstd of .NET's `System.IO.Compression`. Vorticity.Zstd must give back
  the content libzstd decodes, and write libzstd's compressed frames byte for byte, with and without
  dictionaries.
- **zstd's golden files** for decompression, decompression errors and dictionaries.
- **A decodecorpus corpus**: random valid frames from zstd's own generator. They exercise the modes an
  encoder rarely emits, such as RLE and repeated tables, treeless literals and direct Huffman weights.
- **Fuzzing**, with buffers placed against guard pages.
- **Multithreaded compression** checked against libzstd built with threads. These tests are skipped
  until `tools/native-ref/build.sh mt` has built that library.

## Building

```sh
dotnet build -c Release
dotnet test
```

The SDK version is pinned in `global.json`. The native reference in `tools/native-ref` is needed only
for the multithreaded-compression tests and for the benchmarks. `build.sh` downloads the zstd 1.5.7
sources, checks their SHA-256 and builds them with the system C compiler. The scripts target macOS.

## Repository layout

| path | contents |
|---|---|
| `src/Vorticity.Zstd` | the library |
| `tests/Vorticity.Zstd.Tests` | differential tests, golden files, unit tests, fuzzing |
| `bench/Vorticity.Zstd.Perf` | Native AOT benchmarks against libzstd and the platform, in turns (`--corpus`, `--compress`) |
| `bench/Vorticity.Zstd.Benchmarks` | BenchmarkDotNet benchmarks |
| `bench/zstd-corpus.sh` | downloads the benchmark data into `tests/Vorticity.Zstd.Tests/testdata/corpus` |
| `tools/native-ref` | the native reference: libzstd 1.5.7 built from source, its timing harness, `decodecorpus` |
| `testdata` | zstd's golden files, the decodecorpus corpus, the reference frame |

## License

Vorticity.Zstd is licensed under the [Apache License 2.0](../../LICENSE). Its compressor is derived from libzstd
1.5.7, and that part remains under zstd's BSD license. The test data copied from the zstd repository
also stays under that license. See [NOTICE](../../NOTICE).
