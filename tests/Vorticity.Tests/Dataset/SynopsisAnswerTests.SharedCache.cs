// Synopsis caches that share a root (SynopsisCache.OpenShared), as the processes of one machine do: each takes a directory
// of its own under the root and reads from the others' what they kept, regions, a sealed object's bytes and a tree's pages,
// none of it kept again; a root is shared or owned, never both; and a directory outlives the cache that took it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    /// <summary>
    /// Two caches that share a root take a directory each, and read from the other's what it kept: a region the first kept
    /// is a hit of the second's, read from the first's directory and not kept again; what neither holds is a miss of both;
    /// a file the first's directory holds torn is not served, and stays the first's to drop.
    /// </summary>
    [Fact]
    public void CachesSharingARootReadWhatTheOthersKept()
    {
        string root = CacheDirectory();
        try
        {
            using SynopsisCache first = SynopsisCache.OpenShared(root, 1 << 20);
            using SynopsisCache second = SynopsisCache.OpenShared(root, 1 << 20);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "p0"), first.Directory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "p1"), second.Directory);
            first.ListPeers();

            UInt128 uid = 0x5ca1ab1e;
            (IndexSegment Region, byte[] Bytes) RegionOf(int seed)
            {
                byte[] bytes = new byte[2_000 + seed];
                new Random(seed).NextBytes(bytes);
                return (IndexSegment.Of(seed * 65_536L, bytes, 6), bytes);
            }

            (IndexSegment kept, byte[] keptBytes) = RegionOf(1);
            first.Admit(uid, kept, keptBytes);
            Assert.True(second.TryRead(uid, kept, out Vorticity.Buffers.SegmentOwner owner));
            Assert.Equal(keptBytes, owner.Buffer.Span.ToArray());
            owner.Release();
            Assert.Equal((1L, 1L, 0L, 0L), (second.Hits, second.PeerHits, second.Misses, second.HeldBytes));

            (IndexSegment other, byte[] otherBytes) = RegionOf(2);
            second.Admit(uid, other, otherBytes);
            Assert.True(first.TryRead(uid, other, out owner));
            Assert.Equal(otherBytes, owner.Buffer.Span.ToArray());
            owner.Release();

            (IndexSegment neither, _) = RegionOf(3);
            Assert.False(second.TryRead(uid, neither, out _));
            Assert.False(first.TryRead(uid, neither, out _));
            Assert.Equal((1L, 1L), (first.Misses, second.Misses));

            // Torn in the first's directory: not served to the second, and still the first's.
            string file = Directory.GetFiles(first.Directory, SynopsisCache.NameOf(uid, kept), SearchOption.AllDirectories).Single();
            byte[] flipped = [.. keptBytes];
            flipped[7] ^= 1;
            System.IO.File.WriteAllBytes(file, flipped);
            Assert.False(second.TryRead(uid, kept, out _));
            Assert.True(System.IO.File.Exists(file));
            Assert.Equal(0L, second.Corrupt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A root is shared or owned: a cache of its own is refused on a root shared caches hold, and a shared cache on a
    /// directory a cache of its own holds; a cache of its own opened on a root once shared takes none of the directories
    /// the shared caches took for its entries.
    /// </summary>
    [Fact]
    public void ARootIsSharedOrOwned()
    {
        string root = CacheDirectory();
        string owned = CacheDirectory();
        try
        {
            byte[] bytes = new byte[100];
            new Random(71).NextBytes(bytes);
            IndexSegment region = IndexSegment.Of(0, bytes, 0);
            using (SynopsisCache shared = SynopsisCache.OpenShared(root, 1 << 20))
            {
                Assert.Throws<InvalidOperationException>(() => new SynopsisCache(root, 1 << 20));
                shared.Admit(42, region, bytes);
            }

            using (SynopsisCache own = new SynopsisCache(owned, 1 << 20))
            {
                Assert.Throws<InvalidOperationException>(() => SynopsisCache.OpenShared(owned, 1 << 20));
            }

            using SynopsisCache alone = new SynopsisCache(root, 1 << 20);
            Assert.Equal(0, alone.HeldBytes);
            Assert.False(alone.TryRead(42, region, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(owned, recursive: true);
        }
    }

    /// <summary>
    /// The directory a shared cache took outlives it: the others read what it kept once it closed, and the next cache of
    /// the root takes it again, with what it holds.
    /// </summary>
    [Fact]
    public void ASharedCachesDirectoryOutlivesIt()
    {
        string root = CacheDirectory();
        try
        {
            byte[] bytes = new byte[1_000];
            new Random(73).NextBytes(bytes);
            UInt128 uid = 7;
            IndexSegment region = IndexSegment.Of(4_096, bytes, 6);
            using SynopsisCache staying = SynopsisCache.OpenShared(root, 1 << 20);
            using (SynopsisCache leaving = SynopsisCache.OpenShared(root, 1 << 20))
            {
                leaving.Admit(uid, region, bytes);
            }

            staying.ListPeers();
            Assert.True(staying.TryRead(uid, region, out Vorticity.Buffers.SegmentOwner owner));
            Assert.Equal(bytes, owner.Buffer.Span.ToArray());
            owner.Release();

            using SynopsisCache next = SynopsisCache.OpenShared(root, 1 << 20);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "p1"), next.Directory);
            Assert.Equal(bytes.Length, next.HeldBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A sealed object's frames and trailer, and a tree's pages, are read from another cache of the root as regions are:
    /// a frame is held when the other holds it, so that its reader asks no store for it, and none is kept again.
    /// </summary>
    [Fact]
    public void SealedBytesAndPagesAreReadFromTheOthersToo()
    {
        string root = CacheDirectory();
        try
        {
            using SynopsisCache first = SynopsisCache.OpenShared(root, 1 << 20);
            using SynopsisCache second = SynopsisCache.OpenShared(root, 1 << 20);
            UInt128 uid = 0xfeed;
            byte[] frame = new byte[65_536];
            new Random(79).NextBytes(frame);
            string frameName = SynopsisCache.FrameNameOf(uid, 0, frame.Length);
            byte[] trailer = new byte[300];
            new Random(83).NextBytes(trailer);
            string trailerName = SynopsisCache.TrailerNameOf(uid);
            byte[] page = new byte[4_096];
            new Random(89).NextBytes(page);
            UInt128 hash = 0xabcdef;
            first.AdmitSealed(uid, frameName, frame);
            first.AdmitSealed(uid, trailerName, trailer);
            first.AdmitPage(hash, page);
            first.Flush();

            Assert.True(second.HoldsSealed(frameName));
            byte[] read = new byte[frame.Length];
            Assert.True(second.TryReadSealed(frameName, read));
            Assert.Equal(frame, read);
            Assert.True(second.TryReadSealed(trailerName, 1_024, out Vorticity.Buffers.SegmentOwner owner));
            Assert.Equal(trailer, owner.Buffer.Span.ToArray());
            owner.Release();
            Assert.Equal(page, second.TryReadPage(hash, page.Length));
            Assert.Equal((3L, 0L), (second.PeerHits, second.HeldBytes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Two sessions whose caches share a root, as two processes of one machine: once the first read a window's tables from
    /// the store, the second answers the window with no request to the store, every region and page read from the first's
    /// directory, and keeps none again, where a third whose cache is its own asks the store.
    /// </summary>
    [Fact]
    public async Task ASecondProcessAnswersFromTheFirstsDirectory()
    {
        string root = CacheDirectory();
        string alone = CacheDirectory();
        try
        {
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            DateTimeOffset hour = Noon.AddHours(1);
            await using VortexSession firstSession = VortexSession.Create(options => options.SynopsisCache = SynopsisCache.OpenShared(root, 64 << 20));
            await using VortexDataset first = await FilledAsync(store, session: firstSession);
            _ = await first.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
            (List<string> answer, _) = await WindowAsync(first, hour, hour.AddHours(1));
            firstSession.Options.SynopsisCache!.Flush();

            await using VortexSession secondSession = VortexSession.Create(options => options.SynopsisCache = SynopsisCache.OpenShared(root, 64 << 20));
            await using VortexDataset second = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = secondSession }, Ct);
            _ = await second.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
            store.Reset();
            inner.Latency = TimeSpan.FromMilliseconds(20);
            (List<string> shared, _) = await WindowAsync(second, hour, hour.AddHours(1));
            SynopsisCache cache = secondSession.Options.SynopsisCache!;
            Console.Out.Write($"SHARED CACHE: {store.Requests} requests, {cache.PeerHits} reads from the other directory, {cache.HeldBytes} bytes kept.\n");
            Assert.Equal(answer, shared);
            Assert.Equal(0, store.Requests);
            Assert.True(cache.PeerHits > 0);
            Assert.Equal(0, cache.HeldBytes);

            // A third, whose cache is its own, asks the store.
            await using VortexSession thirdSession = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(alone, 64 << 20));
            await using VortexDataset third = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = thirdSession }, Ct);
            _ = await third.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
            store.Reset();
            (List<string> own, _) = await WindowAsync(third, hour, hour.AddHours(1));
            Assert.Equal(answer, own);
            Assert.True(store.Requests > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(alone, recursive: true);
        }
    }
}
