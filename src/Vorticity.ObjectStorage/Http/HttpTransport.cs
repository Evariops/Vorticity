using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// A store's requests over the platform's <see cref="HttpClient"/>: its connections, the deadlines of
/// each attempt, and its retries with their budget. What a request means is the store's; when to send
/// it again is this class's.
/// </summary>
/// <remarks>
/// <para>
/// Each attempt builds its request afresh, signed for the moment it is sent, and is given a deadline
/// for its first byte. A body is read by the store's own code, under a stall deadline that is pushed
/// back by every byte that arrives. A request that may be sent again (a read, a head, a listing, a
/// delete, a put whose outcome a head can prove) is retried on a service failure, a throttle, a
/// connection lost or a deadline missed, with exponential backoff and full jitter, while the store's
/// budget allows.
/// </para>
/// <para>
/// The handler is the platform's: it opens connections as it does for any client, under a deadline of
/// their own, closes rather than drains a body abandoned before its end, follows no redirect and keeps
/// no cookie.
/// </para>
/// </remarks>
internal sealed class HttpTransport : IDisposable
{
    private readonly HttpClient _client;
    private readonly StoreTransportOptions _options;
    private readonly string _provider;
    private readonly Func<HttpResponseMessage, CancellationToken, ValueTask<ServiceError>> _readError;
    private readonly RetryBudget _budget = new RetryBudget();
    private readonly ConcurrencyLimit _limit;

