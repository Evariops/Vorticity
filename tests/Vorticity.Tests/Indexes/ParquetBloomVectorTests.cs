// The Parquet-compatible Bloom variant against Parquet's own filter, byte for byte
// (docs/10-indexes.md §10, last clause of the false-positive row).
//
// `tools/conformance-gen/examples/gen_parquet_bloom_vectors.rs` builds each bitset with the
// `parquet` crate's `Sbbf`, so the vectors carry Parquet's hash (xxHash64, seed 0), its block
// choice, its eight salts in its order and its sizing. `BloomHash.XxHash64` must reproduce them;
// if it did not, a filter we wrote for a Parquet reader would be a filter that drops rows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class ParquetBloomVectorTests
{
    internal sealed record Vector(string Name, int Blocks, byte[][] Keys, byte[] Filter);

    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> cases = [];
        foreach (Vector vector in Load())
        {
            cases.Add(vector.Name, vector.Blocks);
        }

        return cases;
    }

    [Fact]
    public void TheVectorsCoverEveryParquetPhysicalShapeAtEverySize()
    {
        HashSet<(string, int)> covered = [];
        foreach (Vector vector in Load())
        {
            covered.Add((vector.Name, vector.Blocks));
        }

        Assert.Equal(4 * 3, covered.Count);
        Assert.Contains(("byte_array", 64), covered);
        Assert.Contains(("double", 1), covered);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OurParquetVariantIsParquetsByteForByte(string name, int blocks)
    {
        Vector vector = Find(name, blocks);
        uint[] words = new uint[blocks * SplitBlockBloom.WordsPerBlock];
        foreach (byte[] key in vector.Keys)
        {
            SplitBlockBloom.Insert(words, SplitBlockBloom.Hash(key, BloomHash.XxHash64));
        }

        Assert.Equal(vector.Filter.Length, words.Length * sizeof(uint));
        Assert.Equal(Convert.ToHexString(vector.Filter), Convert.ToHexString(MemoryMarshal.AsBytes(words.AsSpan())));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryKeyIsFoundInParquetsOwnBytes(string name, int blocks)
    {
        // The other direction: read Parquet's bitset as ours and probe it. A filter that agreed
        // byte for byte and then answered "absent" would mean our probe and our insert disagree.
        Vector vector = Find(name, blocks);
        ReadOnlySpan<uint> theirs = SplitBlockBloom.Words(vector.Filter);
        foreach (byte[] key in vector.Keys)
        {
            Assert.True(SplitBlockBloom.Contains(theirs, SplitBlockBloom.Hash(key, BloomHash.XxHash64)));
        }
    }

    [Fact]
    public void TheDefaultHashIsNotParquets()
    {
        // The variant exists because the two differ; if XxHash3 ever produced Parquet's bits, the
        // policy's two values would be one and this suite would be proving nothing.
        Vector vector = Find("int64", 8);
        uint[] words = new uint[vector.Blocks * SplitBlockBloom.WordsPerBlock];
        foreach (byte[] key in vector.Keys)
        {
            SplitBlockBloom.Insert(words, SplitBlockBloom.Hash(key, BloomHash.XxHash3));
        }

        Assert.NotEqual(
            Convert.ToHexString(vector.Filter),
            Convert.ToHexString(MemoryMarshal.AsBytes(words.AsSpan())));
    }

    private static Vector Find(string name, int blocks)
    {
        foreach (Vector vector in Load())
        {
            if (vector.Name == name && vector.Blocks == blocks)
            {
                return vector;
            }
        }

        throw new InvalidOperationException($"no Parquet vector {name}/{blocks}");
    }

    private static Vector[]? _cache;

    internal static Vector[] Load([CallerFilePath] string thisFile = "")
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        string path = Path.Combine(new FileInfo(thisFile).Directory!.FullName, "ParquetBloomVectors.json");
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        List<Vector> vectors = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            List<byte[]> keys = [];
            foreach (JsonElement key in element.GetProperty("keys").EnumerateArray())
            {
                keys.Add(Convert.FromHexString(key.GetString()!));
            }

            vectors.Add(new Vector(
                element.GetProperty("name").GetString()!,
                element.GetProperty("blocks").GetInt32(),
                [.. keys],
                Convert.FromHexString(element.GetProperty("filter").GetString()!)));
        }

        _cache = [.. vectors];
        return _cache;
    }
}
