using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.IO;

/// <summary>
/// The object stores a session resolves locations with: <c>s3://bucket/key</c>, <c>gs://bucket/key</c>,
/// <c>az://container/key</c>, or any scheme a caller registers. A string without a scheme is a local
/// path, as it always was.
/// </summary>
/// <remarks>
/// <para>
/// A location's first segment after the scheme names a bucket, and the rest is the key in it. The
/// longest registered prefix serves a location, so a bucket can be served by one store and one of its
/// prefixes by another, with other credentials. A scheme is the caller's choosing, which keeps apart two
/// buckets of one name at two providers: <c>r2://lake</c> beside <c>s3://lake</c>.
/// </para>
/// <para>
/// A store registered with <see cref="Add"/> stays its caller's, who may register it with several
/// sessions and disposes it after them. A store built for a scheme is the session's, which disposes it
/// with itself. <see cref="VortexSession.Default"/> holds none.
/// </para>
/// </remarks>
public sealed class ObjectStoreRegistry
{
    private readonly List<(string Prefix, IObjectStore Store)> _stores = [];
    private readonly Dictionary<string, Func<string, IObjectStore>> _schemes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IObjectStore> _built = new(StringComparer.Ordinal);
    private bool _frozen;
    private int _disposed;

    internal ObjectStoreRegistry()
    {
    }

    /// <summary>Registers <paramref name="store"/> for the locations under <paramref name="prefix"/>.</summary>
    /// <param name="prefix">A scheme and a bucket, <c>s3://lake</c>, or one of its prefixes, <c>s3://lake/tables</c>.</param>
    /// <param name="store">
    /// The store over the bucket: a location's key is passed to it relative to the bucket, whatever
    /// prefix chose it. It stays the caller's: the session never disposes it.
    /// </param>
    /// <exception cref="ArgumentException">The prefix is not a scheme and a bucket, or is registered already.</exception>
    /// <exception cref="InvalidOperationException">The session is created; its options are frozen.</exception>
    public void Add(string prefix, IObjectStore store)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(store);
        ThrowIfFrozen();
        string normalized = prefix.TrimEnd('/');
        if (!TrySplit(normalized, out _, out _, out _))
        {
            throw new ArgumentException($"'{prefix}' is not a scheme and a bucket, such as s3://lake or s3://lake/tables.", nameof(prefix));
        }

        foreach ((string known, _) in _stores)
        {
            if (string.Equals(known, normalized, StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{prefix}' has a store already.", nameof(prefix));
            }
        }

        _stores.Add((normalized, store));
    }

    /// <summary>
    /// Serves every bucket of <paramref name="scheme"/> that no registered prefix serves with a store
    /// built by <paramref name="storeFor"/> on the bucket's first use, kept for the session's life and
    /// disposed with it.
    /// </summary>
    /// <param name="scheme">The scheme, <c>s3</c> for instance, without <c>://</c>.</param>
    /// <param name="storeFor">Builds the store of a bucket, given its name.</param>
    /// <exception cref="ArgumentException">The scheme is not one, or has a builder already.</exception>
    /// <exception cref="InvalidOperationException">The session is created; its options are frozen.</exception>
    public void AddScheme(string scheme, Func<string, IObjectStore> storeFor)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(storeFor);
        ThrowIfFrozen();
        if (!IsScheme(scheme))
        {
            throw new ArgumentException($"'{scheme}' is not a scheme: a letter, then letters, digits, '+', '-' or '.'.", nameof(scheme));
        }

