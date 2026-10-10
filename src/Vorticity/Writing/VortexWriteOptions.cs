using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Vorticity.Indexes;
using Vorticity.Writing;

namespace Vorticity;

/// <summary>How much the writer does beyond the data.</summary>
internal enum WriteProfile
{
    /// <summary>Everything the options ask for.</summary>
    Default = 0,

    /// <summary>
    /// No index, whatever <see cref="VortexWriteOptions.WritePolicy"/> says: byte for byte a write under
    /// <see cref="WritePolicy.None"/> with the same <see cref="VortexWriteOptions.Identity"/>.
    /// </summary>
    Fastest = 1,
}

/// <summary>What the writer optimises for when it prices a column's encodings.</summary>
public enum CompressionProfile : byte
{
    /// <summary>Size and decode speed together, per column chunk.</summary>
    Auto,

    /// <summary>The cheapest encodings to write, and no index.</summary>
    Fastest,

    /// <summary>
    /// Size over decode speed: every column chunk is priced by its bytes alone, run-end among the
    /// other candidates, and zstd, pco, FSST, ALP and ALP-RD are all tried under the bytes of the
    /// best exact plan, with no margin kept for a faster decode, the smallest taken -- on the
    /// column and on every array an encoding makes of it, an ALP's integers, a dictionary's codes
    /// or a run's values, as the reference's compact compressor does. A trial that beats an exact
    /// plan is held to what that plan writes, children included, not to its price. The write costs
    /// more, and a read may decode slower.
    /// </summary>
    Smallest,

    /// <summary>Every column canonical: no encoding at all.</summary>
    None,
}

