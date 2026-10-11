// Locations: a session's registry of stores, the longest prefix serving a location, a scheme whose
// buckets get a store on first use, and a file or a dataset opened at a location as at a path.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.Tests.Dataset;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed partial class LocationTests
{
    internal const int ReadingRows = 20_000;

    private static readonly Lazy<Task<string>> Readings = new Lazy<Task<string>>(WriteReadingsAsync);

    [VortexRecord]
    public partial record struct Reading(int Day, double Celsius);

    /// <summary>A file of <see cref="ReadingRows"/> readings, written once per run.</summary>
    internal static Task<string> ReadingsFileAsync() => Readings.Value;

    [Theory]
    [InlineData("s3://lake/readings.vortex", true)]
    [InlineData("gs://warehouse", true)]
    [InlineData("r2+fast.v2://media/x", true)]
    [InlineData("/data/readings.vortex", false)]
    [InlineData("C:\\data\\readings.vortex", false)]
    [InlineData("relative/readings.vortex", false)]
    [InlineData("://lake/x", false)]
    [InlineData("1s3://lake/x", false)]
    public void AStringWithASchemeIsALocation(string text, bool location) =>
        Assert.Equal(location, ObjectStoreRegistry.IsLocation(text));

    [Fact]
    public async Task AFileAtALocationIsNotAppendedTo()
    {
        // An object cannot grow: the append is refused before any request, and the object is left alone.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore lake = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", lake));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await session.OpenWriterAsync("mem://lake/readings.vortex", cancellationToken: ct));
        Assert.Empty(await lake.ListEntriesAsync(string.Empty, ct));
    }

    [Fact]
    public async Task TheLongestRegisteredPrefixServesALocation()
    {
        await using MemoryObjectStore lake = new MemoryObjectStore();
        await using MemoryObjectStore tables = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.ObjectStores.Add("mem://lake/", lake);
            o.ObjectStores.Add("mem://lake/tables", tables);
        });

        Assert.Equal((tables, "tables/one.vortex"), Located(session, "mem://lake/tables/one.vortex"));
        Assert.Equal((lake, "tablespoon.vortex"), Located(session, "mem://lake/tablespoon.vortex"));
        Assert.Equal((lake, "other/one.vortex"), Located(session, "mem://lake/other/one.vortex"));

        // A bucket's name is matched whole: lake serves nothing of laker.
        Assert.Throws<InvalidOperationException>(() => session.Options.ObjectStores.Locate("mem://laker/one.vortex"));
        Assert.Throws<ArgumentException>(() => session.Options.ObjectStores.Locate("/local/path"));
    }

    [Fact]
    public async Task ASchemeBuildsAStorePerBucketOnFirstUseAndTheSessionDisposesEveryStore()
    {
        List<TrackedStore> built = [];
        TrackedStore registered = new TrackedStore("registered");
        VortexSession session = VortexSession.Create(o =>
        {
            o.ObjectStores.Add("mem://fixed", registered);
            o.ObjectStores.AddScheme("mem", bucket =>
            {
                TrackedStore store = new TrackedStore(bucket);
                built.Add(store);
                return store;
            });
        });

        IObjectStore first = session.Options.ObjectStores.Locate("mem://one/a").Store;
        Assert.Same(first, session.Options.ObjectStores.Locate("mem://one/b").Store);
        Assert.NotSame(first, session.Options.ObjectStores.Locate("mem://two/a").Store);
        Assert.Same(registered, session.Options.ObjectStores.Locate("mem://fixed/a").Store);
        Assert.Equal(["one", "two"], built.Select(s => s.Name));

        await session.DisposeAsync();
        Assert.All(built, store => Assert.Equal(1, store.Disposed));
        Assert.Equal(1, registered.Disposed);
        Assert.Throws<ObjectDisposedException>(() => session.Options.ObjectStores.Locate("mem://one/a"));
    }

    [Fact]
    public async Task ALocationNoStoreServesSaysHowToRegisterOne()
    {
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await VortexSession.Default.OpenAsync("s3://lake/readings.vortex", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("o.ObjectStores.Add(\"s3://lake\", store)", refused.Message, StringComparison.Ordinal);
        Assert.Contains("UseEnvironment", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRegistryIsFrozenWithTheSession()
    {
        await using VortexSession session = VortexSession.Create(_ => { });
        await using MemoryObjectStore store = new MemoryObjectStore();
        Assert.Throws<InvalidOperationException>(() => session.Options.ObjectStores.Add("mem://late", store));
        Assert.Throws<InvalidOperationException>(() => session.Options.ObjectStores.AddScheme("late", _ => store));
        Assert.Throws<ArgumentException>(() => VortexSession.Create(o => o.ObjectStores.Add("not-a-location", store)));
    }

    [Fact]
    public async Task AFileOpensAtALocationInOneRequestOnAStoreThatReadsSuffixes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryObjectStore memory = new MemoryObjectStore();
        await memory.PutIfAbsentAsync("readings/one.vortex", await System.IO.File.ReadAllBytesAsync(await ReadingsFileAsync(), ct), ct);
        CountingObjectStore counting = new CountingObjectStore(memory);
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", counting));
        await using (VortexFile file = await session.OpenAsync("mem://lake/readings/one.vortex", cancellationToken: ct))
        {
            Assert.Equal((1L, 0L), (counting.Requests, counting.CountOf(ObjectOperation.Head)));
            Assert.Equal(ReadingRows, await file.Scan<Reading>().CountAsync(ct));
            Assert.Equal(ReadingRows, await file.Scan<Reading>().Where(r => r.Day >= 0).CountAsync(ct));
        }

        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await session.OpenAsync("mem://lake/readings/none.vortex", cancellationToken: ct));
    }

    [Fact]
    public async Task ADatasetLivesUnderALocationAsUnderADirectory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryObjectStore memory = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", memory));
        DatasetOptions options = new DatasetOptions { Session = session };
        await using (VortexDataset dataset = await VortexDataset.CreateAsync("mem://lake/tables/readings", Reading.Schema, options, ct))
        {
            await AppendAsync(dataset, 100, ct);
        }

        // Every key of the dataset lies under the location, as under a directory.
        List<ObjectListing> all = await memory.ListAsync(string.Empty, null, ct).ToListAsync(ct);
        Assert.NotEmpty(all);
        Assert.All(all, o => Assert.StartsWith("tables/readings/", o.Key, StringComparison.Ordinal));

        await using (VortexDataset reopened = await VortexDataset.OpenAsync("mem://lake/tables/readings/", options, ct))
        {
            Assert.Equal(100, await reopened.Scan<Reading>().CountAsync(ct));
        }

        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await VortexDataset.OpenAsync("mem://lake/tables/none", options, ct));
    }

    [Fact]
    public async Task ADatasetAtALocalDirectoryOpensAFileStoreThere()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-location-{Guid.NewGuid():N}");
        try
        {
            await using (VortexDataset dataset = await VortexDataset.CreateAsync(directory, Reading.Schema, cancellationToken: ct))
            {
                await AppendAsync(dataset, 50, ct);
            }

            Assert.NotEmpty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            await using VortexDataset reopened = await VortexDataset.OpenAsync(directory, cancellationToken: ct);
            Assert.Equal(50, await reopened.Scan<Reading>().CountAsync(ct));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AViewUnderAPrefixSharesItsStoreAndStripsItsKeys()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TrackedStore memory = new TrackedStore("lake");
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", memory));
        IObjectStore view = session.Options.ObjectStores.Resolve("mem://lake/tables/readings");
        await view.PutIfAbsentAsync("commit/1", new byte[] { 1 }, ct);
        Assert.NotNull(await memory.HeadAsync("tables/readings/commit/1", ct));
        Assert.Equal(["commit/1"], await view.ListAsync(string.Empty, null, ct).Select(o => o.Key).ToListAsync(ct));
        Assert.Equal(["commit/1"], await view.ListAsync("comm", "comm", ct).Select(o => o.Key).ToListAsync(ct));
        await view.DeleteAsync(["commit/1"], ct);
        Assert.Null(await memory.HeadAsync("tables/readings/commit/1", ct));

        await view.DisposeAsync();
        Assert.Equal(0, memory.Disposed);
    }

    [Fact]
    public async Task TheEnvironmentServesEveryS3BucketWithAStoreOfItsOwn()
    {
        VortexSession session = VortexSession.Create(o => o.ObjectStores.UseEnvironment());
        S3ObjectStore lake = Assert.IsType<S3ObjectStore>(session.Options.ObjectStores.Locate("s3://lake/a.vortex").Store);
        S3ObjectStore other = Assert.IsType<S3ObjectStore>(session.Options.ObjectStores.Locate("s3://other/b.vortex").Store);
        Assert.Equal(("lake", "other"), (lake.Bucket, other.Bucket));
        Assert.Same(lake, session.Options.ObjectStores.Locate("s3://lake/c.vortex").Store);
        await session.DisposeAsync();
    }

    private static (IObjectStore Store, string Key) Located(VortexSession session, string location) =>
        session.Options.ObjectStores.Locate(location);

    private static async Task AppendAsync(VortexDataset dataset, int rows, CancellationToken ct)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Reading>([.. Enumerable.Range(0, rows).Select(i => new Reading(i, i * 0.25))], ct);
        await dataset.AppendAsync(draft, ct);
    }

    private static async Task<string> WriteReadingsAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-readings-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Reading[] rows = [.. Enumerable.Range(0, ReadingRows).Select(i => new Reading(i / 100, (i % 400) / 10.0))];
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
        {
            await writer.WriteAsync<Reading>(rows, CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        return path;
    }

    /// <summary>A memory store that counts its disposals.</summary>
    private sealed class TrackedStore(string name) : ForwardingStore(new MemoryObjectStore())
    {
        internal string Name => name;

        internal int Disposed;

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed);
            return ValueTask.CompletedTask;
        }
    }
}
