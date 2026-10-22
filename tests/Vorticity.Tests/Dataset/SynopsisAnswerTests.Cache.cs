// The synopsis cache on the local disk: warm, the questions read their tables with no request to the store,
// across a restart, a sealed dataset's too with no plaintext on the disk; a region torn or flipped, or a
// sealed frame flipped, is dropped and read again; a region is written behind its reader and served while it
// waits; one cache owns a directory; only the regions an object's table of contents declares enter it on
// questions that look no identifier up; past its capacity it drops entries; and questions that miss the same
// region at once make one read of it.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    /// <summary>
    /// On a store that answers in 20 ms, a window aligned on an hour reads its tables from the store once,
    /// then from the disk with no request, and so does a session that opens the directory after a restart; a
    /// window that cuts slices then reads only the chunks of its edges.
    /// </summary>
    [Fact]
    public async Task WarmTheTablesComeFromTheDiskAcrossARestart()
    {
        string directory = CacheDirectory();
        try
        {
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            DateTimeOffset hour = Noon.AddHours(1);
            List<string> answer;
            await using (VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64 << 20)))
            await using (VortexDataset dataset = await FilledAsync(store, session: session))
            {
                _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
                inner.Latency = TimeSpan.FromMilliseconds(20);
                foreach ((string name, DateTimeOffset from, DateTimeOffset to, long warm) in new[]
                {
                    ("aligned", hour, hour.AddHours(1), 0L),
                    ("unaligned", Noon.AddMinutes(37.5), Noon.AddMinutes(52.25), 1L),
                })
                {
                    store.Reset();
                    (List<string> cold, _) = await WindowAsync(dataset, from, to);
                    long coldRequests = store.Requests;
                    store.Reset();
                    (List<string> again, ScanMetrics metrics) = await WindowAsync(dataset, from, to);
                    Console.Out.Write($"CACHE {name}: {coldRequests} requests cold, {store.Requests} warm, {store.DependentSteps} dependent steps.\n");
                    Assert.Equal(cold, again);
                    Assert.True(metrics.SynopsisRows > 0);
                    Assert.Equal(warm, store.Requests);
                    Assert.True(coldRequests > warm);
                }

                (answer, _) = await WindowAsync(dataset, hour, hour.AddHours(1));
                Assert.True(session.Options.SynopsisCache!.HeldBytes > 0);
            }

            await using (VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64 << 20)))
            await using (VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct))
            {
                _ = await reopened.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
                store.Reset();
                (List<string> restarted, _) = await WindowAsync(reopened, hour, hour.AddHours(1));
                Assert.Equal(answer, restarted);
                Assert.Equal(0, store.Requests);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A sealed dataset through the cache: a window aligned on an hour reads its tables from the store once, then
    /// from the disk with no request, and so does a session that opens the directory after a restart. Every file
    /// the cache keeps is bytes of a sealed object as the store holds them, a frame or a trailer: no plaintext.
    /// </summary>
    [Fact]
    public async Task ASealedDatasetAnswersWarmFromTheDiskWithNoPlaintextThere()
    {
        string directory = CacheDirectory();
        try
        {
            using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            DateTimeOffset hour = Noon.AddHours(1);
            List<string> answer;
            await using (VortexSession session = Sealed(keyring, directory))
            await using (VortexDataset dataset = await FilledAsync(store, session: session, encrypted: true))
            {
                _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
                store.Reset();
                (List<string> cold, _) = await WindowAsync(dataset, hour, hour.AddHours(1));
                long coldRequests = store.Requests;
                store.Reset();
                (answer, ScanMetrics metrics) = await WindowAsync(dataset, hour, hour.AddHours(1));
                Console.Out.Write($"SEALED CACHE: {coldRequests} requests cold, {store.Requests} warm.\n");
                Assert.Equal(cold, answer);
                Assert.True(metrics.SynopsisRows > 0);
                Assert.True(coldRequests > 0);
                Assert.Equal(0, store.Requests);
                await AssertSealedBytesOnlyAsync(dataset, inner, directory);
            }

            await using (VortexSession session = Sealed(keyring, directory))
            await using (VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct))
            {
                _ = await reopened.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
                store.Reset();
                (List<string> restarted, _) = await WindowAsync(reopened, hour, hour.AddHours(1));
                Assert.Equal(answer, restarted);
                Assert.Equal(0, store.Requests);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The frames and the trailers a sealed dataset's cache keeps, each flipped on the disk: the next question
    /// refuses them, reads them again from the store and answers the same; the one after asks the store nothing.
    /// </summary>
    [Fact]
    public async Task ASealedFrameFlippedOnTheDiskIsReadAgainFromTheStore()
    {
        string directory = CacheDirectory();
        try
        {
            using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            await using VortexSession session = Sealed(keyring, directory);
            await using VortexDataset dataset = await FilledAsync(store, session: session, encrypted: true);
            _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
            DateTimeOffset hour = Noon.AddHours(1);
            (List<string> answer, _) = await WindowAsync(dataset, hour, hour.AddHours(1));
            string[] files = [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != ".lock")];
            foreach (string file in files)
            {
                byte[] bytes = System.IO.File.ReadAllBytes(file);
                bytes[bytes.Length / 2] ^= 1;
                System.IO.File.WriteAllBytes(file, bytes);
            }

            store.Reset();
            (List<string> refused, _) = await WindowAsync(dataset, hour, hour.AddHours(1));
            long again = store.Requests;
            store.Reset();
            (List<string> kept, _) = await WindowAsync(dataset, hour, hour.AddHours(1));
            SynopsisCache cache = session.Options.SynopsisCache!;
            Console.Out.Write($"SEALED FLIPPED: {files.Length} files flipped, {cache.Corrupt} dropped, {again} requests to read them again, {store.Requests} after.\n");
            Assert.Equal(answer, refused);
            Assert.Equal(answer, kept);
            Assert.True(cache.Corrupt > 0);
            Assert.True(again > 0);
            Assert.Equal(0, store.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    /// <summary>
    /// Every question of the matrix asked through the cache, plain and under a small capacity: every file it
    /// keeps is a region an object's table of contents declares, the answers are those of the rows, and past
    /// its capacity it holds no more than its capacity, on the disk as in its count.
    /// </summary>
    [Theory]
    [InlineData(64L << 20)]
    [InlineData(8L << 10)]
    public async Task OnlyTheSynopsisPartEntersTheCache(long capacity)
    {
        string directory = CacheDirectory();
        try
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, capacity));
            await using VortexDataset dataset = await FilledAsync(store, session: session);
            foreach (string question in MatrixQuestions())
            {
                (List<string> cached, _) = await AskAsync(dataset, question, synopses: true);
                (List<string> rows, _) = await AskAsync(dataset, question, synopses: false);
                Assert.True(rows.SequenceEqual(cached), $"'{question}'");
            }

            HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
            await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
            {
                if (SynopsisContents.From(held.Entry.Synopsis.Span) is not { } contents)
                {
                    continue;
                }

                IEnumerable<IndexSegment> regions = contents.Tables.SelectMany(table => table.Chunks.Select(chunk => chunk.Region))
                    .Concat(contents.ChunkDirectories)
                    .Concat((contents.PathDirectories ?? []).Select(path => path.Region))
                    .Concat(contents.DirectoryRegion is { } directoryRegion ? [directoryRegion] : []);
                foreach (IndexSegment region in regions)
                {
                    declared.Add(SynopsisCache.NameOf(held.Entry.Uid, region));
                }
            }

            // The regions read last are written behind their questions: all of them, before the directory is read.
            SynopsisCache cache = session.Options.SynopsisCache!;
            cache.Flush();
            string[] files = [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != ".lock")];
            Console.Out.Write($"CACHED at {capacity} bytes: {files.Length} files, {cache.HeldBytes} bytes, {cache.Hits} hits, {cache.Misses} misses, {cache.Evictions} evictions.\n");
            Assert.NotEmpty(files);
            Assert.All(files, file => Assert.Contains(Path.GetFileName(file), declared));
            Assert.Equal(cache.HeldBytes, files.Sum(file => new FileInfo(file).Length));
            Assert.InRange(cache.HeldBytes, 1, capacity);
            Assert.True(cache.Hits > 0);
            Assert.Equal(capacity < 1 << 20, cache.Evictions > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Eight questions asked at once on a cold cache, over a store that answers in 20 ms, make the requests of
    /// one: each region is read once, the others wait for that read.
    /// </summary>
    [Fact]
    public async Task QuestionsMissingTheSameRegionMakeOneRead()
    {
        long[] requests = new long[2];
        List<string>? reference = null;
        foreach (int questions in new[] { 1, 8 })
        {
            string directory = CacheDirectory();
            try
            {
                await using MemoryObjectStore inner = new MemoryObjectStore();
                await using CountingObjectStore store = new CountingObjectStore(inner);
                await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64 << 20));
                await using VortexDataset dataset = await FilledAsync(store, session: session);
                _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
                inner.Latency = TimeSpan.FromMilliseconds(20);
                store.Reset();
                (List<string> Groups, ScanMetrics Metrics)[] answers = await Task.WhenAll(
                    Enumerable.Range(0, questions).Select(_ => WindowAsync(dataset, Noon, Noon.AddHours(2))));
                requests[questions == 1 ? 0 : 1] = store.Requests;
                reference ??= answers[0].Groups;
                Assert.All(answers, answer => Assert.Equal(reference, answer.Groups));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        Console.Out.Write($"ONE READ: {requests[0]} requests for one question, {requests[1]} for eight at once.\n");
        Assert.Equal(requests[0], requests[1]);
    }

    /// <summary>
    /// A window warmed ahead reads its tables from the disk on its first question, with no request to the
    /// store; warmed again, it reads nothing; and the cache's counters reach a listener of the library's meter.
    /// </summary>
    [Fact]
    public async Task AWarmedWindowAsksTheStoreNothing()
    {
        string directory = CacheDirectory();
        Dictionary<string, long> counted = new Dictionary<string, long>(StringComparer.Ordinal);
        using System.Diagnostics.Metrics.MeterListener listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, listening) =>
        {
            if (instrument.Meter.Name == VortexDiagnostics.MeterName && instrument.Name.StartsWith("vortex.synopsis_cache.", StringComparison.Ordinal))
            {
                listening.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (counted)
            {
                counted[instrument.Name] = counted.GetValueOrDefault(instrument.Name) + value;
            }
        });
        listener.Start();
        try
        {
            await using MemoryObjectStore inner = new MemoryObjectStore();
            await using CountingObjectStore store = new CountingObjectStore(inner);
            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64 << 20));
            await using VortexDataset dataset = await FilledAsync(store, session: session);
            _ = await dataset.Scan<Request>().Where(r => r.At >= Noon).CountAsync(Ct);
            DateTimeOffset hour = Noon.AddHours(1);
            long warmed = await dataset.WarmAsync<Request>(r => r.At >= hour, Ct);
            store.Reset();
            (List<string> answer, ScanMetrics metrics) = await WindowAsync(dataset, hour, hour.AddHours(1));
            Console.Out.Write($"WARMED: {warmed} bytes, then {store.Requests} requests for the hour.\n");
            Assert.True(warmed > 0);
            Assert.True(metrics.SynopsisRows > 0);
            Assert.Equal(0, store.Requests);
            Assert.Equal(0, await dataset.WarmAsync<Request>(r => r.At >= hour, Ct));
            (List<string> rows, _) = await GroupsAsync(dataset.Scan<Request>().With(new ScanOptions { UseSynopses = false })
                .Where(r => r.At >= hour && r.At < hour.AddHours(1)).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));
            Assert.Equal(rows, answer);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        lock (counted)
        {
            Assert.True(counted.GetValueOrDefault("vortex.synopsis_cache.bytes_from_store") > 0);
            Assert.True(counted.GetValueOrDefault("vortex.synopsis_cache.hits") > 0);
            Assert.True(counted.GetValueOrDefault("vortex.synopsis_cache.bytes_from_disk") > 0);
        }
    }

    /// <summary>A count and a sum of bytes by method over a window, and what reading it did.</summary>
    private static Task<(List<string> Groups, ScanMetrics Metrics)> WindowAsync(VortexDataset dataset, DateTimeOffset from, DateTimeOffset to) =>
        GroupsAsync(dataset.Scan<Request>().Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))));

    /// <summary>A session that seals under <paramref name="keyring"/>, with a synopsis cache in <paramref name="directory"/>.</summary>
    private static VortexSession Sealed(VortexKeyring keyring, string directory) => VortexSession.Create(options =>
    {
        options.Keyring = keyring;
        options.SynopsisCache = new SynopsisCache(directory, 64 << 20);
    });

    /// <summary>
    /// Every file the cache in <paramref name="directory"/> keeps is bytes of one of the dataset's sealed objects
    /// as <paramref name="store"/> holds them: a frame, where its name places it, or a trailer, the object's end.
    /// At least one of each.
    /// </summary>
    private static async Task AssertSealedBytesOnlyAsync(VortexDataset dataset, MemoryObjectStore store, string directory)
    {
        Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            keys[held.Entry.Uid.ToString("x32", CultureInfo.InvariantCulture)] = held.Entry.Key;
        }

        int frames = 0;
        int trailers = 0;
        foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != ".lock"))
        {
            string[] parts = Path.GetFileName(file).Split('-');
            byte[] kept = await System.IO.File.ReadAllBytesAsync(file, Ct);
            string key = keys[parts[0]];
            ObjectHead head = (await store.HeadAsync(key, Ct))!.Value;
            using ObjectRange range = await store.GetRangeAsync(key, 0, (int)head.Length, Ct);
            byte[] stored = range.Bytes.ToArray();
            if (parts[^1] == "trailer")
            {
                Assert.Equal(stored[^kept.Length..], kept);
                trailers++;
                continue;
            }

            Assert.Equal("sealed", parts[^1]);
            int offset = Convert.ToInt32(parts[1], 16);
            Assert.Equal(stored.AsSpan(offset, kept.Length).ToArray(), kept);
            frames++;
        }

        Console.Out.Write($"SEALED KEPT: {frames} frames and {trailers} trailers, each the store's bytes.\n");
        Assert.True(frames > 0, "no frame kept");
        Assert.True(trailers > 0, "no trailer kept");
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

    private static string CacheDirectory() => Path.Combine(Path.GetTempPath(), "vorticity-synopsis-cache-" + Guid.NewGuid().ToString("N"));
}
