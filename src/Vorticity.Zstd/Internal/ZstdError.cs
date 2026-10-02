using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// Why a frame was refused. The decoder reports a status, not this; the code exists so that a test
/// can tell one refusal from another, and so that a message says which check failed.
/// </summary>
internal enum ZstdError
{
    None = 0,

    // ---- the source ends before the frame does: NeedMoreData
    Truncated,

    // ---- the frame does not fit: DestinationTooSmall
    DestinationTooSmall,

    // ---- the frame is invalid: InvalidData
    UnknownMagic,
    ReservedBitSet,
    WindowTooLarge,
    SkippableFrameTooLarge,
    DictionaryMismatch,
    ReservedBlockType,
    BlockTooLarge,
    ContentSizeMismatch,
    ChecksumMismatch,
    LiteralsHeader,
    LiteralsSizeTooLarge,
    TreelessWithoutTable,
    HuffmanTable,
    HuffmanStream,
    SequencesHeader,
    FseTable,
    RepeatWithoutTable,
    SequenceBitstream,
    LiteralsOverrun,
    OffsetTooLarge,
}

/// <summary>Raised inside the decoder and caught at the API boundary, which turns it into a status.</summary>
internal sealed class ZstdException : Exception
{
    public ZstdException(ZstdError error)
        : base($"Invalid zstd data: {error}.")
    {
        Error = error;
    }

    public ZstdError Error { get; }
}

/// <summary>Cold throw sites: kept out of line so that the hot paths only carry a compare and a call.</summary>
internal static class Throw
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Error(ZstdError error) => throw new ZstdException(error);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Error<T>(ZstdError error) => throw new ZstdException(error);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Dictionary(string reason) =>
        throw new InvalidDataException($"Invalid zstd dictionary: {reason}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ArgumentNull(string name) => throw new ArgumentNullException(name);
}
