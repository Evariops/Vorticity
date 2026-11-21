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

    /// <summary>
    /// The bytes a step of a compaction job lacked, when the refusal is of a block the step had not reserved under an
    /// account that reserves (<see cref="Buffers.CountedAccount"/>): the step reserves them, waiting for the job to make
    /// room, and runs again. 0 for any other refusal, which fails what asked.
    /// </summary>
    internal long ShortOfRoom { get; set; }
}

/// <summary>
/// What a decode throws, in a scan whose consumer reserves its memory, when it meets a chunk another lane of the scan is
/// decoding whole at that moment: its split is run again once that decode is published, and borrows it, rather than
/// decode the chunk too into blocks of its own.
/// </summary>
internal sealed class ChunkBusyException(long key) : Exception("A chunk another lane is decoding was met; the split runs again once it is published.")
{
    /// <summary>The key the chunk is retained under, which the scan's table tells once it is published or given up (<c>RetainedChunks.WhenFreeAsync</c>).</summary>
    internal long Key => key;
}

/// <summary>
/// What a scan whose consumer reserves its memory throws, on the consumer's flow, when a reservation the scan cannot go on
/// without was not granted, the consumer's job having given back all it had started ahead
/// (<see cref="MemoryReserve.RaiseAsync"/>). A compaction's merge ends then at the key it reached and commits what it
/// merged: nothing is thrown away, and nothing waits on memory only that merge holds.
/// </summary>
internal sealed class ReserveExhaustedException(long bytes, Exception? refusal = null) : Exception(
    refusal is null
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A reservation of {bytes:N0} bytes was not granted, everything started ahead given back.")
        : "A block no reservation covers was refused: " + refusal.Message,
    refusal)
{
    /// <summary>The bytes asked for.</summary>
    internal long Bytes => bytes;

    /// <summary>
    /// The bound of the object a merge was opening when the reservation was not granted, which its rows lie at or past;
    /// null when it was not opening one: an input it was taking the next batch of, whose next key nothing says.
    /// </summary>
    internal ReadOnlyMemory<byte>? Opening { get; set; }

    /// <summary>The reserve that did not grant them: its input needs that much past what it held, which the job's next plan counts it at.</summary>
    internal MemoryReserve? Reserve { get; init; }
}

/// <summary>The component kinds the library's own refusals name.</summary>
internal static class VortexComponentKind
{
    public const ComponentKind Array = ComponentKind.Array;
    public const ComponentKind Layout = ComponentKind.Layout;
    public const ComponentKind DType = ComponentKind.DType;
    public const ComponentKind Aggregate = ComponentKind.Aggregate;
    public const ComponentKind Compression = ComponentKind.Compression;
}
