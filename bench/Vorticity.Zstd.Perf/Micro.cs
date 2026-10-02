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
/// Usage: <c>Vorticity.Zstd.Perf --micro tables|ncount|tree|weights [--frames a,b] [--repeat N]</c>, the repeats
/// for a profiler to attach. Prints the median time
/// of one operation, and its cycles at the clock the M4 Pro's performance cores run (4.44 GHz).
/// </remarks>
internal static class Micro
{
    private const double Ghz = 4.44;

    private sealed record Table(SequenceCode Code, short[] Norm, int TableLog, byte[] Description);

    private sealed record Blocks(List<Table> Tables, List<byte[]> Trees);

    public static int Run(string what, string[] frames, int repeat)
    {
        for (int r = 0; r < repeat; r++)
        foreach (string name in frames)
        {
            Blocks blocks = Parse(BenchFrames.Load(name));
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

                default:
                    Console.WriteLine($"unknown micro-benchmark {what}: tables, ncount, tree, weights");
                    return 1;
            }
        }

        return 0;
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
        for (int s = 0; s < samples.Length; s++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < batch; i++)
            {
                body();
            }

            samples[s] = Stopwatch.GetElapsedTime(t0).TotalNanoseconds / batch / operations;
        }

        Array.Sort(samples);
        double ns = samples[samples.Length / 2];
        string perCell = cells == 0 ? string.Empty : $", {ns * Ghz * operations / cells:F2} cycles a cell";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{frame,-15} {what}: {ns,8:F1} ns ({ns * Ghz,7:F0} cycles) each, min {samples[0]:F1}{perCell}"));
    }

    /// <summary>The table descriptions of a frame's compressed blocks.</summary>
    private static Blocks Parse(byte[] frame)
    {
        var tables = new List<Table>();
        var trees = new List<byte[]>();
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
                ParseBlock(frame.AsSpan(ip, size), tables, trees);
            }

            ip += type == 1 ? 1 : size;
            if ((blockHeader & 1) != 0)
            {
                return new Blocks(tables, trees);
            }
        }
    }

    private static void ParseBlock(ReadOnlySpan<byte> block, List<Table> tables, List<byte[]> trees)
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
            (int hs, int compressed) = sizeFormat switch
            {
                2 => (4, (int)(lhc >> 18)),
                3 => (5, (int)(lhc >> 22) + (block[4] << 10)),
                _ => (3, (int)((lhc >> 14) & 0x3FF)),
            };
            if (literalsType == 2)
            {
                trees.Add(block.Slice(hs, compressed).ToArray());
            }

            section = hs + compressed;
        }

        ReadOnlySpan<byte> sequences = block.Slice(section);
        int nbSeq = sequences[0];
        int at = 1;
        if (nbSeq == 0xFF)
        {
            at = 3;
        }
        else if (nbSeq >= 0x80)
        {
            at = 2;
        }
        else if (nbSeq == 0)
        {
            return;
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
    }
}
