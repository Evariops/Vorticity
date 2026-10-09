using System;

namespace Vorticity;

/// <summary>The base of every exception this library throws on purpose.</summary>
/// <remarks>
/// Argument errors stay <see cref="ArgumentException"/> and misuse stays
/// <see cref="InvalidOperationException"/>: this hierarchy is for what the bytes, the schema or the
/// capabilities of the library say.
/// </remarks>
public class VortexException : Exception
{
    /// <summary>An exception with the default message.</summary>
    public VortexException()
    {
    }

    /// <summary>An exception with <paramref name="message"/>.</summary>
    /// <param name="message">What went wrong.</param>
    public VortexException(string message)
        : base(message)
    {
    }

    /// <summary>An exception with <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public VortexException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a Vortex file violates the format: bad offsets, truncation, out-of-range
/// class I semantic fields, or an exceeded resource cap (<see cref="VortexLimits"/>).
/// </summary>
/// <remarks>
/// This is half of what the library promises about hostile input: a malformed file produces this
/// exception or <see cref="VortexUnsupportedException"/>, never an out-of-bounds access, an
/// unbounded allocation, or a hang.
/// </remarks>
public sealed class VortexFormatException : VortexException
{
    /// <summary>A format violation described by <paramref name="message"/>.</summary>
    /// <param name="message">What the bytes say that the format does not allow.</param>
    public VortexFormatException(string message)
        : base(message)
    {
    }

    /// <summary>A format violation described by <paramref name="message"/>, found through <paramref name="innerException"/>.</summary>
    /// <param name="message">What the bytes say that the format does not allow.</param>
    /// <param name="innerException">The cause.</param>
    public VortexFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a file or a request needs a component (array encoding, layout, dtype extension,
/// index, ...) that this library does not implement, or that the input does not carry.
/// </summary>
/// <remarks>
/// The message always names both the component id and its kind, because that pair is what
/// someone needs to find which format edition introduced the component and which library version
/// is required to read it.
/// </remarks>
public sealed class VortexUnsupportedException : VortexException
{
    /// <summary>A refusal of <paramref name="componentId"/>, which this library does not implement.</summary>
    /// <param name="componentId">The component's id, as the file names it.</param>
    /// <param name="kind">What kind of component it is.</param>
    public VortexUnsupportedException(string componentId, ComponentKind kind)
        : base($"Unsupported Vortex component: {Describe(kind)} '{componentId}'. " +
               "This id is not implemented by Vorticity; check the edition that introduced it " +
               "and the minimum library version required to read it.")
    {
        ComponentId = componentId;
        Kind = kind;
    }

    /// <summary>A refusal of <paramref name="componentId"/>, with what the input lacks or what to do instead.</summary>
    /// <param name="componentId">The component's id, as the file or the request names it.</param>
    /// <param name="kind">What kind of component it is.</param>
    /// <param name="detail">Why, and what would work.</param>
    public VortexUnsupportedException(string componentId, ComponentKind kind, string detail)
        : base($"Unsupported Vortex component: {Describe(kind)} '{componentId}'. {detail}")
    {
        ComponentId = componentId;
        Kind = kind;
    }

    /// <summary>The component id as it appears in the file, e.g. <c>"vortex.pco"</c>.</summary>
    public string ComponentId { get; }

    /// <summary>What kind of component <see cref="ComponentId"/> names.</summary>
    public ComponentKind Kind { get; }

    private static string Describe(ComponentKind kind) => kind switch
    {
        ComponentKind.Array => "array",
        ComponentKind.Layout => "layout",
        ComponentKind.DType => "dtype",
        ComponentKind.Aggregate => "aggregate",
        ComponentKind.Compression => "compression",
        ComponentKind.Encryption => "encryption",
        ComponentKind.Index => "index",
        ComponentKind.Feature => "feature",
        _ => kind.ToString(),
    };
}

/// <summary>
/// Thrown when a record, a builder, a literal or a column request does not fit the schema it is
/// bound to: a missing or ambiguous column, a .NET type the dtype does not map to, or a
/// non-nullable member over a nullable column.
/// </summary>
public sealed class VortexSchemaException : VortexException
{
    /// <summary>A mismatch described by <paramref name="message"/>, naming the member or column and both types.</summary>
    /// <param name="message">What does not fit, and why.</param>
    public VortexSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>A mismatch described by <paramref name="message"/>, found through <paramref name="innerException"/>.</summary>
    /// <param name="message">What does not fit, and why.</param>
    /// <param name="innerException">The cause.</param>
    public VortexSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a query needs more memory than its <see cref="QueryMemoryBudget"/> grants, and nothing it
/// holds can go to disk; or more scratch than its <see cref="ScratchBudget"/> grants, or than its
/// directory's free space leaves. The message names the operator, its groups and the bytes it asked
/// for, and what to change. The query has given back everything it held, its scratch files included.
/// </summary>
public sealed class VortexMemoryException : VortexException
{
    /// <summary>A query out of memory, described by <paramref name="message"/>.</summary>
    /// <param name="message">The operator, its groups, the bytes it held and asked for, and the budget's ceiling.</param>
    public VortexMemoryException(string message)
        : base(message)
    {
    }

    /// <summary>A query out of memory, described by <paramref name="message"/>, found through <paramref name="innerException"/>.</summary>
    /// <param name="message">The operator, its groups, the bytes it held and asked for, and the budget's ceiling.</param>
    /// <param name="innerException">The cause.</param>
    public VortexMemoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The component kinds the library's own refusals name.</summary>
internal static class VortexComponentKind
{
    public const ComponentKind Array = ComponentKind.Array;
    public const ComponentKind Layout = ComponentKind.Layout;
    public const ComponentKind DType = ComponentKind.DType;
    public const ComponentKind Aggregate = ComponentKind.Aggregate;
    public const ComponentKind Compression = ComponentKind.Compression;
    public const ComponentKind Encryption = ComponentKind.Encryption;
    public const ComponentKind Index = ComponentKind.Index;
    public const ComponentKind Feature = ComponentKind.Feature;
}
