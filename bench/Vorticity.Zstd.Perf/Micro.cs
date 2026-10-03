using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Vorticity.Zstd.Bench;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd.Perf;

/// <summary>
/// Micro-benchmarks of a frame's fixed costs, in the Native AOT build: the table descriptions of each
/// block are taken from the frame itself, then each step is timed alone, many times over.
/// </summary>
/// <remarks>
/// Usage: <c>Vorticity.Zstd.Perf --micro tables|ncount|tree|weights|x1|x2|x2build|compress|ncompress|cmatch|nmatch|centropy|cliterals|chist|csequences [--frames a,b] [--repeat N]
/// [--pcprofile file]</c>, the repeats for a profiler to attach; <c>--pcprofile</c> samples the
/// timed loops' program counters (see <see cref="PcSampler"/>). Prints the median time
/// of one operation, and its cycles at the clock the M4 Pro's performance cores run (4.44 GHz).
/// </remarks>
internal static class Micro
{
    private const double Ghz = 4.44;

    private sealed record Table(SequenceCode Code, short[] Norm, int TableLog, byte[] Description);

    private sealed record Literals(byte[] Tree, byte[] Streams, int Size);

    private sealed record Blocks(List<Table> Tables, List<byte[]> Trees, int Sequences, List<Literals> Sections);

    /// <summary>The number of sequences in a frame's compressed blocks.</summary>
    public static int CountSequences(byte[] frame) => Parse(frame).Sequences;

