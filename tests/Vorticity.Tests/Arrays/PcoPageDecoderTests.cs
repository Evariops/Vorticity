// The pco page decoder, against values produced by the reference encoder.
//
// This is the first test in the port that can be wrong in an interesting way: everything before it
// checked a parse, and a parse either matches the reference's or does not. Here a single misplaced
// bit gives a sequence of plausible numbers, which is why the vectors deliberately include a 20-bin
// entropy-coded case, a second-order delta, and a chunk split across five pages.
using System;
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
        PcoChunkMeta chunk = PcoChunkMeta.Read(vector.Header, vector.Meta, latentBits: 64);

        List<long> decoded = [];
        PcoLatentState[] states = new PcoLatentState[PcoPageDecoder.MaxLatentVars];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = new PcoLatentState();
        }

        PcoBatchScratch scratch = new PcoBatchScratch(
            new ulong[PcoPageDecoder.BatchSize],
            new int[PcoPageDecoder.BatchSize],
            new long[PcoPageDecoder.BatchSize],
            states);
        ulong[] secondary = new ulong[PcoPageDecoder.BatchSize];
        for (int page = 0; page < vector.Pages.Length; page++)
        {
            ulong[] latents = new ulong[vector.PerPage[page]];
            PcoPageDecoder.DecodeJoined(
                chunk, vector.Pages[page], vector.PerPage[page], secondary, in scratch, latents, shift: 0);
            foreach (ulong latent in latents)
            {
                // i64's ordered latent form: the unsigned value shifted so that long.MinValue is 0.
                decoded.Add(unchecked((long)latent + long.MinValue));
            }
        }

        Assert.Equal(vector.Values.Length, decoded.Count);
        for (int i = 0; i < decoded.Count; i++)
        {
            if (decoded[i] != vector.Values[i])
            {
                Assert.Fail(
                    $"{name}: value {i} decoded to {decoded[i]}, expected {vector.Values[i]}");
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
}
