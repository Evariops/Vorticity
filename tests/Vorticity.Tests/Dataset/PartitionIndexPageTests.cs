// The pages of an index of a partition (version 4): each entry keeps the high bits of its hashes, those its postings need
// and ten more, and packs the gaps of those cut hashes and the postings' places at their bits; a hash's postings lie in one
// page, a page ends past 64 KiB, and a gap past 32 bits starts a page.
//
// THE ORACLE IS THE POSTINGS WRITTEN: every page decodes to them, in order, each hash cut to the entry's bits and its
// places once each; a lookup of a whole hash finds the places of every hash cut alike, and those alone.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class PartitionIndexPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The bits an entry keeps: those that number its postings, rounded up, and ten more, 64 at most.</summary>
    [Fact]
    public void AnEntryKeepsTheBitsItsPostingsNeedAndTenMore()
    {
        Assert.Equal(10, PartitionIndex.HashBitsFor(0));
        Assert.Equal(10, PartitionIndex.HashBitsFor(1));
        Assert.Equal(11, PartitionIndex.HashBitsFor(2));
        Assert.Equal(30, PartitionIndex.HashBitsFor(1 << 20));
        Assert.Equal(31, PartitionIndex.HashBitsFor((1 << 20) + 1));
        Assert.Equal(64, PartitionIndex.HashBitsFor(long.MaxValue));
    }

    /// <summary>
    /// 120 000 postings of an identifier over five objects, a unit of four blocks a place, and 30 000 of a located column with
    /// values everywhere in some objects: the pages decode to the postings written and find each hash's places, a whole hash
    /// sought as a lookup seeks it; a hash absent finds only the places of hashes cut alike.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PagesHoldTheirPostingsAndFindThem(int seed)
    {
        Random random = new Random(seed);
        PartitionIndex.Covered[] objects =
        [
            new PartitionIndex.Covered(1u, 1_000_000, 8_192),
            new PartitionIndex.Covered(2u, 50_000, 8_192),
            new PartitionIndex.Covered(3u, 8_192, 8_192),
            new PartitionIndex.Covered(4u, 3_000_000, 8_192),
            new PartitionIndex.Covered(5u, 1, 8_192),
        ];
        List<(ulong Hash, int Rank, uint Block)> identifier = Postings(random, objects, 120_000, unitBlocks: 4, everywhere: false);
        List<(ulong Hash, int Rank, uint Block)> located = Postings(random, objects, 30_000, unitBlocks: 1, everywhere: true);
        int identifierBits = PartitionIndex.HashBitsFor(identifier.Count);
        int locatedBits = PartitionIndex.HashBitsFor(located.Count);

        await using MemoryObjectStore store = new MemoryObjectStore();
        ObjectSegmentSink sink = new ObjectSegmentSink(store, "index");
        using (PartitionIndex.Writer writer = new PartitionIndex.Writer(sink))
        {
            await WriteAsync(writer, "Id", PartitionIndex.EntryKind.Postings, 4, identifierBits, identifier, objects);
            await WriteAsync(writer, "Service", PartitionIndex.EntryKind.Locate, 1, locatedBits, located, objects);
            await writer.FinishAsync(objects, Ct);
        }

        Assert.Equal(PutOutcome.Created, await sink.CommitAsync(Ct));
        byte[] bytes = new byte[sink.StoredLength];
        await store.ReadAsync("index", 0, bytes, null, Ct);
        Assert.True(PartitionIndex.TryTail(bytes.AsSpan(bytes.Length - PartitionIndex.TailBytes), out long offset, out int length, out ulong checksum));
        PartitionIndex index = PartitionIndex.Read(bytes.AsSpan((int)offset, length), checksum) ?? throw new InvalidOperationException("the directory does not decode");
        Assert.Equal(objects, index.Objects);

        Hold(bytes, index.Of("Id", PartitionIndex.EntryKind.Postings)!, identifier, identifierBits, random);
        Hold(bytes, index.Of("Service", PartitionIndex.EntryKind.Locate)!, located, locatedBits, random);
    }

    /// <summary>
    /// A few postings that keep every bit of their hashes: gaps past 32 bits start pages of their own, each found by its
    /// first hash.
    /// </summary>
    [Fact]
    public async Task AGapPastThirtyTwoBitsStartsAPage()
    {
        PartitionIndex.Covered[] objects = [new PartitionIndex.Covered(9u, 100_000, 8_192)];
        ulong[] hashes = [0x0000_0000_0000_1000, 0x0000_0000_0000_2000, 0x0000_0002_0000_0000, 0x8000_0000_0000_0000, 0xFFFF_FFFF_FFFF_FFF0];
        List<(ulong Hash, int Rank, uint Block)> postings = [.. hashes.Select((hash, i) => (hash, 0, (uint)i))];
        await using MemoryObjectStore store = new MemoryObjectStore();
        ObjectSegmentSink sink = new ObjectSegmentSink(store, "index");
        using (PartitionIndex.Writer writer = new PartitionIndex.Writer(sink))
        {
            await WriteAsync(writer, "Id", PartitionIndex.EntryKind.Postings, 1, 64, postings, objects);
            await writer.FinishAsync(objects, Ct);
        }

        await sink.CommitAsync(Ct);
        byte[] bytes = new byte[sink.StoredLength];
        await store.ReadAsync("index", 0, bytes, null, Ct);
        PartitionIndex.TryTail(bytes.AsSpan(bytes.Length - PartitionIndex.TailBytes), out long offset, out int length, out ulong checksum);
        PartitionIndex.Entry entry = PartitionIndex.Read(bytes.AsSpan((int)offset, length), checksum)!.Of("Id", PartitionIndex.EntryKind.Postings)!;
        Assert.Equal(4, entry.Pages.Count);
        Hold(bytes, entry, postings, 64, new Random(3));
    }

    /// <summary>
    /// <paramref name="count"/> postings over <paramref name="objects"/>, hashes drawn so that some repeat across objects
    /// and units, sorted by hash, rank and block as an index merges them; a located column's values everywhere in an object now and then.
    /// </summary>
    private static List<(ulong Hash, int Rank, uint Block)> Postings(Random random, PartitionIndex.Covered[] objects, int count, int unitBlocks, bool everywhere)
    {
        uint[] bases = PartitionIndex.Bases(objects, unitBlocks);
        ulong[] values = new ulong[count / 3];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63);
        }

        List<(ulong Hash, int Rank, uint Block)> postings = [];
        for (int i = 0; i < count; i++)
        {
            int rank = random.Next(objects.Length);
            uint units = bases[rank + 1] - bases[rank] - 1;
            uint block = everywhere && random.Next(20) == 0 ? PartitionIndex.Everywhere : (uint)random.Next((int)units);
            postings.Add((values[random.Next(values.Length)], rank, block));
        }

        return [.. postings.Distinct().OrderBy(static p => p.Hash).ThenBy(static p => p.Rank).ThenBy(static p => p.Block)];
    }

    /// <summary>Writes an entry of <paramref name="postings"/>, each hash cut to <paramref name="hashBits"/>, as a consolidation does.</summary>
    private static async Task WriteAsync(
        PartitionIndex.Writer writer, string name, PartitionIndex.EntryKind kind, int unitBlocks, int hashBits,
        List<(ulong Hash, int Rank, uint Block)> postings, PartitionIndex.Covered[] objects)
    {
        writer.Begin(name, kind, unitBlocks, [ulong.MaxValue], hashBits, objects);
        foreach ((ulong hash, int rank, uint block) in postings)
        {
            writer.Add(Cut(hash, hashBits), rank, block);
            await writer.FlushIfFullAsync(Ct);
        }

        await writer.EndAsync(Ct);
    }

    /// <summary>
    /// The entry's pages decode to <paramref name="postings"/> cut to its bits, each hash's places once, in order; each a
    /// page of 64 KiB or one hash past it; a lookup of a written hash, and of hashes no posting holds, finds the places of
    /// every hash cut alike.
    /// </summary>
    private static void Hold(byte[] bytes, PartitionIndex.Entry entry, List<(ulong Hash, int Rank, uint Block)> postings, int hashBits, Random random)
    {
        Assert.Equal(hashBits, entry.HashBits);
        List<(ulong Hash, int Rank, uint Block)> want = [.. postings.Select(p => (Cut(p.Hash, hashBits), p.Rank, p.Block)).Distinct()
            .OrderBy(static p => p.Item1).ThenBy(static p => p.Rank).ThenBy(static p => p.Block)];
        Assert.Equal(want.Count, entry.Postings);
        List<PartitionIndex.Posting> decoded = [];
        foreach (PartitionIndex.Page page in entry.Pages)
        {
            int before = decoded.Count;
            ReadOnlySpan<byte> span = bytes.AsSpan((int)page.Offset, page.Length);
            entry.Decode(span, page, decoded);
            Assert.Equal(page.FirstHash, decoded[before].Hash);
            Assert.True(page.Length < PartitionIndex.PageBytes + (64 << 10), $"a page of {page.Length} bytes");
        }

        Assert.Equal(want, decoded.Select(static p => (p.Hash, p.Rank, p.Block)));
        ILookup<ulong, (int Rank, uint Block)> byCut = want.ToLookup(static p => p.Item1, static p => (p.Rank, p.Block));
        List<ulong> sought = [.. postings.Where((_, i) => i % 37 == 0).Select(static p => p.Hash)];
        for (int i = 0; i < 200; i++)
        {
            sought.Add((ulong)random.NextInt64());
        }

        List<(int Rank, uint Block)> found = [];
        foreach (ulong hash in sought)
        {
            found.Clear();
            int at = entry.PageOf(hash);
            if (at >= 0)
            {
                PartitionIndex.Page page = entry.Pages[at];
                entry.Find(bytes.AsSpan((int)page.Offset, page.Length), page, hash, found);
            }

            Assert.Equal(byCut[Cut(hash, hashBits)].OrderBy(static p => p.Rank).ThenBy(static p => p.Block), found.OrderBy(static p => p.Rank).ThenBy(static p => p.Block));
        }
    }

    private static ulong Cut(ulong hash, int bits) => bits >= 64 ? hash : hash >> (64 - bits);
}
