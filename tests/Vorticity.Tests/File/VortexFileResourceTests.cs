// Resource-bound tests: the shapes where a small, entirely legal file asks the reader to do a
// disproportionate amount of work. Phase 0's review found one of these (a 3.6 KB DType DAG that
// hung the parser); the open path's equivalent is FlatBuffers string SHARING in the footer's
// encoding dictionaries.
//
// A FlatBuffers string may be referenced by any number of tables. `array_specs` is a vector of
// tables each holding one string offset, so a footer of F bytes can declare F/8 spec entries that
// all point at one string of nearly F bytes - and interning them at open, as PHASE1-CONTRACTS.md
// §2.3 asks for, allocates F squared over eight. At F = 300 KB that is 1.6 GB from a file that
// costs a single read. The ids are therefore located at open and materialized on demand.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class VortexFileResourceTests
{
    [Fact]
    public async Task AFooterWhoseSpecIdsAllShareOneLongStringDoesNotInternThemAtOpen()
    {
        const int Entries = 20_000;
        const int IdLength = 40_000;

        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            SharedArraySpecId = new string('x', IdLength),
            SharedArraySpecCount = Entries,
        });

        // Eager interning would be Entries * IdLength * 2 bytes = 1.6 GB of UTF-16.
        const long Amplified = (long)Entries * IdLength * 2;
        Assert.True(bytes.Length < Amplified / 1000, "the file must be far smaller than the amplification");

        TestSegmentSource source = new TestSegmentSource(bytes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Entries, file.ArrayEncodingCount);
        Assert.Equal(ArrayEncodingId.Unknown, file.GetArrayEncoding(0));
        Assert.Equal(ArrayEncodingId.Unknown, file.GetArrayEncoding(Entries - 1));

        // Generous, and still three orders of magnitude below the amplified figure.
        Assert.True(
            allocated < 8L * 1024 * 1024,
            $"open allocated {allocated} bytes; eager interning would have allocated {Amplified}");

        // The id is still reachable, one string at a time, for a diagnostic message.
        string id = file.GetArrayEncodingId(Entries - 1);
        Assert.Equal(IdLength, id.Length);
        Assert.Equal('x', id[0]);
    }

    [Fact]
    public async Task AScanContextOverThatFooterDoesNotInternTheSpecIdsEither()
    {
        // The open path's mitigation is worthless if the first thing a scan does is undo it:
        // BatchAsyncEnumerator builds one ScanContext per lane, in its constructor, before any
        // I/O, so eagerly interning the dictionary there costs the same F-squared - multiplied by
        // the degree of parallelism.
        const int Entries = 20_000;
        const int IdLength = 40_000;

        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            SharedArraySpecId = new string('x', IdLength),
            SharedArraySpecCount = Entries,
        });

        const long Amplified = (long)Entries * IdLength * 2;

        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        long before = GC.GetAllocatedBytesForCurrentThread();
        using ScanContext scan = new ScanContext(file);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Entries, scan.ArrayEncodingCount);
        Assert.Equal(ArrayEncodingId.Unknown, scan.ArrayEncodings[Entries - 1]);

        // The resolved u16 table is 2 bytes an entry and is fine; the id TEXT is not.
        Assert.True(
            allocated < 8L * 1024 * 1024,
            $"building a ScanContext allocated {allocated} bytes; eager interning would have " +
            $"allocated {Amplified}");

        // Still reachable, one string at a time, for the unsupported-encoding message.
        string id = scan.GetArrayEncodingIdText(Entries - 1);
        Assert.Equal(IdLength, id.Length);
        Assert.Equal('x', id[0]);

        // Out-of-range indices keep their placeholder rather than throwing.
        Assert.Contains("99999", scan.GetArrayEncodingIdText(99_999), StringComparison.Ordinal);
        Assert.Contains("-1", scan.GetArrayEncodingIdText(-1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADictionaryOfManySmallIdsStillResolvesEveryEntry()
    {
        DTypeArena arena = new DTypeArena();
        string[] ids = new string[512];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = i % 2 == 0
                ? "vortex.primitive"
                : "unknown.encoding." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            ArraySpecIds = ids,
        });

        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None);

        Assert.Equal(ids.Length, file.ArrayEncodingCount);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal(ids[i], file.GetArrayEncodingId(i));
            Assert.Equal(
                i % 2 == 0 ? ArrayEncodingId.Primitive : ArrayEncodingId.Unknown,
                file.GetArrayEncoding(i));
        }
    }

    [Fact]
    public async Task ConcurrentReadersOfOneOpenFileSeeTheSameAnswers()
    {
        // docs/09-contracts.md §1: concurrent scans on one open file are supported and expected.
        CorpusEntry entry = CorpusManifest.Find("types/user_metadata_segments");
        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(CorpusManifest.Bytes(entry.Id)),
            VortexOpenOptions.Default,
            CancellationToken.None);

        Task[] readers = new Task[8];
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Run(() =>
            {
                for (int round = 0; round < 200; round++)
                {
                    Assert.Equal(entry.RowCount, file.RowCount);
                    Assert.Equal(entry.DType, ManifestDTypeFormatter.Format(file.Schema));
                    Assert.Equal(3, file.MetadataCount);
                    Assert.Equal(33, file.Statistics.FieldCount);
                    Assert.False(file.SegmentSpecs.IsEmpty);
                    Assert.Equal("vortex.flat", file.GetLayoutEncodingId(0));
                }
            });
        }

        await Task.WhenAll(readers);
    }

    [Fact]
    public void ReadOptionsRejectANonPositiveDecompressionCeiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VortexReadOptions { MaxDecompressedSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new VortexReadOptions { MaxDecompressedSize = -1 });
        Assert.Equal(VortexLimits.DefaultMaxDecompressedSize, VortexReadOptions.Default.MaxDecompressedSize);
        Assert.False(VortexReadOptions.Default.VerifyStatistics);
        Assert.False(VortexReadOptions.Default.AllowUnknownComponents);
    }

    [Fact]
    public void OpenOptionsRejectNonsenseAndCarryTheirDefaults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VortexOpenOptions { FileLength = -2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new VortexOpenOptions { InitialReadSize = -1 });
        Assert.Throws<ArgumentNullException>(() => new VortexOpenOptions { Read = null! });

        Assert.Equal(-1, VortexOpenOptions.Default.FileLength);
        Assert.Equal(VortexFileFormat.InitialReadSize, VortexOpenOptions.Default.InitialReadSize);
        Assert.Same(VortexReadOptions.Default, VortexOpenOptions.Default.Read);
        Assert.True(VortexOpenOptions.Default.DType.IsDefault);
        Assert.False(VortexOpenOptions.Default.LeaveSourceOpen);
    }

    [Fact]
    public async Task ReadOptionsReachTheOpenFile()
    {
        VortexReadOptions read = new VortexReadOptions { VerifyStatistics = true, MaxDecompressedSize = 1024 };
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
        });

        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(bytes), new VortexOpenOptions { Read = read }, CancellationToken.None);

        Assert.Same(read, file.ReadOptions);
    }

    [Fact]
    public void TheFormatConstantsMatchTheReference()
    {
        // vortex-file-0.86.1/src/lib.rs, and its own assertions.
        Assert.Equal(8, VortexFileFormat.EofSize);
        Assert.Equal(65527, VortexFileFormat.MaxPostscriptSize);
        Assert.Equal(65535, VortexFileFormat.InitialReadSize);
        Assert.Equal(1, VortexFileFormat.Version);
        Assert.True(VortexFileFormat.MagicBytes.SequenceEqual("VTXF"u8));
        Assert.Equal(VortexLimits.MaxPostscriptSize, VortexFileFormat.MaxPostscriptSize);

        // INITIAL_READ_SIZE is 65535, one byte short of 64 KiB. The docs say "64 KiB"; the
        // constant is authoritative and matching it keeps our read pattern byte-identical.
        Assert.NotEqual(65536, VortexFileFormat.InitialReadSize);
    }
}
