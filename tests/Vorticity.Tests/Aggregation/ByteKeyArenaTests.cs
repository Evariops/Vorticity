using System;
using System.Buffers.Binary;
using System.Linq;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The bytes of the keys of a table of text keys, composite keys or distinct pairs: in pages, so that a
/// table holds more than an array does, two gigabytes, and grows without copying its keys.
/// </summary>
public sealed class ByteKeyArenaTests
{
    /// <summary>The environment variable that lets the tests run that take gigabytes of memory.</summary>
    private const string Heavy = "VORTICITY_HEAVY";

    [Fact]
    public void ATableHoldsMoreThanTwoGigabytesOfKeys()
    {
        // 2.3 million keys of a thousand bytes: 2.3 GB, past what one array holds. Each is found again
        // under its number, and its bytes are its own.
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Heavy) == "1", $"Takes 3 GB of memory; set {Heavy}=1 to run it.");
        const int Keys = 2_300_000;
        const int Length = 1_000;
        ByteKeyTable table = new ByteKeyTable();
        byte[] key = new byte[Length];
        for (int k = 0; k < Keys; k++)
        {
            Fill(key, k);
            Assert.Equal(k, table.GetOrAdd(key, out bool added));
            Assert.True(added);
        }

        Assert.Equal(Keys, table.Count);
        Assert.True(table.Footprint > 2L * Keys * Length / 2, table.Footprint.ToString());
        for (int k = 0; k < Keys; k += 997)
        {
            Fill(key, k);
            Assert.Equal(k, table.GetOrAdd(key, out bool added));
            Assert.False(added);
            Assert.True(table.KeyOf(k).SequenceEqual(key));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeysOverManyPagesAndOneLongerThanAPageAreFoundAndKeptInPlace(bool keepLong)
    {
        // Thirty thousand keys of a hundred bytes over three pages and more, a key of two and a half
        // pages among them, a detached number; then every third key kept, the long one or not, each
        // under its new number, the others gone; then everything forgotten and taken again.
        ByteKeyTable table = new ByteKeyTable();
        byte[] key = new byte[100];
        byte[] longKey = new byte[(5 * ByteKeyTable.PageBytes) / 2];
        Fill(longKey, -1);
        for (int k = 0; k < 30_000; k++)
        {
            if (k == 12_345)
            {
                Assert.Equal(k, table.GetOrAdd(longKey, out bool longAdded));
                Assert.True(longAdded);
                continue;
            }

            if (k == 20_000)
            {
                Assert.Equal(k, table.AddDetached());
                continue;
            }

            Fill(key, k);
            Assert.Equal(k, table.GetOrAdd(key, out bool added));
            Assert.True(added);
        }

        Assert.True(table.KeyOf(12_345).SequenceEqual(longKey));
        Fill(key, 29_999);
        Assert.True(table.KeyOf(29_999).SequenceEqual(key));

        int[] kept = [.. Enumerable.Range(0, 30_000).Where(k => (k % 3 == 0 && k != 20_000 && k != 12_345) || (keepLong && k == 12_345))];

        table.Retain(kept);
        Assert.Equal(kept.Length, table.Count);
        for (int i = 0; i < kept.Length; i++)
        {
            byte[] expected = kept[i] == 12_345 ? longKey : Filled(kept[i]);
            Assert.True(table.KeyOf(i).SequenceEqual(expected), $"key {kept[i]} kept as {i}");
            Assert.Equal(i, table.GetOrAdd(expected, out bool again));
            Assert.False(again);
        }

        Fill(key, 1);
        Assert.Equal(kept.Length, table.GetOrAdd(key, out bool gone));
        Assert.True(gone);

        table.Clear();
        Assert.Equal(0, table.Count);
        for (int k = 0; k < 1_000; k++)
        {
            Fill(key, k + 1_000_000);
            Assert.Equal(k, table.GetOrAdd(key, out bool fresh));
            Assert.True(fresh);
        }

        Assert.Equal(0, table.GetOrAdd(Filled(1_000_000), out bool found));
        Assert.False(found);
    }

    private static byte[] Filled(int k)
    {
        byte[] key = new byte[100];
        Fill(key, k);
        return key;
    }

    /// <summary>A key of its number's bytes, then a filler that depends on it.</summary>
    private static void Fill(Span<byte> key, int k)
    {
        BinaryPrimitives.WriteInt32LittleEndian(key, k);
        for (int i = 4; i < key.Length; i++)
        {
            key[i] = (byte)(k + i);
        }
    }
}
