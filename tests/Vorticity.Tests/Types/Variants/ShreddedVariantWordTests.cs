// The presence of a shredded field's slots read a word of 64 at a time, from a validity at any bit offset: the bits a read
// of each slot gives.
using System;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed class ShreddedVariantWordTests
{
    /// <summary>Bitmaps of any length at any offset, every word of their slots, the last one partial or whole.</summary>
    [Fact]
    public void AWordOfSlotsIsTheirBitsOneByOne()
    {
        Random random = new Random(19);
        for (int trial = 0; trial < 2_000; trial++)
        {
            int rows = random.Next(1, 300);
            int offset = random.Next(0, 70);
            byte[] bits = new byte[(offset + rows + 7) >> 3];
            random.NextBytes(bits);
            for (int word = 0; word << 6 < rows; word++)
            {
                ulong expected = 0;
                for (int i = 0; i < 64 && (word << 6) + i < rows; i++)
                {
                    int at = offset + (word << 6) + i;
                    if (((bits[at >> 3] >> (at & 7)) & 1) != 0)
                    {
                        expected |= 1UL << i;
                    }
                }

                Assert.Equal(expected, ShreddedVariant.BitsWord(bits, offset, word, rows));
            }
        }
    }
}
