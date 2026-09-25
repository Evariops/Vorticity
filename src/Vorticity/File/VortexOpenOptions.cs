using System;
using System.Collections.Immutable;
using Vorticity.File;
using Vorticity.Types;

namespace Vorticity;

/// <summary>What an open does with a file whose tail does not parse.</summary>
public enum VortexTornTailPolicy : byte
{
    /// <summary>
    /// The default: a file that begins as a Vortex file and whose tail does not parse opens at the
    /// last whole version before it -- what a torn append leaves -- and says so in
    /// <see cref="VortexFile.TornTail"/>.
    /// </summary>
    /// <remarks>
    /// The version is looked for from the end backwards, a mebibyte at a time, trying at most 16 end
    /// records: a file whose only footer is damaged is read to its first byte before the open fails.
    /// </remarks>
    ReadPrevious = 0,

    /// <summary>The tail must parse, or the open fails with <see cref="VortexFormatException"/>.</summary>
    Refuse = 1,
}

/// <summary>What an open reads, trusts and refuses.</summary>
/// <remarks>
/// Every option here but <see cref="TornTail"/> exists to remove input/output or to bound work: a
/// supplied <see cref="Schema"/> drops the dtype segment from the tail read, a supplied
/// <see cref="Length"/> drops the length probe, and a raised <see cref="InitialReadSize"/> can only
/// reduce round trips.
/// </remarks>
public sealed record VortexOpenOptions
{
    private readonly int _initialReadSize = 65_536;

    /// <summary>The file length, or -1 for unknown.</summary>
    private readonly long _length = -1;
    private readonly long _maxDecompressedSize = VortexLimits.DefaultMaxDecompressedSize;

    /// <summary>The batch ceiling, or 0 for none, a value no ceiling takes: a nullable would widen the record.</summary>
    private readonly long _maxBatchDecompressedSize;
    private readonly VortexReadOptions? _read;

    /// <summary>The defaults.</summary>
    internal static VortexOpenOptions Default { get; } = new VortexOpenOptions();

    /// <summary>Bytes read from the tail at open; floored at 64 KiB and clamped to the file.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int InitialReadSize
    {
        get => _initialReadSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _initialReadSize = value;
        }
    }

    /// <summary>The file length, when the caller knows it; null probes the source.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long? Length
    {
        get => _length < 0 ? null : _length;
        init
        {
            if (value is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A file length is not negative.");
            }

            _length = value ?? -1;
        }
    }

    /// <summary>What to do with a file whose tail does not parse: open the last whole version before it, or refuse.</summary>
    public VortexTornTailPolicy TornTail { get; init; } = VortexTornTailPolicy.ReadPrevious;

    /// <summary>Whether a statistic is checked against what is decoded rather than trusted; a statistic is a claim.</summary>
    public bool VerifyStatistics { get; init; }

    /// <summary>The schema of a file written without one, or to skip reading the one it embeds; it wins over the file's.</summary>
    public VortexSchema? Schema { get; init; }

    /// <summary>The most bytes one decode may produce; a decode is a block.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxDecompressedSize
    {
        get => _maxDecompressedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecompressedSize = value;
        }
    }

    /// <summary>The most bytes the decodes of one batch may produce together, across its columns; null, the default, sets no such ceiling.</summary>
    /// <remarks>
    /// <see cref="MaxDecompressedSize"/> bounds each decode, so a file of many columns, each declared
    /// to decode near that ceiling, bounds a batch only at the ceiling times the columns. A service
    /// that reads files it does not trust sets this as well; a batch past it throws
    /// <see cref="VortexFormatException"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long? MaxBatchDecompressedSize
    {
        get => _maxBatchDecompressedSize == 0 ? null : _maxBatchDecompressedSize;
        init
        {
            if (value is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A ceiling is positive.");
            }

            _maxBatchDecompressedSize = value ?? 0;
        }
    }

    /// <summary>Index fragments built for this file elsewhere, consulted with its own indexes.</summary>
    public ImmutableArray<IndexFragment> IndexFragments { get; init; }

    /// <summary>The file's dtype, from <see cref="Schema"/>, or default to read the embedded one.</summary>
    internal DType DType { get; init; }

    /// <summary>These options with <see cref="DType"/> built from <see cref="Schema"/>, once, before the open reads it.</summary>
    internal VortexOpenOptions Resolved() =>
        !DType.IsDefault || Schema is null ? this : this with { DType = VortexTypes.ToDType(Schema, new DTypeArena()) };

    /// <summary><see cref="Length"/> as the engine reads it: -1 for unknown.</summary>
    internal long FileLength
    {
        get => _length;
        init => _length = value < 0 ? -1 : value;
    }

    /// <summary>Whether disposing the file leaves a caller's source open.</summary>
    internal bool LeaveSourceOpen { get; init; }

    /// <summary>Whether the open also reads the index directory, rather than the first scan that needs it.</summary>
    internal bool PreloadIndexes { get; init; }

    /// <summary>The bytes the index run cache of the file may hold.</summary>
    internal long IndexCacheBytes { get; init; } = VortexReadOptions.DefaultIndexCacheBytes;

    /// <summary>Read-time policy for every scan of the opened file.</summary>
    /// <remarks>
    /// Options that change none of the read policy's defaults share <see cref="VortexReadOptions.Default"/>,
    /// so that an open, which asks for it more than once, allocates no policy of its own.
    /// </remarks>
    internal VortexReadOptions Read
    {
        get => _read ?? (ReadsAsDefault
            ? VortexReadOptions.Default
            : new VortexReadOptions
            {
                MaxDecompressedSize = MaxDecompressedSize,
                MaxBatchDecompressedSize = MaxBatchDecompressedSize ?? long.MaxValue,
                VerifyStatistics = VerifyStatistics,
                IndexCacheBytes = IndexCacheBytes,
                IndexFragments = Fragments(IndexFragments),
            });
        init => _read = value;
    }

    /// <summary>Whether every value the read policy takes from these options is its default.</summary>
    private bool ReadsAsDefault =>
        MaxDecompressedSize == VortexLimits.DefaultMaxDecompressedSize
        && MaxBatchDecompressedSize is null
        && !VerifyStatistics
        && IndexCacheBytes == VortexReadOptions.DefaultIndexCacheBytes
        && IndexFragments.IsDefaultOrEmpty;

    /// <summary>These options for the first <paramref name="fileLength"/> bytes, refusing a torn tail there.</summary>
    /// <param name="fileLength">The prefix's length.</param>
    internal VortexOpenOptions ForPrefix(long fileLength) => this with
    {
        FileLength = fileLength,
        TornTail = VortexTornTailPolicy.Refuse,
    };

    private static ReadOnlyMemory<byte>[] Fragments(ImmutableArray<IndexFragment> fragments)
    {
        if (fragments.IsDefaultOrEmpty)
        {
            return [];
        }

        ReadOnlyMemory<byte>[] bytes = new ReadOnlyMemory<byte>[fragments.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = fragments[i].Bytes;
        }

        return bytes;
    }
}
