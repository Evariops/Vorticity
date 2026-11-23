// The synopsis cache keeps, under its quota for data, the frames a question reads of a sealed dataset's objects, as the
// store holds them: the same question asked by a session that starts from that cache's directory reads no data object from
// the store, every frame and trailer on the disk is bytes the store holds, a frame flipped on the disk is read again from
// the store, a cache that shares the root reads the other's frames where they lie and keeps none again, and the frames keep
// within the quota, a cache with none keeping none.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetDataCacheTests
{
    [Fact]
    public async Task ARepeatedQuestionOverASealedDatasetReadsNoDataObjectFromTheStore()
    {
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillSealedAsync(store, keyring);
        string directory = CacheDirectory();
        try
        {
            double first = await AskSealedAsync(store, keyring, new SynopsisCache(directory, Regions, Data));
            Assert.True(store.Reads > 0);

            store.Reads = 0;
            SynopsisCache again = new SynopsisCache(directory, Regions, Data);
            Assert.True(again.DataHeldBytes > 0);
            Assert.Equal(first, await AskSealedAsync(store, keyring, again, cache => Assert.True(cache.Hits > 0)));
            Assert.Equal(0, store.Reads);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ASealedDatasetsFramesOnTheDiskAreTheBytesItsStoreHolds()
    {
        // No plaintext reaches the disk: every frame the cache keeps, a region's or the data's, is the bytes the store holds
        // at its place in one of the objects, and every trailer the end of one. No plain object's data segment is kept.
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillSealedAsync(store, keyring);
        string directory = CacheDirectory();
        try
        {
            await AskSealedAsync(store, keyring, new SynopsisCache(directory, Regions, Data));
            List<byte[]> objects = await DataObjectsAsync(inner);
            Assert.Equal(4, objects.Count);

            string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            string[] frames = [.. files.Where(static file => file.EndsWith("-sealed", StringComparison.Ordinal) || file.EndsWith("-sealed-data", StringComparison.Ordinal))];
            string[] trailers = [.. files.Where(static file => file.EndsWith("-trailer", StringComparison.Ordinal))];
            Assert.Contains(frames, static file => file.EndsWith("-sealed-data", StringComparison.Ordinal));
            Assert.Equal(objects.Count, trailers.Length);
            Assert.DoesNotContain(files, static file => file.EndsWith("-data", StringComparison.Ordinal) && !file.EndsWith("-sealed-data", StringComparison.Ordinal));

            foreach (string frame in frames)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(frame);
                long offset = long.Parse(Path.GetFileName(frame).Split('-')[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                Assert.Contains(objects, held => offset + bytes.Length <= held.Length && held.AsSpan((int)offset, bytes.Length).SequenceEqual(bytes));
            }

            foreach (string trailer in trailers)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(trailer);
                Assert.Contains(objects, held => held.AsSpan().EndsWith(bytes));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ASealedFrameFlippedOnTheDiskIsReadAgainFromTheStore()
    {
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillSealedAsync(store, keyring);
        string directory = CacheDirectory();
        try
        {
            double first = await AskSealedAsync(store, keyring, new SynopsisCache(directory, Regions, Data));
            int all = store.Reads;
            string[] files = Directory.GetFiles(directory, "*-sealed-data", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            string flipped = files.OrderByDescending(file => new FileInfo(file).Length).First();
            byte[] bytes = System.IO.File.ReadAllBytes(flipped);
            bytes[bytes.Length / 2] ^= 0x40;
            System.IO.File.WriteAllBytes(flipped, bytes);

            // Its tag fails: the frames read with it are dropped, and the store sends them again, the rest coming from the disk.
            store.Reads = 0;
            Assert.Equal(first, await AskSealedAsync(store, keyring, new SynopsisCache(directory, Regions, Data), cache => Assert.True(cache.Corrupt > 0)));
            Assert.InRange(store.Reads, 1, all - 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ACacheThatSharesARootReadsAnothersSealedFramesWhereTheyLie()
    {
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillSealedAsync(store, keyring);
        string root = CacheDirectory();
        try
        {
            await using VortexSession owner = SealedSession(keyring, SynopsisCache.OpenShared(root, Regions, Data));
            double first = await AskAsync(store, owner, encrypted: true);
            await owner.Options.SynopsisCache!.FlushAsync();

            store.Reads = 0;
            SynopsisCache peer = SynopsisCache.OpenShared(root, Regions, Data);
            Assert.NotEqual(owner.Options.SynopsisCache.Directory, peer.Directory);
            Assert.Equal(first, await AskSealedAsync(store, keyring, peer, cache =>
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
    public async Task SealedFramesKeepWithinTheDataQuotaAndACacheWithoutOneKeepsNone()
    {
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using DataReads store = new DataReads(inner);
        await FillSealedAsync(store, keyring);
        string directory = CacheDirectory();
        try
        {
            // What the question's frames take on the disk without a bound past it, then within one.
            const long Quota = 256 << 10;
            long unbounded = 0;
            string all = CacheDirectory();
            try
            {
                await AskSealedAsync(store, keyring, new SynopsisCache(all, Regions, Data), async cache =>
                {
                    await cache.FlushAsync();
                    unbounded = cache.DataHeldBytes;
                });
            }
            finally
            {
                Directory.Delete(all, recursive: true);
            }

            Assert.True(unbounded > 2 * Quota, $"the question's frames take {unbounded} bytes, too few to try a quota of {Quota}");
            double first = await AskSealedAsync(store, keyring, new SynopsisCache(directory, Regions, Quota), async cache =>
            {
                await cache.FlushAsync();
                Assert.InRange(cache.DataHeldBytes, 1, Quota);
            });

            string none = CacheDirectory();
            try
            {
                Assert.Equal(first, await AskSealedAsync(store, keyring, new SynopsisCache(none, Regions), async cache =>
                {
                    await cache.FlushAsync();
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

    /// <summary>The objects of <see cref="FillAsync"/>, sealed under <paramref name="keyring"/>'s key.</summary>
    private static async Task FillSealedAsync(IObjectStore store, VortexKeyring keyring)
    {
        await using VortexSession session = VortexSession.Create(options => options.Keyring = keyring);
        await FillAsync(store, new DatasetOptions { Session = session, Encrypted = true, Layout = Layout });
    }

    /// <summary>A session that unwraps keys through <paramref name="keyring"/>, over <paramref name="cache"/>.</summary>
    private static VortexSession SealedSession(VortexKeyring keyring, SynopsisCache cache) => VortexSession.Create(options =>
    {
        options.Keyring = keyring;
        options.SynopsisCache = cache;
    });

    /// <summary>The question over the sealed dataset, asked by a handle and a session of their own over <paramref name="cache"/>, then checked by <paramref name="check"/>.</summary>
    private static async Task<double> AskSealedAsync(IObjectStore store, VortexKeyring keyring, SynopsisCache cache, Action<SynopsisCache>? check = null)
    {
        await using VortexSession session = SealedSession(keyring, cache);
        double answer = await AskAsync(store, session, encrypted: true);
        check?.Invoke(cache);
        return answer;
    }

    private static async Task<double> AskSealedAsync(IObjectStore store, VortexKeyring keyring, SynopsisCache cache, Func<SynopsisCache, Task> check)
    {
        await using VortexSession session = SealedSession(keyring, cache);
        double answer = await AskAsync(store, session, encrypted: true);
        await check(cache);
        return answer;
    }

    /// <summary>The bytes of every data object <paramref name="store"/> holds, as it holds them.</summary>
    private static async Task<List<byte[]>> DataObjectsAsync(IObjectStore store)
    {
        List<byte[]> objects = [];
        await foreach (ObjectListing listing in store.ListAsync(CommitKey.DataPrefix, null, Ct))
        {
            byte[] bytes = new byte[listing.Head.Length];
            ObjectRead read = await store.ReadAsync(listing.Key, 0, bytes, null, Ct);
            Assert.Equal(bytes.Length, read.BytesRead);
            objects.Add(bytes);
        }

        return objects;
    }
}
