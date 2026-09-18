// The directory of docs/10-indexes.md §4.1 as bytes, and the reader rules it is held to.
//
// EVERY REFUSAL HERE IS A SUCCESSFUL CALL. An index is a hint (docs/08-semantics.md §5): a stale,
// malformed or foreign directory must read as "no index" and never as an exception, and a bad entry
// must cost that entry and nothing else. So each test below asks `TryParse` a question whose honest
// answer is "not usable", and checks it said so without throwing.
using System;
using System.Collections.Generic;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class IndexDirectoryTests
{
    private const ulong Rows = 100_000;
    private const ulong DataEnd = 1_000_000;

    [Fact]
    public void ADirectoryRoundTripsEveryField()
    {
        WritePolicy policy = WritePolicy.Auto
            .For("id", IndexPolicy.SortedRuns)
            .For("name", IndexPolicy.Bloom(falsePositivePpm: 2_500, resolutions: 3, maxBlocks: 64, minDistinct: 0, hash: BloomHash.XxHash64))
            .For("body", IndexPolicy.NgramBloom(caseInsensitive: true));
        IndexDirectory written = new IndexDirectory(
            Rows,
            PreviousEof: 4_096,
            policy,
            [
                new IndexEntry(
                    IndexKinds.BloomSbbf, [3u], 8_192, [1, 2, 3],
                    [
                        new IndexRun(0, 2, [new IndexSegment(512, 64, 6), new IndexSegment(576, 128, 6)], [[9], [8, 7]]),
                        new IndexRun(2, 10, [new IndexSegment(704, 1, 0)], [], EntryCount: 81_920),
                    ]),
                new IndexEntry(IndexKinds.DictProbe, [], 1_024, [], [new IndexRun(5, 1, [], [])]),
            ]);

        Assert.True(IndexDirectory.TryParse(written.ToBytes(), Rows, DataEnd, out IndexDirectory? read, out string? reason), reason);
        Assert.NotNull(read);
        Assert.Equal(Rows, read!.RowCount);
        Assert.Equal(4_096UL, read.PreviousEof);
        Assert.Equal(2, read.Entries.Count);

        IndexEntry bloom = read.Entries[0];
        Assert.Equal(IndexKinds.BloomSbbf, bloom.Kind);
        Assert.Equal(new uint[] { 3 }, bloom.ColumnPath);
        Assert.Equal(8_192UL, bloom.BlockLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, bloom.Options);
        Assert.Equal(2, bloom.Runs.Count);
        Assert.Equal(new IndexSegment(576, 128, 6), bloom.Runs[0].Payload[1]);
        Assert.Equal(new byte[] { 8, 7 }, bloom.Runs[0].PayloadDTypes[1]);
        Assert.Equal(12UL, bloom.Runs[1].EndBlock);
        Assert.Equal(0UL, bloom.Runs[0].EntryCount);
        Assert.Equal(81_920UL, bloom.Runs[1].EntryCount);

        IndexEntry probe = read.Entries[1];
        Assert.Empty(probe.ColumnPath);
        Assert.Empty(probe.Runs[0].Payload);

        Assert.Equal(IndexPolicy.Auto, read.Policy.Default);
        Assert.Equal(IndexPolicy.SortedRuns, read.Policy.Of("id"));
        IndexPolicy name = read.Policy.Of("name");
        Assert.Equal(IndexPolicyKind.Bloom, name.Kind);
        Assert.Equal(2_500, name.FalsePositivePpm);
        Assert.Equal(3, name.Resolutions);
        Assert.Equal(64, name.MaxBlocks);
        Assert.Equal(0, name.MinDistinct);
        Assert.Equal(BloomHash.XxHash64, name.Hash);
        Assert.True(read.Policy.Of("body").CaseInsensitive);
        Assert.Equal(IndexPolicy.Auto, read.Policy.Of("unlisted"));
    }

    [Fact]
    public void VersionTwoChecksItsTrailerAndCarriesARegionsChecksum()
    {
        // 13 §7 (step 21): `[2][message][XXH3-64 of both]`, and a checksum per region.
        byte[] region = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        IndexSegment checksummed = IndexSegment.Of(512, region, 6);
        IndexDirectory written = new IndexDirectory(
            Rows, 0, WritePolicy.None,
            [new IndexEntry(IndexKinds.BloomSbbf, [0u], 8_192, [], [new IndexRun(0, 1, [checksummed], [])])]);
        byte[] bytes = written.ToBytes();
        Assert.Equal(IndexDirectory.FormatVersion, bytes[0]);

        Assert.True(IndexDirectory.TryParse(bytes, Rows, DataEnd, out IndexDirectory? read, out string? reason), reason);
        IndexSegment back = read!.Entries[0].Runs[0].Payload[0];
        Assert.Equal(checksummed, back);
        Assert.NotNull(back.Checksum);
        Assert.True(back.Holds(region));
        Assert.False(back.Holds([1, 2, 3, 4, 5, 6, 7, 8, 0]));
        Assert.False(back.Holds(region.AsSpan(1)));

        // A region a legacy directory lists is only held to its length.
        Assert.True(new IndexSegment(512, 9, 6).Holds([0, 0, 0, 0, 0, 0, 0, 0, 0]));

        // Every byte of the directory is under the trailer: one flipped bit refuses it whole.
        for (int at = 0; at < bytes.Length; at++)
        {
            byte[] torn = (byte[])bytes.Clone();
            torn[at] ^= 0x10;
            Assert.False(IndexDirectory.TryParse(torn, Rows, DataEnd, out IndexDirectory? none, out string? why), $"byte {at}");
            Assert.Null(none);
            Assert.NotNull(why);
        }
    }

    [Fact]
    public void AVersionOneDirectoryIsRefusedWithItsReason()
    {
        // Version 1 had no trailer and no checksum per region. Nothing has written it since step 21,
        // and the Rust forge fixture that was said to be one never existed: it is refused like any
        // unknown version, and the file reads without its index, which is a hint.
        IndexDirectory written = new IndexDirectory(
            Rows, 0, WritePolicy.None,
            [new IndexEntry(IndexKinds.BloomSbbf, [0u], 8_192, [], [new IndexRun(0, 1, [new IndexSegment(512, 9, 6)], [])])]);
        byte[] v1 = written.ToBytes()[..^8];
        v1[0] = 1;
        int field = Array.IndexOf(v1, (byte)0x08, 1);
        v1[field + 1] = 1;

        Assert.False(IndexDirectory.TryParse(v1, Rows, DataEnd, out IndexDirectory? read, out string? reason));
        Assert.Null(read);
        Assert.Contains("version 1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSamePolicySerializesToTheSameBytes()
    {
        // Overrides live in a dictionary; the bytes must not depend on its iteration order.
        WritePolicy one = WritePolicy.None.For("b", IndexPolicy.Postings).For("a", IndexPolicy.SortedRuns);
        WritePolicy two = WritePolicy.None.For("a", IndexPolicy.SortedRuns).For("b", IndexPolicy.Postings);
        Assert.Equal(
            new IndexDirectory(Rows, 0, one, []).ToBytes(),
            new IndexDirectory(Rows, 0, two, []).ToBytes());
    }

    [Fact]
    public void AStaleDirectoryIsRefusedWhole()
    {
        byte[] bytes = new IndexDirectory(Rows, 0, WritePolicy.Auto, [Probe(0, 1)]).ToBytes();

        Assert.False(IndexDirectory.TryParse(bytes, Rows + 1, DataEnd, out IndexDirectory? read, out string? reason));
        Assert.Null(read);
        Assert.Contains("stale", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKindCostsItsEntryAlone()
    {
        IndexEntry foreign = new IndexEntry("someone.else.v9", [0u], 1_024, [], [new IndexRun(0, 1, [], [])]);
        byte[] bytes = new IndexDirectory(Rows, 0, WritePolicy.Auto, [foreign, Probe(0, 1)]).ToBytes();

        Assert.True(IndexDirectory.TryParse(bytes, Rows, DataEnd, out IndexDirectory? read, out _));
        IndexEntry kept = Assert.Single(read!.Entries);
        Assert.Equal(IndexKinds.DictProbe, kept.Kind);
    }

    public static TheoryData<string, IndexRun[]> UnsoundRuns() => new()
    {
        { "overlapping", [new IndexRun(0, 4, [Seg(0)], []), new IndexRun(3, 2, [Seg(64)], [])] },
        { "out of order", [new IndexRun(4, 1, [Seg(0)], []), new IndexRun(0, 1, [Seg(64)], [])] },
        { "empty run", [new IndexRun(0, 0, [Seg(0)], [])] },
        { "no payload", [new IndexRun(0, 1, [], [])] },
        { "past the data", [new IndexRun(0, 1, [new IndexSegment(DataEnd - 8, 16, 0)], [])] },
        { "wrapping", [new IndexRun(0, 1, [new IndexSegment(ulong.MaxValue - 2, 16, 0)], [])] },
        { "alignment", [new IndexRun(0, 1, [new IndexSegment(0, 16, 40)], [])] },
        { "dtype count", [new IndexRun(0, 1, [Seg(0), Seg(64)], [[1]])] },
    };

    [Theory]
    [MemberData(nameof(UnsoundRuns))]
    public void AnUnsoundRunCostsItsEntryAlone(string label, IndexRun[] runs)
    {
        IndexEntry bad = new IndexEntry(IndexKinds.BloomSbbf, [0u], 1_024, [], runs);
        byte[] bytes = new IndexDirectory(Rows, 0, WritePolicy.Auto, [bad, Probe(0, 1)]).ToBytes();

        Assert.True(IndexDirectory.TryParse(bytes, Rows, DataEnd, out IndexDirectory? read, out _), label);
        IndexEntry kept = Assert.Single(read!.Entries);
        Assert.Equal(IndexKinds.DictProbe, kept.Kind);
    }

    public static TheoryData<string, byte[]> Garbage() => new()
    {
        { "empty", [] },
        { "wrong version byte", [2, 8, 1] },
        { "truncated varint", [1, 16, 0xFF] },
        { "declared version 2", [1, 8, 2, 16, 0] },
        { "length past the end", [1, 42, 200, 1] },
    };

    [Theory]
    [MemberData(nameof(Garbage))]
    public void GarbageIsRefusedWithAReasonAndNeverThrows(string label, byte[] bytes)
    {
        Assert.False(IndexDirectory.TryParse(bytes, 0, DataEnd, out IndexDirectory? read, out string? reason), label);
        Assert.Null(read);
        Assert.False(string.IsNullOrEmpty(reason), label);
    }

    [Fact]
    public void EveryPrefixOfAValidDirectoryIsRefusedOrReadNeverThrown()
    {
        // The fuzzer's cheapest form: a torn write at every length.
        byte[] bytes = new IndexDirectory(
            Rows, 7, WritePolicy.Auto.For("x", IndexPolicy.Bloom()),
            [new IndexEntry(IndexKinds.BloomSbbf, [0u, 2u], 1_024, [5, 5], [new IndexRun(0, 3, [Seg(0), Seg(64)], [[1], [2]])]), Probe(3, 2)])
            .ToBytes();
        for (int length = 0; length <= bytes.Length; length++)
        {
            IndexDirectory.TryParse(bytes.AsSpan(0, length), Rows, DataEnd, out IndexDirectory? read, out string? reason);
            Assert.True(read is not null || reason is not null, $"length {length} returned neither");
        }
    }

    [Fact]
    public void PolicyOptionsAreCheckedAtTheCallAndClampedFromAFile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexPolicy.Bloom(falsePositivePpm: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexPolicy.Bloom(resolutions: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexPolicy.Bloom(maxBlocks: 0));
        Assert.Throws<ArgumentException>(() => WritePolicy.Auto.For(string.Empty, IndexPolicy.None));

        IndexPolicy clamped = IndexPolicy.FromStored(99, 9_000_000, 17, 0, 3, 5, false);
        Assert.Equal(IndexPolicyKind.None, clamped.Kind);
        Assert.Equal(500_000, clamped.FalsePositivePpm);
        Assert.Equal(3, clamped.Resolutions);
        Assert.Equal(IndexPolicy.DefaultMaxBlocks, clamped.MaxBlocks);
        Assert.Equal(3, clamped.MinDistinct);
        Assert.Equal(BloomHash.XxHash3, clamped.Hash);

        // `default` is a usable None with every option at its documented value.
        IndexPolicy zero = default;
        Assert.Equal(IndexPolicy.None, zero);
        Assert.Equal(IndexPolicy.DefaultMinDistinct, zero.MinDistinct);
    }

    private static IndexSegment Seg(ulong offset) => new IndexSegment(offset, 16, 0);

    private static IndexEntry Probe(ulong first, uint count) =>
        new IndexEntry(IndexKinds.DictProbe, [1u], 1_024, [], new List<IndexRun> { new IndexRun(first, count, [], []) });
}
