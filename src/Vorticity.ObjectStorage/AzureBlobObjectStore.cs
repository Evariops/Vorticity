using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Azure;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.Xml;

namespace Vorticity.ObjectStorage;

/// <summary>A container of Azure Blob Storage as an <see cref="IObjectStore"/>.</summary>
/// <remarks>
/// <para>
/// Requests carry the service version the store pins, 2025-01-05, and are authorized by a bearer token,
/// a shared access signature or Shared Key. A read asks for one range, bound to the blob's ETag once
/// known. Azure reads no suffix, so a file of unknown length opens with the blob's properties, then its
/// tail. A listing carries each blob's immutability policy and legal hold, so vacuum asks no head.
/// </para>
/// <para>
/// The store owns its connections, which disposing it closes. Its credentials are the caller's, never
/// disposed, and other stores may share them.
/// </para>
/// </remarks>
public sealed partial class AzureBlobObjectStore : IObjectStore, IUploadService
{
    /// <summary>The service version every request carries, which fixes the shape of the answers.</summary>
    internal const string Version = "2025-01-05";

    private static readonly UriCreationOptions Verbatim = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };

    private readonly AzureBlobStoreOptions _options;
    private readonly AzureAuthorizer _authorizer;
    private readonly HttpTransport _transport;
    private readonly UploadLimits _limits;
    private readonly ServiceClock _clock;
    private readonly string _base;
    private readonly string _containerPath;
    private int _disposed;

    /// <summary>A store over <see cref="AzureBlobStoreOptions.Container"/>.</summary>
    /// <param name="options">The account, the container, the credentials and the connections.</param>
    /// <exception cref="ArgumentException">The endpoint is plain HTTP off the loopback, or the credentials cannot authorize Azure requests.</exception>
    public AzureBlobObjectStore(AzureBlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Account);
        ArgumentException.ThrowIfNullOrEmpty(options.Container);
        ArgumentNullException.ThrowIfNull(options.Credentials);
        Uri endpoint = options.Endpoint ?? new Uri($"https://{options.Account}.blob.core.windows.net");
        if (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new ArgumentException($"An endpoint is reached over HTTPS, or plain HTTP on the loopback for an emulator: '{endpoint}'.", nameof(options));
        }

        _authorizer = options.Credentials.ForAzure ?? throw new ArgumentException(
            $"An Azure store authorizes its requests with tokens, a shared access signature, an account key, the environment's credentials or none, not {options.Credentials}.",
            nameof(options));
        _options = options;
        _limits = LimitsOf(options);
        _clock = new ServiceClock(options.Transport.TimeProvider, "x-ms-date", "r");
        _base = endpoint.GetLeftPart(UriPartial.Authority);
        _containerPath = endpoint.AbsolutePath.TrimEnd('/') + "/" + UriEncoding.Path(options.Container);
        _transport = new HttpTransport(options.Transport, "azure", ReadErrorAsync, options.Handler);
    }

    /// <summary>
    /// A store over <paramref name="container"/> configured from the environment: the account and its
    /// endpoint from <c>AZURE_STORAGE_CONNECTION_STRING</c>, or the account from
    /// <c>AZURE_STORAGE_ACCOUNT</c>, and the credentials of Azure's chain,
    /// <see cref="StoreCredentials.FromEnvironment"/>, which reads the connection string's key or
    /// signature when the environment holds no client secret or workload identity.
    /// </summary>
    /// <param name="container">The container.</param>
    /// <param name="credentials">
    /// The credentials, the environment's when null: credentials shared by several stores renew their
    /// tokens once for all of them.
    /// </param>
    /// <returns>The store, which the caller disposes.</returns>
    /// <exception cref="ObjectStoreException">The environment names no account.</exception>
    public static AzureBlobObjectStore FromEnvironment(string container, StoreCredentials? credentials = null)
    {
        string account;
        Uri? endpoint = null;
        if (Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") is { Length: > 0 } text)
        {
            AzureConnection connection = AzureConnection.Parse(text);
            (account, endpoint) = (connection.Account, connection.BlobEndpoint);
        }
        else
        {
            account = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT") is { Length: > 0 } named
                ? named
                : throw new ObjectStoreException("No Azure account in the environment: set AZURE_STORAGE_ACCOUNT or AZURE_STORAGE_CONNECTION_STRING.");
        }

        return new AzureBlobObjectStore(new AzureBlobStoreOptions
        {
            Account = account,
            Container = container,
            Endpoint = endpoint,
            Credentials = credentials ?? StoreCredentials.FromEnvironment(),
        });
    }

    /// <summary>
    /// A store over <paramref name="container"/> configured by a connection string: its account, its
    /// endpoint, and its account key or shared access signature.
    /// </summary>
    /// <param name="connectionString">The connection string, as the portal shows it.</param>
    /// <param name="container">The container.</param>
    /// <returns>The store, which the caller disposes.</returns>
    /// <exception cref="ArgumentException">The connection string names no account, or holds neither a key nor a signature.</exception>
    public static AzureBlobObjectStore FromConnectionString(string connectionString, string container)
    {
        AzureConnection connection = AzureConnection.Parse(connectionString);
        return new AzureBlobObjectStore(new AzureBlobStoreOptions
        {
            Account = connection.Account,
            Container = container,
            Endpoint = connection.BlobEndpoint,
            Credentials = connection.Credentials()
                ?? throw new ArgumentException("The connection string holds neither an account key nor a shared access signature.", nameof(connectionString)),
        });
    }

    /// <summary>The account.</summary>
    public string Account => _options.Account;

    /// <summary>The container.</summary>
    public string Container => _options.Container;

    /// <inheritdoc/>
    public ObjectStoreCapabilities Capabilities =>
        ObjectStoreCapabilities.ListsInOrder | ObjectStoreCapabilities.ReplacesOnTokenMatch | ObjectStoreCapabilities.KeepsLocks;

    /// <summary>The transport, for tests.</summary>
    internal HttpTransport Transport => _transport;

    /// <inheritdoc/>
    public ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ThrowIfDisposed();
        if (destination.IsEmpty)
        {
            return StoreReads.EmptyAsync(this, key, offset, ifToken, cancellationToken);
        }

        return _transport.SendAsync(
            "GetBlob",
            new ReadRequest(this, key, offset, destination, ifToken),
            static (s, ct) => s.Store.BuildReadAsync(s, ct),
            static (response, s, ct) => s.Store.ConsumeReadAsync(response, s, ct),
            idempotent: true,
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Azure reads no suffix: the blob's properties say its length, then its tail is read bound to its ETag.</remarks>
    public async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        ObjectHead head = await HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
        int wanted = (int)Math.Min(destination.Length, head.Length);
        return wanted == 0
            ? new ObjectRead(0, head.Length, head.Token)
            : await ReadAsync(key, head.Length - wanted, destination[..wanted], head.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        return _transport.SendAsync(
            "GetBlobProperties",
            new KeyRequest(this, key),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Head, s.Key, string.Empty, null, [], ct),
            static async (response, s, ct) => (await s.Store.ConsumeHeadAsync(response, s.Key, ct).ConfigureAwait(false)).Head,
            idempotent: true,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ThrowIfDisposed();
        List<ObjectListing> page = [];
        string? marker = null;
        do
        {
            page.Clear();
            marker = await _transport.SendAsync(
                "ListBlobs",
                new ListRequest(this, prefix, marker is null ? startAfter : null, marker, page),
                static (s, ct) => s.Store.BuildListAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeListAsync(response, s, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
            foreach (ObjectListing listed in page)
            {
                yield return listed;
            }
        }
        while (marker is not null);
    }

    /// <summary>Closes the connections. The credentials are left to the caller, and to the other stores that share them.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transport.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask<HttpRequestMessage> BuildReadAsync(ReadRequest s, CancellationToken cancellationToken)
    {
        string range = string.Create(CultureInfo.InvariantCulture, $"bytes={s.Offset}-{s.Offset + s.Destination.Length - 1}");
        (string, string)[] headers = s.IfToken is { } token
            ? [("x-ms-range", range), ("If-Match", token)]
            : [("x-ms-range", range)];
        return BuildAsync(HttpMethod.Get, s.Key, string.Empty, null, headers, cancellationToken);
    }

    private async ValueTask<ObjectRead> ConsumeReadAsync(HttpResponseMessage response, ReadRequest s, CancellationToken cancellationToken)
    {
        HttpStatusCode status = response.StatusCode;
        if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            long? size = HttpAnswers.ContentRange(response)?.Total;
            await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

            // Only an empty blob refuses a read from its start: its token then comes from its properties.
            if (s.Offset == 0 && size is null or 0)
            {
                return await StoreReads.EmptyAsync(this, s.Key, 0, s.IfToken, cancellationToken).ConfigureAwait(false);
            }

            throw StoreFailures.PastEnd(s.Key, s.Offset, size ?? -1);
        }

        if (status is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
        {
            throw await FailureAsync(response, "GetBlob", s.Key, s.IfToken, cancellationToken).ConfigureAwait(false);
        }

        (long first, long total) = status == HttpStatusCode.PartialContent
            ? HttpAnswers.ContentRange(response) ?? throw new ObjectStoreException($"GetBlob on '{s.Key}' was answered with a partial body and no Content-Range.")
            : (0, response.Content.Headers.ContentLength ?? throw new ObjectStoreException($"GetBlob on '{s.Key}' was answered without a length."));
        StoreFailures.RefusePastEnd(s.Key, s.Offset, total);
        if (first != s.Offset)
        {
            throw new ObjectStoreException($"GetBlob on '{s.Key}' was answered with bytes from {first} where {s.Offset} was asked.");
        }

        int wanted = (int)Math.Min(total - first, s.Destination.Length);
        int read = await _transport.ReadBodyAsync(response, s.Destination[..wanted], cancellationToken).ConfigureAwait(false);
        if (read < wanted)
        {
            throw new StoreRetryException($"the body of GetBlob on '{s.Key}' ended after {read} of {wanted} bytes", RetryBudget.RetryCost);
        }

        return new ObjectRead(read, total, HttpAnswers.ETagOf(response, s.Key, s.IfToken));
    }

    /// <summary>A blob's head, and the put id its metadata carries.</summary>
    private async ValueTask<(ObjectHead? Head, string? PutId)> ConsumeHeadAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "GetBlobProperties", key, null, cancellationToken).ConfigureAwait(false);
        }

        long length = response.Content.Headers.ContentLength
            ?? throw new ObjectStoreException($"GetBlobProperties on '{key}' was answered without a Content-Length.");
        string? until = HttpAnswers.Header(response, "x-ms-immutability-policy-until-date");
        ObjectHead head = new ObjectHead(length, HttpAnswers.ETagOf(response, key, null), HttpAnswers.LastModified(response))
        {
            RetainUntil = until is null ? null : AzureAnswers.Date(until),
            LegalHold = string.Equals(HttpAnswers.Header(response, "x-ms-legal-hold"), "true", StringComparison.OrdinalIgnoreCase),
        };
        return (head, HttpAnswers.Header(response, PutIdHeader));
    }

    private ValueTask<HttpRequestMessage> BuildListAsync(ListRequest s, CancellationToken cancellationToken)
    {
        // The query's pairs; Shared Key sorts them itself, and a signature by token does not sign them.
        string query = string.Concat(
            "restype=container&comp=list&include=immutabilitypolicy%2Clegalhold&maxresults=5000",
            s.Prefix.Length == 0 ? string.Empty : "&prefix=" + UriEncoding.Query(s.Prefix),
            s.StartAfter is null ? string.Empty : "&startFrom=" + UriEncoding.Query(s.StartAfter),
            s.Marker is null ? string.Empty : "&marker=" + UriEncoding.Query(s.Marker));
        return BuildAsync(HttpMethod.Get, null, query, null, [], cancellationToken);
    }

    private async ValueTask<string?> ConsumeListAsync(HttpResponseMessage response, ListRequest s, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "ListBlobs", s.Prefix, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return AzureAnswers.ParseListing(bytes.AsMemory(0, length), s.StartAfter, s.Page);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// A request on <paramref name="key"/>, or on the container when it is null: the version and the date,
    /// <paramref name="headers"/> on the request or its body, and the authorization over them.
    /// </summary>
    private async ValueTask<HttpRequestMessage> BuildAsync(
        HttpMethod method, string? key, string query, HttpContent? content, (string Name, string Value)[] headers, CancellationToken cancellationToken)
    {
        AzureAuthorization authorization = await _authorizer.GetAsync(cancellationToken).ConfigureAwait(false);
        string path = key is null ? _containerPath : _containerPath + "/" + UriEncoding.Path(key);
        if (authorization.Sas is { } sas)
        {
            query = query.Length == 0 ? sas : query + "&" + sas;
        }

        HttpRequestMessage request = new HttpRequestMessage(method, new Uri(_base + path + (query.Length == 0 ? string.Empty : "?" + query), in Verbatim))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("x-ms-version", Version);
        request.Headers.TryAddWithoutValidation("x-ms-date", _clock.Now.ToString("r", CultureInfo.InvariantCulture));
        foreach ((string name, string value) in headers)
        {
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                content!.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (authorization.Bearer is { } token)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }
        else if (authorization.Key is { } accountKey)
        {
            long length = content?.Headers.ContentLength ?? 0;
            request.Headers.TryAddWithoutValidation("Authorization", SharedKeySigner.Authorization(request, authorization.Account!, accountKey, length));
        }

        return request;
    }

    /// <summary>
    /// What a refusal becomes: an absent blob, an ETag that no longer matches, a retry after a clock
    /// corrected or a token renewed, or a failure that names the service's code and request id.
    /// </summary>
    private async ValueTask<Exception> FailureAsync(HttpResponseMessage response, string operation, string key, string? ifToken, CancellationToken cancellationToken)
    {
        if (StoreFailures.OfStatus(response.StatusCode, key, ifToken, "another object") is { } common)
        {
            return common;
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return MapFailure(response, error, operation, key);
    }

    private Exception MapFailure(HttpResponseMessage response, ServiceError error, string operation, string key)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Azure words a date too far off as any failed authentication, so the refused request's own date says it.
            if (_clock.Skewed(response))
            {
                throw new StoreRetryException($"{operation} was refused for a skewed clock: {error}", cost: 0);
            }

            // The token of the refused request, read back from it: a request sent since with a renewed one keeps it.
            if (response.StatusCode == HttpStatusCode.Unauthorized && error.Code is "InvalidAuthenticationInfo" or "ExpiredToken"
                && HttpAnswers.BearerOf(response) is { } refused)
            {
                _authorizer.Expired(refused);
                throw new StoreRetryException($"{operation} was refused for an expired token: {error}", cost: 0);
            }
        }

        if (error.Code == "OperationTimedOut")
        {
            throw new StoreRetryException($"{operation} was too slow for the service: {error}", RetryBudget.RetryCost);
        }

        return error.ToException($"{operation} on '{key}' was refused, as {_authorizer}: {error}");
    }

    private async ValueTask<ServiceError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string? requestId = HttpAnswers.Header(response, "x-ms-request-id");
        string? code = HttpAnswers.Header(response, "x-ms-error-code");
        TimeSpan? retryAfter = HttpAnswers.RetryAfter(response);
        if (response.RequestMessage?.Method == HttpMethod.Head || response.Content.Headers.ContentLength == 0)
        {
            return new ServiceError(status, code, null, requestId, retryAfter);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, HttpAnswers.MaxAnswerBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            (string? bodyCode, string? message, _, _) = ServiceXml.ParseError(bytes.AsMemory(0, length));
            return new ServiceError(status, code ?? bodyCode, message, requestId, retryAfter);
        }
        catch (ObjectStoreException)
        {
            return new ServiceError(status, code, null, requestId, retryAfter);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Refuses a blob name that is empty or past 1 024 characters, which Azure counts where S3 and Cloud Storage count bytes.</summary>
    private static void CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > 1024)
        {
            throw new ArgumentException("A blob's name is at most 1 024 characters.", nameof(key));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private readonly record struct KeyRequest(AzureBlobObjectStore Store, string Key);

    private readonly record struct ReadRequest(AzureBlobObjectStore Store, string Key, long Offset, Memory<byte> Destination, string? IfToken);

    private readonly record struct ListRequest(AzureBlobObjectStore Store, string Prefix, string? StartAfter, string? Marker, List<ObjectListing> Page);
}
