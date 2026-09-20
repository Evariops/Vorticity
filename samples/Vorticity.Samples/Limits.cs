using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Samples;

internal static class Limits
{
    internal static async Task RunAsync()
    {
        Console.WriteLine($"decompression ceiling, by default {VortexLimits.DefaultMaxDecompressedSize} bytes");
        Console.WriteLine($"array depth {VortexLimits.MaxArrayDepth}, dtype depth {VortexLimits.MaxDTypeDepth}, " +
            $"layout depth {VortexLimits.MaxLayoutDepth}");
        Console.WriteLine($"flatbuffer depth {VortexLimits.MaxFlatBufferDepth}, " +
            $"tables {VortexLimits.MaxFlatBufferTables}");
        Console.WriteLine($"metadata segments {VortexLimits.MaxMetadataSegments}, " +
            $"key length {VortexLimits.MaxMetadataKeyLength}");
        Console.WriteLine($"compression specs {VortexLimits.MaxCompressionSpecs}, " +
            $"alignment {VortexLimits.MaxAlignment} (exponent {VortexLimits.MaxAlignmentExponent})");
        Console.WriteLine($"postscript {VortexLimits.MaxPostscriptSize} bytes, " +
            $"eof marker {VortexFileFormat.EofSize}, first read {VortexFileFormat.InitialReadSize}");

        string path = await Demo.ReadingsAsync();

        // The one ceiling a caller moves, and what moving it down does.
        foreach (long ceiling in new long[] { 4_096, 1024 * 1024, VortexLimits.DefaultMaxDecompressedSize })
        {
            try
            {
                await using VortexFile file = await VortexFile.OpenAsync(path, new VortexOpenOptions
                {
                    Read = new VortexReadOptions { MaxDecompressedSize = ceiling },
                });

                long rows = 0;
                await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
                {
                    using (batch)
                    {
                        rows += batch.RowCount;
                    }
                }

                Console.WriteLine($"  ceiling {ceiling}: {rows} rows");
            }
            catch (VortexFormatException e)
            {
                Console.WriteLine($"  ceiling {ceiling}: refused -- {e.Message}");
            }
        }

        // A component this build does not know is a refusal, unless the caller says otherwise.
        Console.WriteLine($"AllowUnknownComponents defaults to {VortexReadOptions.Default.AllowUnknownComponents}");
    }
}
