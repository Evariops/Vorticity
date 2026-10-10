using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Compute;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Every real file this reader reads, rewritten by this package's writer and read back: each column's
/// values, hashed in row order, the same before and after, whatever the row groups and pages either
/// file cuts them into. <c>VORTICITY_PARQUET_DATA</c> names the files' directory.
/// </summary>
public sealed class RealFileRewriteTests
{
    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RewritesEveryRealFileToTheSameValues()
    {
        Assert.SkipWhen(Root is null || !Directory.Exists(Root), "VORTICITY_PARQUET_DATA names no directory of Parquet files.");
        string? reportPath = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_REPORT");
        using StreamWriter? report = reportPath is null ? null : new StreamWriter(reportPath, append: false);
        List<string> failures = [];
        int rewritten = 0;
        foreach (string path in Directory.EnumerateFiles(Root!, "*.parquet", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(Root!, path);
            ulong[] before;
            try
            {
                before = await HashAsync(path);
            }
            catch (Exception e) when (e is ParquetUnsupportedException or ParquetFormatException or VortexSchemaException)
            {
                continue;
            }

            string target = Path.Combine(Path.GetTempPath(), $"vorticity-rewrite-{Guid.NewGuid():N}.parquet");
            try
            {
                Stopwatch write = Stopwatch.StartNew();
                long bytes;
                try
                {
                    bytes = await RewriteAsync(path, target);
                }
                catch (Exception e) when (e is ParquetUnsupportedException or VortexSchemaException or NotSupportedException)
                {
                    // A type this writer does not write, as INT96: said, not failed.
                    report?.WriteLine($"{name}: not written, {e.Message}");
                    continue;
                }

                write.Stop();
                ulong[] after = await HashAsync(target);
                rewritten++;
                report?.WriteLine($"{name}: {new FileInfo(path).Length:N0} bytes rewritten as {bytes:N0} in {write.Elapsed.TotalMilliseconds:F0} ms");
                for (int c = 0; c < before.Length; c++)
                {
                    if (before[c] != after[c])
                    {
                        failures.Add($"{name}: column {c} reads other values once rewritten");
                    }
                }
            }
            finally
            {
                System.IO.File.Delete(target);
            }
        }

        report?.WriteLine($"{rewritten} files rewritten, {failures.Count} columns differ");
        Assert.True(rewritten > 0);
        Assert.Empty(failures);
    }

    private static async Task<long> RewriteAsync(string source, string target)
    {
        await using (ParquetFile file = await ParquetFile.OpenAsync(source, Ct))
        await using (ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(target, file.Schema, ParquetWriteOptions.Default))
        {
            await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
            {
                using (batch)
                {
                    await writer.WriteAsync(batch, Ct);
                }
            }

            await writer.CompleteAsync(Ct);
        }

        return new FileInfo(target).Length;
    }

    /// <summary>Per column, a hash of its values in row order: each as its literal where it is one, as its text where not.</summary>
    private static async Task<ulong[]> HashAsync(string path)
    {
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        XxHash64[] hashes = new XxHash64[file.Schema.Count];
        for (int c = 0; c < hashes.Length; c++)
        {
            hashes[c] = new XxHash64();
        }

        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                Hash(batch, hashes);
            }
        }

        ulong[] results = new ulong[hashes.Length];
        for (int c = 0; c < hashes.Length; c++)
        {
            results[c] = hashes[c].GetCurrentHashAsUInt64();
        }

        return results;
    }

    private static void Hash(RecordBatch batch, XxHash64[] hashes)
    {
        Span<byte> scratch = stackalloc byte[9];
        for (int c = 0; c < hashes.Length; c++)
        {
            int node = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(c);
            bool decimals = batch.Schema[c].Type.Kind == VortexTypeKind.Decimal;
            for (int r = 0; r < batch.RowCount; r++)
            {
                bool read = decimals ? LiteralReader.TryReadDecimal(batch.Arena, node, r, out FilterLiteral value) : LiteralReader.TryRead(batch.Arena, node, r, out value);
                if (!read)
                {
                    hashes[c].Append(Encoding.UTF8.GetBytes(Render.Row(batch, c, r)));
                    continue;
                }

                scratch[0] = (byte)value.Kind;
                switch (value.Kind)
                {
                    case FilterLiteralKind.Bytes:
                        hashes[c].Append(scratch[..1]);
                        BinaryPrimitives.WriteInt32LittleEndian(scratch[1..], value.BytesValue.Length);
                        hashes[c].Append(scratch[1..5]);
                        hashes[c].Append(value.BytesValue);
                        break;
                    case FilterLiteralKind.Float:
                        BinaryPrimitives.WriteInt64LittleEndian(scratch[1..], BitConverter.DoubleToInt64Bits(value.FloatValue));
                        hashes[c].Append(scratch);
                        break;
                    default:
                        BinaryPrimitives.WriteUInt64LittleEndian(scratch[1..], value.UnsignedValue);
                        hashes[c].Append(scratch);
                        break;
                }
            }
        }
    }
}
