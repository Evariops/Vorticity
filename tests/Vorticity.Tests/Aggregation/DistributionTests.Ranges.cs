// A distribution over ranges of a batch some of whose rows a filter keeps: a range's kept rows counted as a sketch fed them
// one at a time counts them, whatever the range's length and wherever it starts in its words.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Aggregation;

public sealed partial class DistributionTests
{
    /// <summary>
    /// Ranges of one row to several words, starting anywhere in a word, over 5,000 durations, a tenth of them zero, of
    /// which a filter keeps three in four, one in sixty-four, or every one but the first: each range's sketch holds the
    /// buckets, the count, the zeros and the bytes of a sketch fed its kept rows one at a time.
    /// </summary>
    [Fact]
    public void ARangesKeptRowsCountAsTheyDoOneAtATime()
    {
        const int Rows = 5_000;
        const int Bits = 7;
        Random random = new Random(23);
        long[] values = [.. Enumerable.Range(0, Rows).Select(_ => random.Next(10) == 0 ? 0L : random.NextInt64(1, 5_000_000))];
        foreach (int keep in (int[])[4, 64, 0])
        {
            ulong[] mask = new ulong[(Rows + 63) / 64];
            for (int row = 0; row < Rows; row++)
            {
                bool kept = keep switch
                {
                    4 => random.Next(4) != 0,
                    64 => random.Next(64) == 0,
                    _ => row > 0,
                };
                if (kept)
                {
                    mask[row >> 6] |= 1UL << row;
                }
            }

            List<int> bounds = [0];
            while (bounds[^1] < Rows)
            {
                bounds.Add(Math.Min(Rows, bounds[^1] + random.Next(1, random.Next(2) == 0 ? 70 : 900)));
            }

            CanonicalArena arena = new CanonicalArena();
            DType dtype = new DTypeArena().Primitive(PType.I64, Nullability.NonNullable);
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), 8, out Span<byte> into);
            MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(into);
            int node = arena.AddPrimitive(dtype, Rows, Validity.NonNullable, PType.I64, buffer);
            BatchInput input = new BatchInput(1, arena, node, Rows, mask);
            DistributionSlot<long, QuantileSketch?> slot = new DistributionSlot<long, QuantileSketch?>(Bits, static sketch => sketch, shelved: false);
            slot.EnsureGroups(bounds.Count - 1);
            for (int g = 0; g + 1 < bounds.Count; g++)
            {
                slot.StepRange(input, bounds[g], bounds[g + 1], g);
            }

            for (int g = 0; g + 1 < bounds.Count; g++)
            {
                QuantileSketch alone = new QuantileSketch(Bits);
                for (int row = bounds[g]; row < bounds[g + 1]; row++)
                {
                    if ((mask[row >> 6] & (1UL << row)) != 0)
                    {
                        alone.Add(values[row]);
                    }
                }

                QuantileSketch? stepped = slot.Result(g);
                if (alone.Count == 0)
                {
                    Assert.Null(stepped);
                    continue;
                }

                Assert.NotNull(stepped);
                Assert.Equal(alone.Buckets(), stepped.Buckets());
                Assert.Equal(alone.Count, stepped.Count);
                Assert.Equal(alone.Zeros, stepped.Zeros);
                Assert.Equal(alone.Bytes, stepped.Bytes);
            }
        }
    }
}
