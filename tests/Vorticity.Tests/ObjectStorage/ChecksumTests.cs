// The checksums the services verify, taken from the platform's System.IO.Hashing: CRC-64/NVME (S3's
// CRC64NVME, Azure's CRC-64) and CRC-32C (Cloud Storage). Each parameter set the stores use is held to
// its catalogued check value and to vectors computed by a bitwise reference written apart from the
// platform, so that a set taken by mistake shows here rather than in a service's refusal.
using System;
using System.IO.Hashing;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class ChecksumTests
{
    [Fact]
    public void Crc64NvmeMatchesItsCatalogueAndAReference()
    {
        Assert.Equal(0xAE8B14860A799888UL, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, "123456789"u8));
        Assert.Equal(0UL, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, []));
        Assert.Equal(0x8C2F8445B4CBFC3CUL, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, "a"u8));
        Assert.Equal(0xCF3473434D4ECF3BUL, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, new byte[32]));
        Assert.Equal(0xA0A06974C34D63C4UL, Crc64.HashToUInt64(Crc64ParameterSet.Nvme, Filled(32, 0xFF)));

        byte[] data = Pattern(10_007);
        Assert.Equal(Reference64(data), Crc64.HashToUInt64(Crc64ParameterSet.Nvme, data));
    }

    [Fact]
    public void Crc32CMatchesItsCatalogueAndTheIscsiVectors()
    {
        Assert.Equal(0xE3069283U, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, "123456789"u8));
        Assert.Equal(0U, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, []));

        // RFC 3720, B.4: 32 bytes of zeros and 32 of ones.
        Assert.Equal(0x8A9136AAU, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, new byte[32]));
        Assert.Equal(0x62A8AB43U, Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, Filled(32, 0xFF)));
    }

    [Fact]
    public void AnAppendInPiecesIsTheHashOfTheWhole()
    {
        // How a multipart upload's whole-object CRC64NVME is kept: its parts appended in order as they fill.
        byte[] data = Pattern(10_007);
        foreach (int cut in new[] { 0, 1, 7, 8, 9, 63, 4_096, 10_006, 10_007 })
        {
            Crc64 crc64 = new Crc64(Crc64ParameterSet.Nvme);
            crc64.Append(data.AsSpan(0, cut));
            crc64.Append(data.AsSpan(cut));
            Assert.Equal(Crc64.HashToUInt64(Crc64ParameterSet.Nvme, data), crc64.GetCurrentHashAsUInt64());

            Crc32 crc32 = new Crc32(Crc32ParameterSet.Crc32C);
            crc32.Append(data.AsSpan(0, cut));
            crc32.Append(data.AsSpan(cut));
            Assert.Equal(Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, data), crc32.GetCurrentHashAsUInt32());
        }
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
