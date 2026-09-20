using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Writing;

namespace Vorticity.Samples;

internal static class EncodingHints
{
    internal static async Task RunAsync()
    {
        // The same two columns, written with one hint after another.
        foreach (VortexEncodingHint hint in new[]
        {
            VortexEncodingHint.Auto,
            VortexEncodingHint.Canonical,
            VortexEncodingHint.Dictionary,
            VortexEncodingHint.Fsst,
            VortexEncodingHint.Zstd,
            VortexEncodingHint.RunEnd,
        })
        {
            await Hinted("city", hint);
        }

        foreach (VortexEncodingHint hint in new[]
        {
            VortexEncodingHint.Auto,
            VortexEncodingHint.Canonical,
            VortexEncodingHint.Alp,
            VortexEncodingHint.BitPacked,
            VortexEncodingHint.Sequence,
            VortexEncodingHint.Zstd,
        })
        {
            await Hinted("celsius", hint);
        }
    }

    private static async Task Hinted(string column, VortexEncodingHint hint)
    {
        string path = Demo.Path($"hint-{column}-{hint}.vortex");
        WriteReport report = await Demo.WriteCitiesAsync(path, new VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, VortexEncodingHint> { [column] = hint },
        });

        string chosen = "?";
        foreach (ColumnWriteReport written in report.Columns)
        {
            if (written.Path == column)
            {
                chosen = string.Join(", ", written.Encodings);
            }
        }

        Console.WriteLine($"  {column} as {hint}: {new FileInfo(path).Length} bytes, encoded {chosen}");
        System.IO.File.Delete(path);
    }
}