    public static int Run(string what, string[] frames, int repeat)
    {
        for (int r = 0; r < repeat; r++)
        foreach (string name in frames)
        {
            // A name ending in -ldm compresses with the long-distance matcher on; in -dict, with a
            // dictionary of the content's kind (compress and ncompress only, timed per frame).
            bool longDistance = name.EndsWith("-ldm", StringComparison.Ordinal);
            bool withDictionary = name.EndsWith("-dict", StringComparison.Ordinal);
            string frameName = longDistance ? name[..^4] : withDictionary ? name[..^5] : name;
            Blocks blocks = Parse(BenchFrames.Load(frameName));
            switch (what)
            {
                case "tables":
                {
                    var set = new SequenceTableSet();
                    int cells = 0;
                    foreach (Table t in blocks.Tables)
                    {
                        cells += 1 << t.TableLog;
                    }

                    Report(name, $"{blocks.Tables.Count} tables, {cells} cells", blocks.Tables.Count, cells, () =>
                    {
                        foreach (Table t in blocks.Tables)
                        {
                            set.Build(t.Code, t.Norm, t.TableLog);
                        }
                    });
                    break;
                }

                case "x1":
                case "x2":
                case "x2build":
                {
                    // The four-stream literal sections alone, their trees read beforehand; x2build
                    // times the double-symbol table's construction from a read tree.
                    bool useDouble = what != "x1";
                    var table = new HuffmanTable();
                    var output = new byte[128 << 10];
                    int symbols = 0;
                    foreach (Literals l in blocks.Sections)
                    {
                        symbols += l.Size;
                    }

                    if (what == "x2build")
                    {
                        var trees = blocks.Sections.ConvertAll(l => l.Tree);
                        Report(name, $"{trees.Count} double-symbol tables", trees.Count, 0, () =>
                        {
                            foreach (byte[] tree in trees)
                            {
                                table.Read(tree);
                                table.BuildDoubleForBenchmark();
                            }
                        });
                        break;
                    }

                    Report(name, $"{blocks.Sections.Count} sections, {symbols} symbols ({what})", blocks.Sections.Count, symbols, () =>
                    {
                        byte[]? current = null;
                        foreach (Literals l in blocks.Sections)
                        {
                            if (!ReferenceEquals(l.Tree, current))
                            {
                                table.Read(l.Tree);
                                current = l.Tree;
                            }

                            table.DecodeFourStreams(l.Streams, output.AsSpan(0, l.Size), useDouble);
                        }
                    });
                    break;
                }

                case "ncount":
                {
                    short[] norm = new short[SequenceCodes.MaxMatchLength + 1];
                    Report(name, $"{blocks.Tables.Count} descriptions", blocks.Tables.Count, 0, () =>
                    {
                        foreach (Table t in blocks.Tables)
                        {
                            int max = SequenceCodes.MaxSymbol(t.Code);
                            Fse.ReadNCount(norm, ref max, out _, t.Description, ZstdError.FseTable);
                        }
                    });
                    break;
                }

                case "tree":
                {
                    var table = new HuffmanTable();
                    Report(name, $"{blocks.Trees.Count} trees", blocks.Trees.Count, 0, () =>
                    {
                        foreach (byte[] tree in blocks.Trees)
                        {
                            table.Read(tree);
                        }
                    });
                    break;
                }

                case "weights":
                {
                    byte[] weights = new byte[256];
                    var compressed = blocks.Trees.FindAll(t => t[0] < 128);
                    Report(name, $"{compressed.Count} FSE-compressed weight sets", compressed.Count, 0, () =>
                    {
                        foreach (byte[] tree in compressed)
                        {
                            Fse.DecodeHuffmanWeights(tree.AsSpan(1, tree[0]), weights);
                        }
                    });
                    break;
                }

                case "compress":
                case "ncompress":
                case "cmatch":
                case "nmatch":
                case "centropy":
                case "cliterals":
                case "chist":
                case "csequences":
                {
                    // Compression's stages alone, on the blocks a real compression of the frame's
                    // content cuts: a cycle count per sequence, and per byte (per literal for cliterals).
                    (byte[] content, int level) = BenchFrames.LoadContent(frameName);
                    byte[]? dictionary = withDictionary ? BenchFrames.Dictionary(frameName) : null;
                    if (dictionary is not null && what is not ("compress" or "ncompress"))
                    {
                        throw new ArgumentException(what + " takes no dictionary");
                    }

                    var compressor = dictionary is null ? new ZstdCompressor(level) { LongDistanceMatching = longDistance } : new ZstdCompressor(level, dictionary);
                    List<ZstdCompressor.BlockRecord> records = dictionary is null ? compressor.RecordBlocks(content) : [];
                    int sequences = dictionary is null ? 0 : 1;
                    int literals = 0;
                    foreach (ZstdCompressor.BlockRecord record in records)
                    {
                        sequences += record.Sequences.Length;
                        literals += record.Literals.Length;
                    }

                    switch (what)
                    {
                        case "compress":
                        {
                            byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
                            Report(name, $"compression, {sequences} sequences, {content.Length} bytes, per sequence", sequences, content.Length,
                                () => compressor.Compress(content, frame, out _, out _));
                            break;
                        }

                        case "ncompress":
                        {
                            NativeReference native = NativeReference.TryLoad() ?? throw new InvalidOperationException("no " + NativeReference.LibraryPath);
                            PcSampler.Libraries.Add((NativeReference.LibraryPath, "_ZSTD_versionNumber", native.VersionAddress));
                            byte[] frame = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
                            Func<byte[], byte[], int> compress = dictionary is null ? (s, o) => native.Compress(s, o, level) : native.WithDictionary(dictionary, level);
                            Report(name, $"libzstd compression, {sequences} sequences, {content.Length} bytes, per sequence", sequences, content.Length,
                                () => compress(content, frame));
                            break;
                        }

                        case "cmatch":
                            Report(name, $"match finding, {sequences} sequences, {content.Length} bytes, per sequence", sequences, content.Length,
                                () => compressor.FindSequencesOnly(content, records));
                            break;
                        case "nmatch":
                        {
                            // libzstd's match finding, per Vorticity.Zstd's sequences (the same, block splits aside).
                            NativeReference native = NativeReference.TryLoad() ?? throw new InvalidOperationException("no " + NativeReference.LibraryPath);
                            PcSampler.Libraries.Add((NativeReference.LibraryPath, "_ZSTD_versionNumber", native.VersionAddress));
                            byte[] collected = new byte[16 * ((content.Length / 3) + 1024)];
                            Report(name, $"libzstd match finding, {sequences} sequences, {content.Length} bytes, per sequence", sequences, content.Length,
                                () => native.GenerateSequences(content, level, collected));
                            break;
                        }
                        case "centropy":
                            Report(name, $"entropy coding, {sequences} sequences, {content.Length} bytes, per sequence", sequences, content.Length,
                                () => compressor.EntropyOnly(records, level, literals: true, sequences: true));
                            break;
                        case "cliterals":
                            Report(name, $"literals, {literals} literals, per sequence", sequences, literals,
                                () => compressor.EntropyOnly(records, level, literals: true, sequences: false));
                            break;
                        case "chist":
                            Report(name, $"literal histograms, {literals} literals, per sequence", sequences, literals,
                                () => HistogramsOnly(records));
                            break;
                        default:
                            Report(name, $"sequences section, {sequences} sequences, per sequence", sequences, sequences,
                                () => compressor.EntropyOnly(records, level, literals: false, sequences: true));
                            break;
                    }

                    break;
                }

                default:
                    Console.WriteLine($"unknown micro-benchmark {what}: tables, ncount, tree, weights, x1, x2, x2build, compress, ncompress, cmatch, nmatch, centropy, cliterals, chist, csequences");
                    return 1;
            }
        }

        return 0;
    }

    /// <summary>The histogram of each block's literals, as literals compression counts them.</summary>
    private static unsafe uint HistogramsOnly(List<ZstdCompressor.BlockRecord> records)
    {
        uint* count = stackalloc uint[256];
        uint total = 0;
        foreach (ZstdCompressor.BlockRecord record in records)
        {
            fixed (byte* literals = record.Literals)
            {
                total += Histogram.CountFast(count, out _, literals, (nuint)record.Literals.Length);
            }
        }

        return total;
    }

