using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A path of a variant column extracted alone, in memory (<c>--micro variants</c>): batches of attribute
/// bags of twenty keys, unshredded, each read through one path into a typed column. No file, no decode:
/// the parse a question pays per row on a variant nobody shredded, and what it allocates per batch.
/// </summary>
/// <remarks>
/// The rows share one dictionary, as the rows of one shape do. Run it with <c>DOTNET_TieredCompilation=0</c>,
/// so that every pass runs the code at its last tier.
/// </remarks>
internal static class VariantMicro
{
    private const int BatchRows = 65_536;

    private const int Batches = 16;

    internal static int Run(int rounds)
    {
        (byte[] metadata, byte[][] values) = Rows();
        Console.WriteLine($"{"variant path",-34} {"rows",11} {"best ms",10} {"ns/row",8} {"B/batch",9} {"present",9}");
        Measure("AsInt64, an integer key", metadata, values, [VariantStep.Of("http.response.status_code")], VariantRead.AsInt64, rounds);
        Measure("AsString, a short text key", metadata, values, [VariantStep.Of("service.name")], VariantRead.AsString, rounds);
        Measure("AsString, a long text key", metadata, values, [VariantStep.Of("http.route")], VariantRead.AsString, rounds);
        Measure("ToInt64, digits as text", metadata, values, [VariantStep.Of("retry.count")], VariantRead.ToInt64, rounds);
        Measure("AsInt64, a key absent", metadata, values, [VariantStep.Of("missing")], VariantRead.AsInt64, rounds);
        Measure("AsString, a nested key", metadata, values, [VariantStep.Of("http"), VariantStep.Of("method")], VariantRead.AsString, rounds);
        Measure("Has, a key", metadata, values, [VariantStep.Of("user.id")], VariantRead.Has, rounds);
        return 0;
    }

    /// <summary>A batch of attribute bags: twenty keys of one shape, the values varying.</summary>
    private static (byte[] Metadata, byte[][] Values) Rows()
    {
        byte[][] values = new byte[BatchRows][];
        byte[] metadata = [];
        for (int row = 0; row < BatchRows; row++)
        {
            (string, Variant)[] fields = new (string, Variant)[20];
            fields[0] = ("service.name", row % 3 == 0 ? "checkout" : "cart");
            fields[1] = ("http.route", row % 2 == 0 ? "/api/v1/orders/{id}/items" : "/api/v1/users/{id}");
            fields[2] = ("http.response.status_code", row % 10 == 0 ? 502L : 200L);
            fields[3] = ("user.id", "u-" + (row % 5000).ToString(CultureInfo.InvariantCulture));
            fields[4] = ("retry.count", (row % 4).ToString(CultureInfo.InvariantCulture));
            fields[5] = ("duration_ms", row * 0.25);
            fields[6] = ("http", Variant.Object(("method", row % 2 == 0 ? "GET" : "POST")));
            for (int k = 7; k < 20; k++)
            {
                fields[k] = ("attr.k" + k.ToString("00", CultureInfo.InvariantCulture), (long)(row % (k * 3)));
            }

            Variant bag = Variant.Object(fields);
            metadata = bag.Metadata.ToArray();
            values[row] = bag.Value.ToArray();
        }

        return (metadata, values);
    }

