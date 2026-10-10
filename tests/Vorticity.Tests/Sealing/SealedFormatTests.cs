// The sealed format, through its two stages: what the sealing stage writes, the reader gives back
// byte for byte, and nothing else.
//
// WHAT IS HELD: any plaintext length round-trips, the empty one and the exact multiples of a frame
// included, however the writer cuts its writes; a Vortex file written through the stage scans as
// the rows written, at every frame size; the key derivation takes its arguments in the order RFC 5869
// gives, as its vectors check; a known answer pins the format; every byte flipped and
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

    public static TheoryData<string, string, string, string, string> Rfc5869 => new TheoryData<string, string, string, string, string>
    {
        // RFC 5869, appendix A, test cases 1 to 3: input key, salt, info, pseudorandom key, output.
        {
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            "000102030405060708090a0b0c",
            "f0f1f2f3f4f5f6f7f8f9",
            "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5",
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        },
        {
            Range(0x00, 0x50),
            Range(0x60, 0xb0),
            Range(0xb0, 0x100),
            "06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244",
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87"
        },
        {
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            "",
            "",
            "19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04",
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"
        },
    };

    [Theory]
    [MemberData(nameof(Rfc5869))]
    public void TheDerivationPassesItsArgumentsToTheKeyDerivationAsTheRfcOrdersThem(string inputKey, string salt, string info, string pseudorandomKey, string output)
    {
        byte[] prk = new byte[32];
        SealedFormat.Extract(Convert.FromHexString(inputKey), Convert.FromHexString(salt), prk);
        Assert.Equal(pseudorandomKey, Convert.ToHexStringLower(prk));

        byte[] okm = new byte[output.Length / 2];
        SealedFormat.Expand(prk, Convert.FromHexString(info), okm);
        Assert.Equal(output, Convert.ToHexStringLower(okm));
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

    /// <summary>That <paramref name="altered"/> fails to open or to read with <paramref name="key"/>, whatever the reason.</summary>
    internal static async Task AssertRefusedAsync(byte[] altered, DataKey key, CancellationToken ct, string what)
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

    /// <summary>The whole plaintext of <paramref name="sealedBytes"/>, opened with <paramref name="key"/> only.</summary>
    internal static async Task<byte[]> OpenWithAsync(byte[] sealedBytes, DataKey key, CancellationToken ct)
    {
        await using SealedSegmentReader reader = await SealedSegmentReader.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(sealedBytes),
            ownsInner: true,
            (descriptor, _) => descriptor.KeyId == key.KeyId && descriptor.WrappedKey.Span.SequenceEqual(key.WrappedKey.Span)
                ? new ValueTask<DataKey>(key.Retain())
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

    /// <summary>The bytes <c>[from, to)</c>, in hex: the long inputs of the RFC's second case.</summary>
    private static string Range(int from, int to)
    {
        byte[] bytes = new byte[to - from];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(from + i);
        }

        return Convert.ToHexStringLower(bytes);
    }
}
