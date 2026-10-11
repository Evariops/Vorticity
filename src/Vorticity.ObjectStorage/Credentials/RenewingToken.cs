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
/// A credential from a source, kept until five minutes before it expires and renewed then by one
/// caller while the others keep using it; a renewal that fails is tried again by a later request until
/// the credential actually expires. A request waits for a renewal only when no credential is held or
/// the one held has expired. A credential replaced may still be signing a request: it is left to the
/// collector, never wiped.
/// </summary>
internal sealed class Renewing<T>(Func<CancellationToken, ValueTask<T>> source, Func<T, DateTimeOffset> expiresOn, TimeProvider time)
{
    /// <summary>How long before its expiry a credential is renewed.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    // Never disposed: its wait handle is never asked for, so it holds nothing to release.
    private readonly SemaphoreSlim _renewing = new SemaphoreSlim(1, 1);
    private Held? _held;

    /// <summary>The current credential, renewed when it is due.</summary>
    internal async ValueTask<T> GetAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        Held? current = Volatile.Read(ref _held);
        if (current is not null && current.ExpiresOn > now + Margin)
        {
            return current.Value;
        }

        // None held, or expired: every caller waits for the renewal. Within the margin: one renews, the others go on.
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
            if (Volatile.Read(ref _held) is { } renewed && renewed.ExpiresOn > time.GetUtcNow() + Margin)
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
