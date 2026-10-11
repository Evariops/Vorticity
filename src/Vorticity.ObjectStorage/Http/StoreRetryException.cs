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
    internal StoreRetryException(string reason, int cost, Exception? innerException = null)
        : base(reason, innerException) => Cost = cost;

    /// <summary>The tokens of the retry budget the retry spends.</summary>
    internal int Cost { get; }
}
