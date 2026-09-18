// The commit object of docs/13-dataset.md §3: header, pages, fragments, table, XXH3-64, and the
// promise that opening it is ONE ranged read.
//
// THE TEAR TEST IS THE POINT OF THIS FILE. §7 says a torn or corrupt header "makes its version
// unreadable with the reason", and a store cannot tell a truncated object from a short one -- it
// hands back what it has. So the test builds a real commit object and truncates it at EVERY byte,
// requiring a CommitFormatException each time: never a wrong answer, never an index out of range,
// never a silent success. A format whose reader is bounds-checked only where someone thought to
// look fails this test at the byte nobody thought of.
using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class CommitObjectTests
{
    private static byte[] Page(byte seed, int length)
    {
        byte[] page = new byte[length];
        for (int i = 0; i < length; i++)
        {
            page[i] = (byte)(seed + (i * 7));
        }

        return page;
    }

    /// <summary>A commit object with two pages, a fragment, an inlined page and a foreign reference.</summary>
    private static (byte[] Bytes, CommitHeader Header, PageReference First, PageReference Second, PageReference Fragment, PageReference Foreign) Build()
    {
        CommitObjectBuilder builder = new CommitObjectBuilder(7);
        PageReference first = builder.AddPage(Page(1, 300));
        PageReference second = builder.AddPage(Page(2, 120));
        PageReference fragment = builder.AddFragment(Page(3, 64));

        // A page an older commit wrote and this one did not change: absolute already, and it must
        // come back exactly as it went in (§4.3).
        PageReference foreign = new PageReference(3, 4_096, 250, (UInt128)0xDEADBEEF << 64 | 0x1234);

        CommitHeader header = new CommitHeader
        {
            Version = 7,
            Parent = 6,
            Seed = 0x0123_4567_89AB_CDEF,
            Schema = Encoding.UTF8.GetBytes("{struct}"),
            ClusteringKey = ["tenant", "stamp"],
            WritePolicy = new byte[] { 1, 2, 3 },
            Chunker = new ChunkerSettings(64 << 10, 128 << 10, 256 << 10),
            Compaction = new CompactionSettings(4, 128L << 20, 10),
            Retention = new RetentionSettings(16, 86_400),
            CreatedAtUnixMilliseconds = 1_758_000_000_000,
            Levels =
            [
                new CommitLevel(0, 3, first, [new InlinedPage(first, Page(1, 300))]),
                new CommitLevel(1, 900, second),
                new CommitLevel(2, 90_000, foreign),
            ],
        };

        return (builder.Build(header), header, first, second, fragment, foreign);
    }

    [Fact]
    public void ACommitObjectRoundTrips()
    {
        (byte[] bytes, CommitHeader written, PageReference first, PageReference second, PageReference fragment, PageReference foreign) = Build();
        CommitObject commit = CommitObject.Open(bytes);

        Assert.Equal(written.Version, commit.Header.Version);
        Assert.Equal(written.Parent, commit.Header.Parent);
        Assert.Equal(written.Seed, commit.Header.Seed);
        Assert.Equal(written.Schema.ToArray(), commit.Header.Schema.ToArray());
        Assert.Equal(written.ClusteringKey, commit.Header.ClusteringKey);
        Assert.Equal(written.WritePolicy.ToArray(), commit.Header.WritePolicy.ToArray());
        Assert.Equal(written.Chunker, commit.Header.Chunker);
        Assert.Equal(written.Compaction, commit.Header.Compaction);
        Assert.Equal(written.Retention, commit.Header.Retention);
        Assert.Equal(written.CreatedAtUnixMilliseconds, commit.Header.CreatedAtUnixMilliseconds);
        Assert.Equal(3, commit.Header.Levels.Count);
        Assert.Equal(bytes.Length, commit.Length);
        Assert.NotNull(commit.Trailer);

        // The references this builder handed out are relative to the pages region, which is what
        // keeps a page's bytes independent of where its object put them.
        PageReference top = commit.Header.Levels[0].Top;
        Assert.Equal(first.Hash, top.Hash);
        Assert.Equal(first.Length, top.Length);
        Assert.Equal(0, top.Offset);
        Assert.Equal(Page(1, 300), commit.Page(bytes, top).ToArray());

        PageReference below = commit.Header.Levels[1].Top;
        Assert.Equal(second.Hash, below.Hash);
        Assert.Equal(300, below.Offset);
        Assert.Equal(Page(2, 120), commit.Page(bytes, below).ToArray());

        // The foreign reference passed through untouched.
        Assert.Equal(foreign, commit.Header.Levels[2].Top);

        // The inlined page is the header's own copy, and it is the same page.
        InlinedPage inlined = Assert.Single(commit.Header.Levels[0].Inlined);
        Assert.Equal(Page(1, 300), inlined.Bytes.ToArray());
        Assert.Equal(top, inlined.Reference);

        // The table names both pages and the fragment, absolutely.
        Assert.Equal(2, commit.Table.Pages.Count);
        Assert.Equal([top, below], commit.Table.Pages);
        PageReference recorded = Assert.Single(commit.Table.Fragments);
        Assert.Equal(fragment.Hash, recorded.Hash);
        Assert.Equal(300 + 120, recorded.Offset);
        Assert.Equal(Page(3, 64), commit.Page(bytes, recorded).ToArray());
    }

    [Fact]
    public void AFragmentAddedBeforeAPageIsWhereItsReferenceSays()
    {
        // The order a real indexing commit adds things in (step 42b): the fragment first, because
        // its reference goes into the leaf entry that a page written after it holds. Every reference
        // handed out is final, whatever follows it.
        CommitObjectBuilder builder = new CommitObjectBuilder(9);
        PageReference fragment = builder.AddFragment(Page(5, 80));
        PageReference page = builder.AddPage(Page(6, 200));
        PageReference later = builder.AddFragment(Page(7, 40));
        byte[] bytes = builder.Build(new CommitHeader
        {
            Version = 9,
            Parent = 8,
            Levels = [new CommitLevel(0, 1, page)],
        });

        CommitObject commit = CommitObject.Open(bytes);
        Assert.Equal((0L, 80L, 280L), (fragment.Offset, page.Offset, later.Offset));
        Assert.Equal(Page(5, 80), commit.Page(bytes, fragment).ToArray());
        Assert.Equal(Page(6, 200), commit.Page(bytes, commit.Header.Levels[0].Top).ToArray());
        Assert.Equal(Page(7, 40), commit.Page(bytes, later).ToArray());
        Assert.Equal([fragment, later], commit.Table.Fragments);
    }

    [Fact]
    public void AnObjectTruncatedAtEveryByteIsRefusedWithAReason()
    {
        (byte[] bytes, _, _, _, _, _) = Build();
        Assert.InRange(bytes.Length, 600, 4_096);

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] torn = bytes[..length];
            CommitFormatException refused = Assert.Throws<CommitFormatException>(
                () => CommitObject.Open(torn, torn.Length));
            Assert.NotEmpty(refused.Message);
        }

        // And the whole object, one byte longer than it says it is, is refused too: a store that
        // handed back more than the object is as wrong as one that handed back less.
        byte[] grown = [.. bytes, 0];
        Assert.Throws<CommitFormatException>(() => CommitObject.Open(grown, grown.Length));
    }

    [Fact]
    public void AByteChangedInTheHeaderOrTheTableFailsTheChecksum()
    {
        (byte[] bytes, _, _, _, _, _) = Build();
        CommitObject commit = CommitObject.Open(bytes);

        // In the header: every byte of it, one at a time.
        for (int at = CommitFormat.PreambleBytes; at < commit.HeaderEnd; at++)
        {
            byte[] flipped = [.. bytes];
            flipped[at] ^= 0xFF;
            Assert.ThrowsAny<Exception>(() => CommitObject.Open(flipped, flipped.Length));
        }

        // In the table: the checksum covers it, which is what §7 asks for.
        long tableOffset = commit.Trailer!.Value.TableOffset;
        for (int at = 0; at < commit.Trailer.Value.TableLength; at++)
        {
            byte[] flipped = [.. bytes];
            flipped[(int)tableOffset + at] ^= 0xFF;
            Assert.ThrowsAny<Exception>(() => CommitObject.Open(flipped, flipped.Length));
        }
    }

    [Fact]
    public void AByteChangedInAPageIsCaughtWhenThePageIsRead()
    {
        // NOT at open, and that is the design: the checksum covers the header and the table, and a
        // page is checked against the reference that sent the reader there (§7). Re-hashing every
        // page at open would make opening cost the whole object.
        (byte[] bytes, _, _, _, _, _) = Build();
        CommitObject commit = CommitObject.Open(bytes);
        PageReference top = commit.Header.Levels[0].Top;

        byte[] flipped = [.. bytes];
        flipped[(int)(commit.HeaderEnd + top.Offset) + 17] ^= 0xFF;

        CommitObject reopened = CommitObject.Open(flipped, flipped.Length);
        CommitFormatException refused = Assert.Throws<CommitFormatException>(
            () => reopened.Page(flipped, top).ToArray());
        Assert.Contains("hashes to", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesThatAreNotACommitObjectAreRefused()
    {
        Assert.Throws<CommitFormatException>(() => CommitObject.Open(ReadOnlySpan<byte>.Empty));
        Assert.Throws<CommitFormatException>(() => CommitObject.Open(new byte[64], 64));

        (byte[] bytes, _, _, _, _, _) = Build();
        byte[] wrongFormat = [.. bytes];
        wrongFormat[8] = 9;
        CommitFormatException refused = Assert.Throws<CommitFormatException>(
            () => CommitObject.Open(wrongFormat, wrongFormat.Length));
        Assert.Contains("format", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuilderRefusesAHeaderThatCommitsAnotherVersion()
    {
        CommitObjectBuilder builder = new CommitObjectBuilder(4);
        Assert.Throws<CommitFormatException>(() => builder.Build(new CommitHeader { Version = 5 }));
    }

    [Fact]
    public async Task OpeningACommitCostsOneRequest()
    {
        // §3: "A reader opens a commit with one ranged read of its first 256 KiB."
        (byte[] bytes, _, _, _, _, _) = Build();
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await store.PutIfAbsentAsync("commit/99999999999999999992.vxc", bytes, default);
        store.Reset();

        CommitObject commit = await CommitObject.OpenAsync(store, "commit/99999999999999999992.vxc", default);
        Assert.Equal(7u, commit.Header.Version);
        Assert.Equal(1, store.Requests);
        Assert.Equal(1, store.DependentSteps);

        // A small commit came back whole, so its table and checksum were verified by that one read.
        Assert.NotNull(commit.Trailer);
        Assert.Equal(2, commit.Table.Pages.Count);
    }

    [Fact]
    public async Task ACommitLargerThanTheOpenReadOpensOnItsHeaderAlone()
    {
        // The other shape of §3's promise: the header is still covered, the table is not, and a
        // page is read by the reference that names it rather than by scanning.
        CommitObjectBuilder builder = new CommitObjectBuilder(11);
        List<PageReference> references = [];
        for (int i = 0; i < 40; i++)
        {
            references.Add(builder.AddPage(Page((byte)i, 16 << 10)));
        }

        byte[] bytes = builder.Build(new CommitHeader
        {
            Version = 11,
            Levels = [new CommitLevel(0, 40, references[^1])],
        });

        Assert.True(bytes.Length > CommitFormat.OpenBytes);

        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        const string key = "commit/99999999999999999988.vxc";
        await store.PutIfAbsentAsync(key, bytes, default);
        store.Reset();

        CommitObject commit = await CommitObject.OpenAsync(store, key, default);
        Assert.Equal(1, store.Requests);
        Assert.Equal(11u, commit.Header.Version);
        Assert.Null(commit.Trailer);
        Assert.Empty(commit.Table.Pages);

        // The last page lies past the open read and is fetched by its reference, checked on arrival.
        PageReference last = commit.Header.Levels[0].Top;
        Assert.True(commit.HeaderEnd + last.Offset > CommitFormat.OpenBytes);
        byte[] page = await CommitObject.ReadPageAsync(store, key, last, commit.HeaderEnd, default);
        Assert.Equal(Page(39, 16 << 10), page);
        Assert.Equal(2, store.Requests);
    }

    [Fact]
    public async Task APageThatDoesNotHashToItsReferenceIsRefusedOnArrival()
    {
        CommitObjectBuilder builder = new CommitObjectBuilder(3);
        PageReference reference = builder.AddPage(Page(5, 1_024));
        byte[] bytes = builder.Build(new CommitHeader { Version = 3, Levels = [new CommitLevel(0, 1, reference)] });

        await using MemoryObjectStore store = new MemoryObjectStore();
        const string key = "commit/99999999999999999996.vxc";
        CommitObject commit = CommitObject.Open(bytes);
        PageReference absolute = commit.Header.Levels[0].Top;

        byte[] flipped = [.. bytes];
        flipped[(int)(commit.HeaderEnd + absolute.Offset)] ^= 0x01;
        await store.PutIfAbsentAsync(key, flipped, default);

        await Assert.ThrowsAsync<CommitFormatException>(
            async () => await CommitObject.ReadPageAsync(store, key, absolute, commit.HeaderEnd, default));
    }

    [Fact]
    public void APageReferenceCarriesTheHashOfItsPage()
    {
        CommitObjectBuilder builder = new CommitObjectBuilder(2);
        byte[] page = Page(9, 77);
        PageReference reference = builder.AddPage(page);
        Assert.Equal(XxHash128.HashToUInt128(page), reference.Hash);
        Assert.Equal(2u, reference.Version);
        Assert.Equal(77, reference.Length);
        Assert.True(reference.Exists);
        Assert.False(PageReference.None.Exists);
    }
}
