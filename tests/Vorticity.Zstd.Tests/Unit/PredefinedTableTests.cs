using System;
using Vorticity.Zstd.Internal;
using Xunit;

namespace Vorticity.Zstd.Tests.Unit;

/// <summary>
/// The predefined sequence tables, built here from the default distributions, against the tables
/// libzstd ships precomputed: the same table builder then serves every FSE-mode table.
/// </summary>
public sealed class PredefinedTableTests
{
    [Fact]
    public void Literal_lengths_match_libzstd() => AssertTable(SequenceCodes.DefaultLiteralLengths, 6, ZstdLiteralLengths);

    [Fact]
    public void Offsets_match_libzstd() => AssertTable(SequenceCodes.DefaultOffsets, 5, ZstdOffsets);

    [Fact]
    public void Match_lengths_match_libzstd() => AssertTable(SequenceCodes.DefaultMatchLengths, 6, ZstdMatchLengths);

    private static void AssertTable(SeqTable table, int tableLog, (int NextState, int NbAdditionalBits, int NbBits, uint BaseValue)[] expected)
    {
        Assert.Equal(tableLog, table.TableLog);
        Assert.Equal(1 << tableLog, expected.Length);

        // Every state takes two entries, alike, and the next states index the whole doubled table
        // set: libzstd's are relative to the table.
        int slot = SequenceTableSet.Slot(table.Code);
        for (int i = 0; i < expected.Length; i++)
        {
            SeqSymbol actual = table.Entries[2 * i];
            Assert.Equal(actual, table.Entries[(2 * i) + 1]);
            int nextState = (actual.NextState - slot) / 2;
            var fields = (nextState, actual.NbAdditionalBits(table.Code), actual.NbBits(table.Code), actual.BaseValue(table.Code));
            Assert.True(fields == expected[i], $"state {i}: expected {expected[i]}, got {fields}");
        }
    }

    /// <summary>libzstd's <c>LL_defaultDTable</c>, entries only: (nextState, nbAdditionalBits, nbBits, baseValue).</summary>
    private static readonly (int, int, int, uint)[] ZstdLiteralLengths =
    [
        (0, 0, 4, 0), (16, 0, 4, 0), (32, 0, 5, 1), (0, 0, 5, 3),
        (0, 0, 5, 4), (0, 0, 5, 6), (0, 0, 5, 7), (0, 0, 5, 9),
        (0, 0, 5, 10), (0, 0, 5, 12), (0, 0, 6, 14), (0, 1, 5, 16),
        (0, 1, 5, 20), (0, 1, 5, 22), (0, 2, 5, 28), (0, 3, 5, 32),
        (0, 4, 5, 48), (32, 6, 5, 64), (0, 7, 5, 128), (0, 8, 6, 256),
        (0, 10, 6, 1024), (0, 12, 6, 4096), (32, 0, 4, 0), (0, 0, 4, 1),
        (0, 0, 5, 2), (32, 0, 5, 4), (0, 0, 5, 5), (32, 0, 5, 7),
        (0, 0, 5, 8), (32, 0, 5, 10), (0, 0, 5, 11), (0, 0, 6, 13),
        (32, 1, 5, 16), (0, 1, 5, 18), (32, 1, 5, 22), (0, 2, 5, 24),
        (32, 3, 5, 32), (0, 3, 5, 40), (0, 6, 4, 64), (16, 6, 4, 64),
        (32, 7, 5, 128), (0, 9, 6, 512), (0, 11, 6, 2048), (48, 0, 4, 0),
        (16, 0, 4, 1), (32, 0, 5, 2), (32, 0, 5, 3), (32, 0, 5, 5),
        (32, 0, 5, 6), (32, 0, 5, 8), (32, 0, 5, 9), (32, 0, 5, 11),
        (32, 0, 5, 12), (0, 0, 6, 15), (32, 1, 5, 18), (32, 1, 5, 20),
        (32, 2, 5, 24), (32, 2, 5, 28), (32, 3, 5, 40), (32, 4, 5, 48),
        (0, 16, 6, 65536), (0, 15, 6, 32768), (0, 14, 6, 16384), (0, 13, 6, 8192),
    ];

