# Limits

The caps that protect a reader from a hostile file, and how to change them.

A file is untrusted input. Every structure a reader follows has a ceiling, so that a malformed or
malicious file makes it refuse rather than allocate without bound or recurse forever.

## The fixed ones

`VortexLimits` holds them, and none of them is settable:

| | |
|---|---|
| `MaxArrayDepth`, `MaxDTypeDepth`, `MaxLayoutDepth` | 64 each — how deep a nested structure may go |
| `MaxFlatBufferDepth` | 128 |
| `MaxFlatBufferTables` | 1 000 000 |
| `MaxMetadataSegments` | 16 |
| `MaxMetadataKeyLength` | 64 |
| `MaxCompressionSpecs` | 8 |
| `MaxAlignment` | 64 bytes, exponent 6 |
| `MaxPostscriptSize` | 65 527 bytes |

They exist because each one bounds a loop or a recursion that a file controls. A file that exceeds
one is malformed, and the reader says so with `VortexFormatException`.

`VortexFileFormat` holds the shape of the tail beside them: an 8-byte end marker, and 65 535 bytes
read at open by default.

## The one you set

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path, new VortexOpenOptions
{
    Read = new VortexReadOptions { MaxDecompressedSize = 64L * 1024 * 1024 },
});
```

`MaxDecompressedSize` bounds what a single decode may materialise. The default is
`VortexLimits.DefaultMaxDecompressedSize`, 268 435 456 bytes. It is a refusal, not a truncation:

| ceiling | |
|---|---|
| 4 096 | refused: `Decoding vortex.runend would materialize 3997696 bytes, above the 4096-byte decompression ceiling.` |
| 1 048 576 | refused, same block |
| 268 435 456 | 1 000 000 rows |

This is the defence against a decompression bomb: a few kilobytes on disk that claim to inflate to
gigabytes. Lower it for input you did not write — low enough to matter, high enough for your largest
honest block, which `RowBlockSize` times the widest row gives you. Raise it only for a file you
trust with genuinely large blocks.

## Components a build does not know

`AllowUnknownComponents` is `false` by default: a file naming an encoding, layout, dtype or
compression scheme this build does not implement is refused at open with
`VortexUnsupportedException`, which names the `Kind` and the `ComponentId`.

Set it to `true` and the open succeeds; the refusal moves to the moment something actually needs
that component. A file whose one exotic column you never read then reads fine, and
`IndexFragmentRefusals` and `IndexDirectoryRefusal` say what was set aside along the way.

## Watch out

* **The ceiling is per decode, not per scan.** A scan that decodes a thousand blocks of 1 MiB never
  approaches a 256 MiB ceiling; one block that claims 300 MiB trips it.
* Statistics are claims a file makes about itself. `VerifyStatistics = true` at open has the reader
  check them against what it decodes — worth it for untrusted input, and not free.
* `IndexCacheBytes` is a memory ceiling too, though not a safety one: it bounds what one open file
  keeps of decoded index runs, and 0 keeps nothing.
* A limit refusal is `VortexFormatException` and an unknown component is `VortexUnsupportedException`
  — see [errors.md](errors.md) for which is which.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- limits
```