/// <summary>What one written file looks like: its blocks, encodings, edition, statistics, indexes and metadata.</summary>
public sealed record VortexWriteOptions
{
    private readonly int _blockRows = 8_192;
    private readonly int _chunkTargetBytes;
    private readonly int _stringBoundBytes = 16;
    private readonly int _degreeOfParallelism;
    private readonly IndexPolicy _indexes = IndexPolicy.None;
    private readonly ImmutableDictionary<string, EncodingHint> _hints = ImmutableDictionary<string, EncodingHint>.Empty.WithComparers(StringComparer.Ordinal);
    private readonly ImmutableDictionary<string, int> _columnChunkTargets = ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);
    private readonly ImmutableDictionary<string, ReadOnlyMemory<byte>> _metadata = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.WithComparers(StringComparer.Ordinal);
    private bool _oneChunkPerWrite;
    private bool _noByteTarget;
    private WritePolicy? _writePolicy;
    private int? _budgetPerMille;
    private IKeyEncoder? _keyEncoder;
    private WriteProfile? _profile;

    /// <summary>The defaults.</summary>
    internal static VortexWriteOptions Default { get; } = new VortexWriteOptions();

    /// <summary>Rows per block: the unit of pruning, of a take, and of a filtered scan's batch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int BlockRows
    {
        get => _blockRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _blockRows = value;
        }
    }

    /// <summary>
    /// The bytes gathered before a chunk of whole blocks is sealed at a flush; 0, the default, lets
    /// the writer size its chunks by what they hold.
    /// </summary>
    /// <remarks>
    /// Sized by the writer, a chunk holds as many whole blocks as keep its widest column within a
    /// megabyte of values, between one block and 128, and its rows within 64 MiB. A chunk of a column
    /// is what a read fetches of it, so a selective read fetches little more than it wants, and at
    /// eight bytes a value that is sixteen blocks, which amortizes what a reader pays per chunk.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int ChunkTargetBytes
    {
        get => _chunkTargetBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _chunkTargetBytes = value;
        }
    }

    /// <summary>
    /// The bytes a column's chunks gather, by top-level column name, for a column that wants larger
    /// chunks than the file's: a dictionary, say, paid once for more rows. Its chunks span whole
    /// chunks of the file, and the other columns keep theirs, which is what a read of them fetches.
    /// </summary>
    /// <remarks>
    /// A target no larger than what the column holds in one of the file's chunks changes nothing.
    /// The column's rows wait, as canonical values, until they reach its target, and a flush or the
    /// completion writes what waits. A file whose columns are chunked apart cannot be appended to.
    /// A name that is no top-level column, or a target that is not positive, throws at
    /// <c>CreateWriter</c>.
    /// </remarks>
    public ImmutableDictionary<string, int> ColumnChunkTargetBytes
    {
        get => _columnChunkTargets;
        init => _columnChunkTargets = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>What the writer optimises for; <see cref="CompressionProfile.None"/> writes every column canonically.</summary>
    public CompressionProfile Compression { get; init; } = CompressionProfile.Auto;

    /// <summary>
    /// How many threads the writer summarizes its columns on as the rows come in, and compresses a
    /// column's zstd frames on; 0 for the session's
    /// <see cref="VortexSessionOptions.MaxDegreeOfParallelism"/>. The file is the same bytes
    /// whatever the degree.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int DegreeOfParallelism
    {
        get => _degreeOfParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _degreeOfParallelism = value;
        }
    }

    /// <summary>
    /// The encoding to write a column with, by column path, for a caller who knows. A hint is priced
    /// against each chunk and written when it still applies, under every profile. Only a constant
    /// and an arithmetic progression, which cost nothing per row, are written before it. An unknown
    /// path throws at <c>CreateWriter</c>.
    /// </summary>
    public ImmutableDictionary<string, EncodingHint> Hints
    {
        get => _hints;
        init => _hints = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The edition every component of the file must belong to: which readers must be able to open it.
    /// The default is the edition the most deployed Rust reader accepts, not the newest.
    /// </summary>
    public VortexEdition TargetEdition { get; init; } = VortexEditions.Default;

    /// <summary>Whether the file carries its statistics: per column the null count and the order, and the exact minimum and maximum of a numeric column.</summary>
    public bool WriteStatistics { get; init; } = true;

    /// <summary>
    /// Whether a file this library opens, created or appended to, is on the device when
    /// <c>CompleteAsync</c> returns; false by default.
    /// </summary>
    /// <remarks>
    /// Everything the postscript names reaches the device before the postscript is written, then
    /// the rest, so that a power cut never leaves a postscript over bytes that are not there: an
    /// append cut short is then a torn tail, which an open steps back from. A created file is then
    /// renamed over its path, and after a power cut the path holds either it, whole, or what was
    /// there before; the rename is made durable by the file system's own journal. A sink the caller
    /// brings is the caller's to make durable. Each completion costs two flushes to the device.
    /// </remarks>
    public bool Durable { get; init; }

    /// <summary>The byte limit of the bounds a text or binary column's zones carry, so that text filters prune; 0 for none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int StringBoundBytes
    {
        get => _stringBoundBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _stringBoundBytes = value;
        }
    }

    /// <summary>The indexes to build; <see cref="IndexPolicy.None"/> by default.</summary>
    public IndexPolicy Indexes
    {
        get => _indexes;
        init => _indexes = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The identity the file carries, pinned for a reproducible write: the same input gives the same bytes. Null draws a fresh one.</summary>
    public Guid? Identity { get; init; }

    /// <summary>User metadata the file carries, by key; read back through <c>VortexFile.Metadata</c>.</summary>
    /// <remarks>
    /// At most 14 entries, keys of at most 64 UTF-8 bytes, and none of the keys this library writes
    /// itself; <c>CreateWriter</c> refuses the rest. An append keeps the file's own entries, and an
    /// entry named again takes its new value.
    /// </remarks>
    public ImmutableDictionary<string, ReadOnlyMemory<byte>> Metadata
    {
        get => _metadata;
        init => _metadata = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The index policy the engine writes under: the internal override when set, <see cref="Indexes"/> otherwise.</summary>
    internal WritePolicy WritePolicy
    {
        get => _writePolicy ?? _indexes.ToWritePolicy();
        init => _writePolicy = value;
    }

    /// <summary>How much the writer does beyond the data.</summary>
    internal WriteProfile Profile
    {
        get => _profile ?? (Compression == CompressionProfile.Fastest ? WriteProfile.Fastest : WriteProfile.Default);
        init => _profile = value;
    }

    /// <summary><see cref="Hints"/> as the engine reads them; null when there is none.</summary>
    internal IReadOnlyDictionary<string, EncodingHint>? EncodingHints
    {
        get => _hints.IsEmpty ? null : _hints;
        init => _hints = value is null ? ImmutableDictionary<string, EncodingHint>.Empty : value.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>The bytes the file's indexes may occupy together, per thousand bytes of data.</summary>
    internal int IndexBudgetPerMille
    {
        get => _budgetPerMille ?? _indexes.BudgetPerMille;
        init => _budgetPerMille = value;
    }

    /// <summary>The encoder of the composite keys the index policy asks for.</summary>
    internal IKeyEncoder? KeyEncoder
    {
        get => _keyEncoder ?? _indexes.KeyEncoder;
        init => _keyEncoder = value;
    }

    /// <summary>Where a locating index's chunk runs wait for their merge; null for the system's temporary directory.</summary>
    internal string? ScratchDirectory { get; init; }

    /// <summary>What the chunk runs may hold in memory before they move to <see cref="ScratchDirectory"/>.</summary>
    internal long ScratchMemoryBytes { get; init; } = IndexWriter.DefaultScratchMemoryBytes;

    /// <summary>The row span above which a sorted run writes its rows at 64 bits.</summary>
    internal long WideRowsAbove { get; init; } = uint.MaxValue;

    /// <summary>When a locating run's segment table goes to fence pages, and how large they are.</summary>
    internal FenceShape Fences { get; init; } = FenceShape.Default;

    /// <summary>
    /// Whether the budget spares the required indexes when they alone are over it: they keep
    /// their first claim on it and are written, and the report says what they cost, where the
    /// write would otherwise fail. What an index a structure cannot be read without asks for: a
    /// dataset's run on its clustering key is as large as the column it orders on a narrow table,
    /// so no object of one is ever big enough to bring it under a share of the data.
    /// </summary>
    internal bool BudgetSparesRequired { get; init; }

    /// <summary>Whether the chooser reads a list's elements from their ingest blocks.</summary>
    internal bool ElementStatistics { get; init; } = true;

    /// <summary>Whether the writer may pick an encoding per column chunk.</summary>
    internal bool Compress
    {
        get => Compression != CompressionProfile.None;
        init => Compression = value ? (Compression == CompressionProfile.None ? CompressionProfile.Auto : Compression) : CompressionProfile.None;
    }

    /// <summary><see cref="WriteStatistics"/>, as the engine names it.</summary>
    internal bool FileStatistics
    {
        get => WriteStatistics;
        init => WriteStatistics = value;
    }

    /// <summary>Rows per chunk, as a multiple; null writes one chunk per call, which only the engine's own tests ask for.</summary>
    internal int? RowBlockSize
    {
        get => _oneChunkPerWrite ? null : _blockRows;
        init
        {
            _oneChunkPerWrite = value is null;
            if (value is { } rows)
            {
                BlockRows = rows;
            }
        }
    }

    /// <summary>Canonical bytes to accumulate before a chunk is emitted; null for no byte threshold.</summary>
    internal long? DataBlockTargetBytes
    {
        get => _noByteTarget ? null : _chunkTargetBytes;
        init
        {
            _noByteTarget = value is null;
            if (value is { } bytes)
            {
                ChunkTargetBytes = (int)Math.Min(bytes, int.MaxValue);
            }
        }
    }

    /// <summary>Whether the writer sizes the chunks, no byte target having been set.</summary>
    internal bool AutomaticChunks => !_noByteTarget && _chunkTargetBytes == 0;

    /// <summary>These options with <see cref="Identity"/> pinned to <paramref name="identity"/>.</summary>
    internal VortexWriteOptions WithIdentity(Guid identity) => this with { Identity = identity };

    /// <summary>These options with a different index policy.</summary>
    internal VortexWriteOptions WithIndexes(WritePolicy indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return this with { WritePolicy = indexes };
    }

    /// <summary>These options with a composite-key encoder.</summary>
    internal VortexWriteOptions WithKeyEncoder(IKeyEncoder keyEncoder)
    {
        ArgumentNullException.ThrowIfNull(keyEncoder);
        return this with { KeyEncoder = keyEncoder };
    }

    /// <summary>A copy with the four things an append decides from the file.</summary>
    internal VortexWriteOptions ForAppend(int rowBlockSize, WritePolicy indexes, bool fileStatistics, int budgetPerMille) =>
        this with
        {
            WritePolicy = indexes,
            IndexBudgetPerMille = budgetPerMille,
            FileStatistics = fileStatistics,
            RowBlockSize = rowBlockSize,
        };
}
