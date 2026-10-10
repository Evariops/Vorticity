// A column whose statistics say it is sorted when it is not. Without VerifyStatistics the lie may
// give a wrong walk and must not fault; with it, is_sorted is validated and the walk refuses to
// go on.
//
// THE LIE IS FORGED IN THE DATA, NOT IN THE STATISTICS. The writer computes is_sorted from what it
// wrote, so the file is written sorted, with the column pinned to its canonical array so one value
// sits in the bytes as itself, and then that value is overwritten: the statistics, the zone map and
// the null count still describe the column as it was. Data segments carry no checksum, so nothing
// before the key source can notice.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed class SortedColumnLieTests
{
    private const int Rows = 20_480;

    private const int Block = 1_024;

    /// <summary>The row whose value is overwritten: inside zone 4, on no zone boundary.</summary>
    private const int Target = 5_000;

    /// <summary>Strictly ascending and not a progression, which the writer would keep as a sequence.</summary>
    private static long Value(int row) => 1_000L + (3L * row) + (row % 2);

    [Theory]
    [InlineData(15_990L, "below its predecessor, inside its zone's bounds")]
    [InlineData(99_999_999L, "above every bound the zone map states")]
    public async Task ALyingIsSortedWalksWithoutFaultAndIsRefusedUnderVerification(long forged, string what)
    {
        Decoders.EnsureRegistered();
        byte[] bytes = await WriteAsync();
        Forge(bytes, Value(Target), forged);

        List<long> walked = await WalkAsync(bytes, verify: false);
        Assert.Equal(Rows, walked.Count);
        Assert.True(walked.Contains(forged), $"the forged value, {what}, is walked as if it belonged");

        VortexFormatException refused = await Assert.ThrowsAsync<VortexFormatException>(
            () => WalkAsync(bytes, verify: true));
        Assert.Contains("sorted", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeekIntoTheLyingZoneIsRefusedUnderVerification()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        byte[] bytes = await WriteAsync();
        Forge(bytes, Value(Target), 15_990L);

        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(verify: true), CancellationToken.None);
        await using KeyCursor cursor = await file.Keys("sorted").WithSource(KeySourceKind.SortedColumn).OpenAsync(ct);

        // A zone far from the lie decodes and verifies; the lying zone does not.
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(Value(100)), SeekMode.Exact, ct));
        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await cursor.SeekAsync(FilterLiteral.From(Value(Target - 10)), SeekMode.AtOrAfter, ct));
    }

    [Fact]
    public async Task TheTruthfulFileWalksTheSameWithAndWithoutVerification()
    {
        Decoders.EnsureRegistered();
        byte[] bytes = await WriteAsync();

        List<long> plain = await WalkAsync(bytes, verify: false);
        List<long> verified = await WalkAsync(bytes, verify: true);
        Assert.Equal(Rows, plain.Count);
        Assert.Equal(plain, verified);
        for (int row = 0; row < Rows; row++)
        {
            Assert.Equal(Value(row), verified[row]);
        }
    }

    private static void Forge(byte[] bytes, long original, long forged)
    {
        Span<byte> pattern = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(pattern, original);
        int at = bytes.AsSpan().IndexOf(pattern);
        Assert.True(at >= 0, "the pinned canonical column holds the value as itself");
        Assert.True(
            bytes.AsSpan(at + 1).IndexOf(pattern) < 0,
            "the value is written once, so the forgery touches the data and nothing else");
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), forged);
    }

    private static async Task<List<long>> WalkAsync(byte[] bytes, bool verify)
    {
        await using MemorySegmentSource source = new MemorySegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, Options(verify), CancellationToken.None);
        await using KeyCursor cursor = await file.Keys("sorted").WithSource(KeySourceKind.SortedColumn).OpenAsync();
        List<long> keys = [];
        for (bool ok = await cursor.SeekFirstAsync(); ok; ok = await cursor.NextAsync())
        {
            keys.Add(cursor.Key.SignedValue);
        }

        return keys;
    }

    private static VortexOpenOptions Options(bool verify) => new VortexOpenOptions
    {
        LeaveSourceOpen = true,
        Read = new VortexReadOptions { VerifyStatistics = verify },
    };

    private static async Task<byte[]> WriteAsync()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-sorted-lie-{Guid.NewGuid():N}.vortex");
        try
        {
            DTypeArena types = new DTypeArena();
            CanonicalArena arena = new CanonicalArena();
            DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
            DType schema = types.Struct(["sorted"], [i64], Nullability.NonNullable);
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = Block,
                EncodingHints = new Dictionary<string, EncodingHint> { ["sorted"] = EncodingHint.Canonical },
            };

            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, options))
            {
                const int batch = 4_096;
                for (int start = 0; start < Rows; start += batch)
                {
                    int count = Math.Min(batch, Rows - start);
                    VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> raw);
                    for (int i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(raw[(i * sizeof(long))..], Value(start + i));
                    }

                    int column = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, buffer);
                    int root = arena.AddStruct(schema, count, Validity.NonNullable, [column]);
                    using RecordBatch record = new RecordBatch(arena, root, start);
                    await writer.WriteAsync(record, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return await System.IO.File.ReadAllBytesAsync(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
