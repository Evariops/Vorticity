// The sealed format, through its two stages: what the sealing stage writes, the reader gives back
// byte for byte, and nothing else.
//
// WHAT IS HELD: any plaintext length round-trips, the empty one and the exact multiples of a frame
// included, however the writer cuts its writes; a Vortex file written through the stage scans as
// the rows written, at every frame size; a known answer pins the format; every byte flipped and
// every truncation of a small object fails before any plaintext is used, but for the header's
// copy of the descriptor, which a file opened by its tail never reads; a frame taken from another
// object fails; a missing key and a wrong key are told apart.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Sealing;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedFormatTests
{
    private static readonly byte[] FixedKey = SealedObjects.Key(7);
    private static readonly byte[] FixedWrapped = SealedObjects.Key(9);

    public static TheoryData<int, int, int> Lengths => new TheoryData<int, int, int>
    {
        // plaintext length, frame log2, write chunk
        { 0, 12, 1 },
        { 1, 12, 1 },
        { 4095, 12, 100 },
        { 4096, 12, 4096 },
        { 4097, 12, 4096 },
        { 8192, 12, 8192 },
        { 8192, 12, 9000 },
        { 3 * 4096 + 5, 12, 777 },
        { 200_000, 16, 65_536 },
        { 200_000, 16, 70_000 },
        { 3 * 65_536, 16, 1 << 20 },
        { 1_500_000, 20, 300_000 },
    };

    [Theory]
    [MemberData(nameof(Lengths))]
    public async Task AnyPlaintextComesBackAsItWasWritten(int length, int frameLog2, int chunk)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        using DataKey key = await keyring.GenerateAsync(ReadOnlyMemory<byte>.Empty, ct);
        byte[] plaintext = SealedObjects.Pattern(length);

        byte[] sealedBytes = await SealedObjects.SealAsync(plaintext, key, SealParameters.ForFile(frameLog2), chunk, ct);

        long frames = SealedFormat.FrameCount(length, 1 << frameLog2);
        Assert.Equal(length + (frames * SealedFormat.TagBytes), sealedBytes.Length - Overhead(sealedBytes));
        Assert.Equal(plaintext, await SealedObjects.OpenAllAsync(sealedBytes, keyring, ct));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    public async Task AFileWrittenThroughTheStageScansAsTheRowsWritten(int frameLog2)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        using DataKey key = await keyring.GenerateAsync(ReadOnlyMemory<byte>.Empty, ct);
        List<Reading> rows = SealedObjects.Rows(100_000);

        byte[] sealedBytes = await SealedObjects.WriteAsync(rows, key, SealParameters.ForFile(frameLog2), ct);

        Assert.Equal(rows, await SealedObjects.ReadRowsAsync(sealedBytes, keyring, ct));
        Assert.True(SealedLayout.EndsSealed(sealedBytes));
        Assert.Equal("VXSEALED"u8.ToArray(), sealedBytes.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public async Task AKnownAnswerPinsTheFormat()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", FixedKey, FixedWrapped);
        SealParameters pinned = new SealParameters(
            12, SealedObjects.Key(3).AsMemory(0, 16), SealedObjects.Key(4), "ctx"u8.ToArray(), SealedObjects.Key(5));
        byte[] plaintext = SealedObjects.Pattern(10_000);

        byte[] sealedBytes = await SealedObjects.SealAsync(plaintext, key, pinned, 1_000, ct);

        Assert.Equal(10_000 + (3 * 16) + Overhead(sealedBytes), sealedBytes.Length);
        string answer = Convert.ToHexString(SHA256.HashData(sealedBytes));
        Assert.True(answer == KnownAnswer, $"The sealed object hashes to {answer}, where the format's known answer is {KnownAnswer}.");
        Assert.Equal(plaintext, await OpenWithAsync(sealedBytes, key, ct));
    }

    [Fact]
    public async Task EveryByteFlippedFailsButTheHeadersUnreadCopy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", FixedKey, FixedWrapped);
        byte[] plaintext = SealedObjects.Pattern(300);
        byte[] sealedBytes = await SealedObjects.SealAsync(plaintext, key, SealParameters.ForFile(12), 300, ct);
        int header = SealedFormat.HeaderPrefixBytes + DescriptorLength(sealedBytes);

        for (int at = 0; at < sealedBytes.Length; at++)
        {
            byte[] flipped = (byte[])sealedBytes.Clone();
            flipped[at] ^= 0x01;
            if (at < header)
            {
                // The copy of the descriptor at the head is the one an object opened by its head reads.
                Assert.Equal(plaintext, await OpenWithAsync(flipped, key, ct));
                continue;
            }

            await AssertRefusedAsync(flipped, key, ct, $"byte {at} flipped");
        }
    }

    [Fact]
    public async Task EveryTruncationFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", FixedKey, FixedWrapped);
        byte[] sealedBytes = await SealedObjects.SealAsync(SealedObjects.Pattern(9_000), key, SealParameters.ForFile(12), 9_000, ct);

        for (int length = 0; length < sealedBytes.Length; length++)
        {
            await AssertRefusedAsync(sealedBytes.AsSpan(0, length).ToArray(), key, ct, $"cut to {length} bytes");
        }
    }

    [Fact]
    public async Task AFrameFromAnotherObjectFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("kat", FixedKey, FixedWrapped);
        byte[] plaintext = SealedObjects.Pattern(3 * 4096);
        byte[] first = await SealedObjects.SealAsync(plaintext, key, SealParameters.ForFile(12), 4096, ct);
        byte[] second = await SealedObjects.SealAsync(plaintext, key, SealParameters.ForFile(12), 4096, ct);
        int header = SealedFormat.HeaderPrefixBytes + DescriptorLength(first);

        // The same plaintext under the same data key: the frames still differ, each object's key its own.
        Assert.NotEqual(first.AsSpan(header, 4096).ToArray(), second.AsSpan(header, 4096).ToArray());
        byte[] spliced = (byte[])first.Clone();
        second.AsSpan(header, 4096 + 16).CopyTo(spliced.AsSpan(header));
        await AssertRefusedAsync(spliced, key, ct, "a frame of another object");

        // Frames swapped within one object move their index out of their nonce.
        byte[] swapped = (byte[])first.Clone();
        first.AsSpan(header, 4096 + 16).CopyTo(swapped.AsSpan(header + 4096 + 16));
        first.AsSpan(header + 4096 + 16, 4096 + 16).CopyTo(swapped.AsSpan(header));
        await AssertRefusedAsync(swapped, key, ct, "two frames swapped");
    }

    [Fact]
    public async Task AMissingKeyAndAWrongKeyAreToldApart()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        using DataKey key = await keyring.GenerateAsync(ReadOnlyMemory<byte>.Empty, ct);
        byte[] sealedBytes = await SealedObjects.SealAsync(SealedObjects.Pattern(5_000), key, SealParameters.ForFile(12), 5_000, ct);

        using VortexKeyring other = VortexKeyring.FromKeys(new VortexKey("another-key", SealedObjects.Key(1)));
        VortexEncryptionException missing = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await SealedObjects.OpenAllAsync(sealedBytes, other, ct));
        Assert.Equal(VortexEncryptionError.NoKey, missing.Error);
        Assert.Equal("test-key", missing.KeyId);

        using VortexKeyring impostor = VortexKeyring.FromKeys(new VortexKey("test-key", SealedObjects.Key(2)));
        VortexEncryptionException wrong = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await SealedObjects.OpenAllAsync(sealedBytes, impostor, ct));
        Assert.Equal(VortexEncryptionError.Unauthenticated, wrong.Error);
    }

    [Fact]
    public async Task AKeyringRotatesByKeepingItsOlderKeys()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring before = VortexKeyring.FromKeys(new VortexKey("2026-09", SealedObjects.Key(1)));
        using DataKey key = await before.GenerateAsync("dataset"u8.ToArray(), ct);
        SealParameters parameters = SealParameters.ForFile(12) with { KeyContext = "dataset"u8.ToArray() };
        byte[] sealedBytes = await SealedObjects.SealAsync(SealedObjects.Pattern(5_000), key, parameters, 5_000, ct);

        using VortexKeyring after = VortexKeyring.FromKeys(new VortexKey("2026-10", SealedObjects.Key(2)), new VortexKey("2026-09", SealedObjects.Key(1)));
        Assert.Equal(SealedObjects.Pattern(5_000), await SealedObjects.OpenAllAsync(sealedBytes, after, ct));
        using DataKey fresh = await after.GenerateAsync(ReadOnlyMemory<byte>.Empty, ct);
        Assert.Equal("2026-10", fresh.KeyId);
    }

    /// <summary>
    /// The SHA-256 of the known-answer object, which tools/sealed-kat/kat.py computes from the
    /// format's description alone, over another AES-GCM and another HKDF: a change here is a change
    /// of format.
    /// </summary>
    private const string KnownAnswer = "8087393891495280B79C1FCC109CE89A9352F9B8914ED28A9677B48468604FB6";

    private static async Task AssertRefusedAsync(byte[] altered, DataKey key, CancellationToken ct, string what)
    {
        Exception? refused = null;
        try
        {
            byte[] read = await OpenWithAsync(altered, key, ct);
            Assert.Fail($"{what}: the object opened and read {read.Length} bytes.");
        }
        catch (VortexEncryptionException e)
        {
            refused = e;
        }
        catch (VortexFormatException e)
        {
            refused = e;
        }
        catch (VortexUnsupportedException e)
        {
            refused = e;
        }

        Assert.NotNull(refused);
    }

    private static async Task<byte[]> OpenWithAsync(byte[] sealedBytes, DataKey key, CancellationToken ct)
    {
        await using SealedSegmentReader reader = await SealedSegmentReader.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(sealedBytes),
            ownsInner: true,
            (descriptor, _) => descriptor.KeyId == key.KeyId && descriptor.WrappedKey.Span.SequenceEqual(key.WrappedKey.Span)
                ? new ValueTask<DataKey>(key)
                : throw VortexEncryptionException.NoKey(descriptor.KeyId, "another key"),
            ct);
        long length = await reader.GetLengthAsync(ct);
        if (length == 0)
        {
            return [];
        }

        using Vorticity.Buffers.SegmentOwner all = await reader.ReadRangeAsync(0, checked((int)length), 1, ct);
        return all.Buffer.Span.ToArray();
    }

    /// <summary>The header, both descriptors, and the trailer's fixed fields of a one-epoch object.</summary>
    private static int Overhead(byte[] sealedBytes) =>
        SealedFormat.HeaderPrefixBytes + (2 * DescriptorLength(sealedBytes)) + 4 + SealedFormat.EpochEntryBytes + SealedFormat.TrailerSuffixBytes;

    private static int DescriptorLength(byte[] sealedBytes) => BitConverter.ToInt32(sealedBytes, 8);
}
