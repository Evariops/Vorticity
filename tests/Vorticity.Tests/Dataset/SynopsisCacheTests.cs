// The synopsis cache on the local disk, apart from any question: a file the system will not delete yet goes at a later
// eviction; a region torn or flipped anywhere is dropped; a region is written behind its reader and served while it
// waits, and a write that fails behind it leaves the cache working; one cache owns a directory; a sweep keeps what is
// read often; and caches that share a root read what the others kept, regions, a sealed object's bytes and a tree's
// pages, a root shared or owned, never both, and a directory outliving the cache that took it.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class SynopsisCacheTests
{
    /// <summary>
    /// A region let go whose file the system will not delete yet, as Windows refuses while another process holds the file
    /// without sharing its deletion: the file stays on the disk, counted among the bytes held, so the cache lets other
    /// regions go in its place, and goes at a later eviction once the system lets it.
    /// </summary>
    [Fact]
    public void AFileTheSystemWillNotDeleteYetGoesAtALaterEviction()
    {
        string directory = CacheDirectory();
        try
        {
            using SynopsisCache cache = new SynopsisCache(directory, 4 << 20);
            UInt128 uid = 0x6e;
            List<(IndexSegment Region, int Bytes)> admitted = [];
            IndexSegment Admitted(int seed)
            {
                byte[] bytes = new byte[100_000 + seed];
                new Random(seed).NextBytes(bytes);
                IndexSegment region = IndexSegment.Of(seed * 1_048_576L, bytes, 6);
                cache.Admit(uid, region, bytes);
                admitted.Add((region, bytes.Length));
                return region;
            }

            long Live() => admitted.Where(a => cache.Holds(uid, a.Region)).Sum(a => (long)a.Bytes);

            IndexSegment first = Admitted(1);
            string file = Directory.GetFiles(directory, SynopsisCache.NameOf(uid, first), SearchOption.AllDirectories).Single();
            bool refused = true;
            cache.DeleteFault = path => refused && string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            for (int seed = 2; cache.Holds(uid, first); seed++)
            {
                Assert.True(seed < 64, "the first region was never let go");
                Admitted(seed);
            }

            Assert.True(System.IO.File.Exists(file));
            Assert.Equal(Live() + admitted[0].Bytes, cache.HeldBytes);

            // The next region comes in within the capacity, others let go in the file's place.
            Admitted(64);
            Assert.True(System.IO.File.Exists(file));
            Assert.Equal(Live() + admitted[0].Bytes, cache.HeldBytes);
            Assert.True(cache.HeldBytes <= 4 << 20, $"{cache.HeldBytes} bytes held, past the capacity");

            refused = false;
            Admitted(65);
            Assert.False(System.IO.File.Exists(file));
            Assert.Equal(Live(), cache.HeldBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A region whose file is flipped at any byte, or cut short at any length, is dropped, and kept again once read.</summary>
    [Fact]
    public void ARegionFlippedOrTornAnywhereIsDropped()
    {
        string directory = CacheDirectory();
        try
        {
            using SynopsisCache cache = new SynopsisCache(directory, 1 << 20);
            byte[] bytes = new byte[300];
            new Random(61).NextBytes(bytes);
            UInt128 uid = new UInt128(0x0123_4567_89ab_cdef, 0xfedc_ba98_7654_3210);
            IndexSegment region = IndexSegment.Of(4096, bytes, 6);
            cache.Admit(uid, region, bytes);
            Assert.True(cache.TryRead(uid, region, out Vorticity.Buffers.SegmentOwner owner));
            Assert.Equal(bytes, owner.Buffer.Span.ToArray());
            owner.Release();
            string file = Directory.GetFiles(directory, SynopsisCache.NameOf(uid, region), SearchOption.AllDirectories).Single();
            long dropped = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                // The cache maps the files it read, which no one writes then: it lets them go first.
                byte[] flipped = [.. bytes];
                flipped[i] ^= 1;
                cache.CloseFiles();
                System.IO.File.WriteAllBytes(file, flipped);
                Assert.False(cache.TryRead(uid, region, out _), $"byte {i} flipped");
                Assert.False(System.IO.File.Exists(file));
                dropped++;
                cache.Admit(uid, region, bytes);
            }

            for (int length = 0; length < bytes.Length; length += 7)
            {
                cache.CloseFiles();
                System.IO.File.WriteAllBytes(file, bytes[..length]);
                Assert.False(cache.TryRead(uid, region, out _), $"cut at {length}");
                dropped++;
                cache.Admit(uid, region, bytes);
            }

            Assert.Equal(dropped, cache.Corrupt);
            Assert.True(cache.TryRead(uid, region, out owner));
            owner.Release();
            Assert.Equal(bytes.Length, cache.HeldBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A region admitted behind its reader, the reader's buffer let go at once: the cache holds it and serves it,
    /// waiting for its write or written; it is on the disk once the writes waiting are done; and one admitted just
    /// before the cache is disposed is on the disk for the next cache of the directory.
    /// </summary>
    [Fact]
    public void ARegionAdmittedBehindItsReaderIsServedAndWrittenBeforeTheCacheCloses()
    {
        string directory = CacheDirectory();
        try
        {
            UInt128 uid = 0x5eed;
            (IndexSegment Region, byte[] Bytes) Admitted(SynopsisCache cache, int seed)
            {
                byte[] bytes = new byte[4_096 + seed];
                new Random(seed).NextBytes(bytes);
                IndexSegment region = IndexSegment.Of(seed * 65_536L, bytes, 6);
                Vorticity.Buffers.NativeSegmentOwner block = Vorticity.Buffers.AlignedBufferPool.Shared.Rent(bytes.Length, 64);
                bytes.CopyTo(block.WritableSpan);
                cache.Admit(uid, region, block, Vorticity.Buffers.VortexBuffer.FromPinned(block.WritableSpan[..bytes.Length], 6));
                block.Release();
                return (region, bytes);
            }

            IndexSegment first;
            IndexSegment last;
            byte[] firstBytes;
            byte[] lastBytes;
            using (SynopsisCache cache = new SynopsisCache(directory, 1 << 20))
            {
                (first, firstBytes) = Admitted(cache, 1);
                Assert.True(cache.Holds(uid, first));
                Assert.Equal(firstBytes.Length, cache.HeldBytes);
                Assert.True(cache.TryRead(uid, first, out Vorticity.Buffers.SegmentOwner read));
                Assert.Equal(firstBytes, read.Buffer.Span.ToArray());
                read.Release();

                cache.Flush();
                Assert.Single(Directory.GetFiles(directory, SynopsisCache.NameOf(uid, first), SearchOption.AllDirectories));
                Assert.Equal(firstBytes.Length, cache.HeldBytes);
                (last, lastBytes) = Admitted(cache, 2);
            }

            using SynopsisCache reopened = new SynopsisCache(directory, 1 << 20);
            foreach ((IndexSegment region, byte[] bytes) in new[] { (first, firstBytes), (last, lastBytes) })
            {
                Assert.True(reopened.TryRead(uid, region, out Vorticity.Buffers.SegmentOwner owner));
                Assert.Equal(bytes, owner.Buffer.Span.ToArray());
                owner.Release();
            }

            Assert.Equal(firstBytes.Length + lastBytes.Length, reopened.HeldBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A write behind its reader that fails past what the disk throws, here a fault the test raises, is counted and left
    /// to the store: the flush returns, the region is not held, and the next region is written and served as before.
    /// </summary>
    [Fact]
    public async Task AWriteThatFailsBehindItsReaderLeavesTheCacheWorking()
    {
        string directory = CacheDirectory();
        try
        {
            UInt128 uid = 0xfa17;
            using SynopsisCache cache = new SynopsisCache(directory, 1 << 20);
            IndexSegment Admitted(int seed)
            {
                byte[] bytes = new byte[4_096 + seed];
                new Random(seed).NextBytes(bytes);
                IndexSegment region = IndexSegment.Of(seed * 65_536L, bytes, 6);
                Vorticity.Buffers.NativeSegmentOwner block = Vorticity.Buffers.AlignedBufferPool.Shared.Rent(bytes.Length, 64);
                bytes.CopyTo(block.WritableSpan);
                cache.Admit(uid, region, block, Vorticity.Buffers.VortexBuffer.FromPinned(block.WritableSpan[..bytes.Length], 6));
                block.Release();
                return region;
            }

            cache.WriteFault = static _ => throw new InvalidOperationException("a write that fails past the disk's failures");
            IndexSegment failed = Admitted(1);
            Task flushed = Task.Run(cache.Flush, Ct);
            Assert.Same(flushed, await Task.WhenAny(flushed, Task.Delay(TimeSpan.FromSeconds(10), Ct)));
            Assert.Equal(1, cache.FailedWrites);
            Assert.False(cache.Holds(uid, failed));

            cache.WriteFault = null;
            IndexSegment kept = Admitted(2);
            cache.Flush();
            Assert.True(cache.Holds(uid, kept));
            Assert.Single(Directory.GetFiles(directory, SynopsisCache.NameOf(uid, kept), SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// One cache owns a directory: a second is refused while the first is open. Once it is disposed, the next
    /// one finds every entry the first kept, and deletes what a write left under its temporary name.
    /// </summary>
    [Fact]
    public void ADirectoryHasOneCacheAndOutlivesIt()
    {
        string directory = CacheDirectory();
        try
        {
            byte[] bytes = new byte[100];
            new Random(67).NextBytes(bytes);
            UInt128 uid = 42;
            IndexSegment region = IndexSegment.Of(0, bytes, 0);
            using (SynopsisCache first = new SynopsisCache(directory, 1 << 20))
            {
                Assert.Throws<InvalidOperationException>(() => new SynopsisCache(directory, 1 << 20));
                first.Admit(uid, region, bytes);
            }

            string left = Path.Combine(directory, "2a", "torn.0123.tmp");
            System.IO.File.WriteAllBytes(left, bytes);
            using SynopsisCache second = new SynopsisCache(directory, 1 << 20);
            Assert.Equal(bytes.Length, second.HeldBytes);
            Assert.True(second.TryRead(uid, region, out Vorticity.Buffers.SegmentOwner owner));
            Assert.Equal(bytes, owner.Buffer.Span.ToArray());
            owner.Release();
            Assert.False(System.IO.File.Exists(left));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A fresh directory for a cache, which the test deletes.</summary>
    /// <summary>
    /// A sweep of a long window asks a hundred regions once each, through a cache that holds twenty: the four
    /// regions read again and again before it are still there after it, and the sweep's own left but the newest.
    /// </summary>
    [Fact]
    public void ASweepPassesThroughWithoutPushingOutWhatIsReadOften()
    {
        string directory = CacheDirectory();
        try
        {
            byte[] bytes = new byte[1_000];
            using SynopsisCache cache = new SynopsisCache(directory, 20 * bytes.Length);
            IndexSegment RegionOf(int n)
            {
                bytes.AsSpan().Fill((byte)n);
                BitConverter.TryWriteBytes(bytes, n);
                return IndexSegment.Of(n * 4_096L, bytes, 0);
            }

            bool Read(UInt128 uid, int n)
            {
                IndexSegment region = RegionOf(n);
                if (cache.TryRead(uid, region, out Vorticity.Buffers.SegmentOwner owner))
                {
                    owner.Dispose();
                    return true;
                }

                cache.Admit(uid, region, bytes);
                return false;
            }

            UInt128 hot = 1;
            for (int round = 0; round < 3; round++)
            {
                for (int n = 0; n < 4; n++)
                {
                    Read(hot, n);
                }
            }

            UInt128 swept = 2;
            for (int n = 0; n < 100; n++)
            {
                Read(swept, n);
            }

            Assert.All(Enumerable.Range(0, 4), n => Assert.True(cache.Holds(hot, RegionOf(n)), $"hot region {n} pushed out"));
            Assert.InRange(Enumerable.Range(0, 100).Count(n => cache.Holds(swept, RegionOf(n))), 1, 16);
            Assert.InRange(cache.HeldBytes, 1, cache.CapacityBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    private static string CacheDirectory() => Path.Combine(Path.GetTempPath(), "vorticity-synopsis-cache-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
