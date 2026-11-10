// Audit OS-01: what an object store's request costs on the client, authorized by a bearer token: 1,000 reads of 4 KiB from
// Cloud Storage over a transport that answers in memory, under a token of 1,200 characters as OAuth 2.0 gives them.
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.ObjectStorage;

namespace Vorticity.Benchmarks.Audit;

/// <summary>Reads from a store authorized by a token.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditStoreRequestBenchmarks
{
    private const int Reads = 1_000;

    private GoogleCloudStorageObjectStore _store = null!;
    private readonly byte[] _into = new byte[4_096];

    /// <summary>Makes the store.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string token = "ya29." + new string('t', 1_195);
        _store = new GoogleCloudStorageObjectStore(new GoogleCloudStorageOptions
        {
            Bucket = "warehouse",
            Credentials = StoreCredentials.Token(_ => ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)))),
            Handler = new Answers(),
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });
        Read();
    }

    /// <summary>Disposes the store.</summary>
    [GlobalCleanup]
    public void Cleanup() => _store.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>The reads; their bytes.</summary>
    [Benchmark]
    public long Read() => ReadAsync().GetAwaiter().GetResult();

    private async Task<long> ReadAsync()
    {
        long bytes = 0;
        for (int i = 0; i < Reads; i++)
        {
            bytes += (await _store.ReadAsync("data/object.vortex", 4_096L * (i % 64), _into, null, CancellationToken.None).ConfigureAwait(false)).BytesRead;
        }

        return bytes;
    }

    /// <summary>Answers every range read with its bytes, as the service does.</summary>
    private sealed class Answers : HttpMessageHandler
    {
        private readonly byte[] _object = new byte[4_096 * 64];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            System.Net.Http.Headers.RangeItemHeaderValue range = System.Linq.Enumerable.First(request.Headers.Range!.Ranges);
            int first = (int)range.From!.Value;
            int last = Math.Min((int)range.To!.Value, _object.Length - 1);
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(_object, first, last - first + 1),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {first}-{last}/{_object.Length}");
            response.Headers.TryAddWithoutValidation("x-goog-generation", "1");
            return Task.FromResult(response);
        }
    }
}
