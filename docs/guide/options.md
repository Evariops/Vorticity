# Options

Three option objects, each a record with `init` properties and a `Default`. Set only what you mean
to change; the rest of the object keeps its default.

## Opening

```csharp
VortexOpenOptions open = new VortexOpenOptions
{
    Read = new VortexReadOptions
    {
        MaxDecompressedSize = 64L * 1024 * 1024,
        IndexCacheBytes = 16L * 1024 * 1024,
        VerifyStatistics = true,
    },
    PreloadIndexes = true,
    TornTail = VortexTornTailPolicy.Refuse,
};

await using VortexFile file = await VortexFile.OpenAsync(path, open);
```

| option | default | what it changes |
|---|---|---|
| `InitialReadSize` | 65 535 | the tail read at open; larger catches the index directory in one round on a big footer |
| `PreloadIndexes` | `false` | read the index directory at open rather than at the first scan that wants it |
| `LeaveSourceOpen` | `false` | the file does not dispose the source it was given |
| `TornTail` | `ReadPrevious` | `Refuse` throws instead of falling back to the version before a torn append |
| `DType` | unset | the schema for a file that embeds none; without it such a file refuses to open |
| `FileLength` | `-1` | the length, when you already know it; the default issues one length probe |

## Reading

| option | default | what it changes |
|---|---|---|
| `MaxDecompressedSize` | 268 435 456 | the ceiling on what one decode may materialise |
| `IndexCacheBytes` | 67 108 864 | the decoded index runs one open file keeps for its cursors, oldest out first; 0 keeps nothing |
| `VerifyStatistics` | `false` | check the file's own statistics against what is decoded |
| `AllowUnknownComponents` | `false` | tolerate a component this build does not know, until something needs it |
| `IndexFragments` | empty | index pages held outside the file, handed in at open |

The ceiling is a refusal, not a truncation:

```
Decoding vortex.runend would materialize 3997696 bytes, above the 4096-byte decompression ceiling.
```

It exists so that a hostile or corrupt file cannot make a reader allocate without bound. Lower it
for untrusted input; raise it for a file with genuinely large blocks. `limits.md` is the
rest of that subject.

## Writing

```csharp
VortexWriteOptions options = new VortexWriteOptions
{
    RowBlockSize = 1_024,
    Compress = false,
    Profile = WriteProfile.Fastest,
};
```

| option | default | what it changes |
|---|---|---|
| `RowBlockSize` | 8 192 | rows per block: the unit of pruning, of a take, and of a batch |
| `DataBlockTargetBytes` | 1 048 576 | how much data goes into one segment |
| `Compress` | `true` | choose an encoding per column, or store the canonical form |
| `Profile` | `Default` | `Fastest` spends less time choosing |
| `FileStatistics` | `true` | write the per-column summaries the open reads back |
| `TargetEdition` | `Core20260803` | the edition a reader must understand: see `editions.md` |
| `IndexBudgetPerMille` | 100 | what the automatic index policy may spend, in thousandths of the file |
| `EncodingHints` | empty | steer a column by name: see `encoding-hints.md` |
| `Indexes` | `Auto` | the index policy: see `indexes.md` |
| `Identity` | minted | sixteen bytes naming this version of the bytes; pin it to make the write reproducible |
| `StringBoundBytes` | 0 | how many bytes of string bounds a text column's zones carry; 0 is none, and none prunes nothing |
| `ScratchDirectory` | the system's | where a locating index's runs wait for their merge once they outgrow memory |

The same million rows, written five ways:

| | bytes |
|---|---|
| default | 1 523 369 |
| `Compress = false` | 12 007 260 |
| `Profile = Fastest` | 1 523 244 |
| `FileStatistics = false` | 1 523 193 |
| `RowBlockSize = 1024` | 1 557 498 |

Compression is what makes the file eight times smaller. The profile costs time, not size, on this
data. Statistics cost 176 bytes. Blocks eight times smaller cost 34 129 bytes of extra per-block
bookkeeping and buy eight times finer pruning — which is the trade to think about, and
[filter-rows.md](filter-rows.md) is where it pays off.

## Scanning

The scan builder's options are per scan — `WithMaxBatchRows`, `WithPruning`, `WithIndexes`,
`WithMetrics`, `WithDegreeOfParallelism` — with one exception:

```csharp
ScanBuilder.DefaultDegreeOfParallelism = 4;   // process-wide, default 1
```

It is a static, it is read once when a builder is constructed, and a per-scan
`WithDegreeOfParallelism` wins over it. `threads.md` says what to expect from it.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- options
```