        if (!_schemes.TryAdd(scheme, storeFor))
        {
            throw new ArgumentException($"The scheme '{scheme}' has a builder already.", nameof(scheme));
        }
    }

    /// <summary>
    /// The store under <paramref name="location"/>, a view of its bucket's store whose keys are relative
    /// to the location, as a dataset's are to a directory. The view shares the store's connections and is
    /// the session's to dispose, not the caller's.
    /// </summary>
    /// <param name="location">A scheme, a bucket and an optional prefix: <c>r2://media/tables/readings/</c>.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentException">The location has no scheme.</exception>
    /// <exception cref="InvalidOperationException">No store serves the location's scheme and bucket.</exception>
    public IObjectStore Resolve(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        (IObjectStore store, string key) = Locate(location);
        if (key.Length > 0 && !key.EndsWith('/'))
        {
            key += "/";
        }

        return key.Length == 0 ? new PrefixedObjectStore(store, string.Empty) : new PrefixedObjectStore(store, key);
    }

    /// <summary>Whether <paramref name="text"/> is a location, <c>scheme://...</c>, rather than a local path.</summary>
    /// <param name="text">A path or a location.</param>
    /// <returns>True for a scheme followed by <c>://</c>.</returns>
    public static bool IsLocation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int separator = text.IndexOf("://", StringComparison.Ordinal);
        return separator > 0 && IsScheme(text.AsSpan(0, separator));
    }

    /// <summary>The store serving <paramref name="location"/>, and the key it names in the bucket.</summary>
    /// <exception cref="ArgumentException">The location has no scheme.</exception>
    /// <exception cref="InvalidOperationException">No store serves the location's scheme and bucket.</exception>
    internal (IObjectStore Store, string Key) Locate(string location)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!TrySplit(location, out string scheme, out string bucket, out string key))
        {
            throw new ArgumentException($"'{location}' is not a location: a scheme, a bucket and a key, such as s3://lake/readings.vortex.", nameof(location));
        }

        IObjectStore? best = null;
        int longest = -1;
        foreach ((string prefix, IObjectStore store) in _stores)
        {
            if (prefix.Length > longest
                && location.StartsWith(prefix, StringComparison.Ordinal)
                && (location.Length == prefix.Length || location[prefix.Length] == '/'))
            {
                best = store;
                longest = prefix.Length;
            }
        }

        if (best is not null)
        {
            return (best, key);
        }

        if (_schemes.TryGetValue(scheme, out Func<string, IObjectStore>? storeFor))
        {
            string name = $"{scheme.ToLowerInvariant()}://{bucket}";
            lock (_built)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (!_built.TryGetValue(name, out IObjectStore? built))
                {
                    built = storeFor(bucket) ?? throw new InvalidOperationException($"The builder of '{scheme}' gave no store for '{bucket}'.");
                    _built.Add(name, built);
                }

                return (built, key);
            }
        }

        throw new InvalidOperationException(
            $"No store serves '{scheme}://{bucket}'. Register one in the session's options, " +
            $"o.ObjectStores.Add(\"{scheme}://{bucket}\", store), or let the environment configure them with " +
            "o.ObjectStores.UseEnvironment() from Vorticity.ObjectStorage.");
    }

    internal void Freeze() => _frozen = true;

    /// <summary>Disposes every store built for a scheme, each once; the stores registered are their callers'.</summary>
    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // A builder may hand one store to several buckets.
        HashSet<IObjectStore> built = new HashSet<IObjectStore>(ReferenceEqualityComparer.Instance);
        lock (_built)
        {
            built.UnionWith(_built.Values);
            _built.Clear();
        }

        foreach (IObjectStore store in built)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException("The session is created; its options are frozen. Register stores inside VortexSession.Create.");
        }
    }

    /// <summary>Splits <c>scheme://bucket/key</c>; the key may be empty, the bucket may not.</summary>
    private static bool TrySplit(string location, out string scheme, out string bucket, out string key)
    {
        scheme = bucket = key = string.Empty;
        int separator = location.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !IsScheme(location.AsSpan(0, separator)))
        {
            return false;
        }

        ReadOnlySpan<char> rest = location.AsSpan(separator + 3);
        int slash = rest.IndexOf('/');
        ReadOnlySpan<char> name = slash < 0 ? rest : rest[..slash];
        if (name.IsEmpty)
        {
            return false;
        }

        scheme = location[..separator];
        bucket = name.ToString();
        key = slash < 0 ? string.Empty : rest[(slash + 1)..].ToString();
        return true;
    }

    private static bool IsScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A store under a prefix of another: every key it is given is the prefix and the key, and every key it
/// lists is stripped of the prefix. It shares the store under it and never disposes it.
/// </summary>
internal sealed class PrefixedObjectStore(IObjectStore inner, string prefix) : IObjectStore
{
    /// <summary>The store under the view.</summary>
    internal IObjectStore Inner => inner;

    /// <summary>The keys' prefix in the store under the view.</summary>
    internal string Prefix => prefix;

    public ObjectStoreCapabilities Capabilities => inner.Capabilities;

    public ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken) =>
        inner.ReadAsync(prefix + key, offset, destination, ifToken, cancellationToken);

    public ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken) =>
        inner.ReadTailAsync(prefix + key, destination, cancellationToken);

    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
        inner.HeadAsync(prefix + key, cancellationToken);

    public ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken) =>
        inner.PutAsync(prefix + key, content, length, condition, cancellationToken);

    public ValueTask<PutOutcome> PutAsync(string key, ReadOnlySequence<byte> content, PutCondition condition, CancellationToken cancellationToken) =>
        inner.PutAsync(prefix + key, content, condition, cancellationToken);

    public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (prefix.Length == 0)
        {
            return inner.DeleteAsync(keys, cancellationToken);
        }

        string[] prefixed = new string[keys.Count];
        for (int i = 0; i < prefixed.Length; i++)
        {
            prefixed[i] = prefix + keys[i];
        }

        return inner.DeleteAsync(prefixed, cancellationToken);
    }

    public async IAsyncEnumerable<ObjectListing> ListAsync(string listPrefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listPrefix);
        await foreach (ObjectListing listed in inner.ListAsync(prefix + listPrefix, startAfter is null ? null : prefix + startAfter, cancellationToken).ConfigureAwait(false))
        {
            yield return prefix.Length == 0 ? listed : listed with { Key = listed.Key[prefix.Length..] };
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
