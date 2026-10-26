// The synopsis cache keeps, under a quota of its own, the data a question reads of a plain dataset's objects: the same
// question asked by a session that starts from that cache's directory reads no data object from the store, a cache that
// shares the root reads the other's segments where they lie and keeps none again, a segment flipped on the disk is read
// again from the store, and the data keeps within its quota, a cache with none keeping none.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetDataCacheTests
{
    private const long Regions = 64L << 20;
    private const long Data = 256L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARepeatedQuestionReadsNoDataObjectFromTheStore()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillAsync(store);
        string directory = CacheDirectory();
        try
        {
            double first = await AskAsync(store, new SynopsisCache(directory, Regions, Data));
            Assert.True(store.Reads > 0);

            store.Reads = 0;
            SynopsisCache again = new SynopsisCache(directory, Regions, Data);
            Assert.True(again.DataHeldBytes > 0);
            Assert.Equal(first, await AskAsync(store, again, cache => Assert.True(cache.Hits > 0)));
            Assert.Equal(0, store.Reads);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ACacheThatSharesARootReadsAnothersDataWhereItLies()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillAsync(store);
        string root = CacheDirectory();
        try
        {
            await using VortexSession owner = VortexSession.Create(options => options.SynopsisCache = SynopsisCache.OpenShared(root, Regions, Data));
            double first = await AskAsync(store, owner);
            owner.Options.SynopsisCache!.Flush();

            store.Reads = 0;
            SynopsisCache peer = SynopsisCache.OpenShared(root, Regions, Data);
            Assert.NotEqual(owner.Options.SynopsisCache.Directory, peer.Directory);
            Assert.Equal(first, await AskAsync(store, peer, cache =>
            {
                Assert.True(cache.PeerHits > 0);
                Assert.Equal(0, cache.DataHeldBytes);
            }));
            Assert.Equal(0, store.Reads);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ADataSegmentFlippedOnTheDiskIsReadAgainFromTheStore()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillAsync(store);
        string directory = CacheDirectory();
        try
        {
            double first = await AskAsync(store, new SynopsisCache(directory, Regions, Data));
            int all = store.Reads;
            string[] files = Directory.GetFiles(directory, "*-data", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            string flipped = files.OrderByDescending(file => new FileInfo(file).Length).First();
            byte[] bytes = System.IO.File.ReadAllBytes(flipped);
            bytes[bytes.Length / 2] ^= 0x40;
            System.IO.File.WriteAllBytes(flipped, bytes);

            store.Reads = 0;
            Assert.Equal(first, await AskAsync(store, new SynopsisCache(directory, Regions, Data), cache => Assert.Equal(1, cache.Corrupt)));
            Assert.InRange(store.Reads, 1, all - 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DataKeepsWithinItsQuotaAndACacheWithoutOneKeepsNone()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillAsync(store);
        string directory = CacheDirectory();
        try
        {
            // What the question's data takes on the disk without a bound past it, then within one: a segment past the
            // bytes waiting is let go, and one past the quota pushes older ones out, whichever the writes' pace gives.
            const long Quota = 96 << 10;
            long unbounded = 0;
            string all = CacheDirectory();
            try
            {
                await AskAsync(store, new SynopsisCache(all, Regions, Data), cache =>
                {
                    cache.Flush();
                    unbounded = cache.DataHeldBytes;
                });
            }
            finally
            {
                Directory.Delete(all, recursive: true);
            }

            Assert.True(unbounded > 2 * Quota, $"the question's data takes {unbounded} bytes, too few to try a quota of {Quota}");
            double first = await AskAsync(store, new SynopsisCache(directory, Regions, Quota), cache =>
            {
                cache.Flush();
                Assert.InRange(cache.DataHeldBytes, 1, Quota);
            });

            string none = CacheDirectory();
            try
            {
                Assert.Equal(first, await AskAsync(store, new SynopsisCache(none, Regions), cache =>
                {
                    cache.Flush();
                    Assert.Equal(0, cache.DataHeldBytes);
                }));
                Assert.Empty(Directory.GetFiles(none, "*-data", SearchOption.AllDirectories));
            }
            finally
            {
                Directory.Delete(none, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Four objects of 20 000 samples each, appended, their values random enough not to compress to nothing.</summary>
    private static async Task FillAsync(IObjectStore store)
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Sample.Schema, new DatasetOptions { ClusteringKey = [nameof(Sample.Id)] }, Ct);
        Random random = new Random(0xCA5E);
        for (int part = 0; part < 4; part++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Sample>([.. Enumerable.Range(0, 20_000).Select(i => new Sample((part * 20_000L) + i, i % 17, random.NextDouble()))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }
    }

    /// <summary>The question, asked by a handle and a session of their own over <paramref name="cache"/>, then checked by <paramref name="check"/>.</summary>
    private static async Task<double> AskAsync(IObjectStore store, SynopsisCache cache, Action<SynopsisCache>? check = null)
    {
        await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
        double answer = await AskAsync(store, session);
        check?.Invoke(cache);
        return answer;
    }

    /// <summary>The sum of the values past a quarter, which reads every object's values.</summary>
    private static async Task<double> AskAsync(IObjectStore store, VortexSession session)
    {
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        List<double> values = await dataset.Scan<Sample>().Where(r => r.Value > 0.25).Select(r => r.Value).ToListAsync(Ct);
        return values.Sum();
    }

    private static string CacheDirectory() => Path.Combine(Path.GetTempPath(), "vorticity-data-cache-" + Guid.NewGuid().ToString("N"));

    /// <summary>A store that counts the reads of data objects, of their bytes or their heads.</summary>
    private sealed class DataReads(IObjectStore inner) : ForwardingStore(inner)
    {
        private int _reads;

        internal int Reads
        {
            get => Volatile.Read(ref _reads);
            set => Volatile.Write(ref _reads, value);
        }

        public override ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            Count(key);
            return base.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }

        public override ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            Count(key);
            return base.ReadTailAsync(key, destination, cancellationToken);
        }

        public override ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
        {
            Count(key);
            return base.HeadAsync(key, cancellationToken);
        }

        private void Count(string key)
        {
            if (key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _reads);
            }
        }
    }

    [VortexRecord]
    public partial record struct Sample(long Id, long Group, double Value);
}
