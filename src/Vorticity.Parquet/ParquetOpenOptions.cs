using System;

namespace Vorticity.Parquet;

/// <summary>How a Parquet file is opened and read.</summary>
public sealed record ParquetOpenOptions
{
    private readonly long _maxDecompressedSize = VortexLimits.DefaultMaxDecompressedBytes;
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
    public long MaxDecompressedSize
    {
        get => _maxDecompressedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxDecompressedSize = value;
        }
    }
}
