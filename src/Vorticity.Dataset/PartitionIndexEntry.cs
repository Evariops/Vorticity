using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Dataset;

/// <summary>
/// An entry of the tree of partition indexes (D13, the header's field 28): the index object of a consolidated partition
/// (<see cref="PartitionIndex"/>), under the partition's value as the tree's order encodes it, behind a byte of its own so
/// that a dataset without a partition keys its one all the same. It names the index object, the version that wrote it,
/// and the objects it answers for: the uids of the partition's objects whose questions its pages answer. A purge of one of
/// them takes its uid away by the commit of its replacement.
/// </summary>
/// <param name="Partition">The partition's value, row-encoded as it leads the tree keys of its objects; empty for none.</param>
/// <param name="ObjectKey">The index object's key in the store.</param>
/// <param name="Uid">Its uid, which its sealed bytes are bound to.</param>
/// <param name="Bytes">The bytes the store holds of it.</param>
/// <param name="Version">The version that wrote it.</param>
/// <param name="Covered">The uids of the objects it answers for, in no order.</param>
internal sealed record PartitionIndexEntry(
    ReadOnlyMemory<byte> Partition, string ObjectKey, UInt128 Uid, long Bytes, ulong Version, IReadOnlyList<UInt128> Covered)
{
    /// <summary>The byte every tree key starts with, before the partition's value.</summary>
    private const byte KeyLead = 1;

    /// <summary>The version of the entry's value.</summary>
    private const ulong FormatVersion = 1;

    /// <summary>The entry's key in the tree.</summary>
    internal byte[] TreeKey => TreeKeyOf(Partition.Span);

    /// <summary>The tree key of the index of <paramref name="partition"/>.</summary>
    internal static byte[] TreeKeyOf(ReadOnlySpan<byte> partition)
    {
        byte[] key = new byte[partition.Length + 1];
        key[0] = KeyLead;
        partition.CopyTo(key.AsSpan(1));
        return key;
    }

    /// <summary>Whether the index answers for the object of uid <paramref name="uid"/>.</summary>
    internal bool Covers(UInt128 uid)
    {
        foreach (UInt128 covered in Covered)
        {
            if (covered == uid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ranks in <paramref name="index"/>, the directory of the object the entry names, of the objects the entry covers, a
    /// bit a rank: what a lookup asks of each object it meets, where it ran through every uid covered.
    /// </summary>
    internal ulong[] RanksIn(PartitionIndex index)
    {
        ulong[] ranks = new ulong[(index.Objects.Count + 63) >> 6];
        for (int i = 0; i < Covered.Count; i++)
        {
            if (index.RankOf(Covered[i]) is int rank and >= 0)
            {
                ranks[rank >> 6] |= 1UL << (rank & 63);
            }
        }

        return ranks;
    }

    /// <summary>The entry's value: its format's version, the version that wrote it, the object's key, uid and bytes, the uids it covers.</summary>
    internal byte[] ToBytes()
    {
        ProtoWriter writer = new ProtoWriter();
        try
        {
            Span<byte> uid = stackalloc byte[16];
            writer.WriteVarint(FormatVersion);
            writer.WriteVarint(Version);
            byte[] key = Encoding.UTF8.GetBytes(ObjectKey);
            writer.WriteVarint((ulong)key.Length);
            writer.WriteRawBytes(key);
            BinaryPrimitives.WriteUInt128LittleEndian(uid, Uid);
            writer.WriteRawBytes(uid);
            writer.WriteVarint((ulong)Bytes);
            writer.WriteVarint((ulong)Covered.Count);
            foreach (UInt128 covered in Covered)
            {
                BinaryPrimitives.WriteUInt128LittleEndian(uid, covered);
                writer.WriteRawBytes(uid);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The entry a tree holds under <paramref name="treeKey"/>.</summary>
    /// <exception cref="VortexFormatException">The value is not an entry's this version reads.</exception>
    internal static PartitionIndexEntry FromBytes(ReadOnlySpan<byte> treeKey, ReadOnlySpan<byte> value)
    {
        ProtoReader reader = new ProtoReader(value);
        if (treeKey.IsEmpty || treeKey[0] != KeyLead || reader.ReadVarint() != FormatVersion)
        {
            throw new VortexFormatException("An entry of the tree of partition indexes is not one this version reads.");
        }

        ulong version = reader.ReadVarint();
        int keyLength = checked((int)reader.ReadVarint());
        if (keyLength > reader.Remaining)
        {
            throw new VortexFormatException("An entry of the tree of partition indexes names its object past its bytes.");
        }

        string key = Encoding.UTF8.GetString(value.Slice(reader.Position, keyLength));
        reader = Skipped(value, reader.Position + keyLength);
        UInt128 uid = Uid128(ref reader);
        long bytes = checked((long)reader.ReadVarint());
        int count = checked((int)reader.ReadVarint());
        if ((long)count * 16 != reader.Remaining)
        {
            throw new VortexFormatException("An entry of the tree of partition indexes names its objects past its bytes.");
        }

        UInt128[] covered = new UInt128[count];
        for (int i = 0; i < count; i++)
        {
            covered[i] = Uid128(ref reader);
        }

        return new PartitionIndexEntry(treeKey[1..].ToArray(), key, uid, bytes, version, covered);

        static ProtoReader Skipped(ReadOnlySpan<byte> value, int at)
        {
            ProtoReader past = new ProtoReader(value[at..]);
            return past;
        }

        static UInt128 Uid128(ref ProtoReader reader)
        {
            ulong low = reader.ReadFixed64();
            ulong high = reader.ReadFixed64();
            return new UInt128(high, low);
        }
    }
}
