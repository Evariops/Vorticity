// The rows of a run of mask words selected before a filtered aggregate folds them, against each row taken alone: every
// width, words empty, full and drawn at random, the rows left out read as the filler.
using System;
using System.Numerics;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed class WordSelectTests
{
    [Fact]
    public void EachRowIsItsValueWhereKeptAndTheFillerElsewhere()
    {
        Check<long>(-7);
        Check<int>(-7);
        Check<short>(-7);
        Check<sbyte>(-7);
        Check<ulong>(7);
        Check<uint>(7);
        Check<ushort>(7);
        Check<byte>(7);
        Check<double>(-7.5);
        Check<float>(-7.5f);
    }

    private static void Check<T>(T fill)
        where T : unmanaged, INumberBase<T>
    {
        Random random = new Random(typeof(T).Name.Length * 101);
        ulong[] words = [0, ulong.MaxValue, 1, 1UL << 63, 0x5555_5555_5555_5555, 0xFFFF_0000_0000_FFFF, (ulong)random.NextInt64(), (ulong)random.NextInt64(), (ulong)random.NextInt64()];
        T[] block = new T[words.Length * 64];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = T.CreateTruncating(random.Next(1, 120));
        }

        T[] into = new T[block.Length];
        WordFold.Select<T>(block, words, fill, into);
        for (int i = 0; i < block.Length; i++)
        {
            bool kept = ((words[i >> 6] >> (i & 63)) & 1) != 0;
            Assert.True((kept ? block[i] : fill) == into[i], $"{typeof(T).Name} row {i}: {into[i]}, kept {kept}");
        }
    }
}
