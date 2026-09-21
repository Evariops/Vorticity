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
        foreach (EncodingHint hint in new[]
        {
            EncodingHint.Auto,
            EncodingHint.Canonical,
            EncodingHint.Dictionary,
            EncodingHint.Fsst,
            EncodingHint.Zstd,
            EncodingHint.RunEnd,
        })
        {
            await Hinted("city", hint);
        }

        foreach (EncodingHint hint in new[]
        {
            EncodingHint.Auto,
            EncodingHint.Canonical,
            EncodingHint.Alp,
            EncodingHint.BitPacked,
            EncodingHint.Sequence,
            EncodingHint.Zstd,
        })
        {
            await Hinted("celsius", hint);
        }
    }

    private static async Task Hinted(string column, EncodingHint hint)
    {
        string path = Demo.Path($"hint-{column}-{hint}.vortex");
        WriteReport report = await Demo.WriteCitiesAsync(path, new VortexWriteOptions
        {
            EncodingHints = new Dictionary<string, EncodingHint> { [column] = hint },
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
