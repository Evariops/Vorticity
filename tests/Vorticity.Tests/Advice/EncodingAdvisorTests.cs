using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Advice;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Scanning;
using Vorticity.Tests.Api;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Advice;

/// <summary>
/// The advisor's tests run alone: the advice waits for the JIT to settle before it measures, and a
/// process whose other tests compile all the while never does.
/// </summary>
[CollectionDefinition(nameof(AdviceCollection), DisableParallelization = true)]
public sealed class AdviceCollection
{
}

/// <summary>
/// What the advice measures that is exact: the profile's counts, the bytes and encodings each
/// candidate writes, which candidates are tried, and the options that take the advice. No test here
/// asserts a time.
/// </summary>
[Collection(nameof(AdviceCollection))]
public sealed class EncodingAdvisorTests
{
    private const int Batch = 65_536;

    private static readonly string[] Cities = ["Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Lille"];

    [Fact]
    public async Task EveryColumnOfRecordsIsProfiledExactly()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Reading[] rows = Readings(40_000);

        EncodingAdvice advice = await VortexSession.Default.AdviseAsync<Reading>(rows, cancellationToken: ct);

        Assert.Equal(40_000, advice.Rows);
        Assert.Equal(40_000, advice.SampledRows);
        Assert.Equal(["Day", "Celsius", "City"], advice.Columns.Select(c => c.Path));

        // Days in runs of a thousand, ascending.
        ColumnProfile day = advice.Columns[0].Profile;
        Assert.Equal((40_000L, 0L, 40L), (day.Rows, day.Nulls, day.Distinct));
        Assert.Equal(1_000, day.AverageRun, 9);
        Assert.True(day.Ascending);
        Assert.Equal(0, day.AverageLength);

        // A temperature null every fiftieth row: the eight tenths those rows would have held are
        // never seen.
        ColumnProfile celsius = advice.Columns[1].Profile;
        Assert.Equal((800L, 392L), (celsius.Nulls, celsius.Distinct));
        Assert.False(celsius.Ascending);

        // Eight cities in runs of seven.
        ColumnProfile city = advice.Columns[2].Profile;
        int changes = rows.Skip(1).Where((r, i) => r.City != rows[i].City).Count();
        Assert.Equal(8, city.Distinct);
        Assert.Equal(40_000.0 / (changes + 1), city.AverageRun, 9);
        Assert.Equal(rows.Average(r => Encoding.UTF8.GetByteCount(r.City)), city.AverageLength, 9);

