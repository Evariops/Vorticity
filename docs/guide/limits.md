# Limits

Open a file you did not write: what a hostile file can make the reader do, what it cannot, and the
caps in between.

```csharp
await using VortexFile file = await VortexSession.Default.OpenAsync(path, new VortexOpenOptions
{
    MaxDecompressedSize = ceiling,
    VerifyStatistics = true,
});

try
{
    long rows = 0;
    await foreach (Columns<Reading> batch in file.Scan<Reading>())
    {
        rows += batch.RowCount;
    }

    Console.WriteLine($"ceiling {ceiling}: {rows} rows");
}
catch (VortexFormatException e)
{
    Console.WriteLine($"ceiling {ceiling}: {e.GetType().Name}: {e.Message}");
}
```

```
ceiling 4096: VortexFormatException: Decoding fastlanes.bitpacked would materialize 131072 bytes, above the 4096-byte decompression ceiling.
ceiling 1048576: 1000000 rows
ceiling 268435456: 1000000 rows
```

**A file is untrusted input.** Every structure the reader follows is bounded, so that a malformed
or malicious file makes it refuse rather than read out of bounds, allocate without limit, or loop
forever. A refusal is a `VortexFormatException` for bytes that break the format or a cap, and a
`VortexUnsupportedException` for a component this build does not implement; nothing else. The
reasoning behind each cap is in [08-semantics.md](../design/08-semantics.md).

## What a damaged file does

The sample writes a 38 436-byte file, then opens a thousand copies of it, each with four bits
flipped at random in its last 4 KiB, where the footer, the layout tree and the last chunk live,
and reads every row of each:

```
1000 copies of a 38436-byte file, four bits flipped in the last 4 KiB of each:
  VortexFormatException: 860
  VortexUnsupportedException: 41
  read whole: 99
```

Every failure is one of the two refusals. The 99 copies that read whole are the other half of the
lesson: **the reader guarantees safety, not integrity.** A flipped bit in a data segment decodes
to a different value, and the format carries no checksum over data. The index regions do carry
one, which `VerifyIndexesAsync` and `vxdump --verify` check.

## The fixed caps

`VortexLimits` holds them. None is settable, because each bounds a loop or a recursion the file
controls:

| | |
|---|---|
| `MaxArrayDepth`, `MaxDTypeDepth`, `MaxLayoutDepth` | 64 each: how deep an encoding tree, a type or a layout tree may nest |
| `MaxFlatBufferDepth` | 128 |
| `MaxFlatBufferTables` | 1 000 000 tables visited per traversal, the bound the reference verifiers use |
| `MaxMetadataSegments` | 16 |
| `MaxMetadataKeyLength` | 64 bytes |
| `MaxCompressionSpecs` | 8 |
| `MaxAlignment` | 64 bytes, `MaxAlignmentExponent` 6 |
| `MaxPostscriptSize` | 65 527 bytes |

A file past any of them is malformed, and says so with `VortexFormatException`.

## The ceiling you set

`VortexOpenOptions.MaxDecompressedSize` bounds what one decode may produce, and a decode is at most
a chunk of one column. The default is `VortexLimits.DefaultMaxDecompressedSize`, 268 435 456 bytes.
It is a refusal, not a truncation, and it is the defence against a decompression bomb: a few bytes on
disk that claim to expand to gigabytes.

The right value is small. A chunk this library writes holds about a megabyte of its widest column
([blocks-and-chunks.md](blocks-and-chunks.md)), so an honest decode is a few megabytes at most; the
first decode the sample makes produces 131 072 bytes, which is why 4 096 is refused above and 1 MiB
is plenty. Lower the ceiling for input you did not write; raise it only for a file you trust whose
chunks are genuinely large, such as long text values.

`VortexOpenOptions.MaxBatchDecompressedSize` bounds what the decodes of one batch produce together,
across its columns. It is off by default: a thousand columns each decoding near the ceiling above
make a batch a thousand times that, which is legitimate for a wide table you wrote and a bomb in a
file you did not. Set it for input you do not trust, at a few times the widest batch you expect;
a batch past it throws `VortexFormatException`.

## Statistics are claims

A file's statistics and zone maps are what the writer said about the values, and the reader
prunes by them. A lying file cannot make the reader fault, but it can make it skip blocks it
should have read. `VerifyStatistics = true` has the reader check what a decode can recompute: a
masked array's values carry no nulls of their own, and a key cursor walking a sorted column finds
each zone's null count, order and bounds as stated, and refuses to go on otherwise. It costs a pass
over the values it checks.

An answer the reader takes from the statistics alone, such as a count, a minimum or a mean that
no block had to be decoded for, is still the file's claim. For input you do not trust, ask the
question over the values, or check it against a scan.

## Components this build does not know

A file may name an encoding, a layout or a type this build does not implement. It still opens,
and the refusal comes when a block needs the component:

```csharp
await using VortexFile file = await VortexSession.Default.OpenAsync(patched);
string unsupported = string.Join(", ", file.ArrayEncodings.Where(c => !c.Supported).Select(c => c.Id));
long rows = 0;
await foreach (BatchView batch in file.Scan("Day", "City"))
{
    rows += batch.RowCount;
}
```

```
opened, not supported: vortex.alz; Day and City read 1000000 rows; Scan<Reading> throws Array vortex.alz
```

The sample renames the encoding of the `Celsius` column in a copy of the file, so this build no
longer knows it. The columns that do not use it read in full; the scan that needs it throws
`VortexUnsupportedException` naming the kind and the id, at its first block. The open cannot refuse
earlier: a footer may list encodings no chunk uses, and which ones a chunk uses is known only when
its bytes are read. `file.ArrayEncodings` and
`file.LayoutEncodings` list what the footer declares, each with `Supported`, so a caller can ask
before scanning.

## Watch out

* **The ceiling is per decode, not per scan.** A scan that decodes a thousand blocks of 1 MiB never
  approaches a 256 MiB ceiling; one block that claims 300 MiB trips it. The batch ceiling, when
  set, adds the decodes of a batch's columns together, and starts again at the next batch.
* The open reads the file's tail in one request of `InitialReadSize` bytes, 64 KiB by default and
  never less; a footer larger than that costs a second read, not a refusal.
* `IndexCacheBytes` on the session bounds what each open file keeps of decoded index runs. It is a
  memory ceiling rather than a safety one, and 0 keeps nothing.
* A cap refusal is `VortexFormatException`, an unknown component `VortexUnsupportedException`:
  [errors.md](errors.md) says which is which.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- limits
```