    /// <summary>libzstd's <c>OF_defaultDTable</c>, entries only: (nextState, nbAdditionalBits, nbBits, baseValue).</summary>
    private static readonly (int, int, int, uint)[] ZstdOffsets =
    [
        (0, 0, 5, 0), (0, 6, 4, 61), (0, 9, 5, 509), (0, 15, 5, 32765),
        (0, 21, 5, 2097149), (0, 3, 5, 5), (0, 7, 4, 125), (0, 12, 5, 4093),
        (0, 18, 5, 262141), (0, 23, 5, 8388605), (0, 5, 5, 29), (0, 8, 4, 253),
        (0, 14, 5, 16381), (0, 20, 5, 1048573), (0, 2, 5, 1), (16, 7, 4, 125),
        (0, 11, 5, 2045), (0, 17, 5, 131069), (0, 22, 5, 4194301), (0, 4, 5, 13),
        (16, 8, 4, 253), (0, 13, 5, 8189), (0, 19, 5, 524285), (0, 1, 5, 1),
        (16, 6, 4, 61), (0, 10, 5, 1021), (0, 16, 5, 65533), (0, 28, 5, 268435453),
        (0, 27, 5, 134217725), (0, 26, 5, 67108861), (0, 25, 5, 33554429), (0, 24, 5, 16777213),
    ];

    /// <summary>libzstd's <c>ML_defaultDTable</c>, entries only: (nextState, nbAdditionalBits, nbBits, baseValue).</summary>
    private static readonly (int, int, int, uint)[] ZstdMatchLengths =
    [
        (0, 0, 6, 3), (0, 0, 4, 4), (32, 0, 5, 5), (0, 0, 5, 6),
        (0, 0, 5, 8), (0, 0, 5, 9), (0, 0, 5, 11), (0, 0, 6, 13),
        (0, 0, 6, 16), (0, 0, 6, 19), (0, 0, 6, 22), (0, 0, 6, 25),
        (0, 0, 6, 28), (0, 0, 6, 31), (0, 0, 6, 34), (0, 1, 6, 37),
        (0, 1, 6, 41), (0, 2, 6, 47), (0, 3, 6, 59), (0, 4, 6, 83),
        (0, 7, 6, 131), (0, 9, 6, 515), (16, 0, 4, 4), (0, 0, 4, 5),
        (32, 0, 5, 6), (0, 0, 5, 7), (32, 0, 5, 9), (0, 0, 5, 10),
        (0, 0, 6, 12), (0, 0, 6, 15), (0, 0, 6, 18), (0, 0, 6, 21),
        (0, 0, 6, 24), (0, 0, 6, 27), (0, 0, 6, 30), (0, 0, 6, 33),
        (0, 1, 6, 35), (0, 1, 6, 39), (0, 2, 6, 43), (0, 3, 6, 51),
        (0, 4, 6, 67), (0, 5, 6, 99), (0, 8, 6, 259), (32, 0, 4, 4),
        (48, 0, 4, 4), (16, 0, 4, 5), (32, 0, 5, 7), (32, 0, 5, 8),
        (32, 0, 5, 10), (32, 0, 5, 11), (0, 0, 6, 14), (0, 0, 6, 17),
        (0, 0, 6, 20), (0, 0, 6, 23), (0, 0, 6, 26), (0, 0, 6, 29),
        (0, 0, 6, 32), (0, 16, 6, 65539), (0, 15, 6, 32771), (0, 14, 6, 16387),
        (0, 13, 6, 8195), (0, 12, 6, 4099), (0, 11, 6, 2051), (0, 10, 6, 1027),
    ];
}
