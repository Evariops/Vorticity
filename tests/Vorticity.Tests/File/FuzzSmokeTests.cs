// A short, deterministic fuzz campaign inside the normal suite.
//
// The real campaign runs nightly, not per-commit, and that is right:
// tens of thousands of mutations do not belong in a build. But nightly-only means a parser
// regression lives for up to a day and is then found by a job nobody is watching, so a few hundred
// mutations run here with a FIXED SEED -- cheap enough to ignore, deterministic enough that a
// failure is reproducible from the test name alone, and enough to catch the class of change that
// breaks every mutated file at once.
//
// The full campaign lives in tests/Vorticity.Fuzz and is what actually explores.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class FuzzSmokeTests
{
    /// <summary>
    /// The invariant: only a format or unsupported exception may escape a mutated file.
    /// </summary>
    [Fact]
    public async Task MutatedCorpusFilesFailCleanlyOrReadCorrectly()
    {
        Decoders.EnsureRegistered();

        string[] seeds =
        [
            "containers/uncompressed_canonical",
            "containers/zoned_many_zones_nulls",
            "distributions/high_cardinality_i64_r8193",
            "types/utf8_nullable_r1025",
            "encodings/dict",
            "encodings/runend",
            "encodings/fastlanes_bitpacked",
        ];

        Random random = new Random(20260912);
        int rejectedAtOpen = 0;
        int deep = 0;

        for (int i = 0; i < 400; i++)
        {
            string id = seeds[random.Next(seeds.Length)];
            byte[] bytes = System.IO.File.ReadAllBytes(Corpus.Path(id));
            Mutate(bytes, random);

            bool opened = false;
            try
            {
                await using MemorySegmentSource source = new MemorySegmentSource(bytes);
                await using VortexFile file = await VortexFile.OpenAsync(
                    source, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);
                opened = true;

                await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    for (int field = 0; field < batch.FieldCount; field++)
                    {
                        VortexColumn column = batch.Column(field);
                        for (int row = 0; row < batch.RowCount; row++)
                        {
                            _ = column.IsValid(row);
                        }
                    }
                }
            }
            catch (VortexFormatException)
            {
                // The expected answer.
            }
            catch (VortexUnsupportedException)
            {
                // Also expected: a mutation can rename an encoding id.
            }
            catch (Exception error)
            {
                Assert.Fail(
                    $"iteration {i} on {id} threw {error.GetType().Name}: {error.Message}");
            }

            if (opened)
            {
                deep++;
            }
            else
            {
                rejectedAtOpen++;
            }
        }

        // The coverage assertion, without which this test is indistinguishable from one that
        // rejects every mutation at byte 0 and proves nothing about the decoders.
        Assert.True(
            deep > 100,
            $"only {deep} of 400 mutations got past the open path ({rejectedAtOpen} refused there)");
    }

    /// <summary>
    /// Two mutations, chosen for the same reason the full mutator's are: a random bit flip lands in
    /// a data buffer and changes a value, which is not a parser question.
    /// </summary>
    private static void Mutate(byte[] bytes, Random random)
    {
        if (random.Next(2) == 0)
        {
            // A word in the tail, where every offset lives.
            int window = Math.Min(bytes.Length, 4096);
            int at = Math.Min(bytes.Length - 4, bytes.Length - window + (random.Next(window) & ~3));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(at), (uint)random.Next());
            return;
        }

        // An extreme value anywhere: the class I fields are lengths and counts.
        int offset = random.Next(bytes.Length - 8) & ~7;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(offset), random.Next(2) == 0 ? ulong.MaxValue : 0x7FFF_FFFF_FFFF_FFFFUL);
    }
}
