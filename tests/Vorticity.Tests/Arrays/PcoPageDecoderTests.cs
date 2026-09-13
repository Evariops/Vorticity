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
        for (int page = 0; page < vector.Pages.Length; page++)
        {
            ulong[] primary = new ulong[vector.PerPage[page]];
            ulong[] secondary = new ulong[vector.PerPage[page]];
            ReadOnlySpan<ulong> latents = PcoPageDecoder.DecodeJoined(
                chunk, vector.Pages[page], vector.PerPage[page], primary, secondary);
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
}
