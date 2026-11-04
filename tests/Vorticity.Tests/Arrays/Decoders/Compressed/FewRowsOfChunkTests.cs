// A scan that reads a few rows of each chunk decodes, of a run-end node, the runs its range falls in alone, and of a
// dictionary, the entries its range's codes name alone: every range gives the rows the whole node gives.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Compressed;

public sealed class FewRowsOfChunkTests
{
    private const int Rows = 60_000;

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public void ARangeOfARunEndNodeOfManyRunsIsItsRows(int offset)
    {
        Random random = new Random(11 + offset);
        List<uint> ends = [];
        List<int> values = [];
        uint end = (uint)offset;
        while (end < offset + Rows)
        {
            end = (uint)Math.Min(end + random.Next(1, 40), offset + Rows);
            ends.Add(end);
            values.Add(random.Next());
        }

        TestNode root = new TestNode("vortex.runend")
            .WithMetadata(TestMetadata.RunEnd(PType.U32, (ulong)ends.Count, (ulong)offset))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));
        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.UInt32([.. ends]), TestBuffers.Int32([.. values]));
        AssertRanges(harness, random, Rows / 4);
    }

    [Fact]
    public void ARangeOfADictionaryOfManyEntriesIsItsRows()
    {
        const int entries = 5_000;
        Random random = new Random(5);
        ushort[] codes = new ushort[Rows];
        for (int row = 0; row < Rows; row++)
        {
            codes[row] = (ushort)random.Next(entries);
        }

        int[] values = new int[entries];
        for (int entry = 0; entry < entries; entry++)
        {
            values[entry] = random.Next();
        }

        TestNode root = new TestNode("vortex.dict")
            .WithMetadata(TestMetadata.Dict(entries, PType.U16, false))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(0))
            .WithChild(new TestNode("vortex.primitive").WithBuffer(1));
        using DecodeHarness harness = DecodeHarness.Load(root, TestBuffers.UInt16(codes), TestBuffers.Int32(values));
        AssertRanges(harness, random, (entries / 4) - 1);
    }

    /// <summary>Ranges of at most <paramref name="most"/> rows, the first and the last among them, decoded as a few rows of the chunk, against the whole node.</summary>
    private static void AssertRanges(DecodeHarness harness, Random random, int most)
    {
        DType i32 = harness.Types.Primitive(PType.I32, Nullability.NonNullable);
        int[] whole = ForDecoderTests.ReadInt32(harness.Node(harness.DecodeRoot(i32, Rows)));
        harness.Scan.FewRowsOfChunks = true;
        List<(int Start, int Count)> ranges = [(0, most), (Rows - most, most), (Rows - 1, 1), (0, 1)];
        for (int i = 0; i < 200; i++)
        {
            int count = random.Next(1, most + 1);
            ranges.Add((random.Next(Rows - count + 1), count));
        }

        foreach ((int start, int count) in ranges)
        {
            int[] range = ForDecoderTests.ReadInt32(harness.Node(harness.DecodeRootRange(i32, Rows, start, count)));
            Assert.Equal(whole.AsSpan(start, count).ToArray(), range);
        }
    }
}
