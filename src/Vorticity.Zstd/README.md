# Vorticity.Zstd

A Zstandard ([RFC 8878](https://www.rfc-editor.org/rfc/rfc8878)) decompressor in fully managed C#:
no native library, no P/Invoke. It aims to be faster than the platform's
`System.IO.Compression.ZstandardDecoder`, in JIT and in Native AOT.

```csharp
var decoder = new ZstdDecompressor();             // reusable: tables and buffers are kept
OperationStatus status = decoder.Decompress(frame, destination, out int consumed, out int written);

var withDictionary = new ZstdDecompressor(dictionaryBytes);   // zstd-format or raw content
ZstdDecompressor.TryGetFrameContentSize(frame, out ulong size);
```

`Decompress` decodes one whole frame per call (a skippable frame decodes to nothing); a warm decoder
allocates nothing. It is not thread-safe.

## Layout

| path | what |
|---|---|
| `src/Vorticity.Zstd` | the library |
| `tests/Vorticity.Zstd.Tests` | differential tests against the platform's libzstd, golden files, unit tests, fuzzing |
| `bench/Vorticity.Zstd.Benchmarks` | BenchmarkDotNet |
| `bench/Vorticity.Zstd.Perf` | Native AOT measurement: Vorticity.Zstd, `ZstandardDecoder` and a fresh libzstd, paired and alternated |
| `tools/native-ref` | the native reference: libzstd 1.5.7 built with today's compiler, its timing harness, `decodecorpus` |
| `testdata` | the reference frame, zstd's golden files, the decodecorpus corpus |
