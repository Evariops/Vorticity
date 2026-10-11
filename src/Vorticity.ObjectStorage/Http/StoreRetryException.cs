using System;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// Thrown by a store's reading of an answer to ask the transport for another attempt: a body cut short,
/// a request the service timed out, a clock it found skewed, credentials it found expired.
/// </summary>
internal sealed class StoreRetryException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">What went wrong, for the message of the last failure.</param>
    /// <param name="cost">The tokens of the retry budget the retry spends; 0 for a retry that corrects the request rather than repeats it.</param>
    /// <param name="innerException">The failure behind it, if any.</param>
    /// <param name="notApplied">
    /// Whether the service refused the request without applying it, so that even a request that may
    /// not be sent twice may be sent again: a conditional put that lost a race with a delete, a body
    /// whose checksum the service found wrong. A retry that costs nothing is never applied either.
    /// </param>
    internal StoreRetryException(string reason, int cost, Exception? innerException = null, bool notApplied = false)
        : base(reason, innerException)
    {
        Cost = cost;
        NotApplied = notApplied || cost == 0;
    }

    /// <summary>The tokens of the retry budget the retry spends.</summary>
    internal int Cost { get; }

    /// <summary>Whether the service refused the request without applying it.</summary>
    internal bool NotApplied { get; }
}

/// <summary>
/// Thrown by the transport when a request that may not be sent twice failed after it may have reached
/// the service: the store then learns from the service what became of it.
/// </summary>
internal sealed class StoreAnswerLostException(string reason, Exception innerException) : Exception(reason, innerException);
