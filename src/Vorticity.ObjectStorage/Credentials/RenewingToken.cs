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
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _renewing = new SemaphoreSlim(1, 1);
    private AccessToken? _token;

    /// <summary>The current token, renewed when it is due.</summary>
    internal async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_token is { } current && current.ExpiresOn - Margin > now)
        {
            return current.Value;
        }

        // Expired: every caller waits for the renewal. Within the margin: one renews, the others go on.
        bool expired = _token is not { } held || held.ExpiresOn <= now;
        if (!expired && !await _renewing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return _token!.Value.Value;
        }

        if (expired)
        {
            await _renewing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (_token is { } renewed && renewed.ExpiresOn - Margin > time.GetUtcNow())
            {
                return renewed.Value;
            }

            AccessToken token = await source(cancellationToken).ConfigureAwait(false);
            _token = token;
            return token.Value;
        }
        catch (Exception failed) when (failed is not OperationCanceledException && !expired && _token is { } stillValid)
        {
            return stillValid.Value;
        }
        finally
        {
            _renewing.Release();
        }
    }

    /// <summary>Drops the token the service refused, so that the next request takes a new one.</summary>
    internal void Expired() => _token = null;

    /// <summary>When the current token expires, for a message: never its value.</summary>
    internal DateTimeOffset? ExpiresOn => _token?.ExpiresOn;

    public void Dispose() => _renewing.Dispose();
}
