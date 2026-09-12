// F10, first half: the writer picks an encoding per column chunk.
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
using Vorticity.Scan;
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
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
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
            path, source.Schema, new VortexWriteOptions { Compress = compress });

        await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }
}
