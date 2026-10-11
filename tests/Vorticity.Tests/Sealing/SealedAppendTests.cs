// Appends to sealed files: an epoch per append, under the data key the file names.
//
// WHAT IS HELD: epochs appended to a sealed object read back as one plaintext, across their seams and
// an empty epoch, each under a salt of its own; every byte of an append flipped fails but the old
// trailer's, which nothing reads any more; epochs reordered or dropped from the middle fail; an append
// handed another data key than the object's is refused before it seals a frame; a sealed
// file appended to through a session reads as the rows written, holds none of them in plain, and
// needs the key, not the sealing policy; a session that seals adds no plaintext to a plain file; an
// abandoned append leaves the file byte for byte as it was; a torn append opens at the version before
// it, is refused by a policy that refuses tears and by an append, and is repaired to that version
// without the key, after which the file takes appends again.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Sealing;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedAppendTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vorticity-sealed-append-tests", Guid.NewGuid().ToString("N"));

    public SealedAppendTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test failure must not be masked by a cleanup failure.
        }
    }

    [Theory]
    [InlineData(12, 10_000, 4_096, 3)]
    [InlineData(12, 0, 1, 8_192)]
    [InlineData(16, 200_000, 65_536, 70_001)]
    public async Task EpochsAppendedReadBackAsOnePlaintext(int frameLog2, int first, int second, int third)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        using DataKey key = await keyring.GenerateAsync(ReadOnlyMemory<byte>.Empty, ct);
        byte[] all = SealedObjects.Pattern(first + second + third);

        // Four epochs, the third empty, each written in pieces of its own size.
        byte[] sealedBytes = await SealedObjects.SealAsync(all.AsMemory(0, first), key, SealParameters.ForFile(frameLog2), 5_000, ct);
        List<long> ends = [sealedBytes.Length];
        sealedBytes = await SealedObjects.AppendSealAsync(sealedBytes, all.AsMemory(first, second), key, 3_000, ct);
        ends.Add(sealedBytes.Length);
        sealedBytes = await SealedObjects.AppendSealAsync(sealedBytes, ReadOnlyMemory<byte>.Empty, key, 1, ct);
        ends.Add(sealedBytes.Length);
        sealedBytes = await SealedObjects.AppendSealAsync(sealedBytes, all.AsMemory(first + second), key, 7_000, ct);

        Assert.Equal(all, await SealedObjects.OpenAllAsync(sealedBytes, keyring, ct));
        await using SealedSegmentReader reader = await SealedObjects.OpenAsync(sealedBytes, keyring, ct);
        SealedEpoch[] epochs = reader.Layout.Epochs;
        Assert.Equal([first, second, 0, third], epochs.Select(e => (int)e.PlainLength));

        // Each epoch starts after the trailer before it, which stays where it was, under a salt of its own.
        Assert.Equal(ends, epochs.Skip(1).Select(e => e.FramesStart));
        Assert.Equal(4, epochs.Select(e => Convert.ToHexString(e.Salt.Span)).Distinct().Count());

        foreach (int seam in new[] { first, first + second })
        {
            int from = Math.Max(0, seam - 10);
            int to = Math.Min(all.Length, seam + 10);
            using Vorticity.Buffers.SegmentOwner read = await reader.ReadRangeAsync(from, to - from, 1, ct);
            Assert.Equal(all.AsSpan(from, to - from).ToArray(), read.Buffer.Span.ToArray());
        }
    }

    [Fact]
    public async Task EveryByteOfAnAppendFlippedFailsButTheOldTrailers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", SealedObjects.Key(7), SealedObjects.Key(9));
        byte[] plaintext = SealedObjects.Pattern(500);
        byte[] once = await SealedObjects.SealAsync(plaintext.AsMemory(0, 300), key, SealParameters.ForFile(12), 300, ct);
        byte[] twice = await SealedObjects.AppendSealAsync(once, plaintext.AsMemory(300), key, 200, ct);
        int descriptor = BitConverter.ToInt32(twice, 8);
        int header = SealedFormat.HeaderPrefixBytes + descriptor;
        int oldTrailer = once.Length - SealedFormat.OneEpochTrailerBytes(descriptor);

        for (int at = 0; at < twice.Length; at++)
        {
            byte[] flipped = (byte[])twice.Clone();
            flipped[at] ^= 0x01;
            if (at < header || (at >= oldTrailer && at < once.Length))
            {
                // The header's copy of the descriptor, and the trailer the append made obsolete.
                Assert.Equal(plaintext, await SealedFormatTests.OpenWithAsync(flipped, key, ct));
                continue;
            }

            await SealedFormatTests.AssertRefusedAsync(flipped, key, ct, $"byte {at} flipped");
        }
    }

    [Fact]
    public async Task EpochsReorderedOrDroppedFromTheMiddleFail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", SealedObjects.Key(7), SealedObjects.Key(9));
        byte[] plaintext = SealedObjects.Pattern(15_000);
        byte[] sealedBytes = await SealedObjects.SealAsync(plaintext.AsMemory(0, 5_000), key, SealParameters.ForFile(12), 5_000, ct);
        sealedBytes = await SealedObjects.AppendSealAsync(sealedBytes, plaintext.AsMemory(5_000, 5_000), key, 5_000, ct);
        sealedBytes = await SealedObjects.AppendSealAsync(sealedBytes, plaintext.AsMemory(10_000), key, 5_000, ct);

        int descriptor = BitConverter.ToInt32(sealedBytes, 8);
        int trailerStart = sealedBytes.Length - BitConverter.ToInt32(sealedBytes, sealedBytes.Length - SealedFormat.TrailerSuffixBytes);
        int entries = trailerStart + descriptor + 4;
        byte[] first = sealedBytes[entries..(entries + SealedFormat.EpochEntryBytes)];
        int second = entries + SealedFormat.EpochEntryBytes;
        byte[] middle = sealedBytes[second..(second + SealedFormat.AppendedEpochEntryBytes)];
        byte[] last = sealedBytes[(second + SealedFormat.AppendedEpochEntryBytes)..(second + (2 * SealedFormat.AppendedEpochEntryBytes))];

        Assert.Equal(plaintext, await SealedFormatTests.OpenWithAsync(WithEpochs(sealedBytes, trailerStart, descriptor, first, middle, last), key, ct));
        await SealedFormatTests.AssertRefusedAsync(WithEpochs(sealedBytes, trailerStart, descriptor, first, last, middle), key, ct, "the last two epochs swapped");
        await SealedFormatTests.AssertRefusedAsync(WithEpochs(sealedBytes, trailerStart, descriptor, first, last), key, ct, "the middle epoch dropped");
    }

    [Fact]
    public async Task AnEmptyLastEpochIsCheckedAtTheOpen()
    {
        // The open checks the last frame, which for an empty epoch is a tag over nothing that no read
        // would reach: a tag altered there fails the open, as an altered last frame of plaintext does.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", SealedObjects.Key(7), SealedObjects.Key(9));
        byte[] once = await SealedObjects.SealAsync(SealedObjects.Pattern(5_000), key, SealParameters.ForFile(12), 5_000, ct);
        byte[] emptied = await SealedObjects.AppendSealAsync(once, ReadOnlyMemory<byte>.Empty, key, 1, ct);
        Assert.Equal(SealedObjects.Pattern(5_000), await SealedFormatTests.OpenWithAsync(emptied, key, ct));

        for (int at = once.Length; at < once.Length + SealedFormat.TagBytes; at++)
        {
            byte[] flipped = (byte[])emptied.Clone();
            flipped[at] ^= 0x01;
            await SealedFormatTests.AssertRefusedAsync(flipped, key, ct, $"the empty epoch's tag byte {at - once.Length} flipped");
        }
    }

    [Fact]
    public async Task AnAppendUnderAnotherDataKeyIsRefusedBeforeItSealsAFrame()
    {
        // An epoch under another key than the object's would make every epoch unreadable: the stage
        // checks the key against the first epoch's commitment before it seals a byte.
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", SealedObjects.Key(7), SealedObjects.Key(9));
        using DataKey other = new DataKey("kat", SealedObjects.Key(8), SealedObjects.Key(9));
        byte[] sealedBytes = await SealedObjects.SealAsync(SealedObjects.Pattern(5_000), key, SealParameters.ForFile(12), 5_000, ct);

        VortexEncryptionException refused = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await SealedObjects.AppendSealAsync(sealedBytes, SealedObjects.Pattern(100), key, 100, ct, appendWith: other));
        Assert.Equal(VortexEncryptionError.Unauthenticated, refused.Error);
        Assert.Equal(SealedObjects.Pattern(5_000), await SealedFormatTests.OpenWithAsync(sealedBytes, key, ct));
    }

    [Fact]
    public async Task ASealedFileTakesAppendsThroughItsSession()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(25_000);
        string path = Path.Combine(_directory, "sealed.vortex");
        await using VortexSession sealing = SealingSession(keyring);
        await WriteAsync(sealing, path, rows.GetRange(0, 10_000), ct);
        await AppendAsync(sealing, path, rows.GetRange(10_000, 5_003), ct);

        // A session that holds the key appends without the policy: the file stays sealed under its own key.
        await using (VortexSession reading = VortexSession.Create(o => o.Keyring = keyring))
        {
            await AppendAsync(reading, path, rows.GetRange(15_003, 9_997), ct);
        }

        await using (VortexFile file = await sealing.OpenAsync(path, cancellationToken: ct))
        {
            Assert.Equal(rows, await SealedObjects.RowsOfAsync(file, ct));
            Assert.Equal(3, ((SealedSegmentReader)SessionReader.Unwrap(file.Source)).Layout.Epochs.Length);
            Assert.Null(file.TornTail);
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        Assert.Equal("VXSEALED"u8.ToArray(), bytes.AsSpan(0, 8).ToArray());
        Assert.True(bytes.AsSpan().IndexOf("Nantes"u8) < 0, "A city's name lies in plain in the sealed file.");

        VortexEncryptionException keyless = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await VortexSession.Default.OpenWriterAsync(path, cancellationToken: ct));
        Assert.Equal(VortexEncryptionError.NoKey, keyless.Error);
    }

    [Fact]
    public async Task ASessionThatSealsAddsNoPlaintextToAPlainFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        string path = Path.Combine(_directory, "plain.vortex");
        await WriteAsync(VortexSession.Default, path, SealedObjects.Rows(1_000), ct);
        byte[] before = await System.IO.File.ReadAllBytesAsync(path, ct);

        await using VortexSession sealing = SealingSession(keyring);
        VortexEncryptionException refused = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await sealing.OpenWriterAsync(path, cancellationToken: ct));
        Assert.Equal(VortexEncryptionError.Refused, refused.Error);
        Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task AnAbandonedSealedAppendLeavesTheFileAsItWas()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(60_000);
        string path = Path.Combine(_directory, "sealed.vortex");
        await using VortexSession sealing = SealingSession(keyring);
        await WriteAsync(sealing, path, rows.GetRange(0, 10_000), ct);
        byte[] before = await System.IO.File.ReadAllBytesAsync(path, ct);

        // Given up after a flush, and disposed before completion: both cut the file back.
        await using (VortexFileWriter writer = await sealing.OpenWriterAsync(path, cancellationToken: ct))
        {
            await writer.WriteAsync<Reading>(rows.GetRange(10_000, 50_000).ToArray(), ct);
            await writer.FlushAsync(ct);
            Assert.True(new FileInfo(path).Length > before.Length);
            writer.Abandon();
        }

        Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(path, ct));
        await using (VortexFileWriter writer = await sealing.OpenWriterAsync(path, cancellationToken: ct))
        {
            await writer.WriteAsync<Reading>(rows.GetRange(10_000, 50_000).ToArray(), ct);
            await writer.FlushAsync(ct);
        }

        Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(path, ct));
        await using VortexFile file = await sealing.OpenAsync(path, cancellationToken: ct);
        Assert.Equal(rows.GetRange(0, 10_000), await SealedObjects.RowsOfAsync(file, ct));
    }

    [Fact]
    public async Task ATornSealedAppendOpensTheVersionBeforeItAndIsRepairedToIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(32_000);
        string path = Path.Combine(_directory, "sealed.vortex");
        await using VortexSession sealing = SealingSession(keyring);
        await WriteAsync(sealing, path, rows.GetRange(0, 10_000), ct);
        byte[] before = await System.IO.File.ReadAllBytesAsync(path, ct);
        await AppendAsync(sealing, path, rows.GetRange(10_000, 20_000), ct);
        byte[] after = await System.IO.File.ReadAllBytesAsync(path, ct);

        // Cut in the new epoch's first frame, in its middle, in its trailer, and one byte short of whole.
        int added = after.Length - before.Length;
        foreach (int cut in new[] { before.Length + 1, before.Length + (added / 2), after.Length - 9, after.Length - 1 })
        {
            await System.IO.File.WriteAllBytesAsync(path, after.AsMemory(0, cut), ct);
            await using (VortexFile file = await sealing.OpenAsync(path, cancellationToken: ct))
            {
                Assert.Equal((cut, before.Length), (file.TornTail?.FileLength, file.TornTail?.ValidLength));
                Assert.Equal(rows.GetRange(0, 10_000), await SealedObjects.RowsOfAsync(file, ct));
            }

            await Assert.ThrowsAsync<VortexFormatException>(
                async () => await sealing.OpenAsync(path, new VortexOpenOptions { TornTail = VortexTornTailPolicy.Refuse }, ct));
            await Assert.ThrowsAsync<VortexFormatException>(async () => await sealing.OpenWriterAsync(path, cancellationToken: ct));
            VortexEncryptionException keyless = await Assert.ThrowsAsync<VortexEncryptionException>(
                async () => await VortexFile.OpenAsync(path, ct));
            Assert.Equal(VortexEncryptionError.NoKey, keyless.Error);
            Assert.Equal(before.Length, await VortexFileRepair.GetValidLengthAsync(path, ct));
        }

        // The repair needs no key, and gives back the file as it was before the append.
        Assert.Equal(new VortexRepairResult(after.Length - 1, before.Length, true), await VortexFileRepair.RepairAsync(path, ct));
        Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(path, ct));
        await AppendAsync(sealing, path, rows.GetRange(10_000, 22_000), ct);
        long whole = new FileInfo(path).Length;
        Assert.Equal(new VortexRepairResult(whole, whole, false), await VortexFileRepair.RepairAsync(path, ct));
        await using VortexFile repaired = await sealing.OpenAsync(path, cancellationToken: ct);
        Assert.Equal(rows, await SealedObjects.RowsOfAsync(repaired, ct));
    }

    private static VortexSession SealingSession(VortexKeyring keyring) => VortexSession.Create(o =>
    {
        o.Keyring = keyring;
        o.EncryptFiles = true;
    });

    private static async Task WriteAsync(VortexSession session, string path, List<Reading> rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
        await writer.WriteAsync<Reading>(rows.ToArray(), ct);
        await writer.CompleteAsync(ct);
    }

    private static async Task AppendAsync(VortexSession session, string path, List<Reading> rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = await session.OpenWriterAsync(path, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows.ToArray(), ct);
        await writer.CompleteAsync(ct);
    }

    /// <summary>The object with its last trailer listing <paramref name="epochs"/>, the entries as a trailer holds them.</summary>
    private static byte[] WithEpochs(byte[] sealedBytes, int trailerStart, int descriptor, params byte[][] epochs)
    {
        List<byte> bytes = [.. sealedBytes.AsSpan(0, trailerStart + descriptor)];
        bytes.AddRange(BitConverter.GetBytes((uint)epochs.Length));
        foreach (byte[] epoch in epochs)
        {
            bytes.AddRange(epoch);
        }

        bytes.AddRange(BitConverter.GetBytes((uint)(bytes.Count - trailerStart + SealedFormat.TrailerSuffixBytes)));
        bytes.AddRange(SealedFormat.TrailerMagic.ToArray());
        return [.. bytes];
    }
}
