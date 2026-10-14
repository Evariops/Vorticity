// The pco page decoder, against values produced by the reference encoder.
//
// This is the first test in the port that can be wrong in an interesting way: everything before it
// checked a parse, and a parse either matches the reference's or does not. Here a single misplaced
// bit gives a sequence of plausible numbers, which is why the vectors deliberately include a 20-bin
// entropy-coded case, a second-order delta, a chunk split across five pages, and then every type,
// mode and delta pco has -- compared bit for bit, a float's sign of zero included.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using Vorticity.Arrays.Decoders.Compressed.Pco;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoPageDecoderTests
{
    public static TheoryData<string> Cases()
    {
        TheoryData<string> data = [];
        foreach (PcoVectorTests.Vector vector in PcoVectorTests.Load())
        {
            data.Add(vector.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryVectorDecodesValueForValue(string name)
    {
        PcoVectorTests.Vector vector = Array.Find(PcoVectorTests.Load(), v => v.Name == name)!;
        PcoNumber number = vector.Number;
        PcoChunkMeta chunk = PcoChunkMeta.Read(vector.Header, vector.Meta, number);
        int width = number.LatentBits / 8;

        List<ulong> decoded = [];
        PcoLatentState[] states = new PcoLatentState[PcoPageDecoder.MaxLatentVars];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = new PcoLatentState();
        }

        PcoBatchScratch scratch = new PcoBatchScratch(
            new ulong[PcoPageDecoder.BatchSize],
            new int[PcoPageDecoder.BatchSize],
            states,
            new ulong[PcoPageDecoder.BatchSize]);
        ulong[] secondary = new ulong[PcoPageDecoder.BatchSize];
        for (int page = 0; page < vector.Pages.Length; page++)
        {
            byte[] numbers = new byte[vector.PerPage[page] * width];
            PcoPageDecoder.DecodeJoined(chunk, vector.Pages[page], vector.PerPage[page], secondary, in scratch, numbers);
            for (int i = 0; i < vector.PerPage[page]; i++)
            {
                decoded.Add(width switch
                {
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(numbers.AsSpan(i * 2)),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(numbers.AsSpan(i * 4)),
                    _ => BinaryPrimitives.ReadUInt64LittleEndian(numbers.AsSpan(i * 8)),
                });
            }
        }

        Assert.Equal(vector.Bits.Length, decoded.Count);
        for (int i = 0; i < decoded.Count; i++)
        {
            if (decoded[i] != vector.Bits[i])
            {
                Assert.Fail($"{name}: value {i} decoded to bits {decoded[i]:x}, expected {vector.Bits[i]:x}");
            }
        }
    }

    /// <summary>
    /// A batch described as a constant or a ramp joins, and shifts, to what its values written out
    /// would, in every combination the page decoder composes, wrapping included.
    /// </summary>
    [Theory]
    [InlineData(13)]
    [InlineData(PcoPageDecoder.BatchSize)]
    public void EveryShapeJoinsAsItsValuesWrittenOut(int batch)
    {
        Random random = new Random(batch);
        ulong NextWord() => (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);

        foreach (PcoBatchShape primaryShape in (PcoBatchShape[])[PcoBatchShape.Written, PcoBatchShape.Constant, PcoBatchShape.Ramp])
        {
            foreach (PcoBatchShape secondaryShape in (PcoBatchShape[])[PcoBatchShape.Written, PcoBatchShape.Constant])
            {
                ulong first = NextWord();
                ulong step = NextWord();
                ulong secondaryValue = NextWord();
                ulong modeBase = NextWord();
                ulong shift = 1UL << 63;
                ulong[] written = new ulong[batch];
                ulong[] secondary = new ulong[batch];
                for (int i = 0; i < batch; i++)
                {
                    written[i] = NextWord();
                    secondary[i] = secondaryShape == PcoBatchShape.Written ? NextWord() : 0;
                }

                ulong[] classic = (ulong[])written.Clone();
                ulong[] joined = (ulong[])written.Clone();
                PcoPageDecoder.Shift(classic, primaryShape, first, step, shift);
                PcoPageDecoder.Join(joined, primaryShape, first, step, secondary, secondaryShape, secondaryValue, modeBase, shift);

                for (int i = 0; i < batch; i++)
                {
                    ulong p = primaryShape switch
                    {
                        PcoBatchShape.Constant => first,
                        PcoBatchShape.Ramp => unchecked(first + ((ulong)i * step)),
                        _ => written[i],
                    };
                    ulong s = secondaryShape == PcoBatchShape.Constant ? secondaryValue : secondary[i];
                    Assert.Equal(unchecked(p + shift), classic[i]);
                    Assert.Equal(unchecked((p * modeBase) + s + shift), joined[i]);
                }
            }
        }
    }

    /// <summary>
    /// An order of a consecutive delta undone a vector at a time gives, at 16 and 32 bits, what the
    /// value-by-value sum gives: every value, wrapping included, and the moment past the batch, over
    /// lengths short of a vector, on one and between them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(PcoPageDecoder.BatchSize)]
    public void PrefixSumsMatchTheValueByValueSum(int length)
    {
        Random random = new Random(length);
        foreach (ulong bias in (ulong[])[0, 1UL << 15, 1UL << 31])
        {
            ushort[] shorts = new ushort[length];
            uint[] words = new uint[length];
            for (int i = 0; i < length; i++)
            {
                shorts[i] = (ushort)random.Next();
                words[i] = (uint)random.NextInt64();
            }

            ulong moment = (ulong)random.NextInt64();
            ushort[] expectedShorts = (ushort[])shorts.Clone();
            uint[] expectedWords = (uint[])words.Clone();
            ulong shortsMoment = Reference(expectedShorts, moment, bias);
            ulong wordsMoment = Reference(expectedWords, moment, bias);

            Assert.Equal((ushort)shortsMoment, (ushort)PcoLatentState.PrefixSums<ushort>(shorts, moment, bias));
            Assert.Equal(expectedShorts, shorts);
            Assert.Equal((uint)wordsMoment, (uint)PcoLatentState.PrefixSums<uint>(words, moment, bias));
            Assert.Equal(expectedWords, words);
        }

        static ulong Reference<T>(T[] values, ulong moment, ulong bias)
            where T : unmanaged, System.Numerics.IBinaryInteger<T>
        {
            for (int i = 0; i < values.Length; i++)
            {
                ulong previous = unchecked(ulong.CreateTruncating(values[i]) + bias);
                values[i] = T.CreateTruncating(moment);
                moment = unchecked(moment + previous);
            }

            return moment;
        }
    }
}
