// The files a FileObjectStore keeps open after reading them (FileObjectStore.HeldFiles): a read that names the token of
// one reads that file, the bytes of the object the token names; a read with no token opens the key again; this store's
// own puts and deletes let the file go; and no more files stay open than the store keeps.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class FileObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vortex-held-files-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// An object read once, its file then deleted under the store: a read that names its token reads it whole from the file
    /// held open, and a read with no token finds no object; a store that keeps no file finds none either way.
    /// </summary>
    [Fact]
    public async Task AReadNamingTheTokenOfAHeldFileReadsItWhereverTheKeyWent()
    {
        foreach (int held in new[] { 64, 0 })
        {
            string root = Path.Combine(_root, held.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using FileObjectStore store = new FileObjectStore(root) { HeldFiles = held };
            await store.PutIfAbsentAsync("data/a.vortex", Encoding.UTF8.GetBytes("the first object"), Ct);
            byte[] buffer = new byte[64];
            ObjectRead first = await store.ReadAsync("data/a.vortex", 0, buffer, null, Ct);
            Assert.Equal("the first object", Encoding.UTF8.GetString(buffer, 0, first.BytesRead));

            System.IO.File.Delete(Path.Combine(root, "data", "a.vortex"));
            if (held > 0)
            {
                ObjectRead again = await store.ReadAsync("data/a.vortex", 4, buffer, first.Token, Ct);
                Assert.Equal("first object", Encoding.UTF8.GetString(buffer, 0, again.BytesRead));
                Assert.Equal(first.Token, again.Token);
            }
            else
            {
                await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync("data/a.vortex", 4, buffer, first.Token, Ct));
            }

            await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync("data/a.vortex", 0, buffer, null, Ct));
        }
    }

    /// <summary>
    /// The store's own delete and put of a key it holds the file of let that file go: a read that names the first object's
    /// token then meets the second, and is refused as before.
    /// </summary>
    [Fact]
    public async Task ThisStoresOwnWritesLetItsHeldFileGo()
    {
        await using FileObjectStore store = new FileObjectStore(_root);
        await store.PutIfAbsentAsync("data/b.vortex", Encoding.UTF8.GetBytes("one"), Ct);
        byte[] buffer = new byte[64];
        ObjectRead first = await store.ReadAsync("data/b.vortex", 0, buffer, null, Ct);

        await store.DeleteAsync(["data/b.vortex"], Ct);
        await store.PutIfAbsentAsync("data/b.vortex", Encoding.UTF8.GetBytes("two, longer"), Ct);
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.ReadAsync("data/b.vortex", 0, buffer, first.Token, Ct));
        ObjectRead second = await store.ReadAsync("data/b.vortex", 0, buffer, null, Ct);
        Assert.Equal("two, longer", Encoding.UTF8.GetString(buffer, 0, second.BytesRead));
        Assert.NotEqual(first.Token, second.Token);
    }

    /// <summary>
    /// A store that keeps two files, three objects read in turn: the first one's file is let go, and a read that names its
    /// token once its file is deleted finds nothing, where the last two are read from their files.
    /// </summary>
    [Fact]
    public async Task NoMoreFilesStayOpenThanTheStoreKeeps()
    {
        await using FileObjectStore store = new FileObjectStore(_root) { HeldFiles = 2 };
        List<string> tokens = [];
        byte[] buffer = new byte[64];
        foreach (string name in new[] { "c", "d", "e" })
        {
            await store.PutIfAbsentAsync($"data/{name}.vortex", Encoding.UTF8.GetBytes($"object {name}"), Ct);
            tokens.Add((await store.ReadAsync($"data/{name}.vortex", 0, buffer, null, Ct)).Token);
        }

        foreach (string name in new[] { "c", "d", "e" })
        {
            System.IO.File.Delete(Path.Combine(_root, "data", $"{name}.vortex"));
        }

        await Assert.ThrowsAsync<ObjectNotFoundException>(async () => await store.ReadAsync("data/c.vortex", 0, buffer, tokens[0], Ct));
        Assert.Equal("object d", Encoding.UTF8.GetString(buffer, 0, (await store.ReadAsync("data/d.vortex", 0, buffer, tokens[1], Ct)).BytesRead));
        Assert.Equal("object e", Encoding.UTF8.GetString(buffer, 0, (await store.ReadAsync("data/e.vortex", 0, buffer, tokens[2], Ct)).BytesRead));
    }
}
