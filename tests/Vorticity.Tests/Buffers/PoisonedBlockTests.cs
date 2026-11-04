using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Buffers;

/// <summary>
/// The blocks of the shared pool are not zeroed when rented, nor cleared when given back: filled with garbage at both, a
/// write and a read that use only the bytes they wrote, and none of a block they gave back, give the same file and the
/// same rows as from clean blocks.
/// </summary>
/// <remarks>
/// The pool's watch is the process's, so the tests run alone. A dataset's object keeps state of its own in pooled blocks
/// (the registers of its counts), which the second set of options writes.
/// </remarks>
[Collection(nameof(SharedPoolCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class PoisonedBlockTests
{
    private const int Rows = 20_000;

    /// <summary>Batches that end inside a block, as a dataset's appends come.</summary>
    private const int BatchRows = 3_000;

    private static readonly Guid Pinned = new Guid("5e5e5e5e-5e5e-4e5e-8e5e-5e5e5e5e5e5e");

    private static readonly VariantShredding Plan = VariantShredding.Object(
        ("duration", VariantShredding.Primitive(VariantClass.Integer)),
        ("route", VariantShredding.Primitive(VariantClass.String)),
        ("status", VariantShredding.Primitive(VariantClass.Integer)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task AFileWrittenAndReadFromPoisonedBlocksIsTheOneWrittenAndReadFromCleanOnes(int degree, bool asAnObject)
    {
        Doc[] rows = Build();
        VortexWriteOptions options = Options(degree, asAnObject);
        byte[] clean = await WriteAsync(rows, options);
        List<Doc> cleanRows = await ReadAsync(clean);
        Assert.Equal(rows, cleanRows);

        Poison poison = new Poison();
        AlignedBufferPool.Watch = poison;
        try
        {
            byte[] poisoned = await WriteAsync(rows, options);
            List<Doc> read = await ReadAsync(poisoned);
            Assert.True(Volatile.Read(ref poison.Blocks) > 0, "the write must rent blocks from the shared pool");
            Assert.Equal(clean, poisoned);
            Assert.Equal(cleanRows, read);
        }
        finally
        {
            AlignedBufferPool.Watch = null;
        }
    }

    /// <summary>
    /// The options of a file, or, <paramref name="asAnObject"/>, of a dataset's object: a table of contents, each column's
    /// counts and sheets, statistics by granule, every column gathered to a target of its own.
    /// </summary>
    private static VortexWriteOptions Options(int degree, bool asAnObject) => new VortexWriteOptions
    {
        Identity = Pinned,
        BlockRows = 1_024,
        DegreeOfParallelism = degree,
        ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = Plan },
        CarriesContents = asAnObject,
        Counted = asAnObject,
        Granulated = asAnObject,
        ColumnSheetBytes = asAnObject ? 0 : null,
        GatheredChunkBytes = asAnObject ? 64 << 10 : 0,
    };

    private static async Task<byte[]> WriteAsync(Doc[] rows, VortexWriteOptions options)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-poison-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options))
            {
                for (int start = 0; start < rows.Length; start += BatchRows)
                {
                    await writer.WriteAsync<Doc>(rows.AsSpan(start, Math.Min(BatchRows, rows.Length - start)), Ct);
                }

                await writer.CompleteAsync(Ct);
            }

            return await System.IO.File.ReadAllBytesAsync(path, Ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<List<Doc>> ReadAsync(byte[] bytes)
    {
        await using VortexFile file = await VortexFile.OpenAsync(new Vorticity.Tests.File.TestSegmentSource(bytes), VortexOpenOptions.Default, Ct);
        return await file.Scan<Doc>().ToRecordsAsync(Ct).ToListAsync(Ct);
    }

    /// <summary>
    /// A key, a service of five, a status, a note that is null now and then, and attributes: three shredded keys, the
    /// duration all over, and a trace now and then that stays in the residual.
    /// </summary>
    private static Doc[] Build()
    {
        Random random = new Random(11);
        string[] services = ["cart", "checkout", "search", "payment", "catalog"];
        return [.. Enumerable.Range(0, Rows).Select(i => new Doc(
            i,
            services[i % services.Length],
            i % 7 == 0 ? 500 : 200,
            i % 13 == 0 ? null : $"note {random.Next(5_000)}",
            i % 3 == 0
                ? Variant.Object(
                    ("duration", Variant.From((long)random.Next(1_000_000))),
                    ("route", Variant.From($"/api/{i % 37}")),
                    ("status", Variant.From((long)(i % 11 == 0 ? 500 : 200))),
                    ("trace", Variant.From($"t{random.Next()}")))
                : Variant.Object(
                    ("duration", Variant.From((long)random.Next(1_000_000))),
                    ("route", Variant.From($"/api/{i % 37}")),
                    ("status", Variant.From((long)(i % 11 == 0 ? 500 : 200))))))];
    }

    /// <summary>Fills each block with garbage as it is rented, and with other garbage as it is given back.</summary>
    private sealed class Poison : AlignedBufferPool.INativeWatch
    {
        internal long Blocks;

        void AlignedBufferPool.INativeWatch.Rented(NativeSegmentOwner owner)
        {
            owner.WritableSpan.Fill(0xA5);
            Interlocked.Increment(ref Blocks);
        }

        void AlignedBufferPool.INativeWatch.Released(NativeSegmentOwner owner) => owner.WritableSpan.Fill(0x5A);
    }

    [VortexRecord]
    public partial record struct Doc(long Id, string Service, long Status, string? Note, Variant Attributes);
}
