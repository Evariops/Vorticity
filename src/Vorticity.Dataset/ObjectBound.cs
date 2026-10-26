using System;

namespace Vorticity.Dataset;

/// <summary>
/// The bound a rewrite announces to the store for each object it streams: what its inputs weigh, times
/// the most bytes a rewrite writes for each byte it reads, doubled each time the content runs past it.
/// </summary>
/// <remarks>
/// <para>
/// An object the sink streams goes to the store before its length is known. Without a bound, a service
/// sizes its parts for an unknown length, and its parts then reach so far: about a terabyte on Amazon
/// S3, whose parts may grow, 168 GB on Cloudflare R2, whose parts are all of one size, and 106 GB on
/// Scaleway, which takes a thousand parts. A day of rows at a few thousand a second, in one object, is
/// past that, which would fail at its end, after its upload, every time. With a bound, the store
/// sizes its parts from it at the first, or holds the object on disk first, or refuses at once.
/// </para>
/// <para>
/// A bound the content runs past is refused by the store, which is its promise. The sink sees it first,
/// at the write that passes it, and the rewrite starts again under a bound twice as large, which a rare
/// object pays once.
/// </para>
/// </remarks>
internal static class ObjectBound
{
    /// <summary>
    /// The most bytes a rewrite writes for each byte it reads. On the lifecycle bench's five scenes, measured when the
    /// library compacted by periods too (periods of a day and of an hour, with and without late rows, leveled in order and under late
    /// rows), a job wrote 1.024 bytes a byte at worst, a merge of neighbours 1.016 and a seal 1.000.
    /// </summary>
    internal const double OutPerIn = 1.03;

    /// <summary>
    /// The most times a rewrite starts again under a doubled bound: past it the bound is 65 536 times
    /// what a rewrite wrote at worst, which no content reaches but one that lost its way.
    /// </summary>
    internal const int MostDoublings = 16;

    /// <summary>
    /// The bound for objects of a rewrite that reads <paramref name="bytesIn"/> bytes, after
    /// <paramref name="doublings"/> runs past it, at <paramref name="outPerIn"/> bytes a byte read;
    /// never under one byte.
    /// </summary>
    internal static long Of(long bytesIn, int doublings, double outPerIn = OutPerIn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesIn);
        ArgumentOutOfRangeException.ThrowIfNegative(doublings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outPerIn);
        double bound = Math.Max(bytesIn * outPerIn, 1) * Math.Pow(2, doublings);
        return bound >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(bound);
    }
}

/// <summary>An object's content runs past the bound its rewrite announced to the store; the rewrite starts again under a larger one.</summary>
internal sealed class ObjectPastBoundException : VortexException
{
    /// <summary>Creates the exception with a default message.</summary>
    public ObjectPastBoundException()
        : base("An object ran past the bound its rewrite announced.")
    {
    }

    /// <summary>Carries a message.</summary>
    public ObjectPastBoundException(string message)
        : base(message)
    {
    }

    /// <summary>Carries a message and the cause.</summary>
    public ObjectPastBoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
