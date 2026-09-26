// What a LIKE pays per row for the shape of its pattern.
//
// `s LIKE pattern` over one decoded batch of strings of one length. `Original` is the loop as it
// was, the backtracking matcher over every row, which the library keeps for a pattern with `_`;
// `Library` is the kernel, which reads a pattern without `_` once into its segments and finds them
// with the runtime's vectorised searches.
using System;
using System.Buffers.Binary;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One <c>LIKE</c> over one decoded batch, against the length of its values.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class LikeBenchmarks
{
    /// <summary>Rows of the batch.</summary>
    [Params(8_192)]
    public int Rows { get; set; }

    /// <summary>The bytes of each value.</summary>
    [Params(24, 256, 2_048)]
    public int Length { get; set; }

    /// <summary>The pattern's shape.</summary>
    [Params(Shape.Contains, Shape.Prefix, Shape.Suffix, Shape.Segments, Shape.Repeated)]
    public Shape Pattern { get; set; }

    /// <summary>A shape of pattern.</summary>
    public enum Shape
    {
        /// <summary><c>%needle%</c>, in one row in a hundred.</summary>
        Contains,

        /// <summary><c>needle%</c>, at the start of one row in a hundred.</summary>
        Prefix,

        /// <summary><c>%needle</c>, at the end of one row in a hundred.</summary>
        Suffix,

        /// <summary><c>%ne%ed%le%</c>: three segments in order.</summary>
        Segments,

        /// <summary><c>%aaaaaaaaaaaaaaab%</c> over runs of <c>a</c>: a backtracking walk's worst case.</summary>
        Repeated,
    }

    private CanonicalArena _arena = null!;
    private int _column;
    private FilterLiteral _literal;
    private byte[] _pattern = [];
    private byte[] _states = [];

    /// <summary>Builds the batch and the pattern.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _pattern = Pattern switch
        {
            Shape.Contains => "%needle%"u8.ToArray(),
            Shape.Prefix => "needle%"u8.ToArray(),
            Shape.Suffix => "%needle"u8.ToArray(),
            Shape.Segments => "%ne%ed%le%"u8.ToArray(),
            _ => "%aaaaaaaaaaaaaaab%"u8.ToArray(),
        };
        _literal = FilterLiteral.From(_pattern);

        Random random = new Random(20260926);
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        _arena = new CanonicalArena();
        VortexBuffer heap = _arena.Allocate(Rows * Length, 1, out Span<byte> heapBytes);
        VortexBuffer views = _arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        ReadOnlySpan<byte> needle = "needle"u8;
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> value = heapBytes.Slice(i * Length, Length);
            for (int b = 0; b < Length; b++)
            {
                // Runs of `a` broken now and then for the repeated shape, sixteen letters otherwise.
                value[b] = Pattern == Shape.Repeated
                    ? (random.Next(64) == 0 ? (byte)'c' : (byte)'a')
                    : (byte)('a' + random.Next(16));
            }

            if (i % 100 == 0)
            {
                int at = Pattern switch
                {
                    Shape.Prefix => 0,
                    Shape.Suffix => Length - needle.Length,
                    _ => random.Next(Length - needle.Length + 1),
                };
                needle.CopyTo(value[at..]);
            }

            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, Length);
            if (Length <= 12)
            {
                value.CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], i * Length);
        }

        _column = _arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, [heap]);
        _states = new byte[Rows];
    }

    /// <summary>Gives the arena's blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _arena.Reset();

    /// <summary>The backtracking matcher over every row, as the kernel ran it.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        ViewValues values = new ViewValues(_arena.GetNode(_column));
        for (int i = 0; i < _states.Length; i++)
        {
            _states[i] = BytePattern.Like(values.At(i), _pattern, (byte)'\\') ? Trilean.True : Trilean.False;
        }

        return Trilean.CountTrue(_states);
    }

    /// <summary>The kernel of the library.</summary>
    [Benchmark]
    public int Library()
    {
        ComparisonKernels.StringMatch(_arena, _column, StringMatchOp.Like, _literal, (byte)'\\', _states);
        return Trilean.CountTrue(_states);
    }
}
