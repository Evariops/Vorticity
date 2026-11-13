// The commit objects of an encrypted dataset, read by several reads of one handle at once.
//
// WHAT IS HELD: reads that resolve one commit object at once each decrypt it, and so does a read
// that took the layout another one kept, whatever the store lets go of meanwhile: a second
// resolution of the same object, the object deleted, or the store disposed. The store under them
// holds every read of the commit object back until the test lets it answer, so each interleaving is
// chosen, never timed.
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Api;
using Vorticity.Tests.Dataset;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedCommitReadTests
{
    /// <summary>The most bytes a read asks of a commit object: more than one of a few rows holds.</summary>
    private const int ReadBytes = 1 << 16;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ReadsThatResolveACommitObjectAtOnceEachDecryptIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(o => o.Keyring = keyring);
        await using MemoryObjectStore memory = await EncryptedStoreAsync(session, ct);
        string key = await NewestCommitAsync(memory, ct);
        byte[] expected = await PlaintextAsync(memory, session, key, ct);

        HeldReads held = new HeldReads(memory, key);
        await using SealedObjectStore store = (SealedObjectStore)SealedObjectStore.Over(held, session);

        // Two reads find no layout and resolve the object. The first one's layout is kept, and a third read takes it and
        // waits for its frames while the second resolution lands.
        Task<byte[]> first = ReadAsync(store, key, ct);
        TaskCompletionSource firstHead = await held.NextAsync(ct);
        Task<byte[]> second = ReadAsync(store, key, ct);
        TaskCompletionSource secondHead = await held.NextAsync(ct);
        firstHead.SetResult();
        Assert.Equal(expected, await first.WaitAsync(Patience, ct));

        Task<byte[]> third = ReadAsync(store, key, ct);
        TaskCompletionSource thirdFrames = await held.NextAsync(ct);
        secondHead.SetResult();
        Assert.Equal(expected, await second.WaitAsync(Patience, ct));
        thirdFrames.SetResult();
        Assert.Equal(expected, await third.WaitAsync(Patience, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadDecryptsWithTheLayoutItTookWhateverTheStoreLetsGoOf(bool dispose)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = VortexSession.Create(o => o.Keyring = keyring);
        await using MemoryObjectStore memory = await EncryptedStoreAsync(session, ct);
        string key = await NewestCommitAsync(memory, ct);
        byte[] expected = await PlaintextAsync(memory, session, key, ct);

        HeldReads held = new HeldReads(memory, key);
        await using SealedObjectStore store = (SealedObjectStore)SealedObjectStore.Over(held, session);
        Task<byte[]> first = ReadAsync(store, key, ct);
        (await held.NextAsync(ct)).SetResult();
        Assert.Equal(expected, await first.WaitAsync(Patience, ct));

        // A read takes the layout kept and has its frames in hand when the store lets go of the layout.
        Task<byte[]> second = ReadAsync(store, key, ct);
        TaskCompletionSource frames = await held.NextAsync(ct);
        if (dispose)
        {
            await store.DisposeAsync();
        }
        else
        {
            await store.DeleteAsync([key], ct);
        }

        frames.SetResult();
        Assert.Equal(expected, await second.WaitAsync(Patience, ct));
    }

    private static async Task<MemoryObjectStore> EncryptedStoreAsync(VortexSession session, CancellationToken ct)
    {
        MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Reading.Schema, new DatasetOptions { Session = session, Encrypted = true }, ct);
        ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Reading>(SealedObjects.Rows(100).ToArray(), ct);
        await dataset.AppendAsync(draft, ct);
        return store;
    }

    private static async Task<string> NewestCommitAsync(IObjectStore store, CancellationToken ct)
    {
        string? newest = null;
        await foreach ((string key, _, _) in store.ListAsync(CommitKey.Prefix, null, ct))
        {
            newest = key;
        }

        return newest ?? throw new InvalidOperationException("The dataset has no commit object.");
    }

    /// <summary>The commit object's plaintext, read alone by a store of its own.</summary>
    private static async Task<byte[]> PlaintextAsync(IObjectStore memory, VortexSession session, string key, CancellationToken ct)
    {
        await using IObjectStore store = SealedObjectStore.Over(memory, session);
        byte[] plaintext = await ReadAsync(store, key, ct);
        Assert.True(plaintext.AsSpan().StartsWith("VXCOMMIT"u8), $"'{key}' did not open to a commit object");
        return plaintext;
    }

    private static async Task<byte[]> ReadAsync(IObjectStore store, string key, CancellationToken ct)
    {
        using ObjectRange range = await store.GetRangeAsync(key, 0, ReadBytes, ct);
        return range.Bytes.ToArray();
    }

    /// <summary>A store whose every read of one key, once the store under it has answered, waits until the test lets it through.</summary>
    private sealed class HeldReads(IObjectStore inner, string held) : ForwardingStore(inner)
    {
        private readonly Channel<TaskCompletionSource> _arrived = Channel.CreateUnbounded<TaskCompletionSource>();

        /// <summary>The next read held, in the order they came, which the test lets through by setting it.</summary>
        internal async Task<TaskCompletionSource> NextAsync(CancellationToken ct) =>
            await _arrived.Reader.ReadAsync(ct).AsTask().WaitAsync(Patience, ct);

        public override async ValueTask<ObjectRead> ReadAsync(
            string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            ObjectRead read = await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
            if (string.Equals(key, held, StringComparison.Ordinal))
            {
                TaskCompletionSource through = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _arrived.Writer.TryWrite(through);
                await through.Task.WaitAsync(cancellationToken);
            }

            return read;
        }
    }
}