    private static void Measure(string name, byte[] metadata, byte[][] values, VariantStep[] steps, VariantRead read, int rounds)
    {
        VariantFieldExpr path = new(new ColumnFieldExpr("attributes"), steps, read);
        double best = double.MaxValue;
        long bytesPerBatch = 0;
        long present = 0;
        for (int round = 0; round < rounds; round++)
        {
            (double ms, long bytes, long found) = Pass(path, metadata, values);
            best = Math.Min(best, ms);
            bytesPerBatch = bytes / Batches;
            present = found;
        }

        long rows = (long)BatchRows * Batches;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-34} {rows,11:N0} {best,10:F1} {best * 1e6 / rows,8:F2} {bytesPerBatch,9:N0} {present,9:N0}"));
    }

    /// <summary>One pass: each batch's rows a canonical variant node, the path extracted from it; the extraction alone timed.</summary>
    private static (double Ms, long Bytes, long Present) Pass(VariantFieldExpr path, byte[] metadata, byte[][] values)
    {
        GC.Collect();
        RetainingArena arena = new RetainingArena();
        DTypeArena types = new DTypeArena();
        DType binary = types.Binary(Nullability.NonNullable);
        DType variant = types.Variant(Nullability.NonNullable);
        long ticks = 0;
        long bytes = 0;
        long present = 0;
        for (int batch = 0; batch < Batches; batch++)
        {
            arena.ResetKeepingBlocks();
            int node = Batch(arena, binary, variant, metadata, values);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int extracted = path.Apply(arena, node);
            ticks += Stopwatch.GetTimestamp() - start;
            bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            present += Present(arena, extracted);
        }

        arena.Reset();
        return (ticks * 1000.0 / Stopwatch.Frequency, bytes, present);
    }

    /// <summary>The batch as the decoder delivers it: a struct of the metadata and the values, each a binary column of views over one data buffer.</summary>
    private static int Batch(CanonicalArena arena, DType binary, DType variant, byte[] metadata, byte[][] values)
    {
        int rows = values.Length;
        int total = 0;
        foreach (byte[] value in values)
        {
            total += value.Length;
        }

        VortexBuffer data = arena.Allocate(total + metadata.Length, 1, out Span<byte> heap);
        VortexBuffer metadataViews = arena.Allocate(rows * CanonicalSupport.ViewSize, 16, out Span<byte> metadataBytes);
        VortexBuffer valueViews = arena.Allocate(rows * CanonicalSupport.ViewSize, 16, out Span<byte> valueBytes);
        metadata.CopyTo(heap);
        int at = metadata.Length;
        for (int row = 0; row < rows; row++)
        {
            CanonicalSupport.WriteReferenceView(metadataBytes.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize), metadata.Length, metadata, 0, 0);
            byte[] value = values[row];
            value.CopyTo(heap[at..]);
            CanonicalSupport.WriteReferenceView(valueBytes.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize), value.Length, value, 0, at);
            at += value.Length;
        }

        ReadOnlySpan<VortexBuffer> buffers = MemoryMarshal.CreateReadOnlySpan(ref data, 1);
        int metadataNode = arena.AddVarBinView(binary, rows, Validity.NonNullable, metadataViews, buffers);
        int valueNode = arena.AddVarBinView(binary, rows, Validity.NonNullable, valueViews, buffers);
        return arena.AddStruct(variant, rows, Validity.NonNullable, [metadataNode, valueNode]);
    }

    /// <summary>The rows the extracted column holds a value for, read once the pass is timed: what tells two builds apart.</summary>
    private static long Present(CanonicalArena arena, int node)
    {
        CanonicalNode column = arena.GetNode(node);
        if (column.Kind == CanonicalKind.Extension)
        {
            column = arena.GetNode(column.StorageIndex);
        }

        if (column.DType.Kind == DTypeKind.Bool && column.Validity.Kind == ValidityKind.NonNullable)
        {
            long set = 0;
            ReadOnlySpan<byte> bits = column.Bits.Span;
            for (int row = 0; row < column.Length; row++)
            {
                set += (bits[(column.BitOffset + row) >> 3] >> ((column.BitOffset + row) & 7)) & 1;
            }

            return set;
        }

        return column.Validity.Kind switch
        {
            ValidityKind.AllInvalid => 0,
            ValidityKind.Bitmap => Count(arena, column.Validity.CanonicalNodeIndex, column.Length),
            _ => column.Length,
        };
    }

    private static long Count(CanonicalArena arena, int bitmap, int rows)
    {
        CanonicalNode bits = arena.GetNode(bitmap);
        ReadOnlySpan<byte> raw = bits.Bits.Span;
        long set = 0;
        for (int row = 0; row < rows; row++)
        {
            set += (raw[(bits.BitOffset + row) >> 3] >> ((bits.BitOffset + row) & 7)) & 1;
        }

        return set;
    }
}
