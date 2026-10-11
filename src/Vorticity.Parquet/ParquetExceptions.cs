using System;

namespace Vorticity.Parquet;

/// <summary>
/// Thrown when a Parquet file violates the format: bad offsets, truncation, a field memory depends on
/// out of its range, or an exceeded cap.
/// </summary>
/// <remarks>
/// With <see cref="ParquetUnsupportedException"/>, it is everything a hostile Parquet file can make
/// the reader throw: never an out-of-bounds access, an unbounded allocation, or a hang. It derives
/// from <see cref="VortexException"/>, the base of every exception the library throws on purpose.
/// </remarks>
public sealed class ParquetFormatException : VortexException
{
    /// <summary>A format violation described by <paramref name="message"/>.</summary>
    /// <param name="message">What the bytes say that the format does not allow.</param>
    public ParquetFormatException(string message)
        : base(message)
    {
    }

    /// <summary>A format violation described by <paramref name="message"/>, found through <paramref name="innerException"/>.</summary>
    /// <param name="message">What the bytes say that the format does not allow.</param>
    /// <param name="innerException">The cause.</param>
    public ParquetFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a Parquet file needs a component this library does not implement: an encoding, a
/// codec, a logical type, a page type, a file version, encryption, or a feature.
/// </summary>
/// <remarks>
/// The message names the component's kind and its id as the standard spells it. A component is
/// refused only by the read that needs it: a column whose pages use an unknown encoding fails when
/// it is decoded, and the file's other columns still read.
/// </remarks>
public sealed class ParquetUnsupportedException : VortexException
{
    /// <summary>A refusal of <paramref name="componentId"/>, which this library does not implement.</summary>
    /// <param name="componentId">The component's id, as the standard names it.</param>
    /// <param name="kind">What kind of component it is.</param>
    public ParquetUnsupportedException(string componentId, ParquetComponentKind kind)
        : base($"Unsupported Parquet {Describe(kind)} '{componentId}'.")
    {
        ComponentId = componentId;
        Kind = kind;
    }

    /// <summary>A refusal of <paramref name="componentId"/>, with why and what would work.</summary>
    /// <param name="componentId">The component's id, as the standard names it.</param>
    /// <param name="kind">What kind of component it is.</param>
    /// <param name="detail">Why, and what would work.</param>
    public ParquetUnsupportedException(string componentId, ParquetComponentKind kind, string detail)
        : base($"Unsupported Parquet {Describe(kind)} '{componentId}'. {detail}")
    {
        ComponentId = componentId;
        Kind = kind;
    }

    /// <summary>The component's id, as the standard names it, e.g. <c>"LZO"</c>.</summary>
    public string ComponentId { get; }

    /// <summary>What kind of component <see cref="ComponentId"/> names.</summary>
    public ParquetComponentKind Kind { get; }

    private static string Describe(ParquetComponentKind kind) => kind switch
    {
        ParquetComponentKind.Encoding => "encoding",
        ParquetComponentKind.Codec => "compression codec",
        ParquetComponentKind.LogicalType => "logical type",
        ParquetComponentKind.PhysicalType => "physical type",
        ParquetComponentKind.PageType => "page type",
        ParquetComponentKind.Version => "file version",
        ParquetComponentKind.Encryption => "encryption",
        _ => "feature",
    };
}

/// <summary>Which kind of component a <see cref="ParquetUnsupportedException"/> refuses.</summary>
public enum ParquetComponentKind : byte
{
    /// <summary>A page encoding, such as <c>BYTE_STREAM_SPLIT</c>.</summary>
    Encoding = 0,

    /// <summary>A compression codec, such as <c>LZO</c>.</summary>
    Codec = 1,

    /// <summary>A logical type annotation, such as <c>VARIANT</c>.</summary>
    LogicalType = 2,

    /// <summary>A physical type.</summary>
    PhysicalType = 3,

    /// <summary>A page type.</summary>
    PageType = 4,

    /// <summary>A file version other than 1 and 2.</summary>
    Version = 5,

    /// <summary>Modular encryption.</summary>
    Encryption = 6,

    /// <summary>A capability this library does not offer for this input.</summary>
    Feature = 7,
}