    /// <summary>A transport for one store.</summary>
    /// <param name="options">Its connections, deadlines and retries.</param>
    /// <param name="provider">The provider's name, for the instruments' tags.</param>
    /// <param name="readError">Reads what the service said in a refusal's body.</param>
    /// <param name="handler">A handler to send through instead of the platform's, which tests give; the transport does not dispose it.</param>
    internal HttpTransport(
        StoreTransportOptions options,
        string provider,
        Func<HttpResponseMessage, CancellationToken, ValueTask<ServiceError>> readError,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _provider = provider;
        _readError = readError;
        _client = handler is null
            ? new HttpClient(CreateHandler(options), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _client.DefaultRequestVersion = HttpVersion.Version11;
        _client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        _limit = new ConcurrencyLimit(options.MaxConnections, options.TimeProvider);
    }

    /// <summary>The store's options.</summary>
    internal StoreTransportOptions Options => _options;

    /// <summary>The budget's tokens left, for tests.</summary>
    internal int BudgetLeft => _budget.Tokens;

    /// <summary>The requests in flight the store allows now, halved by a throttle.</summary>
    internal int ConcurrencyLimit => _limit.Limit;

    /// <summary>
    /// Sends a request built by <paramref name="build"/> and hands its answer to
    /// <paramref name="consume"/>, sending it again while a failure may be cured and the budget allows.
    /// </summary>
    /// <typeparam name="TState">What the request is built from, passed to both delegates so that neither captures it.</typeparam>
    /// <typeparam name="TResult">What <paramref name="consume"/> makes of the answer.</typeparam>
    /// <param name="operation">The operation, for the instruments and the messages.</param>
    /// <param name="state">What the request is built from.</param>
    /// <param name="build">Builds one attempt's request, signed.</param>
    /// <param name="consume">
    /// Reads an answer whose status is not a service failure, body included, and maps it; throws
    /// <see cref="StoreRetryException"/> to ask for another attempt. The transport disposes the answer.
    /// </param>
    /// <param name="idempotent">Whether the request may be sent again after it may have reached the service.</param>
    /// <param name="cancellationToken">Cancels the request and its retries.</param>
    /// <returns>What <paramref name="consume"/> returned.</returns>
    /// <exception cref="ObjectStoreException">The service failed, or the connection did, past the attempts or the budget.</exception>
    internal async ValueTask<TResult> SendAsync<TState, TResult>(
        string operation,
        TState state,
        Func<TState, CancellationToken, ValueTask<HttpRequestMessage>> build,
        Func<HttpResponseMessage, TState, CancellationToken, ValueTask<TResult>> consume,
        bool idempotent,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cost;
            string failure;
            Exception? cause = null;
            ServiceError? refused = null;
            HttpResponseMessage? response = null;
            bool sendAgain = false;
            await _limit.EnterAsync(cancellationToken).ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            try
            {
                response = await FirstByteAsync(state, build, cancellationToken).ConfigureAwait(false);
                StoreTelemetry.FirstByteAfter(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                int status = (int)response.StatusCode;
                StoreTelemetry.Request(operation, _provider, status);
                if (!ServiceError.IsRetryable(status))
                {
                    TResult result = await consume(response, state, cancellationToken).ConfigureAwait(false);
                    _budget.Succeeded();
                    _limit.Succeeded();
                    return result;
                }

                ServiceError error = await _readError(response, cancellationToken).ConfigureAwait(false);
                refused = error;
                if (error.IsThrottle)
                {
                    StoreTelemetry.Throttled();
                    _limit.Throttled();
                }

                failure = error.ToString();
                cost = RetryBudget.RetryCost;
            }
            catch (StoreRetryException retry)
            {
                failure = retry.Message;
                cause = retry.InnerException ?? retry;
                cost = retry.Cost;
                if (retry.NotApplied && !idempotent)
                {
                    // Refused without being applied: this attempt may be sent again, once more.
                    if (attempt >= _options.MaxAttempts || (cost > 0 && !_budget.TrySpend(cost)))
                    {
                        throw new ObjectStoreException($"{operation} failed after {attempt} attempts: {failure}", cause);
                    }

                    sendAgain = true;
                }
            }
            catch (OperationCanceledException timeout) when (!cancellationToken.IsCancellationRequested)
            {
                StoreTelemetry.Request(operation, _provider, 0);
                failure = $"no answer within {_options.FirstByteTimeout.TotalSeconds:0.###} s";
                cause = timeout;
                cost = RetryBudget.TimeoutCost;
            }
            catch (Exception lost) when (lost is HttpRequestException or IOException or SocketException)
            {
                StoreTelemetry.Request(operation, _provider, 0);
                failure = lost.Message;
                cause = lost;
                cost = RetryBudget.RetryCost;
            }
            finally
            {
                response?.Dispose();
                _limit.Exit();
            }

            if (sendAgain)
            {
                StoreTelemetry.Retried();
                await DelayAsync(cost == 0 ? 0 : attempt, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!idempotent)
            {
                // The request may have been applied: its store asks the service what became of it.
                throw new StoreAnswerLostException($"{operation} failed after it may have reached the service: {failure}", cause ?? new IOException(failure));
            }

            if (attempt >= _options.MaxAttempts || (cost > 0 && !_budget.TrySpend(cost)))
            {
                string why = attempt >= _options.MaxAttempts ? $"after {attempt} attempts" : "the store's retry budget is spent";
                throw new ObjectStoreException($"{operation} failed {why}: {failure}", cause ?? new IOException(failure));
            }

            StoreTelemetry.Retried();
            await DelayAsync(cost == 0 ? 0 : attempt, refused, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends one attempt's request and waits for its answer's headers, under the first-byte deadline.</summary>
    private async ValueTask<HttpResponseMessage> FirstByteAsync<TState>(
        TState state, Func<TState, CancellationToken, ValueTask<HttpRequestMessage>> build, CancellationToken cancellationToken)
    {
        HttpRequestMessage request = await build(state, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.FirstByteTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// Waits before attempt <paramref name="attempt"/> + 1 of a request the store retries itself, a
    /// conditional put whose outcome it learned; spends the budget's tokens and says whether it may.
    /// </summary>
    internal async ValueTask<bool> RetryAfterAsync(int attempt, CancellationToken cancellationToken)
    {
        if (attempt >= _options.MaxAttempts || !_budget.TrySpend(RetryBudget.RetryCost))
        {
            return false;
        }

        StoreTelemetry.Retried();
        await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask DelayAsync(int attempt, ServiceError? refused, CancellationToken cancellationToken)
    {
        TimeSpan delay = attempt == 0 ? TimeSpan.Zero : Backoff(attempt, refused);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads an answer's body into <paramref name="destination"/> until it ends or the destination is
    /// full, under the stall deadline. The answer's disposal closes the body.
    /// </summary>
    /// <returns>The bytes read.</returns>
    /// <exception cref="StoreRetryException">The body stalled or its connection failed.</exception>
    internal async ValueTask<int> ReadBodyAsync(HttpResponseMessage response, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int total = 0;
        try
        {
            Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(_options.StallTimeout);
            while (total < destination.Length)
            {
                int read = await body.ReadAsync(destination[total..], stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                stall.CancelAfter(_options.StallTimeout);
            }
        }
        catch (OperationCanceledException stalled) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StoreRetryException($"the body stalled for {_options.StallTimeout.TotalSeconds:0.###} s after {total} bytes", RetryBudget.TimeoutCost, stalled);
        }
        catch (Exception lost) when (lost is HttpRequestException or IOException)
        {
            throw new StoreRetryException($"the body failed after {total} bytes: {lost.Message}", RetryBudget.RetryCost, lost);
        }

        StoreTelemetry.Read(total);
        return total;
    }

    /// <summary>Reads a small body whole, up to <paramref name="limit"/> bytes, into a pooled array the caller returns.</summary>
    /// <exception cref="StoreRetryException">The body stalled or its connection failed.</exception>
    /// <exception cref="ObjectStoreException">The body is longer than <paramref name="limit"/>.</exception>
    internal async ValueTask<(byte[] Bytes, int Length)> ReadSmallBodyAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        long? announced = response.Content.Headers.ContentLength;
        if (announced > limit)
        {
            throw new ObjectStoreException($"An answer of {announced} bytes is past the {limit} this store reads.");
        }

        byte[] bytes = ArrayPool<byte>.Shared.Rent((int)Math.Min(announced ?? 16_384, limit) + 1);
        int length = 0;
        try
        {
            while (true)
            {
                int read = await ReadBodyAsync(response, bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                length += read;
                if (length < bytes.Length)
                {
                    return (bytes, length);
                }

                if (length > limit)
                {
                    throw new ObjectStoreException($"An answer past {limit} bytes is longer than this store reads.");
                }

                byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Min(bytes.Length * 2, limit + 1));
                bytes.AsSpan(0, length).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(bytes);
                bytes = larger;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(bytes);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The delay before attempt <paramref name="attempt"/> + 1: full jitter over an exponential ceiling, at least what <c>Retry-After</c> asked.</summary>
    private TimeSpan Backoff(int attempt, ServiceError? refused)
    {
        double ceiling = Math.Min(_options.RetryMaxDelay.TotalMilliseconds, _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        TimeSpan delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
        if (refused is { RetryAfter: { } after } && after > delay)
        {
            delay = after < _options.RetryMaxDelay ? after : _options.RetryMaxDelay;
        }

        return delay;
    }

    /// <summary>
    /// The platform's handler, set for object stores: its pool follows the service's name resolution,
    /// a connection that takes too long to open is given up, and a body abandoned is closed rather than
    /// drained. Which address a new connection takes is the platform's choice.
    /// </summary>
    private static SocketsHttpHandler CreateHandler(StoreTransportOptions options) => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = options.MaxConnections,
        ConnectTimeout = options.ConnectTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
        MaxResponseDrainSize = 0,
        ResponseDrainTimeout = TimeSpan.Zero,
        Expect100ContinueTimeout = TimeSpan.FromSeconds(1),
        UseProxy = true,
        Proxy = options.Proxy,
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        },
    };
}