        string[] kinds = ["integers", "floating point", "text"];
        for (int i = 0; i < advice.Columns.Length; i++)
        {
            ColumnEncodingAdvice column = advice.Columns[i];
            Assert.Same(column.Recommended, column.Candidates[0]);
            Assert.All(column.Candidates, c =>
            {
                Assert.NotEmpty(c.WrittenAs);
                Assert.True(c.BytesPerValue > 0, $"{column.Path} {c.Hint}: {c.BytesPerValue} B a value");
                Assert.True(c.Cost > 0, $"{column.Path} {c.Hint}: a cost of {c.Cost}");
            });
            Assert.StartsWith(kinds[i], column.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACandidateWritesWhatAWriteUnderItsOptionsWritesAndTheAdviceIsTaken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int rows = 200_000;
        string path = await WriteLongsAsync(rows, row => (long)(Mix(row) % 1_000), ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            EncodingAdvice advice = await VortexSession.Default.AdviseAsync(file, cancellationToken: ct);
            ColumnEncodingAdvice column = Assert.Single(advice.Columns);

            foreach (EncodingCandidate candidate in column.Candidates)
            {
                VortexWriteOptions options = new VortexWriteOptions { ChunkTargetBytes = candidate.ChunkTargetBytes };
                if (candidate.Hint != EncodingHint.Auto)
                {
                    options = options with { Hints = options.Hints.Add("v", candidate.Hint) };
                }

                WriteReport report = await CopyAsync(file, options, ct);
                Assert.Equal(candidate.WrittenAs, EncodingAdvisor.Summarize(report.Columns[0].Encodings));
                Assert.Equal(candidate.BytesPerValue, (double)report.Bytes.Total / rows);
            }

            WriteReport taken = await CopyAsync(file, advice.ToWriteOptions(), ct);
            Assert.Equal(column.Recommended.WrittenAs, EncodingAdvisor.Summarize(taken.Columns[0].Encodings));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AColumnWhoseValuesRepeatAcrossChunksIsTriedAtLargerChunks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Five thousand values, each back every five thousand rows: a chunk of the writer's own
        // size holds each 26 times, which a dictionary does not pay for.
        EncodingAdvice repeating = await AdviseLongsAsync(300_000, row => row * 7_919 % 5_000, ct);
        // A thousand values in no order: each 131 times in a chunk already.
        EncodingAdvice dense = await AdviseLongsAsync(300_000, row => (long)(Mix(row) % 1_000), ct);

        Assert.Contains(repeating.Columns[0].Candidates, c => c.ChunkTargetBytes > 0);
        Assert.DoesNotContain(dense.Columns[0].Candidates, c => c.ChunkTargetBytes > 0);
        Assert.InRange(repeating.Columns[0].Profile.RowsPerDistinctInChunk, 1, 100);
        Assert.True(dense.Columns[0].Profile.RowsPerDistinctInChunk >= 100);
    }

    [Fact]
    public async Task ABooleanColumnIsOfferedItsBitmapAndItsRuns()
    {
        EncodingAdvice advice = await AdviseBoolsAsync(262_144, row => row % 100 == 0, TestContext.Current.CancellationToken);

        ImmutableArray<EncodingCandidate> candidates = advice.Columns[0].Candidates;
        Assert.Contains(candidates, c => c.WrittenAs.Any(w => w.StartsWith("RunEnd", StringComparison.Ordinal)));
        Assert.Contains(candidates, c => c.WrittenAs.Any(w => w.StartsWith("Canonical", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OnlyTheSampleIsMeasured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        EncodingAdvice advice = await VortexSession.Default.AdviseAsync<Reading>(
            Readings(100_000), EncodingGoal.Default with { SampleRows = 20_000 }, ct);

        // Two windows of a block each: the most whole blocks twenty thousand rows hold, spread.
        Assert.Equal(100_000, advice.Rows);
        Assert.Equal(16_384, advice.SampledRows);
        Assert.All(advice.Columns, c => Assert.Equal(16_384, c.Profile.Rows));
    }

    [Fact]
    public async Task TheBytesAloneAreWrittenUnderTheSmallestProfile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Jittered timestamps: bit-packing is what decodes fastest, zstd what is smallest.
        EncodingAdvice advice = await AdviseLongsAsync(
            262_144, row => 1_700_000_000_000L + (row * 1000) + (long)(Mix(row) % 1000), ct, EncodingGoal.Smallest);

        Assert.Contains(advice.Columns[0].Recommended.WrittenAs, w => w.StartsWith("Zstd", StringComparison.Ordinal));
        Assert.Equal(CompressionProfile.Smallest, advice.ToWriteOptions().Compression);
    }

    [Fact]
    public async Task AColumnTheAdviceCannotMeasureIsPassedOver()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        const int rows = 16_384;
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType inner = types.Struct(["x"], [i64], Nullability.NonNullable);
        DType schema = types.Struct(["a", "s"], [i64, inner], Nullability.NonNullable);
        int a = Longs(arena, i64, 0, rows, row => row % 7);
        int x = Longs(arena, i64, 0, rows, row => row % 11);
        int s = arena.AddStruct(inner, rows, Validity.NonNullable, [x]);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [a, s]);
        string path = Temp();
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions()))
            {
                using (RecordBatch batch = new RecordBatch(arena, root, 0))
                {
                    await writer.WriteAsync(batch, ct);
                }

                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            EncodingAdvice advice = await VortexSession.Default.AdviseAsync(file, cancellationToken: ct);

            Assert.Equal("a", Assert.Single(advice.Columns).Path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileWhoseRootIsNotAStructIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        int values = Longs(arena, i64, 0, 1_024, row => row);
        string path = Temp();
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, i64, new VortexWriteOptions()))
            {
                using (RecordBatch batch = new RecordBatch(arena, values, 0))
                {
                    await writer.WriteAsync(batch, ct);
                }

                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            await Assert.ThrowsAsync<ArgumentException>(async () => await VortexSession.Default.AdviseAsync(file, cancellationToken: ct));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------ plumbing

    private static Reading[] Readings(int rows) =>
        [.. Enumerable.Range(0, rows).Select(i => new Reading(i / 1_000, i % 50 == 0 ? null : 10.0 + (i % 400 / 10.0), Cities[i / 7 % Cities.Length]))];

    private static async Task<EncodingAdvice> AdviseLongsAsync(int rows, Func<long, long> value, CancellationToken ct, EncodingGoal? goal = null)
    {
        string path = await WriteLongsAsync(rows, value, ct);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            return await VortexSession.Default.AdviseAsync(file, goal, ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<EncodingAdvice> AdviseBoolsAsync(int rows, Func<long, bool> value, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType flag = types.Bool(Nullability.NonNullable);
        DType schema = types.Struct(["v"], [flag], Nullability.NonNullable);
        string path = Temp();
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions()))
            {
                for (int start = 0; start < rows; start += Batch)
                {
                    int count = Math.Min(Batch, rows - start);
                    CanonicalArena arena = new CanonicalArena();
                    VortexBuffer buffer = arena.Allocate((count + 7) / 8, 64, out Span<byte> bits);
                    bits.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        if (value(start + i))
                        {
                            bits[i >> 3] |= (byte)(1 << (i & 7));
                        }
                    }

                    int column = arena.AddBool(flag, count, Validity.NonNullable, buffer, 0);
                    int root = arena.AddStruct(schema, count, Validity.NonNullable, [column]);
                    using RecordBatch batch = new RecordBatch(arena, root, start);
                    await writer.WriteAsync(batch, ct);
                }

                await writer.CompleteAsync(ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, ct);
            return await VortexSession.Default.AdviseAsync(file, cancellationToken: ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A file of one column of <paramref name="rows"/> 64-bit integers named <c>v</c>, written with the defaults.</summary>
    private static async Task<string> WriteLongsAsync(int rows, Func<long, long> value, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct(["v"], [i64], Nullability.NonNullable);
        string path = Temp();
        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions());
        for (int start = 0; start < rows; start += Batch)
        {
            int count = Math.Min(Batch, rows - start);
            CanonicalArena arena = new CanonicalArena();
            int column = Longs(arena, i64, start, count, value);
            int root = arena.AddStruct(schema, count, Validity.NonNullable, [column]);
            using RecordBatch batch = new RecordBatch(arena, root, start);
            await writer.WriteAsync(batch, ct);
        }

        await writer.CompleteAsync(ct);
        return path;
    }

    /// <summary>The file's rows written again under <paramref name="options"/>, into memory.</summary>
    private static async Task<WriteReport> CopyAsync(VortexFile file, VortexWriteOptions options, CancellationToken ct)
    {
        using MemoryStream stream = new MemoryStream();
        await using VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), file.DType, options);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(ct))
        {
            await writer.WriteAsync(batch, ct);
        }

        return await writer.CompleteAsync(ct);
    }

    private static int Longs(CanonicalArena arena, DType dtype, long start, int count, Func<long, long> value)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), 64, out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = value(start + i);
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), $"vorticity-advice-{Guid.NewGuid():N}.vortex");

    private static ulong Mix(long row)
    {
        ulong z = (ulong)row + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
