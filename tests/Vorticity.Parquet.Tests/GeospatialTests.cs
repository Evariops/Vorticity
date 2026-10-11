using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet.Metadata;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// GEOMETRY and GEOGRAPHY: WKB read as binary under an extension that keeps the CRS and the edge
/// algorithm, written back with them, and the bounding box and types each chunk's metadata gives,
/// worked out on the write from the values: NaN coordinates skipped, a GEOGRAPHY's box given only
/// where no value has an edge, and nothing where a value is not ISO WKB.
/// </summary>
public sealed class GeospatialTests : IDisposable
{
    private readonly List<string> _paths = [];

    private static string? Root => Environment.GetEnvironmentVariable("VORTICITY_PARQUET_DATA");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData("crs-default.parquet", "parquet.geometry", "")]
    [InlineData("crs-srid.parquet", "parquet.geometry", "srid:5070")]
    [InlineData("crs-geography.parquet", "parquet.geography", "\0")]
    public async Task ReadsTheSuitesGeospatialColumnsWithTheirCrs(string name, string extension, string metadata)
    {
        string? path = Root is null || !Directory.Exists(Root) ? null : Directory.EnumerateFiles(Root, name, SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(path is null, $"VORTICITY_PARQUET_DATA holds no {name}.");
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        VortexType type = file.Schema[file.Schema.Count - 1].Type;
        Assert.Equal(extension, type.ExtensionId);
        Assert.Equal(VortexTypeKind.Binary, type.StorageType!.Kind);
        Assert.Equal(metadata, Encoding.UTF8.GetString(type.ExtensionMetadata.Span));
    }

    /// <summary>
    /// A CRS kept in the file's key-value metadata reads as its PROJJSON itself, which the type then
    /// carries wherever it is written; the annotation shows as the file wrote it.
    /// </summary>
    [Fact]
    public async Task ReadsACrsTheKeyValueMetadataHoldsAsItsProjjson()
    {
        string? path = Root is null || !Directory.Exists(Root) ? null : Directory.EnumerateFiles(Root, "crs-projjson.parquet", SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(path is null, "VORTICITY_PARQUET_DATA holds no crs-projjson.parquet.");
        await using ParquetFile file = await ParquetFile.OpenAsync(path, Ct);
        string projjson = file.KeyValueMetadata.Single(p => p.Key == "projjson_epsg_5070").Value!;
        Assert.StartsWith("{", projjson, StringComparison.Ordinal);
        Assert.Equal(projjson, Encoding.UTF8.GetString(file.Schema[1].Type.ExtensionMetadata.Span));
        Assert.Equal("GEOMETRY(projjson:projjson_epsg_5070)", file.Metadata.Columns[1].LogicalType);

        await using ParquetFile written = await ParquetFile.OpenAsync(await RewriteAsync(path), Ct);
        Assert.Equal(projjson, Encoding.UTF8.GetString(written.Schema[1].Type.ExtensionMetadata.Span));
    }

    /// <summary>The suite's geometries, rewritten: every row group's box and types the union of the source's, and true to the values.</summary>
    [Fact]
    public async Task WritesTheBoxAndTypesOfTheSuitesGeometries()
    {
        string? path = Root is null || !Directory.Exists(Root) ? null : Directory.EnumerateFiles(Root, "geospatial.parquet", SearchOption.AllDirectories).FirstOrDefault();
        Assert.SkipWhen(path is null, "VORTICITY_PARQUET_DATA holds no geospatial.parquet.");
        string target = await RewriteAsync(path);

        await using ParquetFile source = await ParquetFile.OpenAsync(path, Ct);
        ParquetGeospatialInfo[] boxes = [.. source.Metadata.RowGroups.Select(g => g.Chunks[^1].Geospatial).OfType<ParquetGeospatialInfo>()];
        await using ParquetFile written = await ParquetFile.OpenAsync(target, Ct);
        Assert.Equal(VortexTypeKind.Extension, written.Schema[2].Type.Kind);
        Assert.Equal("parquet.geometry", written.Schema[2].Type.ExtensionId);
        Assert.Empty(await written.VerifyAsync(Ct));
        ParquetChunkInfo chunk = Assert.Single(written.Metadata.RowGroups).Chunks[^1];
        ParquetGeospatialInfo geospatial = chunk.Geospatial!;
        Assert.Equal(boxes.SelectMany(b => b.Types).Distinct().Order(), geospatial.Types);
        ParquetBoundingBox box = geospatial.Box!;
        IEnumerable<ParquetBoundingBox> sources = boxes.Select(b => b.Box).OfType<ParquetBoundingBox>();
        Assert.Equal(sources.Min(b => b.XMin), box.XMin);
        Assert.Equal(sources.Max(b => b.XMax), box.XMax);
        Assert.Equal(sources.Min(b => b.YMin), box.YMin);
        Assert.Equal(sources.Max(b => b.YMax), box.YMax);
        Assert.Equal(sources.Min(b => b.ZMin), box.ZMin);
        Assert.Equal(sources.Max(b => b.ZMax), box.ZMax);
        Assert.Equal(sources.Min(b => b.MMin), box.MMin);
        Assert.Equal(sources.Max(b => b.MMax), box.MMax);
        Assert.Null(chunk.Statistics!.Min);
        Assert.False(chunk.HasColumnIndex);
    }

    /// <summary>
    /// Points, a line and an empty point whose NaN coordinates bound nothing; little- and
    /// big-endian WKB alike; a GEOMETRY's box over every vertex, a GEOGRAPHY's only while no value
    /// has an edge, since a geodesic leaves its vertices' box.
    /// </summary>
    [Fact]
    public async Task WritesABoxOfVerticesAndTheTypesOfTheValues()
    {
        byte[][] values =
        [
            Point(30, 10),
            Point(-170, 60, bigEndian: true),
            Point(double.NaN, double.NaN),
            LineString((10, 20), (40, -5)),
        ];
        ParquetGeospatialInfo geometry = await WriteAsync(LogicalTypeKind.Geometry, values);
        Assert.Equal([1, 2], geometry.Types);
        Assert.Equal(new ParquetBoundingBox(-170, 40, -5, 60, null, null, null, null), geometry.Box);

        ParquetGeospatialInfo geography = await WriteAsync(LogicalTypeKind.Geography, values);
        Assert.Equal([1, 2], geography.Types);
        Assert.Null(geography.Box);

        ParquetGeospatialInfo points = await WriteAsync(LogicalTypeKind.Geography, values[..3]);
        Assert.Equal([1], points.Types);
        Assert.Equal(new ParquetBoundingBox(-170, 30, 10, 60, null, null, null, null), points.Box);
    }

    /// <summary>A value that is not ISO WKB leaves the chunk without geospatial statistics: a box that missed it would prune its row.</summary>
    [Theory]
    [InlineData(new byte[] { 1, 1, 0, 0, 0x20, 0xE6, 0x10, 0, 0 })]
    [InlineData(new byte[] { 1, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 2, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 8, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 2, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x0F })]
    public async Task WritesNoStatisticsForAValueThatIsNotIsoWkb(byte[] value)
    {
        ParquetGeospatialInfo? statistics = await WriteRawAsync(LogicalTypeKind.Geometry, [Point(1, 2), value]);
        Assert.Null(statistics);
    }

    private async Task<ParquetGeospatialInfo> WriteAsync(LogicalTypeKind kind, byte[][] values) =>
        (await WriteRawAsync(kind, values))!;

    /// <summary>The geospatial statistics this writer gives a chunk of <paramref name="values"/>, read from a hand-built column of them rewritten.</summary>
    private async Task<ParquetGeospatialInfo?> WriteRawAsync(LogicalTypeKind kind, byte[][] values)
    {
        HandBuiltFile file = new(1);
        file.Leaf("shape", FieldRepetition.Required, PhysicalType.ByteArray, logical: kind);
        file.Rows = values.Length;
        file.Chunk(PhysicalType.ByteArray, "shape").V2(null, 0, new byte[values.Length], 0, values.Length, HandBuiltFile.Binaries(values));
        string source = Path.Combine(Path.GetTempPath(), $"vx-geo-{Guid.NewGuid():N}.parquet");
        _paths.Add(source);
        await System.IO.File.WriteAllBytesAsync(source, file.ToBytes(), Ct);
        string target = await RewriteAsync(source);
        await using ParquetFile written = await ParquetFile.OpenAsync(target, Ct);
        Assert.Equal(kind == LogicalTypeKind.Geometry ? "GEOMETRY" : "GEOGRAPHY", written.Metadata.Columns[0].LogicalType);
        return Assert.Single(written.Metadata.RowGroups).Chunks[0].Geospatial;
    }

    private async Task<string> RewriteAsync(string source)
    {
        string target = Path.Combine(Path.GetTempPath(), $"vx-geo-{Guid.NewGuid():N}.parquet");
        _paths.Add(target);
        await using ParquetFile file = await ParquetFile.OpenAsync(source, Ct);
        await using ParquetFileWriter writer = VortexSession.Default.CreateParquetWriter(target, file.Schema, ParquetWriteOptions.Default);
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(Ct))
        {
            using (batch)
            {
                await writer.WriteAsync(batch, Ct);
            }
        }

        await writer.CompleteAsync(Ct);
        return target;
    }

    private static byte[] Point(double x, double y, bool bigEndian = false)
    {
        byte[] bytes = new byte[21];
        bytes[0] = bigEndian ? (byte)0 : (byte)1;
        Write(bytes.AsSpan(1), 1u, bigEndian);
        Write(bytes.AsSpan(5), x, bigEndian);
        Write(bytes.AsSpan(13), y, bigEndian);
        return bytes;
    }

    private static byte[] LineString(params (double X, double Y)[] points)
    {
        byte[] bytes = new byte[9 + (16 * points.Length)];
        bytes[0] = 1;
        Write(bytes.AsSpan(1), 2u, bigEndian: false);
        Write(bytes.AsSpan(5), (uint)points.Length, bigEndian: false);
        for (int i = 0; i < points.Length; i++)
        {
            Write(bytes.AsSpan(9 + (16 * i)), points[i].X, bigEndian: false);
            Write(bytes.AsSpan(17 + (16 * i)), points[i].Y, bigEndian: false);
        }

        return bytes;
    }

    private static void Write(Span<byte> into, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(into, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(into, value);
        }
    }

    private static void Write(Span<byte> into, double value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteDoubleBigEndian(into, value);
        }
        else
        {
            BinaryPrimitives.WriteDoubleLittleEndian(into, value);
        }
    }
}
