// A take over pco columns decodes only the pages that hold the rows it wants: the rows it returns
// must be the ones a full scan puts at those indices.
//
// The corpus's pco files hold one bin, one chunk and a page or two, so a take over them never
// crosses a page or a chunk. This file is one chunk of the writer's for every column, past a pco
// chunk of 2^18 values, in pages of 8,192, so a take steps across both; and half its columns are
// nullable, so a row's value is found by its rank among the valid rows, through stretches of nulls
// that empty a page of its wanted values, and the first rows all null.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class PcoSelectionTests
{
    /// <summary>A pco chunk and a part of the next, in one chunk of the writer's.</summary>
    private const int Rows = 300_000;

    private static readonly PType[] Types =
        [PType.I16, PType.U16, PType.I32, PType.U32, PType.I64, PType.U64, PType.F16, PType.F32, PType.F64];

    [Fact]
    public async Task ATakeOfPcoColumnsReadsWhatAFullScanPutsAtThoseRows()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"pco-selection-{Guid.NewGuid():N}.vortex");
        try
        {
            WriteReport report = await WriteAsync(path);
            Assert.All(
                report.Columns,
                column => Assert.All(column.Encodings, scheme => Assert.Equal(nameof(EncodingHint.Pco), scheme)));

            List<UInt128> all = [];
            await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
                {
                    Values.DigestRows(batch, all);
                }
            }

            Assert.Equal(Rows, all.Count);

            // The rows around every page, which the whole node serves, decoded once; and rows pages
            // apart, which only the pages that hold them serve.
            foreach (long[] wanted in new[] { Wanted(), Sparse })
            {
                List<UInt128> taken = [];
                await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
                {
                    await foreach (RecordBatch batch in file.ScanBuilder().Take(wanted).ExecuteAsync().WithCancellation(CancellationToken.None))
                    {
                        Values.DigestRows(batch, taken);
                    }
                }

                Assert.Equal(wanted.Select(row => all[(int)row]), taken);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Rows nine pages apart, which any batch holding two of them finds four pages apart and more:
    /// the first, null, and the last, past the pco chunk where no column has nulls.
    /// </summary>
    private static readonly long[] Sparse = [0, 75_001, 150_002, 225_001, 299_999];

    /// <summary>
    /// The rows a take asks for: both ends, both sides of each page and of the pco chunk, the rows
    /// around and inside the stretches of nulls, and a prime stride between them.
    /// </summary>
    private static long[] Wanted()
    {
        SortedSet<long> rows = [0, 1, Rows - 2, Rows - 1];
        for (long page = 8_192; page < Rows; page += 8_192)
        {
            rows.Add(page - 1);
            rows.Add(page);
        }

        foreach (long row in new long[] { 8_999, 9_000, 9_001, 99_999, 100_000, 104_000, 119_999, 120_000, 262_143, 262_144, 262_145 })
        {
            rows.Add(row);
        }

        for (long row = 17; row < Rows; row += 7_919)
        {
            rows.Add(row);
        }

        return [.. rows];
    }

    /// <summary>Every type pco stores, without nulls and with them, written as one chunk of pco.</summary>
    private static async Task<WriteReport> WriteAsync(string path)
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        DType boolType = types.Bool(Nullability.NonNullable);

        // A third of the rows null, every row in the first 9 000, and every row in
        // [100 000, 120 000), two pages of values and more.
        VortexBuffer bitsBuffer = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> bits);
        bits.Clear();
        for (int row = 0; row < Rows; row++)
        {
            if (row % 3 != 0 && row >= 9_000 && (row < 100_000 || row >= 120_000))
            {
                bits[row >> 3] |= (byte)(1 << (row & 7));
            }
        }

        int validity = arena.AddBool(boolType, Rows, Validity.NonNullable, bitsBuffer, 0);

        List<string> names = [];
        List<DType> fields = [];
        List<int> columns = [];
        foreach (PType ptype in Types)
        {
            foreach (bool nullable in new[] { false, true })
            {
                DType dtype = types.Primitive(ptype, nullable ? Nullability.Nullable : Nullability.NonNullable);
                int width = ptype.ByteWidth();
                VortexBuffer buffer = arena.Allocate(Rows * width, width, out Span<byte> bytes);
                for (int row = 0; row < Rows; row++)
                {
                    Write(bytes.Slice(row * width, width), ptype, row);
                }

                names.Add($"{ptype}{(nullable ? "_null" : string.Empty)}");
                fields.Add(dtype);
                columns.Add(arena.AddPrimitive(dtype, Rows, nullable ? Validity.Bitmap(validity) : Validity.NonNullable, ptype, buffer));
            }
        }

        DType schema = types.Struct([.. names], [.. fields], Nullability.NonNullable);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [.. columns]);
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = null,
            DataBlockTargetBytes = null,
            Hints = names.ToImmutableDictionary(name => name, _ => EncodingHint.Pco),
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        using (RecordBatch batch = new RecordBatch(arena, root, 0))
        {
            await writer.WriteAsync(batch);
        }

        return await writer.CompleteAsync(CancellationToken.None);
    }

    /// <summary>
    /// Row <paramref name="row"/>'s value: a walk with jumps and repeats, whose spread a pco chunk
    /// needs many bins of a table to describe, held to the type.
    /// </summary>
    private static void Write(Span<byte> into, PType ptype, int row)
    {
        long walk = (row / 7) + ((row * 2_654_435_761L) % 1_021) - ((row % 97) * 13);
        switch (ptype)
        {
            case PType.I16:
                MemoryMarshal.Write(into, (short)walk);
                break;
            case PType.U16:
                MemoryMarshal.Write(into, (ushort)walk);
                break;
            case PType.I32:
                MemoryMarshal.Write(into, (int)(walk * 1_000));
                break;
            case PType.U32:
                MemoryMarshal.Write(into, (uint)(walk * 1_000));
                break;
            case PType.I64:
                MemoryMarshal.Write(into, walk * 1_000_003);
                break;
            case PType.U64:
                MemoryMarshal.Write(into, (ulong)walk * 1_000_003);
                break;
            case PType.F16:
                MemoryMarshal.Write(into, (Half)(walk / 8.0));
                break;
            case PType.F32:
                MemoryMarshal.Write(into, (float)(walk * 0.1));
                break;
            default:
                MemoryMarshal.Write(into, walk * 0.01);
                break;
        }
    }
}
