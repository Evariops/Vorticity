using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Vorticity.Parquet.Metadata;
using Vorticity.Zstd;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// Every codec a page can be compressed with, behind one decompression and one compression: the
/// raw data of a page fed whole, with no framing, as the standard asks of all but the deprecated
/// LZ4.
/// </summary>
/// <remarks>
/// <para>
/// A decompression writes into a destination of exactly the page's uncompressed length, which the
/// caller has held to its cap before allocating it, and a page that decompresses to any other length
/// is <see cref="ParquetFormatException"/>. ZSTD goes through <c>Vorticity.Zstd</c>, with the
/// decompressor the caller's scan holds; SNAPPY and LZ4_RAW are this package's; GZIP and BROTLI are
/// the base class library's. LZO and the deprecated LZ4 have no format the standard gives, and are
/// refused, as is a codec number the standard does not define.
/// </para>
/// <para>
/// GZIP is the one codec that allocates per page: the base class library reads it through a stream.
/// </para>
/// </remarks>
internal static class PageCodecs
{
    /// <summary>Decompresses a page's bytes into all of <paramref name="destination"/>.</summary>
    internal static void Decompress(CompressionCodec codec, ReadOnlySpan<byte> source, Span<byte> destination, ZstdDecompressor? zstd)
    {
        switch (codec)
        {
            case CompressionCodec.Uncompressed:
                if (source.Length != destination.Length)
                {
                    ParquetThrow.Format($"An uncompressed page holds {source.Length} bytes where its header declares {destination.Length}.");
                }

                source.CopyTo(destination);
                return;
            case CompressionCodec.Snappy:
                Snappy.Decompress(source, destination);
                return;
            case CompressionCodec.Lz4Raw:
                Lz4Block.Decompress(source, destination);
                return;
            case CompressionCodec.Zstd:
                DecompressZstd(source, destination, zstd ?? throw new ArgumentNullException(nameof(zstd)));
                return;
            case CompressionCodec.Gzip:
                DecompressGzip(source, destination);
                return;
            case CompressionCodec.Brotli:
                if (!BrotliDecoder.TryDecompress(source, destination, out int written) || written != destination.Length)
                {
                    ParquetThrow.Format("A Brotli page is corrupt, or decompresses to another length than its header declares.");
                }

                return;
            default:
                Refuse(codec);
                return;
        }
    }

    /// <summary>
    /// The page's frames decompressed one after another: the standard says nothing of how many a page
    /// holds, so a reader takes every one, and the total must be the declared length.
    /// </summary>
    private static void DecompressZstd(ReadOnlySpan<byte> source, Span<byte> destination, ZstdDecompressor zstd)
    {
        int read = 0;
        int written = 0;
        while (read < source.Length)
        {
            OperationStatus status = zstd.Decompress(source[read..], destination[written..], out int consumed, out int produced);
            if (status != OperationStatus.Done)
            {
                ParquetThrow.Format(status == OperationStatus.DestinationTooSmall
                    ? "A ZSTD page decompresses to more than its header declares."
                    : "A ZSTD page is corrupt.");
            }

            read += consumed;
            written += produced;
        }

        if (written != destination.Length)
        {
            ParquetThrow.Format("A ZSTD page decompresses to fewer bytes than its header declares.");
        }
    }

    private static unsafe void DecompressGzip(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            // Even an empty member has a header and a trailer; no bytes at all hold no member.
            if (!destination.IsEmpty)
            {
                ParquetThrow.Format("A GZIP page holds no member.");
            }

            return;
        }

