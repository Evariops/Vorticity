using System;
using Vorticity.Types;
using Vorticity.File;

namespace Vorticity;

/// <summary>What an open does with a file whose tail does not parse.</summary>
public enum VortexTornTailPolicy
{
    /// <summary>
    /// The default: a file that begins as a Vortex file and whose tail does not parse opens at the
    /// last whole version before it -- what a torn append leaves -- and says so in
    /// <see cref="VortexFile.TornTail"/>.
    /// </summary>
    ReadPrevious = 0,

    /// <summary>The tail must parse, or the open fails.</summary>
    Refuse = 1,
}

/// <summary>
/// Options for <see cref="VortexFile.OpenAsync(string, VortexOpenOptions, System.Threading.CancellationToken)"/>.
/// Every option here but <see cref="TornTail"/> exists to remove input/output: a supplied
/// <see cref="DType"/> drops the dtype segment from the second-read decision, a supplied
/// <see cref="FileLength"/> drops the length probe, and a raised <see cref="InitialReadSize"/> can
/// only reduce round trips. <see cref="TornTail"/> is the one that can add reads, and only on a
/// file that would otherwise not open at all.
/// </summary>
public sealed class VortexOpenOptions
{
    private readonly long _fileLength = -1;
    private readonly int _initialReadSize = VortexFileFormat.InitialReadSize;
    private readonly VortexReadOptions _read = VortexReadOptions.Default;

    /// <summary>The defaults: probe the length, read 65535 tail bytes, own the source.</summary>
    public static VortexOpenOptions Default { get; } = new VortexOpenOptions();

    /// <summary>
    /// The file's DType, supplied out of band.
    /// </summary>
    /// <remarks>
    /// <b>Required</b> for a file written with its dtype segment excluded. When both this and an
    /// embedded dtype segment are present, <b>this wins</b>: the segment is never read, never
    /// parsed, and is excluded from the second-read decision. There is deliberately no consistency
    /// check and no warning, since a check would cost the very read that supplying a DType exists
    /// to avoid. Leave it <c>default</c> to read the embedded one.
    /// </remarks>
    public DType DType { get; init; }

    /// <summary>
    /// The file length in bytes, when the caller already knows it. <c>-1</c> (the default) issues
    /// one length probe.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below <c>-1</c>.</exception>
    public long FileLength
    {
        get => _fileLength;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -1);
            _fileLength = value;
        }
    }

    /// <summary>
    /// Bytes to read from the tail before parsing. Floored at
    /// <see cref="VortexFileFormat.InitialReadSize"/> (65535) and clamped to the file; raising it
    /// can only reduce round trips.
    /// </summary>
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

    /// <summary>
    /// When <see langword="true"/>, disposing the file leaves the segment source open. Ignored by
    /// the path-based overloads, which always own the source they created.
    /// </summary>
    public bool LeaveSourceOpen { get; init; }

    /// <summary>Read-time policy for every scan of the opened file. Never <see langword="null"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public VortexReadOptions Read
    {
        get => _read;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _read = value;
        }
    }

    /// <summary>
    /// What to do with a file whose tail does not parse: open the last whole version before it, the
    /// default, or refuse.
    /// </summary>
    /// <remarks>
    /// An in-place append is not atomic: a tear leaves the old file whole before the torn bytes.
    /// Reading that version is safe -- it is a file that was complete
    /// -- and <see cref="VortexFile.TornTail"/> says it happened. Finding it walks back from the end
    /// for an end-of-file record, so it costs reads in proportion to the torn bytes; a file that does
    /// not begin with the Vortex magic is refused without the walk.
    /// </remarks>
    public VortexTornTailPolicy TornTail { get; init; }

    /// <summary>
    /// Whether the open also reads the index directory -- its fragments included -- rather than the
    /// first scan that needs it. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// For an object store, where a lazy read is one more round trip in the middle of a query. The
    /// directory usually lies inside the tail the open reads anyway, and then preloading it costs no
    /// request at all; <see cref="VortexFile.Indexes"/> answers from it without one.
    /// </remarks>
    public bool PreloadIndexes { get; init; }

    /// <summary>These options for the first <paramref name="fileLength"/> bytes, refusing a torn tail there.</summary>
    /// <param name="fileLength">The prefix's length.</param>
    internal VortexOpenOptions ForPrefix(long fileLength) => new VortexOpenOptions
    {
        DType = DType,
        FileLength = fileLength,
        InitialReadSize = InitialReadSize,
        LeaveSourceOpen = LeaveSourceOpen,
        Read = Read,
        TornTail = VortexTornTailPolicy.Refuse,
        PreloadIndexes = PreloadIndexes,
    };
}
