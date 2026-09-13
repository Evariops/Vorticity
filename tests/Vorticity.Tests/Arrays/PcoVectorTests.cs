// pco chunk metadata over vectors the conformance corpus cannot provide.
//
// WHY THESE EXIST. All four `vortex.pco` corpus files carry ONE bin with an ANS size log of zero -
// the degenerate path - so they exercise neither the entropy coder nor anything that varies per
// value. `tools/conformance-gen/examples/gen_pco_vectors.rs` compresses chosen sequences with pco's
// own encoder and records, per case, the bytes AND pco's own parse of them. Regenerate with:
//
//   cd tools/conformance-gen && cargo run --release --example gen_pco_vectors > \
//     ../../tests/Vorticity.Tests/Arrays/PcoVectors.json
//
// The cases are chosen for what the corpus lacks: a 20-bin table at ANS size log 7, a narrow one at
// log 9, a second-order consecutive delta, and an IntMult chunk split across five pages.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Vorticity.Arrays.Decoders.Compressed.Pco;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoVectorTests
{
    /// <summary>One generated case.</summary>
    internal sealed record Vector(
        string Name,
        string Mode,
        string Delta,
        (string Key, int AnsSizeLog, int BinCount)[] Latents,
        byte[] Header,
        byte[] Meta,
        byte[][] Pages,
        int[] PerPage,
        long[] Values);

    internal static Vector[] Load([CallerFilePath] string thisFile = "")
    {
        string path = System.IO.Path.Combine(
            new FileInfo(thisFile).Directory!.FullName, "PcoVectors.json");
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(path));

        List<Vector> vectors = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            List<(string, int, int)> latents = [];
            foreach (JsonElement latent in element.GetProperty("latents").EnumerateArray())
            {
                latents.Add((
                    latent.GetProperty("key").GetString()!,
                    latent.GetProperty("ans_size_log").GetInt32(),
                    latent.GetProperty("n_bins").GetInt32()));
            }

            List<byte[]> pages = [];
            foreach (JsonElement page in element.GetProperty("pages").EnumerateArray())
            {
                pages.Add(Convert.FromHexString(page.GetString()!));
            }

            List<int> perPage = [];
            foreach (JsonElement n in element.GetProperty("n_per_page").EnumerateArray())
            {
                perPage.Add(n.GetInt32());
            }

            List<long> values = [];
            foreach (JsonElement value in element.GetProperty("values").EnumerateArray())
            {
                values.Add(value.GetInt64());
            }

            vectors.Add(new Vector(
                element.GetProperty("name").GetString()!,
                element.GetProperty("mode").GetString()!,
                element.GetProperty("delta").GetString()!,
                [.. latents],
                Convert.FromHexString(element.GetProperty("header").GetString()!),
                Convert.FromHexString(element.GetProperty("meta").GetString()!),
                [.. pages],
                [.. perPage],
                [.. values]));
        }

        return [.. vectors];
    }

    /// <summary>Every generated case's metadata parses to what pco says it is.</summary>
    /// <remarks>
    /// The corpus file checked in <c>PcoChunkMetaTests</c> has a single bin, so it cannot tell a
    /// correct bin loop from one that reads the first bin and stops. The 20-bin case here can.
    /// </remarks>
    [Fact]
    public void EveryVectorsMetadataMatchesTheReferenceParse()
    {
        Vector[] vectors = Load();
        Assert.Equal(4, vectors.Length);

        foreach (Vector vector in vectors)
        {
            PcoChunkMeta chunk = PcoChunkMeta.Read(vector.Header, vector.Meta, latentBits: 64);

            string mode = chunk.Mode switch
            {
                PcoModeKind.Classic => "Classic",
                PcoModeKind.IntMult => $"IntMult(U64({chunk.ModeBase}))",
                PcoModeKind.FloatMult => $"FloatMult(U64({chunk.ModeBase}))",
                _ => "FloatQuant",
            };
            Assert.Equal(vector.Mode, mode);

            string delta = chunk.Delta switch
            {
                PcoDeltaKind.NoOp => "NoOp",
                PcoDeltaKind.Consecutive =>
                    $"Consecutive {{ order: {chunk.DeltaOrder}, secondary_uses_delta: " +
                    $"{(chunk.SecondaryUsesDelta ? "true" : "false")} }}",
                _ => "Lookback",
            };
            Assert.Equal(vector.Delta, delta);

            List<(string, int, int)> parsed = [];
            if (chunk.DeltaLatent is { } deltaVar)
            {
                parsed.Add(("Delta", deltaVar.AnsSizeLog, deltaVar.Bins.Length));
            }

            parsed.Add(("Primary", chunk.Primary.AnsSizeLog, chunk.Primary.Bins.Length));
            if (chunk.Secondary is { } secondaryVar)
            {
                parsed.Add(("Secondary", secondaryVar.AnsSizeLog, secondaryVar.Bins.Length));
            }

            Assert.Equal(vector.Latents.Length, parsed.Count);
            for (int i = 0; i < parsed.Count; i++)
            {
                Assert.Equal(vector.Latents[i], parsed[i]);
            }
        }
    }

    /// <summary>The 20-bin case builds a full ANS table whose weights fill it exactly.</summary>
    /// <remarks>
    /// The weights come from a real encoder rather than from a hand-made list, so this is the first
    /// point at which the table construction meets bin weights it did not choose.
    /// </remarks>
    [Fact]
    public void TheManyBinCaseBuildsAFullAnsTable()
    {
        Vector vector = Array.Find(Load(), v => v.Name == "many_bins_classic")!;
        PcoChunkMeta chunk = PcoChunkMeta.Read(vector.Header, vector.Meta, latentBits: 64);

        Assert.Equal(20, chunk.Primary.Bins.Length);
        Assert.Equal(7, chunk.Primary.AnsSizeLog);

        PcoAnsTable table = PcoAnsTable.Build(chunk.Primary.AnsSizeLog, chunk.Primary.Bins);
        Assert.Equal(128, table.Size);

        int[] counted = new int[chunk.Primary.Bins.Length];
        foreach (uint symbol in table.StateSymbols)
        {
            counted[symbol]++;
        }

        for (int i = 0; i < chunk.Primary.Bins.Length; i++)
        {
            Assert.Equal((int)chunk.Primary.Bins[i].Weight, counted[i]);
        }
    }
}
