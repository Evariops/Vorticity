using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The table of the keys wider than a word: slots of a hash and a group, the key read from the groups'
/// keys. Every key found again under its first number, through growths, chains and a seed; the first
/// pass as the lookup finds; a third of the bytes of a table whose slots hold their keys, or less; and
/// group-bys of UUIDs as LINQ makes them, on the lanes and through the core.
/// </summary>
public sealed partial class WideKeyTableTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryKeyFindsTheGroupItWasFirstGiven(bool inOrder)
    {
        // 200 000 keys met twice, at random or in a row: a growth at every doubling, then every key
        // found again; the first pass finds a key at home or leaves it to the lookup, never another's.
        const int count = 200_000;
        Random random = new Random(17);
        UInt128[] keys = new UInt128[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = inOrder ? (UInt128)(1_000_000 + i) : new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
        }

        WideKeyTable<UInt128> table = new WideKeyTable<UInt128>();
        UInt128[] held = new UInt128[count];
        int groups = Insert(ref table, keys, held, 0);
        Assert.Equal(count, groups);
        Assert.Equal(groups, Insert(ref table, keys, held, groups));
        Assert.Equal(count, table.Count);
        Assert.False(table.Seeded);

        UInt128[] probe = new UInt128[8_192];
        keys.AsSpan(0, 4_096).CopyTo(probe);
        for (int i = 4_096; i < probe.Length; i++)
        {
            probe[i] = inOrder ? (UInt128)i : new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
        }

        foreach (int ahead in (int[])[0, 8])
        {
            int[] found = new int[probe.Length];
            uint[] hashes = new uint[probe.Length];
            table.FindAtHome(probe, found, hashes, ahead, held.AsSpan(0, groups), out bool missed);
            Assert.True(missed);
            int home = 0;
            for (int i = 0; i < probe.Length; i++)
            {
                int expected = i < 4_096 ? i : -1;
                Assert.True(found[i] == -1 || found[i] == expected, $"key {i} found as {found[i]}");
                home += found[i] >= 0 ? 1 : 0;
            }

            Assert.True(home > 2_048, $"{home} of 4 096 keys at home");
        }
    }

    [Fact]
    public void KeysThatShareTheirHashTakeASeedAndAreAllFound()
    {
        // Halves equal fold to zero: every key homed at the same slot, its line full, then its chain,
        // until a chain of 64 links makes the table take a seed and hash every key again.
        WideKeyTable<UInt128> table = new WideKeyTable<UInt128>();
        UInt128[] keys = new UInt128[5_000];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new UInt128((ulong)i, (ulong)i);
        }

        UInt128[] held = new UInt128[keys.Length];
        Assert.Equal(keys.Length, Insert(ref table, keys, held, 0));
        Assert.True(table.Seeded);
        Assert.Equal(keys.Length, Insert(ref table, keys, held, keys.Length));

        int[] found = new int[keys.Length];
        table.FindAtHome(keys, found, [], 0, held, out _);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.True(found[i] == -1 || found[i] == i, $"key {i} found as {found[i]}");
        }
    }

    [Fact]
    public void ShortTextsAndKeysOf32BytesAreFoundToo()
    {
        WideKeyTable<TextWord> words = new WideKeyTable<TextWord>();
        TextWord[] texts = new TextWord[50_000];
        for (int i = 0; i < texts.Length; i++)
        {
            Assert.True(ShortTextKeys.TryWord(System.Text.Encoding.UTF8.GetBytes($"id{i:D10}"), out texts[i]));
        }

        TextWord[] heldTexts = new TextWord[texts.Length];
        Assert.Equal(texts.Length, Insert(ref words, texts, heldTexts, 0));
        Assert.Equal(texts.Length, Insert(ref words, texts, heldTexts, texts.Length));

        Random random = new Random(29);
        WideKeyTable<Int256> wide = new WideKeyTable<Int256>();
        Int256[] values = new Int256[50_000];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = new Int256(new Int128((ulong)random.NextInt64(long.MinValue, long.MaxValue), (ulong)random.NextInt64()));
        }

        Int256[] heldValues = new Int256[values.Length];
        Assert.Equal(values.Length, Insert(ref wide, values, heldValues, 0));
        Assert.Equal(values.Length, Insert(ref wide, values, heldValues, values.Length));
    }

    [Fact]
    public void AReservedTableDoesNotGrowAndAClearedOneNumbersAgain()
    {
        Random random = new Random(31);
        UInt128[] keys = new UInt128[10_000];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
        }

        WideKeyTable<UInt128> table = new WideKeyTable<UInt128>();
        table.Reserve(keys.Length);
        long reserved = table.Footprint;
        UInt128[] held = new UInt128[keys.Length];
        Assert.Equal(keys.Length, Insert(ref table, keys, held, 0));

        // The slots placed once: only the lines' chains grow, a key in fifty or so past a full line.
        Assert.True(table.Footprint < reserved * 11 / 10, $"{table.Footprint:N0} bytes, {reserved:N0} reserved");

        // Cleared, the keys come back in the other order and take the numbers of that order.
        table.Clear(0);
        Array.Reverse(keys);
        Assert.Equal(keys.Length, Insert(ref table, keys, held, 0));
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(i, table.GetOrAdd(keys[i], keys.Length, held));
        }
    }

    [Fact]
    public void AClearedTableMakesRoomForTheKeysItHeldBesideThoseKept()
    {
        // 164 keys, the most 277 slots hold before they grow at 166; five kept and 164 more would pass
        // it: cleared with room for 169, the table doubles empty, then takes them all without a growth.
        Random random = new Random(43);
        UInt128[] keys = new UInt128[169];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
        }

        WideKeyTable<UInt128> table = new WideKeyTable<UInt128>();
        UInt128[] held = new UInt128[keys.Length];
        Assert.Equal(164, Insert(ref table, keys[..164], held, 0));
        long before = table.Footprint;
        table.Clear(169);
        long room = table.Footprint;
        Assert.True(room > before, $"{room:N0} bytes, {before:N0} before");
        Assert.Equal(169, Insert(ref table, keys, held, 0));
        Assert.Equal(room, table.Footprint);

        // Room it has already: nothing grows.
        table.Clear(100);
        Assert.Equal(room, table.Footprint);
    }

    [Fact]
    public void ATableOfWideKeysTakesAThirdOfTheBytesOfOneWhoseSlotsHoldThem()
    {
        // The same keys in both, the same growths: slots of 8 bytes against the key and its group.
        Random random = new Random(37);
        WideKeyTable<UInt128> wide = new WideKeyTable<UInt128>();
        KeyTable<UInt128> holding = new KeyTable<UInt128>();
        UInt128[] held = new UInt128[100_000];
        for (int i = 0; i < held.Length; i++)
        {
            UInt128 key = new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
            Assert.Equal(i, wide.GetOrAdd(key, i, held));
            Assert.Equal(i, holding.GetOrAdd(key, i));
            held[i] = key;
        }

        Assert.True(wide.Footprint * 5 / 2 < holding.Footprint, $"{wide.Footprint:N0} bytes against {holding.Footprint:N0}, slots of {SlotBytes<UInt128>()}");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(14, true)]
    public async Task AUuidKeyGroupsAsLinqDoes(int degree, bool core)
    {
        // 150 000 users, half drawn at random and half in a row, each met in both halves of the file:
        // by two lanes at four; at fourteen through the core, its tables split small.
        const int users = 150_000;
        Random random = new Random(41);
        Guid[] ids = new Guid[users];
        for (int i = 0; i < users; i++)
        {
            ids[i] = i % 2 == 0 ? new Guid(random.Next(), (short)random.Next(), (short)random.Next(), (byte)random.Next(), (byte)random.Next(), 1, 2, 3, 4, 5, 6) : new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        Visit[] rows = new Visit[2 * users];
        for (int row = 0; row < rows.Length; row++)
        {
            int user = row % users;
            rows[row] = new Visit(ids[user], row % 7, (long)(row % 1_000));
        }

        string path = Path.Combine(AppContext.BaseDirectory, "wide-keys", $"visits-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
        {
            await writer.WriteAsync<Visit>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Visit>().GroupBy(r => r.User).Select(g => (g.Key, g.Count(), g.Sum(r => r.Value)));
            if (core)
            {
                query.Plan.Core = true;
                query.Plan.CoreLanes = 1;
                query.Plan.CoreCapacity = 96;
                query.Plan.CoreFloor = 16;
                query.Plan.CoreTableGroups = 40;
                query.Plan.CoreBatchEntries = 8;
            }

            Dictionary<Guid, UserTotal> read = [];
            await foreach (UserTotal total in query.As<UserTotal>().ToRecordsAsync(Ct))
            {
                read.Add(total.User, total);
            }

            Dictionary<Guid, UserTotal> expected = rows.GroupBy(r => r.User).ToDictionary(g => g.Key, g => new UserTotal(g.Key, g.Count(), g.Sum(r => r.Value)));
            Assert.Equal(expected.Count, read.Count);
            foreach ((Guid user, UserTotal total) in expected)
            {
                Assert.Equal(total, read[user]);
            }

            if (core)
            {
                CoreRun run = query.Plan.LastRun!.Core!;
                Assert.True(run.Bursts > 0 && run.Splits > 0, run.ToString());
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AStreamingKeyWithAUuidPartCondensesAndKeepsItsOpenGroups(int degree)
    {
        // Twenty days in order, 10 000 rows a day over 30 000 users: the users' part passes 4 096 groups
        // within the first day, moves to slots of a hash and a group, then keeps the open day's users at
        // each batch closed.
        const int users = 30_000;
        Random random = new Random(47);
        Guid[] ids = new Guid[users];
        for (int i = 0; i < users; i++)
        {
            ids[i] = new Guid(random.Next(), (short)random.Next(), (short)random.Next(), 7, 7, 7, 7, 7, 7, 7, (byte)i);
        }

        Visit[] rows = new Visit[20 * 10_000];
        for (int row = 0; row < rows.Length; row++)
        {
            rows[row] = new Visit(ids[random.Next(users)], row / 10_000, row % 1_000);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "wide-keys", $"days-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Visit>(path))
        {
            await writer.WriteAsync<Visit>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation query = file.Scan<Visit>().GroupBy(r => (r.Day, r.User)).Select(g => (g.Key.Day, g.Key.User, g.Count(), g.Sum(r => r.Value)));
            Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)query.Query) >= 0);

            Dictionary<(int, Guid), DayUserTotal> read = [];
            await foreach (DayUserTotal total in query.As<DayUserTotal>().ToRecordsAsync(Ct))
            {
                read.Add((total.Day, total.User), total);
            }

            Dictionary<(int, Guid), DayUserTotal> expected = rows.GroupBy(r => (r.Day, r.User))
                .ToDictionary(g => g.Key, g => new DayUserTotal(g.Key.Day, g.Key.User, g.Count(), g.Sum(r => r.Value)));
            Assert.Equal(expected.Count, read.Count);
            foreach (((int, Guid) key, DayUserTotal total) in expected)
            {
                Assert.Equal(total, read[key]);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The keys into the table in their order, numbered from <paramref name="groups"/> as they are new; the groups at the end.</summary>
    private static int Insert<TValue>(ref WideKeyTable<TValue> table, TValue[] keys, TValue[] held, int groups)
        where TValue : unmanaged, IEquatable<TValue>
    {
        foreach (TValue key in keys)
        {
            int group = table.GetOrAdd(key, groups, held);
            if (group == groups)
            {
                held[groups++] = key;
            }
            else
            {
                Assert.True(held[group].Equals(key), $"key {key} found as group {group}, which holds {held[group]}");
            }
        }

        return groups;
    }

    /// <summary>The bytes of a slot that holds a key of <typeparamref name="TValue"/> and its group, as <see cref="KeyTable{TValue}"/> lays it.</summary>
    private static int SlotBytes<TValue>()
        where TValue : unmanaged => Unsafe.SizeOf<(TValue Key, int Group)>();

    [VortexRecord]
    public partial record struct Visit(Guid User, int Day, long Value);

    [VortexRecord]
    public partial record struct UserTotal(Guid User, long Count, long Sum);

    [VortexRecord]
    public partial record struct DayUserTotal(int Day, Guid User, long Count, long Sum);
}
