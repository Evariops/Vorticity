using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The service's clock as its refusals teach it: the platform's, shifted by the offset of the service's
/// <c>Date</c>, so that a request signed with a date is dated as the service expects.
/// </summary>
/// <param name="time">The platform's clock.</param>
/// <param name="header">The header a request carries its date in, <c>x-amz-date</c> or <c>x-ms-date</c>.</param>
/// <param name="format">That date's format.</param>
internal sealed class ServiceClock(TimeProvider time, string header, string format)
{
    /// <summary>The gap past which a request is taken as refused for its date, under the 15 minutes S3 and Azure tolerate.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private long _offsetTicks;

    /// <summary>The service's time now.</summary>
    internal DateTimeOffset Now => time.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>
    /// Learns the service's clock from the <c>Date</c> of a refusal, and says whether the refused request
    /// was dated more than five minutes off it, read back from the request: dated anew, it is worth
    /// sending again. Every request signed before the clock was learned is, and a request dated close to
    /// the service's clock was refused for another reason, so it is not sent again for its date.
    /// </summary>
    internal bool Skewed(HttpResponseMessage response)
    {
        if (response.Headers.Date is not { } service)
        {
            return false;
        }

        Interlocked.Exchange(ref _offsetTicks, (service - time.GetUtcNow()).Ticks);
        return HttpAnswers.RequestHeader(response, header) is { } value
            && DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset sent)
            && (service - sent).Duration() >= Tolerance;
    }
}
