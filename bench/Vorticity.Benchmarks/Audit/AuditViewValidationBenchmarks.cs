// What the validation of a chunk of text views costs a row, the work a decode of
// `vortex.varbinview` does besides wrapping its buffers.
//
// 65,536 views of one data buffer, pinned: values of 8 bytes (inline in their views), of 24 ASCII bytes,
// and of 24 bytes with a two-byte character in every value (UTF-8 past ASCII). Each is validated as a
// UTF-8 column without nulls, as a decode of the whole chunk validates it.
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Benchmarks.Audit;

/// <summary>The validation of a chunk of text views, a row at a time.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditViewValidationBenchmarks
{
    private const int Rows = 65_536;

    /// <summary>The values: inline of 8 bytes, 24 ASCII bytes, 24 bytes past ASCII.</summary>
    [Params("inline8", "ascii24", "utf8-24")]
    public string Values { get; set; } = "ascii24";

    private byte[] _views = null!;
    private byte[] _data = null!;

    /// <summary>Writes the views and their data.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _views = GC.AllocateArray<byte>(Rows * 16, pinned: true);
        int width = Values == "inline8" ? 8 : 24;
        _data = GC.AllocateArray<byte>(Values == "inline8" ? 1 : Rows * width, pinned: true);
        Random random = new Random(7);
        Span<byte> value = stackalloc byte[width];
        for (int row = 0; row < Rows; row++)
        {
            for (int i = 0; i < width; i++)
            {
                value[i] = (byte)('a' + random.Next(26));
            }

            if (Values == "utf8-24")
            {
                // "é", two bytes, somewhere in the value.
                int at = random.Next(width - 1);
                value[at] = 0xC3;
                value[at + 1] = 0xA9;
            }

            Span<byte> view = _views.AsSpan(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, width);
            if (width <= 12)
            {
                value.CopyTo(view[4..]);
            }
            else
            {
                value.CopyTo(_data.AsSpan(row * width));
                value[..4].CopyTo(view[4..]);
                BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(view[12..], row * width);
            }
        }

        Validate();
        Console.WriteLine($"// {Values}: {(Values == "inline8" ? "inline" : Encoding.UTF8.GetString(_data.AsSpan(0, 24)))}");
    }

    /// <summary>The validation of every view, as a UTF-8 column without nulls.</summary>
    [Benchmark(OperationsPerInvoke = Rows)]
    public int Validate()
    {
        Span<VortexBuffer> buffers = [VortexBuffer.FromPinned(_data, 0)];
        VarBinViewDecoder.ValidateViews(_views, buffers, ValidityMask.NonNullable, Rows, requireUtf8: true);
        return buffers.Length;
    }
}
