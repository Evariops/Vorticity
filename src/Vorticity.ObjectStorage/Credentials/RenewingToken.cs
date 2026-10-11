using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>
/// A bearer token from a source, kept until five minutes before it expires and renewed then by one
/// caller while the others keep using it; a renewal that fails is tried again by a later request until
/// the token actually expires. A request waits for a renewal only when its token has expired.
/// </summary>
internal sealed class RenewingToken(Func<CancellationToken, ValueTask<AccessToken>> source, TimeProvider time) : IDisposable
{
    private readonly Renewing<AccessToken> _token = new Renewing<AccessToken>(source, static token => token.ExpiresOn, TimeSpan.FromMinutes(5), time);

    /// <summary>The current token, renewed when it is due.</summary>
    internal async ValueTask<string> GetAsync(CancellationToken cancellationToken) =>
        (await _token.GetAsync(cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Drops the token the service refused, so that the next request takes a new one.</summary>
    internal void Expired() => _token.Expired();

    /// <summary>When the current token expires, for a message: never its value.</summary>
    internal DateTimeOffset? ExpiresOn => _token.ExpiresOn;

    public void Dispose() => _token.Dispose();
}

/// <summary>
/// A credential from a source, kept until <paramref name="margin"/> before it expires and renewed then by
/// one caller while the others keep using it; a renewal that fails is tried again by a later request
/// until the credential actually expires. A request waits for a renewal only when its credential has
/// expired. A credential replaced is left to its finalizer, since a request may still sign with it.
/// </summary>
internal sealed class Renewing<T>(Func<CancellationToken, ValueTask<T>> source, Func<T, DateTimeOffset> expiresOn, TimeSpan margin, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _renewing = new SemaphoreSlim(1, 1);
    private Held? _held;

    /// <summary>The current credential, renewed when it is due.</summary>
    internal async ValueTask<T> GetAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        Held? current = Volatile.Read(ref _held);
        if (current is not null && current.ExpiresOn - margin > now)
        {
            return current.Value;
        }

        // Expired: every caller waits for the renewal. Within the margin: one renews, the others go on.
        bool expired = current is null || current.ExpiresOn <= now;
        if (!expired && !await _renewing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return current!.Value;
        }

        if (expired)
        {
            await _renewing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (Volatile.Read(ref _held) is { } renewed && renewed.ExpiresOn - margin > time.GetUtcNow())
            {
                return renewed.Value;
            }

            T fresh = await source(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _held, new Held(fresh, expiresOn(fresh)));
            return fresh;
        }
        catch (Exception failed) when (failed is not OperationCanceledException && !expired && Volatile.Read(ref _held) is { } stillValid)
        {
            return stillValid.Value;
        }
        finally
        {
            _renewing.Release();
        }
    }

    /// <summary>Drops the credential the service refused, so that the next request takes a new one.</summary>
    internal void Expired() => Volatile.Write(ref _held, null);

    /// <summary>When the current credential expires, for a message: never its value.</summary>
    internal DateTimeOffset? ExpiresOn => Volatile.Read(ref _held)?.ExpiresOn;

    public void Dispose() => _renewing.Dispose();

    private sealed record Held(T Value, DateTimeOffset ExpiresOn);
}
