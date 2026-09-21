// The commit key, built so that one List(prefix: "commit/", max: 1) returns the newest commit
// with no hint and no probe.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class CommitKeyTests
{
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(42UL)]
    [InlineData(1_000_000UL)]
    [InlineData(ulong.MaxValue)]
    public void AVersionRoundTripsThroughItsKey(ulong version)
    {
        string key = CommitKey.For(version);
        Assert.StartsWith("commit/", key, StringComparison.Ordinal);
        Assert.EndsWith(".vxc", key, StringComparison.Ordinal);
        Assert.Equal(20, key.Length - "commit/".Length - ".vxc".Length);
        Assert.True(ObjectKey.IsValid(key));

        Assert.True(CommitKey.TryParse(key, out ulong parsed));
        Assert.Equal(version, parsed);
    }

    [Fact]
    public void TheNewestVersionSortsFirst()
    {
        // The whole point: ordinal order over the keys is descending order over the versions.
        ulong[] versions = [1, 2, 3, 10, 99, 100, 1_000_000, ulong.MaxValue];
        List<string> keys = [.. versions.Select(CommitKey.For)];
        List<string> sorted = [.. keys.OrderBy(key => key, StringComparer.Ordinal)];

        Assert.Equal(keys.AsEnumerable().Reverse(), sorted);
    }

    [Fact]
    public async Task OneListingOfOneKeyFindsTheNewestCommit()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        foreach (ulong committed in new ulong[] { 1, 2, 3, 17, 4 })
        {
            await store.PutIfAbsentAsync(CommitKey.For(committed), new byte[] { 1 }, default);
        }

        // A data object under another prefix must not be in the way.
        await store.PutIfAbsentAsync(CommitKey.ForData("abcdef"), new byte[] { 1 }, default);
        store.Reset();

        IReadOnlyList<string> newest = await store.ListAsync(CommitKey.Prefix, null, 1, default);
        Assert.True(CommitKey.TryParse(Assert.Single(newest), out ulong version));
        Assert.Equal(17UL, version);
        Assert.Equal(1, store.Requests);
    }

    [Fact]
    public void WhatIsNotACommitKeyIsNotParsed()
    {
        foreach (string key in new[]
        {
            "",
            "commit/1.vxc",
            "commit/99999999999999999999.vxc",
            "commit/0000000000000000000a.vxc",
            "data/abc.vortex",
            "commit/99999999999999999998.vxcx",
        })
        {
            Assert.False(CommitKey.TryParse(key, out _), key);
        }

        Assert.False(CommitKey.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommitKey.For(0));
    }

    [Fact]
    public void ADataKeyIsAKey()
    {
        string key = CommitKey.ForData("0123456789abcdef0123456789abcdef");
        Assert.Equal("data/0123456789abcdef0123456789abcdef.vortex", key);
        Assert.True(ObjectKey.IsValid(key));
        Assert.Throws<ArgumentException>(() => CommitKey.ForData("a/b"));
        Assert.Throws<ArgumentException>(() => CommitKey.ForData(string.Empty));
    }
}