        fixed (byte* input = source)
        {
            using UnmanagedMemoryStream stream = new(input, source.Length);
            using GZipStream gzip = new(stream, CompressionMode.Decompress);
            int written = 0;
            try
            {
                while (written < destination.Length)
                {
                    int read = gzip.Read(destination[written..]);
                    if (read == 0)
                    {
                        break;
                    }

                    written += read;
                }

                Span<byte> probe = stackalloc byte[1];
                if (written != destination.Length || gzip.Read(probe) != 0)
                {
                    ParquetThrow.Format("A GZIP page decompresses to another length than its header declares.");
                }
            }
            catch (InvalidDataException exception)
            {
                throw new ParquetFormatException("A GZIP page is corrupt.", exception);
            }
        }
    }

    /// <summary>The most bytes <paramref name="length"/> bytes compress to under <paramref name="codec"/>.</summary>
    internal static int MaxCompressedLength(CompressionCodec codec, int length) => codec switch
    {
        CompressionCodec.Uncompressed => length,
        CompressionCodec.Snappy => Snappy.MaxCompressedLength(length),
        CompressionCodec.Lz4Raw => Lz4Block.MaxCompressedLength(length),
        CompressionCodec.Zstd => ZstdCompressor.GetMaxCompressedLength(length),
        CompressionCodec.Brotli => BrotliEncoder.GetMaxCompressedLength(length),

        // Deflate stores incompressible input in blocks of at most 65 535 bytes, five bytes of
        // header each, and GZIP adds 18 bytes of its own.
        CompressionCodec.Gzip => length + 5 * (length / 16_383 + 1) + 64,
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "No codec this library writes."),
    };

    /// <summary>
    /// Compresses a page's bytes; <paramref name="zstd"/> is the compressor of the level the writer
    /// chose, and <paramref name="level"/> the level of the others that take one.
    /// </summary>
    internal static int Compress(CompressionCodec codec, int level, ReadOnlySpan<byte> source, Span<byte> destination, ZstdCompressor? zstd)
    {
        switch (codec)
        {
            case CompressionCodec.Uncompressed:
                source.CopyTo(destination);
                return source.Length;
            case CompressionCodec.Snappy:
                return Snappy.Compress(source, destination);
            case CompressionCodec.Lz4Raw:
                return Lz4Block.Compress(source, destination);
            case CompressionCodec.Zstd:
                OperationStatus status = (zstd ?? throw new ArgumentNullException(nameof(zstd))).Compress(source, destination, out _, out int written);
                if (status != OperationStatus.Done)
                {
                    throw new InvalidOperationException($"A ZSTD compression of {source.Length} bytes ended {status}.");
                }

                return written;
            case CompressionCodec.Brotli:
                if (!BrotliEncoder.TryCompress(source, destination, out int brotli, Math.Clamp(level, 0, 11), 22))
                {
                    throw new InvalidOperationException("A Brotli compression did not fit its bound.");
                }

                return brotli;
            case CompressionCodec.Gzip:
                return CompressGzip(level, source, destination);
            default:
                throw new ArgumentOutOfRangeException(nameof(codec), codec, "No codec this library writes.");
        }
    }

    private static unsafe int CompressGzip(int level, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        CompressionLevel compression = level switch
        {
            <= 0 => CompressionLevel.Fastest,
            >= 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };
        fixed (byte* output = destination)
        {
            using UnmanagedMemoryStream stream = new(output, 0, destination.Length, FileAccess.Write);
            using (GZipStream gzip = new(stream, compression, leaveOpen: true))
            {
                gzip.Write(source);
            }

            return (int)stream.Position;
        }
    }

    private static void Refuse(CompressionCodec codec)
    {
        switch (codec)
        {
            case CompressionCodec.Lzo:
                ParquetThrow.Unsupported("LZO", ParquetComponentKind.Codec, "The standard gives no format for it, only a library to be compatible with.");
                break;
            case CompressionCodec.Lz4:
                ParquetThrow.Unsupported("LZ4", ParquetComponentKind.Codec, "The deprecated LZ4 codec carries a framing the standard does not document; LZ4_RAW is the interoperable one.");
                break;
            default:
                ParquetThrow.Unsupported(((int)codec).ToString(CultureInfo.InvariantCulture), ParquetComponentKind.Codec);
                break;
        }
    }
}
