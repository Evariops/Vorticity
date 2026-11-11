// What a literal costs compressed by a chunk's FSST symbol table, as an equality or a prefix pushed into an
// FSST column compresses its literal once per chunk.
//
// A table of 255 symbols of 1 to 8 bytes drawn from text, a literal of 20 bytes made of some of them and a few others.
using System;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Benchmarks.Audit;

/// <summary>A literal compressed by an FSST table.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditFsstCompressBenchmarks
{
    private readonly byte[] _symbols = new byte[255 * 8];
    private readonly byte[] _lengths = new byte[255];
    private readonly byte[] _into = new byte[64];
    private byte[] _literal = [];

    /// <summary>Draws the table and the literal.</summary>
    [GlobalSetup]
    public void Setup()
    {
        Random random = new Random(193);
        byte[] text = Encoding.ASCII.GetBytes("checkout-service/api/v2/cart/items?user=12345&region=eu-west-3 GET POST 503 latency_ms");
        for (int code = 0; code < 255; code++)
        {
            int width = random.Next(1, 9);
            int at = random.Next(text.Length - width);
            text.AsSpan(at, width).CopyTo(_symbols.AsSpan(code * 8));
            _lengths[code] = (byte)width;
        }

        _literal = Encoding.ASCII.GetBytes("checkout-service/api");
    }

    /// <summary>The literal compressed.</summary>
    [Benchmark]
    public int Compress() =>
        FsstSymbolTable.Create(_symbols, _lengths, "audit").TryCompress(_literal, _into, out int written) ? written : -1;
}
