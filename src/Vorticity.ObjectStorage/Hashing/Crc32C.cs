using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Vorticity.ObjectStorage.Hashing;

/// <summary>
/// CRC-32C, Castagnoli's polynomial, which Cloud Storage records for every object and checks on every
/// put. <see cref="BitOperations.Crc32C(uint, ulong)"/> runs the processor's instruction where it has
/// one, SSE 4.2 or Arm's CRC32, and a table where it has none.
/// </summary>
internal static class Crc32C
{
    /// <summary>The CRC of <paramref name="data"/>.</summary>
    internal static uint Hash(ReadOnlySpan<byte> data) => ~Update(uint.MaxValue, data);

    /// <summary>
    /// The register after <paramref name="data"/>, from <paramref name="crc"/>: start from
    /// <see cref="uint.MaxValue"/>, and complement the last register for the CRC.
    /// </summary>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return crc;
    }
}
