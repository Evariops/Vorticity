// The sealed envelope meets bytes nobody wrote on purpose.
//
// WHAT IS HELD: a sealed object mutated at random, and in the fields its structure hangs on (the
// header's length, the trailer's length, the count of epochs, where an epoch starts and what it holds),
// either fails with one of the three exceptions of the format (format, unsupported, encryption) or
// reads back the plaintext it was sealed from, bit for bit, or, cut at the end of an earlier trailer,
// the plaintext of that earlier version, and does either quickly. A value read that
// is not the one sealed, any other exception, or a slow failure is a finding. Deterministic by seed: a
// finding names the seed, the iteration and the mutation.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Sealing;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedFuzzTests
{
    /// <summary>The run's seed: fixed, or VORTICITY_SEALED_FUZZ_SEED for a campaign of another.</summary>
    private static readonly int Seed = int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_SEALED_FUZZ_SEED"), out int seed) ? seed : 20261011;

    /// <summary>The mutations a run tries: a few thousand each commit, VORTICITY_SEALED_FUZZ_ITERATIONS for a long campaign.</summary>
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_SEALED_FUZZ_ITERATIONS"), out int iterations) ? iterations : 4_000;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task AMutatedEnvelopeFailsCleanlyOrReadsBackWhatWasSealed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DataKey key = new DataKey("fuzz", SealedObjects.Key(7), SealedObjects.Key(9));

        // A small object, one of four epochs one of them empty, and a Vortex file read through its frames.
        // A cut at the end of an earlier trailer is that earlier version, whole: the object lengths of
        // the versions and the plaintext each holds.
        byte[] pattern = SealedObjects.Pattern(9_000);
        byte[] small = await SealedObjects.SealAsync(pattern, key, SealParameters.ForFile(12), 1_000, ct);
        Dictionary<int, int> versions = new Dictionary<int, int> { [small.Length] = 9_000 };
        byte[] epochs = await SealedObjects.AppendSealAsync(small, pattern.AsMemory(0, 5_000), key, 700, ct);
        versions[epochs.Length] = 14_000;
        epochs = await SealedObjects.AppendSealAsync(epochs, ReadOnlyMemory<byte>.Empty, key, 1, ct);
        versions[epochs.Length] = 14_000;
        epochs = await SealedObjects.AppendSealAsync(epochs, pattern.AsMemory(5_000), key, 4_000, ct);
        versions[epochs.Length] = 18_000;
        List<Reading> rows = SealedObjects.Rows(3_000);
        byte[] file = await SealedObjects.WriteAsync(rows, key, SealParameters.ForFile(12), ct);
        (byte[] Sealed, byte[]? Plain)[] seeds = [(small, pattern), (epochs, [.. pattern, .. pattern]), (file, null)];

        Random random = new Random(Seed);
        int read = 0;
        List<string> findings = [];
        for (int i = 0; i < Iterations && findings.Count < 10; i++)
        {
            int which = random.Next(seeds.Length);
            (byte[] original, byte[]? plain) = seeds[which];
            (byte[] mutated, string what) = Mutate(original, random);
            Stopwatch clock = Stopwatch.StartNew();
            string? finding;
            try
            {
                finding = plain is not null
                    ? await ReadBackAsync(mutated, key, versions.TryGetValue(mutated.Length, out int held) ? plain[..held] : plain, ct)
                    : await ScanBackAsync(mutated, key, rows, ct);
                read += finding is null ? 1 : 0;
            }
            catch (VortexFormatException)
            {
                finding = null;
            }
            catch (VortexUnsupportedException)
            {
                finding = null;
            }
            catch (VortexEncryptionException)
            {
                finding = null;
            }
            catch (Exception e)
            {
                finding = $"{e.GetType().Name}: {e.Message} {e.StackTrace?.Split('\n')[0].Trim()}";
            }

            // The walk that repairs a file and judges a commit, without the key: the end of a version
            // it gives is the object's own, or one the object really had.
            try
            {
                finding ??= await WalkAsync(mutated, original.Length, versions, file.Length, ct);
            }
            catch (VortexFormatException)
            {
                // Allowed, as for the open.
            }
            catch (Exception e)
            {
                finding = $"the walk: {e.GetType().Name}: {e.Message} {e.StackTrace?.Split('\n')[0].Trim()}";
            }

            if (finding is null && clock.Elapsed > Budget)
            {
                finding = $"took {clock.Elapsed.TotalSeconds:F1} s";
            }

            if (finding is not null)
            {
                findings.Add($"seed {Seed}, iteration {i}, object {which}, {what}: {finding}");
            }
        }

        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));

        // Some mutations land where nothing is read, the header's copy of the descriptor or a trailer
        // an append made obsolete; most must not, or the campaign proved little about the parser.
        Assert.InRange(read, 1, Iterations / 2);
    }

    /// <summary>Reads the whole plaintext back, or the reason it differs from <paramref name="plain"/>.</summary>
    private static async Task<string?> ReadBackAsync(byte[] mutated, DataKey key, byte[] plain, CancellationToken ct)
    {
        await using SealedSegmentReader reader = await OpenAsync(mutated, key, ct);
        long length = await reader.GetLengthAsync(ct);
        if (length != plain.Length)
        {
            return $"opened as {length} plaintext bytes, where {plain.Length} were sealed";
        }

        using Vorticity.Buffers.SegmentOwner all = await reader.ReadRangeAsync(0, plain.Length, 1, ct);
        return all.Buffer.Span.SequenceEqual(plain) ? null : "read back bytes that were not sealed";
    }

    /// <summary>Scans the Vortex file inside, or the reason its rows differ from <paramref name="rows"/>.</summary>
    private static async Task<string?> ScanBackAsync(byte[] mutated, DataKey key, List<Reading> rows, CancellationToken ct)
    {
        SealedSegmentReader reader = await OpenAsync(mutated, key, ct);
        await using VortexFile file = await VortexFile.OpenAsync(reader, VortexOpenOptions.Default, ct);
        List<Reading> read = await SealedObjects.RowsOfAsync(file, ct);
        return System.Linq.Enumerable.SequenceEqual(read, rows) ? null : "scanned rows that were not sealed";
    }

    /// <summary>
    /// Where the keyless walk ends <paramref name="mutated"/>, checked: -1, the object's own length, the
    /// original's, or an earlier version's; and the wholeness a dataset judges a commit by agreeing.
    /// </summary>
    private static async Task<string?> WalkAsync(byte[] mutated, int original, Dictionary<int, int> versions, int file, CancellationToken ct)
    {
        await using Vorticity.IO.MemorySegmentSource source = new Vorticity.IO.MemorySegmentSource(mutated);
        long end = await SealedFiles.WholeEndAsync(source, mutated.Length, ct);
        bool whole = await SealedFiles.IsWholeAsync(source, mutated.Length, ct);
        if (whole != (end == mutated.Length))
        {
            return $"the walk ends at {end} of {mutated.Length}, and the object is said {(whole ? "whole" : "not whole")}";
        }

        bool known = end == -1 || end == mutated.Length || end == original || end == file || versions.ContainsKey((int)end);
        return known && end <= mutated.Length ? null : $"the walk ends at {end}, no version's end, in an object of {mutated.Length}";
    }

    private static ValueTask<SealedSegmentReader> OpenAsync(byte[] mutated, DataKey key, CancellationToken ct) =>
        SealedSegmentReader.OpenAsync(
            new Vorticity.IO.MemorySegmentSource(mutated),
            ownsInner: true,
            (descriptor, _) => descriptor.KeyId == key.KeyId && descriptor.WrappedKey.Span.SequenceEqual(key.WrappedKey.Span)
                ? new ValueTask<DataKey>(key.Retain())
                : throw VortexEncryptionException.NoKey(descriptor.KeyId, "another key"),
            ct);

    /// <summary>One mutation, and what it was.</summary>
    private static (byte[] Mutated, string What) Mutate(byte[] original, Random random)
    {
        byte[] bytes = (byte[])original.Clone();
        int length = bytes.Length;
        int descriptor = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        int trailerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(length - SealedFormat.TrailerSuffixBytes));
        int trailer = length - trailerLength;
        int count = trailer + descriptor;
        switch (random.Next(9))
        {
            case 0:
            {
                int at = random.Next(length);
                bytes[at] ^= (byte)(1 << random.Next(8));
                return (bytes, $"bit flipped at {at}");
            }

            case 1:
            {
                // Somewhere in the trailer, where the structure is.
                int at = trailer + random.Next(trailerLength);
                bytes[at] = (byte)random.Next(256);
                return (bytes, $"trailer byte {at - trailer} set");
            }

            case 2:
            {
                int cut = random.Next(length);
                return (bytes[..cut], $"cut to {cut}");
            }

            case 3:
            {
                int extra = random.Next(1, 200);
                byte[] longer = new byte[length + extra];
                bytes.CopyTo(longer, 0);
                random.NextBytes(longer.AsSpan(length));
                return (longer, $"{extra} bytes added");
            }

            case 4:
            {
                uint value = Extreme32(random, length);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(length - SealedFormat.TrailerSuffixBytes), value);
                return (bytes, $"trailer length {value}");
            }

            case 5:
            {
                uint value = Extreme32(random, length);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(count), value);
                return (bytes, $"epoch count {value}");
            }

            case 6:
            {
                // An epoch's start or plaintext length, in the first entry or a later one.
                int epochs = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(count));
                int epoch = random.Next(Math.Max(1, epochs));
                int entry = count + 4 + (epoch == 0 ? 0 : SealedFormat.EpochEntryBytes + ((epoch - 1) * SealedFormat.AppendedEpochEntryBytes));
                int field = entry + (random.Next(2) * 8);
                long value = Extreme64(random, length);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field), value);
                return (bytes, $"epoch {epoch} field {(field - entry) / 8} set to {value}");
            }

            case 7:
            {
                uint value = Extreme32(random, length);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
                return (bytes, $"header descriptor length {value}");
            }

            default:
            {
                // A field of the trailer's descriptor: its version, frame size or a length prefix.
                int at = trailer + random.Next(Math.Min(descriptor, 64));
                bytes[at] = (byte)(random.Next(2) == 0 ? 0xFF : random.Next(256));
                return (bytes, $"trailer descriptor byte {at - trailer} set");
            }
        }
    }

    private static uint Extreme32(Random random, int length) => random.Next(7) switch
    {
        0 => 0,
        1 => uint.MaxValue,
        2 => int.MaxValue,
        3 => (uint)length,
        4 => (uint)length + 1,
        5 => (uint)Math.Max(0, length - 1),
        _ => (uint)random.Next(),
    };

    private static long Extreme64(Random random, int length) => random.Next(8) switch
    {
        0 => 0,
        1 => -1,
        2 => long.MaxValue,
        3 => long.MinValue,
        4 => length,
        5 => length - 1,
        6 => (long)uint.MaxValue << 12,
        _ => random.NextInt64(),
    };
}
