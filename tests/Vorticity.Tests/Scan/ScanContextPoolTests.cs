// The scan contexts are the process's: a scan takes one per lane and gives it back, and the next
// scan, of the same file or of another, takes it again. Three things are held. A context comes back
// holding nothing of the scan it served: no node check -- a fact about one file's bytes, which on
// another file would let a malformed node skip its validation -- no pushed predicate or projection,
// no mask, sink or selection, and the next scan's file and encodings. A scan reads the same values
// whatever the scans before it set on the contexts it takes. And a batch its caller owns keeps its
// dtypes when the context it came from serves another scan.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanContextPoolTests
{
    private const string Zoned = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task AContextComesBackHoldingNothingOfTheScanItServed()
    {
        Decoders.EnsureRegistered();
        await using VortexFile first = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        await using VortexFile second = await VortexFile.OpenAsync(
            Corpus.Path("encodings/runend"), CancellationToken.None);

        ScanContext context = ScanContexts.Shared.Rent(first);

        // A dtype the scan derived, which a batch its caller owns may hold.
        DTypeArena types = context.Types;
        types.Primitive(PType.I32, Nullability.NonNullable);
        context.LiveBlocks = new BlockMask(first.RowCount, 1024);
        context.Metrics = new ScanMetrics();
        context.KeepEncodings = true;
        context.ExchangePushedFields(FieldMask.All);
        context.FieldsHonoured = true;
        context.ExchangePushedPredicate("monotone"u8.ToArray(), ComparisonOp.Equal, FilterLiteral.From(1L));
        uint? outer = context.BeginNodeCheckScope(3);
        long key = ScanContext.NodeCheckKey(3, 2)!.Value;
        context.MarkNodeChecked(key);
        context.EndNodeCheckScope(outer);
        Assert.True(context.IsNodeChecked(key));
        context.ExchangeSelection([1, 2, 3], 3);

        ScanContexts.Shared.Return(context);
        ScanContext again = ScanContexts.Shared.Rent(second);
        try
        {
            // The pool is the process's, so another test may have taken this context in between;
            // a context that is not this one proves nothing, and one that is must be clean.
            if (ReferenceEquals(again, context))
            {
                Assert.False(again.IsNodeChecked(key));
                Assert.Null(again.LiveBlocks);
                Assert.Null(again.Metrics);
                Assert.False(again.KeepEncodings);
                Assert.False(again.FieldsHonoured);
                Assert.True(again.PushedField.IsEmpty);
                Assert.True(again.PushedFields.IsAll);
                Assert.False(again.HasSelection);
                Assert.Equal(0, again.Batch);
                Assert.NotSame(types, again.Types);
            }

            Assert.Same(second, again.File);
            Assert.Same(second.ReadOptions, again.Options);
            Assert.Equal(second.ArrayEncodingCount, again.ArrayEncodingCount);
            for (int i = 0; i < second.ArrayEncodingCount; i++)
            {
                Assert.Equal(second.GetArrayEncoding(i), again.ArrayEncodings[i]);
            }
        }
        finally
        {
            ScanContexts.Shared.Return(again);
        }
    }

    [Fact]
    public async Task AContextKeepsAnArenaThatHoldsNothing()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);

        ScanContext context = ScanContexts.Shared.Rent(file);
        DTypeArena types = context.Types;
        types.InternName("a name, which no batch holds"u8);
        ScanContexts.Shared.Return(context);
        ScanContext again = ScanContexts.Shared.Rent(file);
        try
        {
            // No dtype came out of the arena, so nothing a caller owns can hold one of its nodes.
            if (ReferenceEquals(again, context))
            {
                Assert.Same(types, again.Types);
            }
        }
        finally
        {
            ScanContexts.Shared.Return(again);
        }
    }

    [Fact]
    public async Task APoolNoScanTookFromOverAMinuteLetsItsContextsGo()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);
        ScanContexts pool = new ScanContexts(4);
        pool.Return(pool.Rent(file));
        Assert.Equal(1, pool.Count);

        // Taken since the sweep before, so idle from this one on.
        pool.TrimIdle(1_000, everything: false);
        Assert.Equal(1, pool.Count);
        pool.TrimIdle(1_000 + ScanContexts.IdleMilliseconds - 1, everything: false);
        Assert.Equal(1, pool.Count);
        pool.TrimIdle(1_000 + ScanContexts.IdleMilliseconds, everything: false);
        Assert.Equal(0, pool.Count);

        // A high memory load lets them go at once, taken or not.
        pool.Return(pool.Rent(file));
        pool.TrimIdle(1_000, everything: true);
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void APoolIsSweptByACollection()
    {
        ScanContexts pool = new ScanContexts(4).Swept();
        int before = pool.Sweeps;
        for (int i = 0; i < 3 && pool.Sweeps == before; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.True(pool.Sweeps > before);
    }

    [Fact]
    public async Task AScanReadsTheSameValuesWhateverTheScansBeforeItSet()
    {
        Decoders.EnsureRegistered();
        string[] entries =
        [
            Zoned,
            "encodings/runend",
            "encodings/alp",
            "encodings/fastlanes_bitpacked_patched_no_chunk_offsets",
            "encodings/dict",
            "encodings/varbin",
        ];

        Dictionary<string, long> first = [];
        foreach (string entry in entries)
        {
            first[entry] = await FingerprintAsync(entry);
        }

        // Every per-scan switch set on the contexts, file after file: a filter pushed down, a
        // projection, a take, several lanes, encoded delivery through the typed path.
        for (int round = 0; round < 3; round++)
        {
            foreach (string entry in entries)
            {
                await DisturbAsync(entry);
            }

            foreach (string entry in entries)
            {
                Assert.Equal(first[entry], await FingerprintAsync(entry));
            }
        }
    }

    [Fact]
    public async Task ABatchItsCallerOwnsKeepsItsDtypesWhenItsContextServesAnotherScan()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(Zoned), CancellationToken.None);

        // A projection: the batch's struct dtype is derived in the context's own arena.
        List<RecordBatch> owned = [];
        await foreach (RecordBatch batch in file.Scan("monotone", "strs").ToBatchesAsync(CancellationToken.None))
        {
            owned.Add(batch);
        }

        string schema = owned[0].Schema.ToString();
        long before = 0;
        foreach (RecordBatch batch in owned)
        {
            before += Fingerprint(batch.Arena, batch.RootIndex);
        }

        for (int i = 0; i < 4; i++)
        {
            await DisturbAsync("encodings/varbin");
            await DisturbAsync(Zoned);
        }

        long after = 0;
        foreach (RecordBatch batch in owned)
        {
            after += Fingerprint(batch.Arena, batch.RootIndex);
        }

        Assert.Equal(schema, owned[0].Schema.ToString());
        Assert.Equal(before, after);
        foreach (RecordBatch batch in owned)
        {
            batch.Dispose();
        }
    }

    private static async Task<long> FingerprintAsync(string entry)
    {
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);
        long sum = 0;
        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            sum = (sum * 31) + Fingerprint(batch.Arena, batch.RootIndex);
            rows += batch.RowCount;
        }

        Assert.Equal(file.RowCount, rows);
        return sum;
    }

    private static async Task DisturbAsync(string entry)
    {
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Path(entry), CancellationToken.None);
        long rows = file.RowCount;

        // A file whose root is one column, as most encoding fixtures are, is taken and read in
        // lanes; a table is also filtered and projected on its first column.
        bool table = file.DType.Kind == DTypeKind.Struct;
        string? column = table ? file.DType.GetFieldName(0) : null;
        ScanBuilder taking = file.ScanBuilder().Take([0, rows / 3, rows - 1]);
        if (column is not null)
        {
            taking = taking.Where(Expr.IsNotNull(Expr.Field(column)));
        }

        await foreach (RecordBatch batch in taking.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            Fingerprint(batch.Arena, batch.RootIndex);
        }

        ScanBuilder lanes = file.ScanBuilder().WithDegreeOfParallelism(2).WithMaxBatchRows(1024);
        if (column is not null)
        {
            lanes = lanes.Project(column);
        }

        await foreach (RecordBatch batch in lanes.ExecuteAsync().WithCancellation(CancellationToken.None))
        {
            Fingerprint(batch.Arena, batch.RootIndex);
        }

        if (column is not null)
        {
            await foreach (BatchView view in file.Scan(column).WithCancellation(CancellationToken.None))
            {
                Assert.True(view.RowCount >= 0);
            }
        }
    }

    /// <summary>Every value a consumer reads, folded: fixed widths, bits, views and the bytes of the strings out of line, the fields of a struct.</summary>
    private static long Fingerprint(CanonicalArena arena, int index)
    {
        CanonicalNode node = arena.GetNode(index);
        long sum = node.Length;
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return sum + Words(node.Bits.Span);
            case CanonicalKind.Primitive:
            case CanonicalKind.Decimal:
                return sum + Words(node.Values.Span);
            case CanonicalKind.VarBinView:
            {
                ReadOnlySpan<byte> views = node.Views.Span;
                sum += Words(views);
                for (int i = 0; i + 16 <= views.Length; i += 16)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(views[i..]);
                    if (length > 12)
                    {
                        int buffer = BinaryPrimitives.ReadInt32LittleEndian(views[(i + 8)..]);
                        int offset = BinaryPrimitives.ReadInt32LittleEndian(views[(i + 12)..]);
                        sum = (sum * 31) + Words(node.GetDataBuffer(buffer).Span.Slice(offset, length));
                    }
                }

                return sum;
            }

            case CanonicalKind.Struct:
                for (int f = 0; f < node.FieldCount; f++)
                {
                    sum = (sum * 31) + Fingerprint(arena, node.GetFieldIndex(f));
                }

                return sum;
            case CanonicalKind.Extension:
                return sum + Fingerprint(arena, node.StorageIndex);
            default:
                return sum;
        }
    }

    private static long Words(ReadOnlySpan<byte> bytes)
    {
        long sum = 0;
        ReadOnlySpan<long> words = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < words.Length; i++)
        {
            sum = (sum * 31) + words[i];
        }

        for (int b = words.Length * 8; b < bytes.Length; b++)
        {
            sum = (sum * 31) + bytes[b];
        }

        return sum;
    }
}
