using System;

namespace Vorticity.Parquet;

/// <summary>How a Parquet file is opened and read.</summary>
public sealed record ParquetOpenOptions
{
    private readonly long _maxDecompressedBytes = VortexLimits.DefaultMaxDecompressedBytes;
    private readonly int _maxFooterBytes = 256 * 1024 * 1024;

    /// <summary>The options an open takes when it is given none.</summary>
    public static ParquetOpenOptions Default { get; } = new();

    /// <summary>
    /// The most bytes a footer may take, which the open reads and holds whole: 256 MiB by default.
    /// A footer's length is a file's own claim, and this keeps a forged one from sizing a read.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int MaxFooterBytes
    {
        get => _maxFooterBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxFooterBytes = value;
        }
    }

    /// <summary>
    /// The most bytes one page may decode to, and one column of a batch: a file's declared sizes are
    /// held to it before anything is allocated. <see cref="VortexLimits.DefaultMaxDecompressedBytes"/>
    /// by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxDecompressedBytes
    {
        get => _maxDecompressedBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecompressedBytes = value;
        }
    }

    /// <summary>
    /// Whether each page a scan decodes is held to its checksum, where its writer gave it one: a page
    /// whose bytes do not match fails the scan with a <see cref="ParquetFormatException"/>. Off by
    /// default, since a check reads every byte of a page where a view of it would not.
    /// </summary>
    public bool VerifyChecksums { get; init; }

    /// <summary>
    /// The keys of a file encrypted by the standard's modular encryption, and the identity it is held
    /// to; null to read a plaintext file, or a file's plaintext footer and columns.
    /// </summary>
    public ParquetDecryption? Decryption { get; init; }
}
