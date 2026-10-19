using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Serialization.Schemas;
using Vorticity.Types.Variants;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What shredding a path costs and saves in the bytes of a file (<c>--micro shredcost</c>): one chunk of
/// 100 000 rows of attributes, a varying number of integer, text and double paths, each held by one row
/// (the fixed cost of a path in a chunk) or by every row (what each occurrence saves), written whole
/// and shredded. Also the tables a shredded path adds to the chunk's encoding tree.
/// </summary>
internal static class ShredCostMicro
{
    private const int Rows = 100_000;

    internal static async Task<int> Run()
    {
        Console.WriteLine("paths  held by    class     whole B   shredded B   per path B   per occurrence B   tables per path");
        foreach (VariantClass @class in new[] { VariantClass.Integer, VariantClass.String, VariantClass.Double, VariantClass.Boolean })
        {
            foreach (int paths in new[] { 10, 100, 1_000 })
            {
                foreach (Held held in new[] { Held.OneRow, Held.EveryRow, Held.FirstHalf })
                {
                    if (held != Held.OneRow && paths > 100)
                    {
                        continue;
                    }

                    ShredRow[] rows = Generate(paths, held, @class);
                    VariantShredding plan = VariantShredding.Object([.. Enumerable.Range(0, paths).Select(p => ($"path-{p:D4}", VariantShredding.Primitive(@class)))]);
                    (long whole, int wholeTables) = await WriteAsync(rows, null);
                    (long shredded, int shreddedTables) = await WriteAsync(rows, plan);
                    long occurrences = held switch { Held.EveryRow => (long)paths * Rows, Held.FirstHalf => (long)paths * Rows / 2, _ => paths };
                    Console.WriteLine(
                        $"{paths,5}  {held,-9}  {@class,-8} {whole,10} {shredded,12} {(double)(shredded - whole) / paths,12:F1} {(double)(whole - shredded) / occurrences,18:F3} {(double)(shreddedTables - wholeTables) / paths,17:F2}");
                }
            }
        }

        return 0;
    }

    /// <summary>Which rows hold each path.</summary>
    private enum Held
    {
        OneRow,
        EveryRow,
        FirstHalf,
    }

    /// <summary>
    /// The rows, each an id and attributes: <paramref name="paths"/> paths of one class held by one row
    /// each, by every row, or by the first half of the rows, as rows sorted by shape hold them, beside a
    /// key no shredding names.
    /// </summary>
    private static ShredRow[] Generate(int paths, Held held, VariantClass @class)
    {
        Random random = new Random(3);
        ShredRow[] rows = new ShredRow[Rows];
        for (int i = 0; i < Rows; i++)
        {
            List<(string, Variant)> fields = [("kept", Variant.From(random.Next(1_000)))];
            for (int p = 0; p < paths; p++)
            {
                if (held == Held.EveryRow || (held == Held.FirstHalf && i < Rows / 2) || (held == Held.OneRow && i == p * (Rows / paths)))
                {
                    fields.Add(($"path-{p:D4}", @class switch
                    {
                        VariantClass.Integer => Variant.From(random.Next(100_000)),
                        VariantClass.String => Variant.From($"value-{random.Next(500)}"),
                        VariantClass.Boolean => Variant.From(random.Next(2) == 0),
                        _ => Variant.From(random.NextDouble()),
                    }));
                }
            }

            rows[i] = new ShredRow(i, Variant.Object(fields.ToArray()));
        }

        return rows;
    }

    /// <summary>The rows in one chunk, whole or shredded: the file's bytes, and the tables of its largest encoding tree.</summary>
    private static async Task<(long Bytes, int Tables)> WriteAsync(ShredRow[] rows, VariantShredding? plan)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vortex-shredcost-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = Rows,
                ChunkTargetBytes = 1 << 30,
                ShreddedVariants = plan is null ? null : new Dictionary<string, VariantShredding> { [nameof(ShredRow.Attributes)] = plan },
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<ShredRow>(path, options))
            {
                await writer.WriteAsync<ShredRow>(rows);
                await writer.CompleteAsync();
            }

            byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);
            int tables = 0;
            await using (VortexFile file = await VortexFile.OpenAsync(path))
            {
                foreach (VortexSegment segment in file.SegmentMap)
                {
                    tables = Math.Max(tables, Tables(bytes.AsSpan((int)segment.Offset, (int)segment.Length)));
                }
            }

            return (bytes.Length, tables);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The tables of a segment's encoding tree, or 0 when the segment holds none.</summary>
    private static int Tables(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 8)
        {
            return 0;
        }

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(segment[^4..]);
        if (length <= 0 || length > segment.Length - 4)
        {
            return 0;
        }

        try
        {
            int budget = VortexLimits.MaxFlatBufferTables;
            ArrayView view = ArrayView.Root(segment[(segment.Length - 4 - length)..^4], ref budget);
            return Count(view.Root_);
        }
        catch (VortexFormatException)
        {
            return 0;
        }
    }

    private static int Count(ArrayNodeView node)
    {
        int tables = node.HasStats ? 2 : 1;
        for (int i = 0; i < node.ChildCount; i++)
        {
            tables += Count(node.GetChild(i));
        }

        return tables;
    }
}

[VortexRecord]
public partial record struct ShredRow(long Id, Variant Attributes);
