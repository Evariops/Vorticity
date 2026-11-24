using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// Bearer tokens from a source, kept until five minutes before they expire or until the service refuses
/// one, and renewed then by one caller while the others keep using the current one.
/// </summary>
internal sealed class RenewingToken : TokenSource
{
    private readonly Renewing<AccessToken> _token;
    private readonly string _named;

    /// <summary>Tokens from <paramref name="source"/>, which messages call <paramref name="named"/>.</summary>
    internal RenewingToken(Func<CancellationToken, ValueTask<AccessToken>> source, string named, TimeProvider time)
    {
        _token = new Renewing<AccessToken>(source, static token => token.ExpiresOn, time);
        _named = named;
    }

    internal override async ValueTask<string?> GetAsync(CancellationToken cancellationToken) =>
        (await _token.GetAsync(cancellationToken).ConfigureAwait(false)).Value;

    internal override void Expired(string token) =>
        _token.Expired(token, static (held, refused) => string.Equals(held.Value, refused, StringComparison.Ordinal));

    /// <summary>The tokens as a message may name them: their source and expiry, never their value.</summary>
    public override string ToString() => _token.TryPeek(out AccessToken current) ? $"{_named} (one expiring {current.ExpiresOn:O})" : _named;
}

/// <summary>
/// A credential from a source, kept until five minutes before it expires and renewed then in the
/// background while every request keeps using it; a renewal that fails is tried again after a pause
/// until the credential actually expires. A request waits for a renewal only when no credential is
/// held or the one held has expired, and its cancellation ends its wait, never the renewal others wait
/// for. A credential replaced may still be signing a request: it is left to the collector, never wiped.
/// </summary>
/// <remarks>
/// A request that renewed in the margin itself waited out the identity service's timeout when the service did not answer,
/// 5 to 30 seconds with a valid credential in hand, and the next request did the same at once: through an outage of the
/// service, a request of data was always held.
/// </remarks>
internal sealed class Renewing<T>(Func<CancellationToken, ValueTask<T>> source, Func<T, DateTimeOffset> expiresOn, TimeProvider time)
{
    /// <summary>How long before its expiry a credential is renewed.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a renewal failed in the margin the next one waits: Azure.Core's own delay before it retries a token's
    /// refresh, within a margin of five minutes alike, so that an outage costs the service a call a request every 30 s.
    /// </summary>
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new Lock();
    private Held? _held;

    // The renewal under way, or the last one; and, after one failed in the margin, when the next may start.
    private Task<Renewal>? _renewal;
    private DateTimeOffset _retryAt;

    /// <summary>The current credential, renewed when it is due.</summary>
    internal async ValueTask<T> GetAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        Held? current = Volatile.Read(ref _held);
        if (current is not null && current.ExpiresOn > now + Margin)
        {
            return current.Value;
        }

        // Within the margin: the one held serves, and a renewal starts behind it unless one runs or a failed one asked
        // for a pause.
        if (current is not null && current.ExpiresOn > now)
        {
            // A source that answers at once has renewed it already: the fresh one serves this request too.
            StartRenewal(now);
            return (Volatile.Read(ref _held) ?? current).Value;
        }

        // None held, or expired: the request waits for a renewal, a pause or not, its cancellation ending its wait alone.
        Renewal renewal = await RenewalAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (renewal.Failure is { } failure)
        {
            failure.Throw();
        }

        return renewal.Held!.Value;
    }

    /// <summary>Starts a renewal behind the credential held, unless one runs or a failed one's pause lasts.</summary>
    private void StartRenewal(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_renewal is not { IsCompleted: false } && now >= _retryAt)
            {
                // Kept, and awaited by the next request that needs it: its failure is a value, never thrown at nobody.
                _renewal = RenewAsync();
            }
        }
    }

    /// <summary>The renewal under way, or one started now.</summary>
    private Task<Renewal> RenewalAsync()
    {
        lock (_gate)
        {
            if (_renewal is { IsCompleted: false } running)
            {
                return running;
            }

            _renewal = RenewAsync();
            return _renewal;
        }
    }

    /// <summary>
    /// Takes a credential from the source, on a token of its own: what it failed with is kept for the requests that wait,
    /// never thrown at a task nobody awaits.
    /// </summary>
    private async Task<Renewal> RenewAsync()
    {
        try
        {
            T fresh = await source(CancellationToken.None).ConfigureAwait(false);
            Held held = new Held(fresh, expiresOn(fresh));
            Volatile.Write(ref _held, held);
            return new Renewal(held, null);
        }
        catch (Exception failed)
        {
            lock (_gate)
            {
                _retryAt = time.GetUtcNow() + RetryPause;
            }

            return new Renewal(null, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed));
        }
    }

    private sealed record Renewal(Held? Held, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure);

    /// <summary>
    /// Drops the credential held when <paramref name="matches"/> says it is the one the service refused,
    /// so that the next request takes a new one; a credential renewed since the refused request was
    /// signed is kept.
    /// </summary>
    internal void Expired<TArg>(TArg refused, Func<T, TArg, bool> matches)
    {
        Held? held = Volatile.Read(ref _held);
        if (held is not null && matches(held.Value, refused))
        {
            Interlocked.CompareExchange(ref _held, null, held);
        }
    }

    /// <summary>The credential held, for a message; false when none is.</summary>
    internal bool TryPeek([MaybeNullWhen(false)] out T value)
    {
        if (Volatile.Read(ref _held) is { } held)
        {
            value = held.Value;
            return true;
        }

        value = default;
        return false;
    }

    private sealed record Held(T Value, DateTimeOffset ExpiresOn);
}
