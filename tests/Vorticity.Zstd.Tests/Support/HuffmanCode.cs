using System;
using System.Collections.Generic;
using System.Linq;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// A Huffman code as the format describes it, built independently of the decoder: weights per symbol,
/// and the canonical codes they imply (by increasing weight, then by symbol). Encodes literals into
/// the streams a literals section carries.
/// </summary>
internal sealed class HuffmanCode
{
    private HuffmanCode(byte[] weights)
    {
        Weights = weights;
        int total = weights.Where(w => w > 0).Sum(w => 1 << (w - 1));
        TableLog = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)total);
        if (1 << TableLog != total)
        {
            throw new ArgumentException("weights do not complete a code", nameof(weights));
        }

        Codes = new uint[weights.Length];
        Lengths = new int[weights.Length];
        int position = 0;
        foreach (int s in Enumerable.Range(0, weights.Length).Where(s => weights[s] > 0).OrderBy(s => weights[s]).ThenBy(s => s))
        {
            int w = weights[s];
            Lengths[s] = TableLog + 1 - w;
            Codes[s] = (uint)(position >> (w - 1));
            position += 1 << (w - 1);
        }
    }

    /// <summary>Per symbol: 0 for an absent symbol, otherwise <c>TableLog + 1 - length</c>.</summary>
    public byte[] Weights { get; }

    public int TableLog { get; }

    public uint[] Codes { get; }

    public int[] Lengths { get; }

    public static HuffmanCode FromWeights(params byte[] weights) => new(weights);

    /// <summary>
    /// A random complete code over <paramref name="symbolCount"/> symbols, none longer than
    /// <paramref name="maxLength"/> bits: a leaf of the tree is split until every symbol has one.
    /// </summary>
    public static HuffmanCode Random(Random random, int symbolCount, int maxLength)
    {
        var depths = new List<int> { 1, 1 };
        while (depths.Count < symbolCount)
        {
            int[] splittable = Enumerable.Range(0, depths.Count).Where(i => depths[i] < maxLength).ToArray();
            int leaf = splittable[random.Next(splittable.Length)];
            depths[leaf]++;
            depths.Add(depths[leaf]);
        }

        int longest = depths.Max();
        byte[] weights = new byte[symbolCount];
        int[] order = Enumerable.Range(0, symbolCount).OrderBy(_ => random.Next()).ToArray();
        for (int i = 0; i < symbolCount; i++)
        {
            weights[order[i]] = (byte)(longest + 1 - depths[i]);
        }

        return new HuffmanCode(weights);
    }

    /// <summary>
    /// The tree description in the direct representation: the weights of every symbol but the last
    /// present one, four bits each. At most 128 weights.
    /// </summary>
    public byte[] DescribeDirect()
    {
        int last = Array.FindLastIndex(Weights, w => w > 0);
        int count = last; // the last symbol's weight is implied
        if (count < 1 || count > 128)
        {
            throw new InvalidOperationException("direct representation holds 1 to 128 weights");
        }

        byte[] description = new byte[1 + ((count + 1) / 2)];
        description[0] = (byte)(127 + count);
        for (int n = 0; n < count; n++)
        {
            description[1 + (n / 2)] |= (byte)(n % 2 == 0 ? Weights[n] << 4 : Weights[n]);
        }

        return description;
    }

    /// <summary>One stream: symbols written last first, so that a backward reader yields them in order.</summary>
    public byte[] EncodeStream(ReadOnlySpan<byte> symbols)
    {
        var bits = new BitWriter();
        for (int i = symbols.Length - 1; i >= 0; i--)
        {
            bits.Add(Codes[symbols[i]], Lengths[symbols[i]]);
        }

        return bits.Close();
    }

    /// <summary>Four streams behind their jump table, each a quarter of the symbols rounded up.</summary>
    public byte[] EncodeFourStreams(ReadOnlySpan<byte> symbols)
    {
        int segment = (symbols.Length + 3) / 4;
        byte[][] streams =
        [
            EncodeStream(symbols.Slice(0, segment)),
            EncodeStream(symbols.Slice(segment, segment)),
            EncodeStream(symbols.Slice(2 * segment, segment)),
            EncodeStream(symbols.Slice(3 * segment)),
        ];
        var output = new List<byte>();
        for (int i = 0; i < 3; i++)
        {
            output.Add((byte)streams[i].Length);
            output.Add((byte)(streams[i].Length >> 8));
        }

        foreach (byte[] stream in streams)
        {
            output.AddRange(stream);
        }

        return [.. output];
    }

    /// <summary>
    /// A literals section compressed with this code: its description (unless treeless, which reuses
    /// the previous block's), then one or four streams.
    /// </summary>
    public byte[] Literals(ReadOnlySpan<byte> literals, bool fourStreams, bool treeless = false)
    {
        byte[] payload = [.. (treeless ? [] : DescribeDirect()), .. fourStreams ? EncodeFourStreams(literals) : EncodeStream(literals)];
        return [.. CompressedLiteralsHeader(treeless ? 3 : 2, literals.Length, payload.Length, fourStreams), .. payload];
    }

    public static byte[] CompressedLiteralsHeader(int type, int regenerated, int compressed, bool fourStreams)
    {
        int largest = Math.Max(regenerated, compressed);
        if (largest < 1024)
        {
            long h = (long)type | ((long)(fourStreams ? 1 : 0) << 2) | ((long)regenerated << 4) | ((long)compressed << 14);
            return [(byte)h, (byte)(h >> 8), (byte)(h >> 16)];
        }

        if (!fourStreams)
        {
            throw new ArgumentException("a single stream holds fewer than 1024 literals", nameof(fourStreams));
        }

        if (largest < 16384)
        {
            long h = (long)type | (2L << 2) | ((long)regenerated << 4) | ((long)compressed << 18);
            return [(byte)h, (byte)(h >> 8), (byte)(h >> 16), (byte)(h >> 24)];
        }

        long l = (long)type | (3L << 2) | ((long)regenerated << 4) | ((long)compressed << 22);
        return [(byte)l, (byte)(l >> 8), (byte)(l >> 16), (byte)(l >> 24), (byte)(l >> 32)];
    }
}
