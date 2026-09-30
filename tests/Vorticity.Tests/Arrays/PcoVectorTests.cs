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
// log 9, a second-order consecutive delta, an IntMult chunk split across five pages; then each of
// the nine types Vortex stores as pco, and each mode and delta over the latents they need --
// FloatMult, FloatQuant and Dict, lookbacks, and convolutions over 16- and 32-bit latents.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Vorticity.Arrays.Decoders.Compressed.Pco;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoVectorTests
{
    /// <summary>One generated case.</summary>
    internal sealed record Vector(
        string Name,
        PType PType,
        string Mode,
        string Delta,
        (string Key, int AnsSizeLog, int BinCount)[] Latents,
        byte[] Header,
        byte[] Meta,
        byte[][] Pages,
        int[] PerPage,
        ulong[] Bits)
    {
        /// <summary>The numbers the case holds.</summary>
        internal PcoNumber Number => PcoNumber.Of(PType)!.Value;
    }

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

            List<ulong> bits = [];
            foreach (JsonElement value in element.GetProperty("bits").EnumerateArray())
            {
                bits.Add(value.GetUInt64());
            }

            vectors.Add(new Vector(
                element.GetProperty("name").GetString()!,
                PTypeOf(element.GetProperty("ptype").GetString()!),
                element.GetProperty("mode").GetString()!,
                element.GetProperty("delta").GetString()!,
                [.. latents],
                Convert.FromHexString(element.GetProperty("header").GetString()!),
                Convert.FromHexString(element.GetProperty("meta").GetString()!),
                [.. pages],
                [.. perPage],
                [.. bits]));
        }

        return [.. vectors];
    }

    private static PType PTypeOf(string name) => name switch
    {
        "u16" => PType.U16,
        "u32" => PType.U32,
        "u64" => PType.U64,
        "i16" => PType.I16,
        "i32" => PType.I32,
        "i64" => PType.I64,
        "f16" => PType.F16,
        "f32" => PType.F32,
        _ => PType.F64,
    };

    /// <summary>Every generated case's metadata parses to what pco says it is.</summary>
    /// <remarks>
    /// The corpus file checked in <c>PcoChunkMetaTests</c> has a single bin, so it cannot tell a
    /// correct bin loop from one that reads the first bin and stops. The 20-bin case here can, and
    /// the others each a mode, a delta or a latent width the corpus has none of.
    /// </remarks>
    [Fact]
    public void EveryVectorsMetadataMatchesTheReferenceParse()
    {
        Vector[] vectors = Load();
        Assert.Equal(26, vectors.Length);

        foreach (Vector vector in vectors)
        {
            PcoChunkMeta chunk = PcoChunkMeta.Read(vector.Header, vector.Meta, vector.Number);
            Assert.Equal(vector.Mode, Describe(chunk));
            Assert.Equal(vector.Delta, DescribeDelta(chunk));

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

    /// <summary>The mode as pco's <c>Debug</c> prints it, latents in their width's variant.</summary>
    private static string Describe(PcoChunkMeta chunk)
    {
        PcoNumber number = chunk.Number;
        string variant = $"U{number.LatentBits}";
        return chunk.Mode switch
        {
            PcoModeKind.Classic => "Classic",
            PcoModeKind.IntMult => $"IntMult({variant}({chunk.ModeBase}))",
            PcoModeKind.FloatMult => $"FloatMult({variant}({chunk.ModeBase}))",
            PcoModeKind.FloatQuant => $"FloatQuant({chunk.ModeBase})",
            _ => $"Dict({variant}([{string.Join(", ", chunk.Dictionary.ToArray().Select(n => Latent(n, number)))}]))",
        };
    }

    /// <summary>A number's bits back as the latent pco keeps it, <c>to_latent_ordered</c>.</summary>
    private static ulong Latent(ulong bits, PcoNumber number) => number.Kind switch
    {
        PcoNumberKind.Signed => unchecked(bits - number.Mid) & number.Mask,
        PcoNumberKind.Float => number.FloatToLatentOrdered(bits),
        _ => bits,
    };

    /// <summary>The delta encoding as pco's <c>Debug</c> prints it.</summary>
    private static string DescribeDelta(PcoChunkMeta chunk)
    {
        string secondary = chunk.SecondaryUsesDelta ? "true" : "false";
        return chunk.Delta switch
        {
            PcoDeltaKind.NoOp => "NoOp",
            PcoDeltaKind.Consecutive => $"Consecutive {{ order: {chunk.DeltaOrder}, secondary_uses_delta: {secondary} }}",
            PcoDeltaKind.Lookback =>
                $"Lookback {{ config: DeltaLookbackConfig {{ state_n_log: {chunk.LookbackStateLog}, " +
                $"window_n_log: {chunk.LookbackWindowLog} }}, secondary_uses_delta: {secondary} }}",
            _ =>
                $"Conv1(DeltaConv1Config {{ quantization: {chunk.Conv1Quantization}, bias: {chunk.Conv1Bias}, " +
                $"weights: [{string.Join(", ", chunk.Conv1Weights.ToArray())}] }})",
        };
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
        PcoChunkMeta chunk = PcoChunkMeta.Read(vector.Header, vector.Meta, vector.Number);

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

    /// <summary>
    /// Metadata refilled vector after vector -- a 20-bin table, then single bins, then 20 bins
    /// again, every type and mode between -- reads each as metadata read afresh would, tables,
    /// dictionaries and delta parameters included.
    /// </summary>
    [Fact]
    public void RefilledMetadataReadsAsFreshMetadata()
    {
        Vector[] vectors = Load();
        PcoChunkMeta refilled = PcoChunkMeta.Read(vectors[0].Header, vectors[0].Meta, vectors[0].Number);
        foreach (Vector vector in (Vector[])[.. vectors, .. vectors])
        {
            refilled.Refill(vector.Header, vector.Meta, vector.Number);
            PcoChunkMeta fresh = PcoChunkMeta.Read(vector.Header, vector.Meta, vector.Number);

            Assert.Equal(fresh.Number, refilled.Number);
            Assert.Equal(fresh.Dictionary.ToArray(), refilled.Dictionary.ToArray());
            Assert.Equal(fresh.LookbackWindowLog, refilled.LookbackWindowLog);
            Assert.Equal(fresh.LookbackStateLog, refilled.LookbackStateLog);
            Assert.Equal(fresh.Conv1Quantization, refilled.Conv1Quantization);
            Assert.Equal(fresh.Conv1Bias, refilled.Conv1Bias);
            Assert.Equal(fresh.Conv1Weights.ToArray(), refilled.Conv1Weights.ToArray());
            Assert.Equal(fresh.DeltaLatent is null, refilled.DeltaLatent is null);
            Assert.Equal(fresh.Mode, refilled.Mode);
            Assert.Equal(fresh.ModeBase, refilled.ModeBase);
            Assert.Equal(fresh.Delta, refilled.Delta);
            Assert.Equal(fresh.DeltaOrder, refilled.DeltaOrder);
            Assert.Equal(fresh.SecondaryUsesDelta, refilled.SecondaryUsesDelta);
            Assert.Equal(fresh.Secondary is null, refilled.Secondary is null);
            AssertSameVar(fresh.Primary, refilled.Primary);
            if (fresh.Secondary is { } secondary)
            {
                AssertSameVar(secondary, refilled.Secondary!.Value);
            }
        }
    }

    private static void AssertSameVar(PcoLatentVar expected, PcoLatentVar actual)
    {
        Assert.Equal(expected.AnsSizeLog, actual.AnsSizeLog);
        Assert.Equal(expected.Bins.ToArray(), actual.Bins.ToArray());
        Assert.Equal(expected.Table.Size, actual.Table.Size);
        Assert.Equal(expected.Table.StateSymbols.ToArray(), actual.Table.StateSymbols.ToArray());
        Assert.Equal(expected.Table.Nodes.ToArray(), actual.Table.Nodes.ToArray());
        Assert.Equal(expected.Table.StateLowers.ToArray(), actual.Table.StateLowers.ToArray());
    }
}
