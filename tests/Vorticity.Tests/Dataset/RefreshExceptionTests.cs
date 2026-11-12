using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.ObjectStorage.Credentials;
using Vorticity.Tests.ObjectStorage;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A refresh that finds nothing new asks for the next commit, which is absent, in one round trip with a head: the store
/// answers that absence and no exception is thrown on the way, on every store of the library, behind one that counts its
/// requests as without, and on an encrypted dataset. Exceptions are counted on the test's own flow, whatever runs beside it.
/// </summary>
public sealed partial class RefreshExceptionTests
{
    private const int Refreshes = 10;

    private static readonly AsyncLocal<StrongBox<int>?> Thrown = new AsyncLocal<StrongBox<int>?>();

    static RefreshExceptionTests() =>
        AppDomain.CurrentDomain.FirstChanceException += static (_, _) =>
        {
            if (Thrown.Value is { } count)
            {
                Interlocked.Increment(ref count.Value);
            }
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("memory", false)]
    [InlineData("memory", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("prefixed", true)]
    [InlineData("s3", false)]
    [InlineData("s3", true)]
    [InlineData("azure", true)]
    [InlineData("google", true)]
    public async Task ARefreshThatFindsNothingNewThrowsNothing(string kind, bool counted)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "refresh", Guid.NewGuid().ToString("N"));
        using S3Emulator s3 = new S3Emulator();
        using AzureEmulator azure = new AzureEmulator();
        using GoogleEmulator google = new GoogleEmulator();
        IObjectStore inner = kind switch
        {
            "memory" => new MemoryObjectStore(),
            "file" => new FileObjectStore(directory),
            "prefixed" => new PrefixedObjectStore(new MemoryObjectStore(), "lake/"),
            "s3" => S3(s3),
            "azure" => AzureTests.Store(azure),
            _ => GoogleTests.Store(google),
        };

        try
        {
            CountingObjectStore? counting = counted ? new CountingObjectStore(inner) : null;
            IObjectStore store = (IObjectStore?)counting ?? inner;
            (int thrown, long reads, long heads) = await RefreshAsync(store, counting, new DatasetOptions());
            Assert.Equal(0, thrown);
            if (counting is not null)
            {
                Assert.Equal((Refreshes, Refreshes), (reads, heads));
            }
        }
        finally
        {
            await inner.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnEncryptedDatasetsRefreshThrowsNothing()
    {
        using VortexKeyring keyring = Sealing.SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(options => options.Keyring = keyring);
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        (int thrown, long reads, long heads) = await RefreshAsync(store, store, new DatasetOptions { Session = session, Encrypted = true });
        Assert.Equal(0, thrown);
        Assert.Equal((Refreshes, Refreshes), (reads, heads));
    }

    [Fact]
    public async Task ARefreshThatFindsACommitStillReadsItInOneRoundTrip()
    {
        // The read that finds nothing is the read that finds the next commit, when there is one: no request more.
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, Point.Schema, new DatasetOptions(), Ct);
        await AppendAsync(writer, 1);
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        await reader.RefreshAsync(Ct);
        for (int i = 0; i < Refreshes; i++)
        {
            await AppendAsync(writer, i + 2);
            store.Reset();
            Thrown.Value = new StrongBox<int>();
            Assert.Equal(writer.Version, await reader.RefreshAsync(Ct));
            Assert.Equal(0, Thrown.Value.Value);
            Thrown.Value = null;
            Assert.Equal((1, 1), (store.CountOf(ObjectOperation.Read), store.CountOf(ObjectOperation.Head)));
        }
    }

    /// <summary>
    /// A dataset of one commit, opened by a reader that refreshes once, then <see cref="Refreshes"/> times with nothing new:
    /// the exceptions thrown on those refreshes, and the reads and heads <paramref name="counting"/> saw.
    /// </summary>
    private static async Task<(int Thrown, long Reads, long Heads)> RefreshAsync(IObjectStore store, CountingObjectStore? counting, DatasetOptions options)
    {
        await using VortexDataset writer = await VortexDataset.CreateAsync(store, Point.Schema, options, Ct);
        await AppendAsync(writer, 1);
        await using VortexDataset reader = await VortexDataset.OpenAsync(store, options, Ct);
        await reader.RefreshAsync(Ct);
        counting?.Reset();
        StrongBox<int> thrown = new StrongBox<int>();
        Thrown.Value = thrown;
        try
        {
            for (int i = 0; i < Refreshes; i++)
            {
                Assert.Equal(writer.Version, await reader.RefreshAsync(Ct));
            }
        }
        finally
        {
            Thrown.Value = null;
        }

        return (thrown.Value, counting?.CountOf(ObjectOperation.Read) ?? 0, counting?.CountOf(ObjectOperation.Head) ?? 0);
    }

    private static async Task AppendAsync(VortexDataset dataset, long at)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Point>([new Point(at, at * 0.5)], Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    private static S3ObjectStore S3(S3Emulator emulator) =>
        new S3ObjectStore(new S3StoreOptions
        {
            Bucket = "lake",
            Region = "eu-west-3",
            Credentials = StoreCredentials.AccessKey(S3Emulator.AccessKey, S3Emulator.Secret),
            Handler = emulator,
            Transport = new StoreTransportOptions { RetryBaseDelay = TimeSpan.Zero },
        });

    [VortexRecord]
    public partial record struct Point(long At, double Value);
}
