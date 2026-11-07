// How a FileObjectStore shows what it creates. A commit object, the key writers race for and readers read before any
// version names it, is written beside its key under a name no key can have, then shown whole by an exclusive rename,
// or a link where the file system has no such rename, either refused when the key names an object. Any other creation
// claims its own key and writes in place under its lock. The claim the store once made for every key let a failed put
// delete another put's commit, made a put answer Exists for its own empty claim, and let RemoveTornCommitAsync delete a
// commit being written. These tests hold the code to what prevents each: no commit under its key until it is whole,
// nothing left under or beside a key by a put that fails, one put of many creating a key, and a repair that never meets
// a commit being written.
using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class FileObjectStorePutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vortex-file-puts-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A commit half written, shown by a rename or by a link: its key names nothing, a read and a head find no object, a
    /// listing shows no key, and the whole commit appears once the put returns Created.
    /// </summary>
    [Theory]
    [InlineData("rename")]
    [InlineData("link")]
    public async Task ACommitBeingWrittenIsNotUnderItsKey(string showing)
    {
        await using FileObjectStore store = Store(showing);
        string key = CommitKey.For(1);
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        ValueTask<PutOutcome> put = store.PutIfAbsentAsync(key, pipe.Reader, 8, Ct);
        await pipe.Writer.WriteAsync(Bytes("half"), Ct);

        Assert.Null(await store.HeadAsync(key, Ct));
        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync(key, 0, new byte[8], null, Ct));
        Assert.Empty(await store.ListAsync(CommitKey.Prefix, null, Ct).ToListAsync(Ct));

        await pipe.Writer.WriteAsync(Bytes("done"), Ct);
        await pipe.Writer.CompleteAsync();
        Assert.Equal(PutOutcome.Created, await put);
        byte[] whole = new byte[16];
        ObjectRead read = await store.ReadAsync(key, 0, whole, null, Ct);
        Assert.Equal("halfdone", Encoding.UTF8.GetString(whole, 0, read.BytesRead));
        Assert.Equal(PutOutcome.Exists, await store.PutIfAbsentAsync(key, Bytes("another"), Ct));
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A put whose content ends short of what it announced throws and leaves nothing: no object under its key, no file
    /// beside it, and the key free for the next put. That holds for a commit, shown by a rename, a link or a claim, and
    /// for a data object, which is always claimed.
    /// </summary>
    [Theory]
    [InlineData("rename", true)]
    [InlineData("link", true)]
    [InlineData("claim", true)]
    [InlineData("rename", false)]
    public async Task APutThatFailsLeavesNothingUnderOrBesideItsKey(string showing, bool commit)
    {
        await using FileObjectStore store = Store(showing);
        string key = commit ? CommitKey.For(3) : "data/short.vortex";
        Pipe pipe = new Pipe();
        ValueTask<PutOutcome> put = store.PutIfAbsentAsync(key, pipe.Reader, 16, Ct);
        await pipe.Writer.WriteAsync(Bytes("only eight"), Ct);
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<ObjectStoreException>(async () => await put);
        Assert.Null(await store.HeadAsync(key, Ct));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync(key, Bytes("the second put"), Ct));
    }

    /// <summary>
    /// Many puts of one key at once, through each way of showing it: exactly one creates it, the others are told it
    /// exists, the key holds the creator's bytes, and nothing is left beside it.
    /// </summary>
    [Theory]
    [InlineData("rename", true)]
    [InlineData("link", true)]
    [InlineData("claim", true)]
    [InlineData("rename", false)]
    public async Task OnePutOfManyCreatesTheKeyAndTheOthersAreToldItExists(string showing, bool commit)
    {
        await using FileObjectStore store = Store(showing);
        string key = commit ? CommitKey.For(2) : "data/raced.vortex";
        const int writers = 16;
        PutOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, writers).Select(writer =>
            Task.Run(async () => await store.PutIfAbsentAsync(key, Bytes($"writer {writer:00}"), Ct), Ct)));

        int creator = Array.IndexOf(outcomes, PutOutcome.Created);
        Assert.True(creator >= 0);
        Assert.Equal(writers - 1, outcomes.Count(outcome => outcome == PutOutcome.Exists));
        byte[] bytes = new byte[16];
        ObjectRead read = await store.ReadAsync(key, 0, bytes, null, Ct);
        Assert.Equal($"writer {creator:00}", Encoding.UTF8.GetString(bytes, 0, read.BytesRead));
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The repair of a torn commit run while the next commit is being written: it finds no commit to judge, removes
    /// nothing and waits for nothing, and the commit lands whole. A claim showed the commit under its key from its first
    /// byte, and the repair waited for its lock, or deleted it when it read it before the lock.
    /// </summary>
    [Fact]
    public async Task TheRepairOfATornCommitLeavesACommitBeingWrittenAlone()
    {
        await using FileObjectStore store = new FileObjectStore(_root);
        string key = CommitKey.For(1);
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        ValueTask<PutOutcome> put = store.PutIfAbsentAsync(key, pipe.Reader, 8, Ct);
        await pipe.Writer.WriteAsync(Bytes("half"), Ct);

        Assert.Null(await VortexDataset.RemoveTornCommitAsync(store, Ct));

        await pipe.Writer.WriteAsync(Bytes("done"), Ct);
        await pipe.Writer.CompleteAsync();
        Assert.Equal(PutOutcome.Created, await put);
        Assert.Equal(8, (await store.HeadAsync(key, Ct))!.Value.Length);
    }

    /// <summary>
    /// A file a put left beside its key when its process died is shown by no listing, and the first listing past a day
    /// takes it away; one younger stays, since its put may still be writing.
    /// </summary>
    [Fact]
    public async Task AFileAPutLeftBesideItsKeyGoesADayLater()
    {
        await using FileObjectStore store = new FileObjectStore(_root);
        await store.PutIfAbsentAsync("data/kept.vortex", Bytes("kept"), Ct);
        string directory = Path.Combine(_root, "data");
        string old = Path.Combine(directory, "kept.vortex.·0123456789abcdef0123456789abcdef");
        string young = Path.Combine(directory, "other.vortex.·fedcba9876543210fedcba9876543210");
        System.IO.File.WriteAllBytes(old, Bytes("left by a put that died"));
        System.IO.File.WriteAllBytes(young, Bytes("a put still writing"));
        System.IO.File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(2));

        Assert.Equal(new[] { "data/kept.vortex" }, (await store.ListAsync("data/", null, Ct).ToListAsync(Ct)).Select(listed => listed.Key));
        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(young));
    }

    /// <summary>A commit key under a location's prefix is a commit key; a data object's key, or a lookalike, is not.</summary>
    [Fact]
    public void ACommitKeyIsToldUnderAPrefixToo()
    {
        Assert.True(CommitKey.IsCommit(CommitKey.For(7)));
        Assert.True(CommitKey.IsCommit("datasets/requests/" + CommitKey.For(7)));
        Assert.False(CommitKey.IsCommit("data/" + CommitKey.For(7)[CommitKey.Prefix.Length..]));
        Assert.False(CommitKey.IsCommit("xcommit/" + CommitKey.For(7)[CommitKey.Prefix.Length..]));
        Assert.False(CommitKey.IsCommit("data/one.vortex"));
    }

    private FileObjectStore Store(string showing) => showing switch
    {
        "link" => new FileObjectStore(_root) { LinksKeys = true },
        "claim" => new FileObjectStore(_root) { ClaimsKeys = true },
        _ => new FileObjectStore(_root),
    };

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
