// The writer picks an encoding per column chunk.
//
// Two properties, and as everywhere else they fail for opposite reasons. The value tests assert
// compression changes no value -- the round-trip sweep already covers that across the whole corpus,
// so what is here is the narrower question of whether each SCHEME round-trips. The size test asserts
// it actually compresses, which is the failure the value tests cannot see: a compressor that always
// chooses "canonical" passes every correctness test ever written.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Columns;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class CompressionTests
{
    [Theory]
    [InlineData("distributions/long_runs_i32_r8193")]
    [InlineData("distributions/constant_i64_r1024")]
    [InlineData("distributions/low_cardinality_utf8_r8193")]
    [InlineData("containers/zoned_many_zones_nulls")]
    [InlineData("distributions/all_null_i64_r1024")]
    public async Task CompressingAColumnChangesNoValue(string id)
    {
        List<string> canonical = await Copy(id, compress: false);
        List<string> compressed = await Copy(id, compress: true);
        Assert.Equal(canonical, compressed);
        Assert.NotEmpty(canonical);
    }

    [Theory]
    [InlineData("distributions/long_runs_i32_r8193")]
    [InlineData("distributions/constant_i64_r1024")]
    public async Task CompressionActuallyShrinksACompressibleColumn(string id)
    {
        // The half the value tests cannot see. A compressor that always answers "leave it alone"
        // passes every correctness test there is.
        long canonical = await Size(id, compress: false);
        long compressed = await Size(id, compress: true);

        Assert.True(
            compressed * 2 < canonical,
            $"{id}: {compressed} bytes compressed against {canonical} canonical");
    }

    [Fact]
    public async Task ARunOfNullsIsOneRunRatherThanNone()
    {
        // Nullness is part of the value the compressor deduplicates: an all-null column is ONE run,
        // not a column of incomparable rows. Treating a null as "not equal to anything" -- which is
        // what the filter's three-valued logic says -- would leave it uncompressed.
        long canonical = await Size("distributions/all_null_i64_r1024", compress: false);
        long compressed = await Size("distributions/all_null_i64_r1024", compress: true);
        Assert.True(compressed < canonical, $"{compressed} against {canonical}");
    }

    /// <summary>
    /// A column below the row threshold but above the BYTE threshold is still a candidate.
    /// </summary>
    /// <remarks>
    /// The guard exists to skip columns too small for a second array and its metadata to pay for
    /// themselves, and that cost is measured in bytes. Sixteen rows of two hundred bytes each is
    /// three kilobytes with two distinct values - exactly the shape a row-count-only guard throws
    /// away.
    /// </remarks>
    [Fact]
    public void ASmallRowCountButLargeByteColumnIsStillCompressed()
    {
        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Utf8Node(TwoDistinct(200), Nullability.NonNullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.NotEqual(ColumnScheme.None, plan.Scheme);
    }

    /// <summary>
    /// The other half, without which the test above is satisfied by a compressor that never
    /// declines: the same sixteen rows of SHORT strings stay canonical, because there the second
    /// array really would cost more than it saves.
    /// </summary>
    [Fact]
    public void ASmallRowCountAndSmallByteColumnIsLeftAlone()
    {
        using ColumnFixture fixture = new ColumnFixture();
        int node = fixture.Utf8Node(TwoDistinct(4), Nullability.NonNullable);

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, node);
        Assert.Equal(ColumnScheme.None, plan.Scheme);
    }

    /// <summary>Sixteen rows over two distinct values of <paramref name="width"/> bytes.</summary>
    private static byte[]?[] TwoDistinct(int width)
    {
        byte[] first = new byte[width];
        byte[] second = new byte[width];
        Array.Fill(first, (byte)'a');
        Array.Fill(second, (byte)'b');

        byte[]?[] values = new byte[]?[16];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i < 8 ? first : second;
        }

        return values;
    }

    [Fact]
    public async Task ACompressedFileStillCarriesItsZoneMap()
    {
        // The two features are independent and both touch the layout tree: a column encoded as a
        // dict or a run-end is still wrapped in vortex.zoned, and its bounds still come from the
        // canonical values rather than from the codes.
        string path = await Write("containers/zoned_many_zones_nulls", compress: true);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            LayoutTree tree = LayoutTree.Parse(file);

            int zoned = 0;
            for (int i = 0; i < tree.Root.ChildCount; i++)
            {
                if (tree.Root.GetChild(i).Encoding == LayoutEncodingId.Zoned)
                {
                    zoned++;
                }
            }

            Assert.Equal(tree.Root.ChildCount, zoned);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<List<string>> Copy(string id, bool compress)
    {
        string path = await Write(id, compress);
        try
        {
            List<string> values = [];
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                Values.Describe(batch, values);
            }

            return values;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<long> Size(string id, bool compress)
    {
        string path = await Write(id, compress);
        try
        {
            return new FileInfo(path).Length;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<string> Write(string id, bool compress)
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");

        await using VortexFile source = await VortexFile.OpenAsync(
            Corpus.Path(id), CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            path, source.DType, new VortexWriteOptions { Compress = compress });

        await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }
}
