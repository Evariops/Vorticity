using System;

namespace Vorticity;

/// <summary>What an encoding advice ranks its candidates by.</summary>
public enum EncodingObjective : byte
{
    /// <summary>The time a read takes: the bytes at the storage's throughput, then their decoding.</summary>
    ReadTime,

    /// <summary>
    /// The bytes alone, the time to scan breaking a tie; the candidates are written under
    /// <see cref="CompressionProfile.Smallest"/>, and the options the advice gives set it.
    /// </summary>
    Size,
}

/// <summary>
/// The reads a file will serve, which <see cref="VortexSession.AdviseAsync(VortexFile, EncodingGoal?, System.Threading.CancellationToken)"/>
/// ranks each column's encodings for.
/// </summary>
public sealed record EncodingGoal
{
    /// <summary>The time to read, by whole scans, from a local drive.</summary>
    public static EncodingGoal Default { get; } = new();

    /// <summary>The fewest bytes, whatever they cost to decode.</summary>
    public static EncodingGoal Smallest { get; } = new() { Objective = EncodingObjective.Size };

    /// <summary>What the candidates are ranked by.</summary>
    public EncodingObjective Objective { get; init; } = EncodingObjective.ReadTime;

    /// <summary>
    /// The throughput the file is read at, in bytes per second: the storage's, or the link's to it.
    /// The default is a local drive's; an object store read one stream at a time delivers about a
    /// tenth of a gigabyte, the page cache ten or more.
    /// </summary>
    /// <remarks>
    /// The advice times a decode on one thread. A scan that decodes on several shares the storage
    /// between them: give the throughput divided by the threads.
    /// </remarks>
    public long StorageBytesPerSecond { get; init; } = 2_000_000_000;

    /// <summary>
    /// How many single-row reads, a lookup by key or by position, the data serves for each whole
    /// scan of it; 0 for data that is only scanned. Each reads the chunk its row lies in, at the
    /// storage's throughput, and decodes it as a take does.
    /// </summary>
    public double LookupsPerScan { get; init; }

    /// <summary>
    /// The rows of each column the advice measures, in windows spread over the data; all of them
    /// when the data holds fewer.
    /// </summary>
    public long SampleRows { get; init; } = 1_048_576;

    /// <summary>Throws when a value cannot describe a goal.</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StorageBytesPerSecond, nameof(StorageBytesPerSecond));
        ArgumentOutOfRangeException.ThrowIfNegative(LookupsPerScan, nameof(LookupsPerScan));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SampleRows, nameof(SampleRows));
        if (!Enum.IsDefined(Objective))
        {
            throw new ArgumentOutOfRangeException(nameof(Objective), Objective, "Not an encoding objective.");
        }
    }
}
