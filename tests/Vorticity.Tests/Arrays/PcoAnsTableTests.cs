// pco's tANS table, checked against the reference crate's own test vectors.
//
// WHERE THE EXPECTATIONS COME FROM. pco-1.0.3/src/ans/spec.rs carries these exact cases in its own
// unit tests:
//
//   weights [1, 1, 3, 11] -> symbols [0, 3, 2, 3, 2, 3, 3, 3, 3, 1, 3, 2, 3, 3, 3, 3]
//   weights [1]           -> symbols [0]
//   weights [2]           -> symbols [0, 0]
//
// That matters more here than anywhere else in the port. The four pco files in the corpus all have
// ONE bin with an ANS size log of zero -- the degenerate path -- so making them read would say
// nothing whatever about the entropy coder. These vectors exercise the spreading that those files
// cannot, and they are the reference's own, not my arithmetic repeated back.
//
// The spreading is also the one part of pco upstream marks "needs to remain backward compatible":
// a reimplementation has no freedom in it, and a disagreement decodes silently to other symbols.
using System;

using Vorticity.Arrays.Decoders.Compressed.Pco;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoAnsTableTests
{
    [Fact]
    public void SpreadingMatchesTheReferenceVectors()
    {
        Assert.Equal<uint>(
            [0, 3, 2, 3, 2, 3, 3, 3, 3, 1, 3, 2, 3, 3, 3, 3],
            PcoAnsTable.Spread(4, [1, 1, 3, 11]));

        Assert.Equal<uint>([0], PcoAnsTable.Spread(0, [1]));
        Assert.Equal<uint>([0, 0], PcoAnsTable.Spread(1, [2]));
    }

    /// <summary>Weights that do not fill the table are refused rather than spread anyway.</summary>
    /// <remarks>
    /// Upstream returns a corruption error here. Spreading a short weight list would leave slots
    /// holding symbol 0 by default and decode a valid-looking stream into the wrong bins.
    /// </remarks>
    [Fact]
    public void WeightsThatDoNotSumToTheTableSizeAreRefused()
    {
        Assert.Throws<VortexFormatException>(() => PcoAnsTable.Spread(4, [1, 1, 3]));
        Assert.Throws<VortexFormatException>(() => PcoAnsTable.Spread(2, [1, 1, 3, 11]));
    }

    /// <summary>Every slot's symbol appears exactly as often as its weight says.</summary>
    /// <remarks>
    /// A property rather than a vector, and it holds for any weights: the spreading is a
    /// permutation of the multiset, so a stride that failed to be coprime with the table size -
    /// the one thing `ChooseStride`'s odd adjustment exists to guarantee - would show up here as a
    /// slot written twice and another left at zero.
    /// </remarks>
    [Theory]
    [InlineData(3, new uint[] { 3, 3, 2 })]
    [InlineData(3, new uint[] { 7, 1 })]
    [InlineData(5, new uint[] { 1, 2, 3, 4, 5, 6, 7, 4 })]
    [InlineData(8, new uint[] { 200, 56 })]
    public void EverySymbolGetsExactlyItsWeightOfSlots(int sizeLog, uint[] weights)
    {
        uint[] symbols = PcoAnsTable.Spread(sizeLog, weights);
        Assert.Equal(1 << sizeLog, symbols.Length);

        int[] counted = new int[weights.Length];
        foreach (uint symbol in symbols)
        {
            counted[symbol]++;
        }

        for (int i = 0; i < weights.Length; i++)
        {
            Assert.Equal((int)weights[i], counted[i]);
        }
    }

    /// <summary>
    /// The node table reproduces upstream's dense example, slot for slot.
    /// </summary>
    /// <remarks>
    /// `ans_encoder_decoder_dense` uses the spec {size_log 3, symbols [0,1,2,0,1,2,0,1],
    /// weights [3,3,2]}. `bits_to_read` is the gap in leading zeros between a symbol's running
    /// occurrence count and the table size, so symbol 0's three slots read 1, 1 and 2 bits and its
    /// state bases climb accordingly - which is the arithmetic a transposed or off-by-one table
    /// would break immediately.
    /// </remarks>
    [Fact]
    public void TheNodeTableFollowsTheRunningOccurrenceCounts()
    {
        PcoBin[] bins =
        [
            new PcoBin(3, 100, 0),
            new PcoBin(3, 200, 1),
            new PcoBin(2, 300, 2),
        ];

        PcoAnsTable table = PcoAnsTable.Build(3, bins);
        Assert.Equal(8, table.Size);

        // Derived from the definition rather than copied: for each slot, the symbol's running count
        // shifted left until it lands in [8, 16), minus 8.
        uint[] running = [3, 3, 2];
        for (int slot = 0; slot < table.Size; slot++)
        {
            uint symbol = table.StateSymbols[slot];
            uint next = running[symbol];
            int bits = 0;
            while (next << bits < 8)
            {
                bits++;
            }

            Assert.Equal(bits, table.Nodes[slot].BitsToRead);
            Assert.Equal((int)((next << bits) - 8), table.Nodes[slot].NextStateIndexBase);
            Assert.Equal(bins[symbol].OffsetBits, table.Nodes[slot].OffsetBits);
            Assert.Equal(bins[symbol].Lower, table.StateLowers[slot]);
            running[symbol]++;
        }
    }

    /// <summary>A latent with no bins still gets a one-slot table that reads nothing.</summary>
    [Fact]
    public void ALatentWithNoBinsGetsADegenerateTable()
    {
        PcoAnsTable table = PcoAnsTable.Build(0, []);
        Assert.Equal(1, table.Size);
        Assert.Equal(0, table.Nodes[0].OffsetBits);
        Assert.Equal(0, table.Nodes[0].BitsToRead);
        Assert.Equal(0UL, table.StateLowers[0]);
    }
}
