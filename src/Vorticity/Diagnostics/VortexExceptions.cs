using System;

namespace Vorticity;

/// <summary>
/// Thrown when a Vortex file violates the format: bad offsets, truncation, out-of-range
/// class I semantic fields, or an exceeded resource cap (<see cref="VortexLimits"/>).
/// </summary>
/// <remarks>
/// This is half of what the library promises about hostile input: a malformed file produces this
/// exception or <see cref="VortexUnsupportedException"/>, never an out-of-bounds access, an
/// unbounded allocation, or a hang.
/// </remarks>
public sealed class VortexFormatException : Exception
{
    public VortexFormatException(string message) : base(message) { }

    public VortexFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a file requires a component (array encoding, layout, dtype extension, ...) that
/// this library does not implement.
/// </summary>
/// <remarks>
/// The message <em>always</em> names both the component id and its kind, because that pair is
/// what someone needs to find which format edition introduced the component and which library
/// version is required to read it.
/// </remarks>
public sealed class VortexUnsupportedException : Exception
{
    public VortexUnsupportedException(string componentId, string kind)
        : base($"Unsupported Vortex component: {kind} '{componentId}'. " +
               "This id is not implemented by Vorticity; check the edition that introduced it " +
               "and the minimum library version required to read it.")
    {
        ComponentId = componentId;
        Kind = kind;
    }

    public VortexUnsupportedException(string componentId, string kind, string detail)
        : base($"Unsupported Vortex component: {kind} '{componentId}'. {detail}")
    {
        ComponentId = componentId;
        Kind = kind;
    }

    /// <summary>The component id as it appears in the file, e.g. <c>"vortex.pco"</c>.</summary>
    public string ComponentId { get; }

    /// <summary>The component kind: <c>"array"</c>, <c>"layout"</c>, <c>"dtype"</c>, <c>"aggregate"</c>, <c>"compression"</c>.</summary>
    public string Kind { get; }
}

/// <summary>Component kind strings used by <see cref="VortexUnsupportedException"/>.</summary>
public static class VortexComponentKind
{
    public const string Array = "array";
    public const string Layout = "layout";
    public const string DType = "dtype";
    public const string Aggregate = "aggregate";
    public const string Compression = "compression";
    public const string Encryption = "encryption";
}
