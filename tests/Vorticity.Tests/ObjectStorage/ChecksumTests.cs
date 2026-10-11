// The checksums the services verify: CRC-64/NVME (S3's CRC64NVME, Azure's CRC-64) and CRC-32C (Cloud
// Storage). Each is held to its catalogued check value and to vectors computed by a bitwise reference
// written apart from the implementation, and the combination of two CRCs to the CRC of the two parts
// put end to end.
using System;
using System.Text;
using Vorticity.ObjectStorage.Hashing;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class ChecksumTests
{
    [Fact]
    public void Crc64NvmeMatchesItsCatalogueAndAReference()
    {
        Assert.Equal(0xAE8B14860A799888UL, Crc64Nvme.Hash("123456789"u8));
        Assert.Equal(0UL, Crc64Nvme.Hash([]));
        Assert.Equal(0x8C2F8445B4CBFC3CUL, Crc64Nvme.Hash("a"u8));
        Assert.Equal(0xCF3473434D4ECF3BUL, Crc64Nvme.Hash(new byte[32]));
        Assert.Equal(0xA0A06974C34D63C4UL, Crc64Nvme.Hash(Filled(32, 0xFF)));
    }

    [Fact]
    public void Crc32CMatchesItsCatalogueAndTheIscsiVectors()
    {
        Assert.Equal(0xE3069283U, Crc32C.Hash("123456789"u8));
        Assert.Equal(0U, Crc32C.Hash([]));

        // RFC 3720, B.4: 32 bytes of zeros and 32 of ones.
        Assert.Equal(0x8A9136AAU, Crc32C.Hash(new byte[32]));
        Assert.Equal(0x62A8AB43U, Crc32C.Hash(Filled(32, 0xFF)));
    }

    [Fact]
    public void AnUpdateInPiecesIsTheHashOfTheWhole()
    {
        byte[] data = Pattern(10_007);
        foreach (int cut in new[] { 0, 1, 7, 8, 9, 63, 4_096, 10_006, 10_007 })
        {
            ulong crc64 = Crc64Nvme.Update(Crc64Nvme.Update(ulong.MaxValue, data.AsSpan(0, cut)), data.AsSpan(cut));
            Assert.Equal(Crc64Nvme.Hash(data), ~crc64);
            uint crc32 = Crc32C.Update(Crc32C.Update(uint.MaxValue, data.AsSpan(0, cut)), data.AsSpan(cut));
            Assert.Equal(Crc32C.Hash(data), ~crc32);
        }

        Assert.Equal(Reference64(data), Crc64Nvme.Hash(data));
    }

    [Fact]
    public void TwoCrcsCombineIntoTheCrcOfTheirConcatenation()
    {
        // What gives a multipart upload's whole-object CRC64NVME from its parts', sent in parallel.
        byte[] data = Pattern(100_003);
        foreach (int cut in new[] { 0, 1, 8, 1_000, 65_536, 100_002, 100_003 })
        {
            ulong first = Crc64Nvme.Hash(data.AsSpan(0, cut));
            ulong second = Crc64Nvme.Hash(data.AsSpan(cut));
            Assert.Equal(Crc64Nvme.Hash(data), Crc64Nvme.Combine(first, second, data.Length - cut));
        }

        // Three parts, combined left to right.
        ulong a = Crc64Nvme.Hash(data.AsSpan(0, 30_000));
        ulong b = Crc64Nvme.Hash(data.AsSpan(30_000, 30_000));
        ulong c = Crc64Nvme.Hash(data.AsSpan(60_000));
        Assert.Equal(Crc64Nvme.Hash(data), Crc64Nvme.Combine(Crc64Nvme.Combine(a, b, 30_000), c, data.Length - 60_000));
    }

    /// <summary>The CRC-64/NVME of <paramref name="data"/> a bit at a time, from its definition.</summary>
    private static ulong Reference64(ReadOnlySpan<byte> data)
    {
        ulong crc = ulong.MaxValue;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x9A6C9329AC4BC9B5UL : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        new Random(0x0C_4C).NextBytes(bytes);
        return bytes;
    }
}
