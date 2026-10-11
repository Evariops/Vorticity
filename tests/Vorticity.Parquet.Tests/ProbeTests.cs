using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A file's structure written out as the package's own readers see it: the footer's schema and row
/// groups, each column chunk's metadata and every page header. A tool for the files the real-world
/// test refuses: <c>VORTICITY_PARQUET_PROBE</c> names one, <c>VORTICITY_PARQUET_REPORT</c> where it goes.
/// </summary>
public sealed class ProbeTests
{
    [Fact]
    public void WritesOutAFilesStructure()
    {
        string? path = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_PROBE");
        string? reportPath = Environment.GetEnvironmentVariable("VORTICITY_PARQUET_REPORT");
        Assert.SkipWhen(path is null || reportPath is null, "VORTICITY_PARQUET_PROBE names no file to probe.");
        using StreamWriter report = new(reportPath!, append: false);
        byte[] bytes = System.IO.File.ReadAllBytes(path!);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        report.WriteLine($"{path}: {bytes.Length} bytes, footer {length} bytes");
        Dump(bytes.AsSpan(bytes.Length - 8 - length, length), report);
        ParquetFooter footer;
        try
        {
            footer = ParquetFooter.Read(bytes.AsMemory(bytes.Length - 8 - length, length));
        }
        catch (Exception e)
        {
            report.WriteLine($"footer: {e}");
            return;
        }

        report.WriteLine($"version {footer.Version}, {footer.RowCount} rows, {footer.RowGroups.Length} row groups, created by {Encoding.UTF8.GetString(footer.CreatedBy.Of(footer.Bytes))}");
        foreach (SchemaElement element in footer.Schema)
        {
            report.WriteLine($"  schema {element.Name}: type {(element.HasType ? element.Type.ToString() : "-")}, length {element.TypeLength}, children {element.ChildCount}, converted {element.ConvertedType}, logical {element.LogicalType.Kind}, repetition {(element.HasRepetition ? element.Repetition.ToString() : "-")}");
        }

        for (int group = 0; group < footer.RowGroups.Length; group++)
        {
            RowGroupEntry entry = footer.RowGroups[group];
            report.WriteLine($"row group {group}: {entry.RowCount} rows from {entry.FirstRow}, {entry.ColumnCount} columns");
            for (int column = 0; column < entry.ColumnCount; column++)
            {
                ColumnChunkMetadata chunk;
                try
                {
                    chunk = footer.Chunk(group, column);
                }
                catch (Exception e)
                {
                    report.WriteLine($"  column {column}: {e.Message}");
                    continue;
                }

                report.WriteLine($"  column {column}: {chunk.Type}, codec {chunk.Codec}, {chunk.ValueCount} values, data at {chunk.DataPageOffset}, dictionary at {chunk.DictionaryPageOffset}, {chunk.TotalCompressedSize} bytes ({chunk.TotalUncompressedSize} uncompressed), encodings 0x{chunk.Encodings:X}");
                long start = chunk.DictionaryPageOffset > 0 && chunk.DictionaryPageOffset < chunk.DataPageOffset ? chunk.DictionaryPageOffset : chunk.DataPageOffset;
                long end = start + chunk.TotalCompressedSize;
                long at = start;
                while (at < end && at < bytes.Length)
                {
                    try
                    {
                        PageHeader header = PageHeader.Read(bytes.AsSpan((int)at, (int)Math.Min(end - at, bytes.Length - at)));
                        report.WriteLine($"    page at {at}: {header.Type}, header {header.HeaderLength}, {header.CompressedPageSize} bytes ({header.UncompressedPageSize} uncompressed), values {header.ValueCount}, rows {header.RowCount}, nulls {header.NullCount}, encoding {header.Encoding}, definition levels {header.DefinitionLevelsLength} {header.DefinitionLevelEncoding}, repetition levels {header.RepetitionLevelsLength} {header.RepetitionLevelEncoding}, compressed {header.IsCompressed}");
                        at += header.HeaderLength + header.CompressedPageSize;
                    }
                    catch (Exception e)
                    {
                        report.WriteLine($"    page at {at}: {e.Message}");
                        break;
                    }
                }
            }
        }
    }

    /// <summary>The footer's Thrift as field ids and wire types, structs and lists nested, without reading any meaning into them.</summary>
    private static void Dump(ReadOnlySpan<byte> footer, StreamWriter report)
    {
        Vorticity.Parquet.Thrift.ThriftCompactReader reader = new(footer);
        try
        {
            DumpStruct(ref reader, report, "");
        }
        catch (Exception e)
        {
            report.WriteLine($"thrift: {e.Message}");
        }
    }

    private static void DumpStruct(ref Vorticity.Parquet.Thrift.ThriftCompactReader reader, StreamWriter report, string indent)
    {
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out Vorticity.Parquet.Thrift.ThriftType type, out short id))
        {
            report.WriteLine($"{indent}{id}: {type}");
            DumpValue(ref reader, type, report, indent + "  ");
        }

        reader.ExitStruct(saved);
    }

    private static void DumpValue(ref Vorticity.Parquet.Thrift.ThriftCompactReader reader, Vorticity.Parquet.Thrift.ThriftType type, StreamWriter report, string indent)
    {
        switch (type)
        {
            case Vorticity.Parquet.Thrift.ThriftType.Struct:
                DumpStruct(ref reader, report, indent);
                return;
            case Vorticity.Parquet.Thrift.ThriftType.List:
            case Vorticity.Parquet.Thrift.ThriftType.Set:
                int count = reader.ReadListHeader(out Vorticity.Parquet.Thrift.ThriftType element);
                report.WriteLine($"{indent}list of {count} {element}");
                for (int i = 0; i < count && i < 3; i++)
                {
                    DumpValue(ref reader, element, report, indent + "  ");
                }

                for (int i = 3; i < count; i++)
                {
                    if (element == Vorticity.Parquet.Thrift.ThriftType.Struct)
                    {
                        reader.Skip(element);
                    }
                    else
                    {
                        DumpValue(ref reader, element, report, indent + "  ");
                    }
                }

                return;
            default:
                reader.Skip(type);
                return;
        }
    }
}
