using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.ObjectStorage;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.IO;

/// <summary>What a read from an object store holds, and for whom.</summary>
public sealed class ObjectStoreSourceTests
{
    /// <summary>
    /// A segment longer than the pool's largest block, read from a store, is counted for the scan that reads it, as a local
    /// read's is: allocated outside any account, a segment another writer made 82 MB went unseen by the governor.
    /// </summary>
    [Fact]
    public async Task ASegmentPastThePoolsLargestBlockIsCountedForItsReader()
    {
        const int Length = 40 << 20;
        byte[] content = new byte[Length];
        content[Length - 1] = 7;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutAsync("big", PipeReader.Create(new ReadOnlySequence<byte>(content)), Length, PutCondition.None, TestContext.Current.CancellationToken);

        await using ObjectStoreSource source = new ObjectStoreSource(store, "big", Length);
        CountedAccount account = new CountedAccount(MemoryAccount.Caller);
        using (SegmentRequestSet requests = new SegmentRequestSet { Account = account })
        {
            requests.Add(new SegmentSpec(0, Length, 0, 0, 0));
            await source.ReadManyAsync(requests, TestContext.Current.CancellationToken);
            Assert.Equal(7, requests.GetBuffer(0).Span[Length - 1]);
            Assert.True(account.Held >= Length, $"{account.Held} bytes counted for a segment of {Length}");
        }

        Assert.Equal(0, account.Held);
    }
}
