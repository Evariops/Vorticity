using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.ObjectStorage.Http;
using Vorticity.ObjectStorage.S3;

namespace Vorticity.ObjectStorage;

/// <summary>
/// A bucket of Amazon S3, or of a service that speaks its API (Cloudflare R2, OVHcloud, Scaleway,
/// Ceph), as an <see cref="IObjectStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every request is signed with Signature Version 4 and sent over the platform's HTTP client, its
/// connections kept by the store and spread over the endpoint's addresses. A read asks for one range
/// and lands in the caller's memory, bound to the object's ETag once known, so that an object replaced
/// under its reader is refused by the service rather than downloaded. A suffix range reads a file's
/// tail and its length in one request where the service is known to answer one.
/// </para>
/// <para>
/// The store owns its credentials and its connections; disposing it wipes the one and closes the
/// other. A session that holds it disposes it with itself.
/// </para>
/// </remarks>
public sealed partial class S3ObjectStore : IObjectStore
{
    private static readonly UriCreationOptions Verbatim = new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true };

    private readonly S3StoreOptions _options;
    private readonly HttpTransport _transport;
    private readonly S3Profile _profile;
    private readonly ObjectStoreCapabilities _capabilities;
    private readonly TimeProvider _time;
    private Target? _target;
    private Task<Target>? _resolving;
    private long _skewTicks;
    private int _disposed;

    /// <summary>A store over <see cref="S3StoreOptions.Bucket"/>.</summary>
    /// <param name="options">The bucket, its endpoint, its keys and its connections.</param>
    /// <exception cref="ArgumentException">The endpoint is plain HTTP off the loopback, or carries a path.</exception>
    public S3ObjectStore(S3StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Bucket);
        ArgumentNullException.ThrowIfNull(options.Credentials);
        if (options.Endpoint is { } endpoint)
        {
            CheckEndpoint(endpoint);
        }

        _options = options;
        _profile = options.Profile ?? ProfileOf(options.Endpoint);
        _capabilities = CapabilitiesOf(_profile, options);
        _time = options.Transport.TimeProvider;
        _transport = new HttpTransport(options.Transport, ProviderName(_profile), ReadErrorAsync, options.Handler);

        // Amazon S3 says a bucket's region at the first request; another service is told it, or
        // signs for the one its profile or its host names.
        string? region = options.Region ?? DefaultRegion(_profile, options.Endpoint);
        if (region is not null)
        {
            _target = TargetFor(region);
        }
        else if (_profile != S3Profile.Amazon)
        {
            throw new ArgumentException($"A store on {_profile} needs its region: set S3StoreOptions.Region.", nameof(options));
        }
    }

    /// <summary>
    /// A store over <paramref name="bucket"/> configured from the environment, as the AWS SDKs read it:
    /// the region from <c>AWS_REGION</c> or <c>AWS_DEFAULT_REGION</c>, the endpoint from
    /// <c>AWS_ENDPOINT_URL_S3</c> or <c>AWS_ENDPOINT_URL</c>, its profile from the endpoint's host, and
    /// the keys from the AWS credential chain (<see cref="S3Credentials.Default"/>), the environment's
    /// first.
    /// </summary>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The store; the caller, or the session it is given to, disposes it.</returns>
    public static S3ObjectStore FromEnvironment(string bucket)
    {
        string? endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL_S3") is { Length: > 0 } s3
            ? s3
            : Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
        string? region = Environment.GetEnvironmentVariable("AWS_REGION") is { Length: > 0 } r
            ? r
            : Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        return new S3ObjectStore(new S3StoreOptions
        {
            Bucket = bucket,
            Region = string.IsNullOrEmpty(region) ? null : region,
            Endpoint = string.IsNullOrEmpty(endpoint) ? null : new Uri(endpoint, UriKind.Absolute),
            Credentials = S3Credentials.Default(),
        });
    }

    /// <summary>A store over a bucket of Cloudflare R2.</summary>
    /// <param name="accountId">The account's id, which names its endpoint.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="credentials">An R2 API token's key pair.</param>
    /// <returns>The store.</returns>
    public static S3ObjectStore ForR2(string accountId, string bucket, S3Credentials credentials)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        return new S3ObjectStore(new S3StoreOptions
        {
            Bucket = bucket,
            Credentials = credentials,
            Endpoint = new Uri($"https://{accountId}.r2.cloudflarestorage.com"),
            Profile = S3Profile.R2,
        });
    }

    /// <summary>A store over a bucket of OVHcloud Object Storage.</summary>
    /// <param name="region">The region, <c>gra</c> or <c>sbg</c> for instance.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="credentials">The S3 key pair of an OVHcloud user.</param>
    /// <returns>The store.</returns>
    public static S3ObjectStore ForOvhcloud(string region, string bucket, S3Credentials credentials)
    {
        ArgumentException.ThrowIfNullOrEmpty(region);
        return new S3ObjectStore(new S3StoreOptions
        {
            Bucket = bucket,
            Credentials = credentials,
            Region = region,
            Endpoint = new Uri($"https://s3.{region}.io.cloud.ovh.net"),
            Profile = S3Profile.Ovhcloud,
        });
    }

    /// <summary>A store over a bucket of Scaleway Object Storage.</summary>
    /// <param name="region">The region, <c>fr-par</c> for instance.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="credentials">The key pair of a Scaleway API key.</param>
    /// <returns>The store.</returns>
    public static S3ObjectStore ForScaleway(string region, string bucket, S3Credentials credentials)
    {
        ArgumentException.ThrowIfNullOrEmpty(region);
        return new S3ObjectStore(new S3StoreOptions
        {
            Bucket = bucket,
            Credentials = credentials,
            Region = region,
            Endpoint = new Uri($"https://s3.{region}.scw.cloud"),
            Profile = S3Profile.Scaleway,
        });
    }

    /// <summary>The bucket.</summary>
    public string Bucket => _options.Bucket;

    /// <summary>The service the store speaks to.</summary>
    public S3Profile Profile => _profile;

    /// <inheritdoc/>
    public ObjectStoreCapabilities Capabilities => _capabilities;

    /// <summary>The transport, for tests.</summary>
    internal HttpTransport Transport => _transport;

    /// <inheritdoc/>
    public ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ThrowIfDisposed();
        return ReadCoreAsync(key, offset, destination, ifToken, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        return (_capabilities & ObjectStoreCapabilities.ReadsSuffix) != 0
            ? ReadSuffixAsync(key, destination, cancellationToken)
            : ReadTailByHeadAsync(key, destination, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        CheckKey(key);
        ThrowIfDisposed();
        return _transport.SendAsync(
            "HeadObject",
            new Request(this, key),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Head, s.Key, null, ct),
            static (response, s, ct) => s.Store.ConsumeHeadAsync(response, s.Key, ct),
            idempotent: true,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ThrowIfDisposed();
        List<ObjectListing> page = [];
        string? continuation = null;
        do
        {
            page.Clear();
            continuation = await _transport.SendAsync(
                "ListObjectsV2",
                new ListRequest(this, prefix, continuation is null ? startAfter : null, continuation, page),
                static (s, ct) => s.Store.BuildListAsync(s, ct),
                static (response, s, ct) => s.Store.ConsumeListAsync(response, s, ct),
                idempotent: true,
                cancellationToken).ConfigureAwait(false);
            foreach (ObjectListing listed in page)
            {
                yield return listed;
            }
        }
        while (continuation is not null);
    }

    /// <summary>Closes the connections and wipes the keys.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _transport.Dispose();
            _options.Credentials.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask<ObjectRead> ReadCoreAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return ReadNothingAsync(key, offset, ifToken, cancellationToken);
        }

        return _transport.SendAsync(
            "GetObject",
            new ReadRequest(this, key, offset, destination, ifToken),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Get, s.Key, s, ct),
            static (response, s, ct) => s.Store.ConsumeReadAsync(response, s, ct),
            idempotent: true,
            cancellationToken);
    }

    /// <summary>A read into no memory: a head, which says the length and the token a read would.</summary>
    private async ValueTask<ObjectRead> ReadNothingAsync(string key, long offset, string? ifToken, CancellationToken cancellationToken)
    {
        ObjectHead head = await HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
        if (ifToken is not null && !string.Equals(ifToken, head.Token, StringComparison.Ordinal))
        {
            throw ObjectStoreExtensions.Changed(key, ifToken, head.Token);
        }

        if (offset >= head.Length && !(offset == 0 && head.Length == 0))
        {
            throw ObjectStoreExtensions.PastEnd(key, offset, head.Length);
        }

        return new ObjectRead(0, head.Length, head.Token);
    }

    private ValueTask<ObjectRead> ReadSuffixAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
        {
            return ReadNothingAsync(key, 0, null, cancellationToken);
        }

        return _transport.SendAsync(
            "GetObject",
            new ReadRequest(this, key, -1, destination, null),
            static (s, ct) => s.Store.BuildAsync(HttpMethod.Get, s.Key, s, ct),
            static (response, s, ct) => s.Store.ConsumeReadAsync(response, s, ct),
            idempotent: true,
            cancellationToken);
    }

    /// <summary>The tail on a service whose suffix reads are not proven: a head for the length, then a range bound to its token.</summary>
    private async ValueTask<ObjectRead> ReadTailByHeadAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectHead head = await HeadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw ObjectNotFoundException.For(key);
        int wanted = (int)Math.Min(destination.Length, head.Length);
        if (wanted == 0)
        {
            return new ObjectRead(0, head.Length, head.Token);
        }

        return await ReadCoreAsync(key, head.Length - wanted, destination[..wanted], head.Token, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ObjectRead> ConsumeReadAsync(HttpResponseMessage response, ReadRequest s, CancellationToken cancellationToken)
    {
        HttpStatusCode status = response.StatusCode;
        if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            (ServiceError _, long? actual) = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
            long? size = S3Answers.ContentRange(response)?.Total ?? actual;

            // Only an empty object refuses a read from its start: its token then comes from a head.
            if (s.Offset <= 0 && size is null or 0)
            {
                return await ReadNothingAsync(s.Key, 0, s.IfToken, cancellationToken).ConfigureAwait(false);
            }

            throw ObjectStoreExtensions.PastEnd(s.Key, s.Offset, size ?? -1);
        }

        if (status is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
        {
            throw await FailureAsync(response, "GetObject", s.Key, s.IfToken, cancellationToken).ConfigureAwait(false);
        }

        long announced = response.Content.Headers.ContentLength
            ?? throw new ObjectStoreException($"GetObject on '{s.Key}' was answered without a Content-Length.");
        long first;
        long total;
        if (status == HttpStatusCode.PartialContent)
        {
            (first, total) = S3Answers.ContentRange(response)
                ?? throw new ObjectStoreException($"GetObject on '{s.Key}' was answered with a partial body and no Content-Range.");
        }
        else if (s.Offset > 0 || (s.Offset < 0 && announced > s.Destination.Length))
        {
            throw new ObjectStoreException($"GetObject on '{s.Key}' was answered with the whole object, {announced} bytes, where a range was asked: the service ignores ranges.");
        }
        else
        {
            // The whole object: a range from 0 the service served whole, or a suffix longer than the object.
            (first, total) = (0, announced);
        }

        if ((s.Offset >= 0 && first != s.Offset) || first + announced > total)
        {
            throw new ObjectStoreException($"GetObject on '{s.Key}' was answered with bytes {first}+{announced} of {total} where {s.Offset} was asked.");
        }

        int wanted = (int)Math.Min(announced, s.Destination.Length);
        int read = await _transport.ReadBodyAsync(response, s.Destination[..wanted], cancellationToken).ConfigureAwait(false);
        if (read < wanted)
        {
            throw new StoreRetryException($"the body of GetObject on '{s.Key}' ended after {read} of {wanted} bytes", RetryBudget.RetryCost);
        }

        return new ObjectRead(read, total, TokenOf(response, s.Key, s.IfToken));
    }

    private async ValueTask<ObjectHead?> ConsumeHeadAsync(HttpResponseMessage response, string key, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "HeadObject", key, null, cancellationToken).ConfigureAwait(false);
        }

        long length = response.Content.Headers.ContentLength
            ?? throw new ObjectStoreException($"HeadObject on '{key}' was answered without a Content-Length.");
        string? retain = S3Answers.Header(response, "x-amz-object-lock-retain-until-date");
        return new ObjectHead(length, TokenOf(response, key, null), S3Answers.LastModified(response))
        {
            RetainUntil = retain is null ? null : S3Answers.IsoDate(retain),
            LegalHold = string.Equals(S3Answers.Header(response, "x-amz-object-lock-legal-hold"), "ON", StringComparison.OrdinalIgnoreCase),
        };
    }

    private async ValueTask<string?> ConsumeListAsync(HttpResponseMessage response, ListRequest s, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await FailureAsync(response, "ListObjectsV2", s.Prefix, null, cancellationToken).ConfigureAwait(false);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, S3Answers.MaxXmlBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return S3Answers.ParseListing(
                bytes.AsSpan(0, length), 0, (_capabilities & ObjectStoreCapabilities.ReportsLocksInListing) != 0, s.Page);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// What a refusal becomes: an absent key, a token that no longer matches, a retry after a clock
    /// corrected or keys renewed, or a failure that names the service's code and request id.
    /// </summary>
    private async ValueTask<Exception> FailureAsync(HttpResponseMessage response, string operation, string key, string? ifToken, CancellationToken cancellationToken)
    {
        HttpStatusCode status = response.StatusCode;
        if (status == HttpStatusCode.NotFound)
        {
            return ObjectNotFoundException.For(key);
        }

        if (status == HttpStatusCode.PreconditionFailed && ifToken is not null)
        {
            return ObjectStoreExtensions.Changed(key, ifToken, "another object");
        }

        ServiceError error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return MapFailure(response, error, operation, key);
    }

    /// <summary>What a refusal whose error was read becomes, as <see cref="FailureAsync"/> says.</summary>
    private Exception MapFailure(HttpResponseMessage response, ServiceError error, string operation, string key)
    {
        HttpStatusCode status = response.StatusCode;
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            if (error.Code is "RequestTimeTooSkewed" or "RequestExpired" || (status == HttpStatusCode.Forbidden && error.Code is null && Skewed(response)))
            {
                Skewed(response);
                throw new StoreRetryException($"{operation} was refused for a skewed clock: {error}", cost: 0);
            }

            if (error.Code is "ExpiredToken" or "TokenRefreshRequired")
            {
                if (_lastKeys is { } stale)
                {
                    _options.Credentials.Expired(stale);
                }

                throw new StoreRetryException($"{operation} was refused for expired keys: {error}", cost: 0);
            }

            if (error.Code == "RequestTimeout")
            {
                throw new StoreRetryException($"{operation} was too slow for the service: {error}", RetryBudget.RetryCost);
            }
        }

        if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            string region = S3Answers.Header(response, "x-amz-bucket-region") ?? "another region";
            return new ObjectStoreException(
                $"{operation} on '{key}' was sent to the wrong region: the bucket '{Bucket}' is in {region}. Set S3StoreOptions.Region. {error}");
        }

        string identity = _lastKeys is { } keys ? $", as {keys}" : string.Empty;
        return new ObjectStoreException($"{operation} on '{key}' was refused{identity}: {error}");
    }

    /// <summary>Learns the service's clock from <c>Date</c> when it is off ours by more than five minutes, and says whether it was.</summary>
    private bool Skewed(HttpResponseMessage response)
    {
        if (response.Headers.Date is not { } service)
        {
            return false;
        }

        TimeSpan offset = service - _time.GetUtcNow();
        if (offset.Duration() < TimeSpan.FromMinutes(5))
        {
            return false;
        }

        Interlocked.Exchange(ref _skewTicks, offset.Ticks);
        return true;
    }

    private async ValueTask<ServiceError> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        (await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false)).Error;

    /// <summary>The service's error, its code, message and request id from the body, which a head has none of; and the object's size a range past its end names.</summary>
    private async ValueTask<(ServiceError Error, long? ActualObjectSize)> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string? requestId = S3Answers.Header(response, "x-amz-request-id");
        TimeSpan? retryAfter = S3Answers.RetryAfter(response);
        if (response.RequestMessage?.Method == HttpMethod.Head || response.Content.Headers.ContentLength == 0)
        {
            return (new ServiceError(status, S3Answers.Header(response, "x-amz-error-code"), null, requestId, retryAfter), null);
        }

        (byte[] bytes, int length) = await _transport.ReadSmallBodyAsync(response, 64 << 10, cancellationToken).ConfigureAwait(false);
        try
        {
            (string? code, string? message, string? id, long? size) = S3Answers.ParseError(bytes.AsSpan(0, length));
            return (new ServiceError(status, code, message, id ?? requestId, retryAfter), size);
        }
        catch (ObjectStoreException)
        {
            // Not the XML of an error: a proxy's page, say. The status says enough.
            return (new ServiceError(status, null, null, requestId, retryAfter), null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>The keys the last request was signed with: the identity a refusal names, and the keys an expiry renews.</summary>
    private AwsKeys? _lastKeys;

    /// <summary>The object's ETag; the caller's own string when it is the token it gave, so a read bound to it allocates none.</summary>
    private static string TokenOf(HttpResponseMessage response, string key, string? ifToken)
    {
        string token = S3Answers.Header(response, "ETag")
            ?? throw new ObjectStoreException($"'{key}' was answered without an ETag.");
        return ifToken is not null && string.Equals(token, ifToken, StringComparison.Ordinal) ? ifToken : token;
    }

    /// <summary>A signed request on <paramref name="key"/>: a range or a suffix for a read, nothing more for a head.</summary>
    private async ValueTask<HttpRequestMessage> BuildAsync(HttpMethod method, string key, ReadRequest? read, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);
        string path = target.PathPrefix + "/" + UriEncoding.Path(key);
        HttpRequestMessage request = new HttpRequestMessage(method, new Uri(target.Base + path, in Verbatim));
        if (read is { } r)
        {
            request.Headers.TryAddWithoutValidation("Range", r.Offset < 0
                ? string.Create(CultureInfo.InvariantCulture, $"bytes=-{r.Destination.Length}")
                : string.Create(CultureInfo.InvariantCulture, $"bytes={r.Offset}-{r.Offset + r.Destination.Length - 1}"));
            if (r.IfToken is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", r.IfToken);
            }
        }

        await SignAsync(request, target, path, string.Empty, SigV4Signer.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        return request;
    }

    private async ValueTask<HttpRequestMessage> BuildListAsync(ListRequest s, CancellationToken cancellationToken)
    {
        Target target = await TargetAsync(cancellationToken).ConfigureAwait(false);

        // The query's pairs, already in the order their names sort in.
        string query = string.Concat(
            s.Continuation is null ? string.Empty : "continuation-token=" + UriEncoding.Query(s.Continuation) + "&",
            "encoding-type=url&list-type=2&max-keys=1000",
            s.Prefix.Length == 0 ? string.Empty : "&prefix=" + UriEncoding.Query(s.Prefix),
            s.StartAfter is null ? string.Empty : "&start-after=" + UriEncoding.Query(s.StartAfter));
        string path = target.PathPrefix.Length == 0 ? "/" : target.PathPrefix;
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(target.Base + path + "?" + query, in Verbatim));
        await SignAsync(request, target, path, query, SigV4Signer.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        return request;
    }

    /// <summary>
    /// Adds the date, the payload hash, the session token and <paramref name="extra"/>, and the signature
    /// over them and the host. An extra header named <c>content-…</c> goes on the body.
    /// </summary>
    private async ValueTask SignAsync(
        HttpRequestMessage request, Target target, string canonicalUri, string canonicalQuery, string payloadHash, CancellationToken cancellationToken,
        (string Name, string Value)[]? extra = null)
    {
        AwsKeys keys = await _options.Credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        _lastKeys = keys;
        string amzDate = SigV4Signer.AmzDate(_time.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _skewTicks)));
        request.Headers.Host = target.Host;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        Signed headers = default;
        headers[0] = ("host", target.Host);
        headers[1] = ("x-amz-content-sha256", payloadHash);
        headers[2] = ("x-amz-date", amzDate);
        int count = 3;
        if (keys.SessionToken is { } token)
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", token);
            headers[count++] = ("x-amz-security-token", token);
        }

        foreach ((string name, string value) in extra ?? [])
        {
            bool added = name.StartsWith("content-", StringComparison.Ordinal)
                ? request.Content!.Headers.TryAddWithoutValidation(name, value)
                : request.Headers.TryAddWithoutValidation(name, value);
            Debug.Assert(added, $"The header {name} was refused.");
            headers[count++] = (name, value);
        }

        string authorization = target.Signer.Authorization(keys, amzDate, request.Method.Method, canonicalUri, canonicalQuery, ((Span<(string, string)>)headers)[..count], payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    private ValueTask<Target> TargetAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _target) is { } target ? new ValueTask<Target>(target) : new ValueTask<Target>(ResolveAsync(cancellationToken));

    /// <summary>The bucket's region, learned once from its own answer to an unsigned head: Amazon S3 says it in <c>x-amz-bucket-region</c> whatever else it answers.</summary>
    private Task<Target> ResolveAsync(CancellationToken cancellationToken)
    {
        Task<Target>? resolving = Volatile.Read(ref _resolving);
        if (resolving is null || resolving.IsFaulted || resolving.IsCanceled)
        {
            Task<Target> started = LearnRegionAsync(cancellationToken);
            resolving = Interlocked.CompareExchange(ref _resolving, started, resolving) == resolving ? started : _resolving!;
        }

        return resolving;
    }

    private async Task<Target> LearnRegionAsync(CancellationToken cancellationToken)
    {
        Uri probe = BucketAddressedByPath(Bucket)
            ? new Uri($"https://s3.amazonaws.com/{UriEncoding.Path(Bucket)}")
            : new Uri($"https://{Bucket}.s3.amazonaws.com/");
        string region = await _transport.SendAsync(
            "HeadBucket",
            (Store: this, Probe: probe),
            static (s, _) => new ValueTask<HttpRequestMessage>(new HttpRequestMessage(HttpMethod.Head, s.Probe)),
            static (response, s, _) => new ValueTask<string>(S3Answers.Header(response, "x-amz-bucket-region")
                ?? throw new ObjectStoreException(
                    $"The region of the bucket '{s.Store.Bucket}' could not be learned ({(int)response.StatusCode}): set S3StoreOptions.Region or AWS_REGION.")),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);
        Target target = TargetFor(region);
        Volatile.Write(ref _target, target);
        return target;
    }

    /// <summary>Where requests go for <paramref name="region"/>: the base, the host signed, the bucket's path when it is addressed by path.</summary>
    private Target TargetFor(string region)
    {
        Uri endpoint = _options.Endpoint ?? new Uri($"https://s3.{region}.amazonaws.com");
        bool byPath = _options.PathStyle ?? (_profile == S3Profile.R2 || IsAddressOrLoopback(endpoint) || BucketAddressedByPath(Bucket));
        string host = byPath ? Authority(endpoint) : $"{Bucket}.{Authority(endpoint)}";
        string baseUri = $"{endpoint.Scheme}://{host}";
        return new Target(baseUri, host, byPath ? "/" + UriEncoding.Path(Bucket) : string.Empty, new SigV4Signer(region));
    }

    private static string Authority(Uri endpoint) => endpoint.IsDefaultPort ? endpoint.IdnHost : $"{endpoint.IdnHost}:{endpoint.Port}";

    private static bool BucketAddressedByPath(string bucket) => bucket.Contains('.', StringComparison.Ordinal);

    private static bool IsAddressOrLoopback(Uri endpoint) =>
        endpoint.IsLoopback || endpoint.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static void CheckEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.AbsolutePath is not ("/" or ""))
        {
            throw new ArgumentException($"An endpoint is a scheme, a host and a port, with no path: '{endpoint}'.", nameof(endpoint));
        }

        if (endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            return;
        }

        throw new ArgumentException($"An endpoint is reached over HTTPS, or plain HTTP on the loopback for an emulator: '{endpoint}'.", nameof(endpoint));
    }

    private static S3Profile ProfileOf(Uri? endpoint)
    {
        if (endpoint is null)
        {
            return S3Profile.Amazon;
        }

        string host = endpoint.IdnHost;
        return host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) ? S3Profile.R2
            : host.EndsWith(".cloud.ovh.net", StringComparison.OrdinalIgnoreCase) ? S3Profile.Ovhcloud
            : host.EndsWith(".scw.cloud", StringComparison.OrdinalIgnoreCase) ? S3Profile.Scaleway
            : host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) ? S3Profile.Amazon
            : S3Profile.Other;
    }

    /// <summary>The region a profile signs for when none is given: R2's <c>auto</c>, or the one an OVHcloud or Scaleway host names.</summary>
    private static string? DefaultRegion(S3Profile profile, Uri? endpoint)
    {
        if (profile == S3Profile.R2)
        {
            return "auto";
        }

        // The other services that speak the API take any region, and default to the first of Amazon's.
        if (profile == S3Profile.Other)
        {
            return "us-east-1";
        }

        if (endpoint is null || profile is not (S3Profile.Ovhcloud or S3Profile.Scaleway))
        {
            return null;
        }

        // s3.<region>.io.cloud.ovh.net, s3.<region>.scw.cloud
        string[] labels = endpoint.IdnHost.Split('.');
        return labels.Length > 2 && labels[0] == "s3" ? labels[1] : null;
    }

    /// <summary>
    /// What the service does: suffix reads proven on Amazon S3 and taken as absent elsewhere until the
    /// contract tests prove them, listings in order everywhere but on OVHcloud, whose order is to check,
    /// and replacement on a token's match everywhere.
    /// </summary>
    private static ObjectStoreCapabilities CapabilitiesOf(S3Profile profile, S3StoreOptions options)
    {
        bool suffix = options.ReadsSuffix ?? profile is S3Profile.Amazon or S3Profile.Other;
        bool ordered = options.ListsInOrder ?? profile != S3Profile.Ovhcloud;
        bool locks = options.KeepsLocks ?? profile != S3Profile.R2;
        return ObjectStoreCapabilities.ReplacesOnTokenMatch
            | (suffix ? ObjectStoreCapabilities.ReadsSuffix : 0)
            | (ordered ? ObjectStoreCapabilities.ListsInOrder : 0)
            | (locks ? ObjectStoreCapabilities.KeepsLocks : 0);
    }

    private static string ProviderName(S3Profile profile) => profile switch
    {
        S3Profile.Amazon => "s3",
        S3Profile.R2 => "r2",
        S3Profile.Ovhcloud => "ovhcloud",
        S3Profile.Scaleway => "scaleway",
        _ => "s3-compatible",
    };

    private static void CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > 1024)
        {
            throw new ArgumentException("A key is at most 1 024 bytes on S3.", nameof(key));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Where requests go: the base URI, the host signed, the bucket's path when addressed by path, and the signer of the region.</summary>
    private sealed record Target(string Base, string Host, string PathPrefix, SigV4Signer Signer);

    private readonly record struct Request(S3ObjectStore Store, string Key);

    /// <summary>A read: from <see cref="Offset"/>, or the suffix the destination holds when it is negative.</summary>
    private readonly record struct ReadRequest(S3ObjectStore Store, string Key, long Offset, Memory<byte> Destination, string? IfToken);

    private readonly record struct ListRequest(S3ObjectStore Store, string Prefix, string? StartAfter, string? Continuation, List<ObjectListing> Page);

    /// <summary>The headers a request signs, held inline.</summary>
    [InlineArray(12)]
    private struct Signed
    {
        private (string, string) _first;
    }
}
