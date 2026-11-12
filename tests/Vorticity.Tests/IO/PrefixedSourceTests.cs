// A source over a store under prefixes reads the store under them by the key the prefixes make, made once: nested
// prefixes join in order, a source over the prefixed store is the same source as one over the store under it with the
// whole key, and its bytes are the object's.
using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.IO;

public sealed class PrefixedSourceTests
{
    [Fact]
    public async Task NestedPrefixesJoinInOrderAndTheSourceReadsTheObject()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        byte[] bytes = new byte[8_192];
        new Random(7).NextBytes(bytes);
        await ((IObjectStore)store).PutAsync("a/b/key", new ReadOnlySequence<byte>(bytes), PutCondition.None, TestContext.Current.CancellationToken);

        IObjectStore nested = new PrefixedObjectStore(new PrefixedObjectStore(store, "a/"), "b/");
        (IObjectStore under, string key) = PrefixedObjectStore.Resolve(nested, "key");
        Assert.Same(store, under);
        Assert.Equal("a/b/key", key);

        await using ObjectStoreSource prefixed = new ObjectStoreSource(nested, "key", bytes.Length);
        await using ObjectStoreSource whole = new ObjectStoreSource(store, "a/b/key", bytes.Length);
        Assert.Equal(whole, prefixed);
        Assert.Equal(whole.GetHashCode(), prefixed.GetHashCode());

        using SegmentOwner owner = await prefixed.ReadAsync(new SegmentSpec(1_024, 2_048, 0, 0, 0), CancellationToken.None);
        Assert.True(owner.Buffer.Span.SequenceEqual(bytes.AsSpan(1_024, 2_048)));
    }
}