    private static void Report(string frame, string what, int operations, int cells, Action body)
    {
        if (operations == 0)
        {
            Console.WriteLine($"{frame}: no {what}");
            return;
        }

        // Batches long enough for the timer, the median of many.
        int batch = 1;
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 2)
        {
            for (int i = 0; i < batch; i++)
            {
                body();
            }

            batch *= 2;
        }

        var samples = new double[101];
        using (PcSampler.Start())
        {
            for (int s = 0; s < samples.Length; s++)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < batch; i++)
                {
                    body();
                }

                samples[s] = Stopwatch.GetElapsedTime(t0).TotalNanoseconds / batch / operations;
            }
        }

        Array.Sort(samples);
        double ns = samples[samples.Length / 2];
        string perCell = cells == 0 ? string.Empty : $", {ns * Ghz * operations / cells:F2} cycles a cell (or symbol)";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{frame,-15} {what}: {ns,8:F1} ns ({ns * Ghz,7:F0} cycles) each, min {samples[0]:F1}{perCell}"));
    }

    /// <summary>The table descriptions of a frame's compressed blocks.</summary>
    private static Blocks Parse(byte[] frame)
    {
        var tables = new List<Table>();
        var trees = new List<byte[]>();
        var sections = new List<Literals>();
        int sequences = 0;
        if (FrameHeader.Parse(frame, out FrameHeader header) != ZstdError.None)
        {
            throw new InvalidOperationException("bad frame");
        }

        int ip = header.HeaderSize;
        while (true)
        {
            int blockHeader = frame[ip] | (frame[ip + 1] << 8) | (frame[ip + 2] << 16);
            ip += 3;
            int type = (blockHeader >> 1) & 3;
            int size = blockHeader >> 3;
            if (type == 2)
            {
                sequences += ParseBlock(frame.AsSpan(ip, size), tables, trees, sections);
            }

            ip += type == 1 ? 1 : size;
            if ((blockHeader & 1) != 0)
            {
                return new Blocks(tables, trees, sequences, sections);
            }
        }
    }

    /// <returns>The block's number of sequences.</returns>
    private static int ParseBlock(ReadOnlySpan<byte> block, List<Table> tables, List<byte[]> trees, List<Literals> sections)
    {
        int literalsType = block[0] & 3;
        int sizeFormat = (block[0] >> 2) & 3;
        int section;
        if (literalsType < 2)
        {
            (int hs, int size) = sizeFormat switch
            {
                1 => (2, (block[0] | (block[1] << 8)) >> 4),
                3 => (3, (block[0] | (block[1] << 8) | (block[2] << 16)) >> 4),
                _ => (1, block[0] >> 3),
            };
            section = hs + (literalsType == 0 ? size : 1);
        }
        else
        {
            uint lhc = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
            (int hs, int compressed, int regenerated) = sizeFormat switch
            {
                2 => (4, (int)(lhc >> 18), (int)((lhc >> 4) & 0x3FFF)),
                3 => (5, (int)(lhc >> 22) + (block[4] << 10), (int)((lhc >> 4) & 0x3FFFF)),
                _ => (3, (int)((lhc >> 14) & 0x3FF), (int)((lhc >> 4) & 0x3FF)),
            };
            if (literalsType == 2)
            {
                trees.Add(block.Slice(hs, compressed).ToArray());
            }

            // Four-stream sections, each with the tree it decodes with (its own, or the last one).
            if (sizeFormat != 0 && trees.Count > 0)
            {
                byte[] tree = trees[^1];
                int treeSize = literalsType == 2 ? new HuffmanTable().Read(tree) : 0;
                sections.Add(new Literals(tree, block.Slice(hs + treeSize, compressed - treeSize).ToArray(), regenerated));
            }

            section = hs + compressed;
        }

        ReadOnlySpan<byte> sequences = block.Slice(section);
        int nbSeq = sequences[0];
        int at = 1;
        if (nbSeq == 0xFF)
        {
            nbSeq = sequences[1] + (sequences[2] << 8) + 0x7F00;
            at = 3;
        }
        else if (nbSeq >= 0x80)
        {
            nbSeq = ((nbSeq - 0x80) << 8) + sequences[1];
            at = 2;
        }
        else if (nbSeq == 0)
        {
            return 0;
        }

        int modes = sequences[at++];
        foreach ((SequenceCode code, int mode) in new[]
        {
            (SequenceCode.LiteralLength, modes >> 6),
            (SequenceCode.Offset, (modes >> 4) & 3),
            (SequenceCode.MatchLength, (modes >> 2) & 3),
        })
        {
            if (mode == 1)
            {
                at++;
            }
            else if (mode == 2)
            {
                short[] norm = new short[SequenceCodes.MaxMatchLength + 1];
                int max = SequenceCodes.MaxSymbol(code);
                int length = Fse.ReadNCount(norm, ref max, out int tableLog, sequences.Slice(at), ZstdError.FseTable);
                tables.Add(new Table(code, norm.AsSpan(0, max + 1).ToArray(), tableLog, sequences.Slice(at, length).ToArray()));
                at += length;
            }
        }

        return nbSeq;
    }
}
