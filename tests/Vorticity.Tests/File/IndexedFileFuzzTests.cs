// A short, deterministic fuzz campaign over a file this library wrote with its own indexes.
//
// The corpus the campaign and FuzzSmokeTests mutate comes from Vortex's own writer: it holds none of the regions this
// library adds, the sorted runs, the postings, the Bloom trees, the fences and their directory. Those are where the audit
// found most of its escapes. Here a file carries every kind, and a few hundred mutations, with a fixed seed, are read by
// filters that walk each index: only a format or an unsupported error may come out.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.File;

public sealed partial class IndexedFileFuzzTests
{
    /// <summary>
    /// The invariant: a file whose bytes were changed, its indexes' among them, fails with a format or an unsupported error,
    /// or answers, whatever filter reads it.
    /// </summary>
    [Fact]
    public async Task MutatedIndexedFilesFailCleanlyOrAnswer()
    {
        Decoders.EnsureRegistered();
        byte[] written = await WrittenAsync();
        VortexExpr[] filters =
        [
            Expr.Eq(Expr.Field("V"), Expr.Literal(FilterLiteral.From(417L))),
            Expr.Eq(Expr.Field("S"), Expr.Literal(FilterLiteral.From("s42"))),
            Expr.Eq(Expr.Field("F"), Expr.Literal(FilterLiteral.From(12.5))),
            Expr.In(Expr.Field("V"), [FilterLiteral.From(1L), FilterLiteral.From(999L)]),
            Expr.And(
                Expr.Ge(Expr.Field("Id"), Expr.Literal(FilterLiteral.From(7_000L))),
                Expr.Lt(Expr.Field("Id"), Expr.Literal(FilterLiteral.From(9_000L)))),
        ];

        Random random = new Random(20261124);
        int opened = 0;
        for (int i = 0; i < 400; i++)
        {
            byte[] bytes = (byte[])written.Clone();
            Mutate(bytes, random);
            VortexExpr filter = filters[random.Next(filters.Length)];
            try
            {
                await using MemorySegmentSource source = new MemorySegmentSource(bytes);
                await using VortexFile file = await VortexFile.OpenAsync(source, new VortexOpenOptions { LeaveSourceOpen = true }, TestContext.Current.CancellationToken);
                opened++;
                _ = await file.ScanBuilder().Where(filter).CountAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception expected) when (expected is VortexFormatException or VortexUnsupportedException)
            {
            }
            catch (Exception error)
            {
                Assert.Fail($"iteration {i}, a filter {filter}, threw {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
            }
        }

        // Without it, a test that refuses every mutation at the open proves nothing of the indexes.
        Assert.True(opened > 100, $"only {opened} of 400 mutations got past the open");
    }

    /// <summary>
    /// A word changed where the indexes, their directory and the footer lie, toward the end, half the time; an extreme value
    /// anywhere otherwise: a count or a length, which the readers must hold to the bytes they have.
    /// </summary>
    private static void Mutate(byte[] bytes, Random random)
    {
        if (random.Next(2) == 0)
        {
            int window = Math.Min(bytes.Length, 64 << 10);
            int at = Math.Min(bytes.Length - 4, bytes.Length - window + (random.Next(window) & ~3));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)random.Next());
            return;
        }

        int offset = random.Next(bytes.Length - 8) & ~7;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(offset), random.Next(3) switch { 0 => ulong.MaxValue, 1 => 0x7FFF_FFFF_FFFF_FFFFUL, _ => 0x8000_0000UL });
    }

    /// <summary>A file of 20 000 rows with sorted runs, postings and a Bloom tree beside its zones, as bytes.</summary>
    private static async Task<byte[]> WrittenAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-indexed-fuzz-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                RowBlockSize = 1_024,
                IndexBudgetPerMille = 1_000_000,
                WritePolicy = WritePolicy.Auto
                    .For("V", IndexSpec.SortedRuns.WithSegmentEntries(512))
                    .For("S", IndexSpec.Postings)
                    .For("F", IndexSpec.Bloom(resolutions: 3)),
            };

            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, options))
            {
                Row[] rows = [.. Enumerable.Range(0, 20_000).Select(static i => new Row(i, (int)(i * 7919L % 1_000), "s" + (i * 31 % 211), i * 13 % 101 / 4.0))];
                await writer.WriteAsync<Row>(rows, TestContext.Current.CancellationToken);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            return await System.IO.File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Row(long Id, int V, string S, double F);
}
