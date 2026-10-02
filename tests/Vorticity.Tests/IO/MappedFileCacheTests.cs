using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Buffers;
using Vorticity.IO;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The files a session keeps mapped once closed: taken over by the next open of the same file,
/// never by another file under the same name, and let go without cutting off a file still open.
/// </summary>
public sealed class MappedFileCacheTests
{
    private const int FileLength = 40_000;

    [Fact]
    public void ASixtyFourBitLinuxMacOSOrWindowsTellsOneFileFromAnother()
    {
        // The tests below skip where the platform cannot tell; this one keeps a platform that
        // should from losing it without a failure.
        Assert.SkipUnless(
            OperatingSystem.IsWindows()
                || ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64),
            "only Windows and 64-bit Linux and macOS read a file's identity");
        Assert.True(FileInode.IsSupported);
    }

    [Fact]
    public void AFileHasOneInodeWhateverTheHandleAndAnotherFileAnother()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        using TempFile first = new TempFile(Pattern(FileLength));
        using TempFile second = new TempFile(Pattern(FileLength));

        using SafeFileHandle a = Open(first.Path_);
        using SafeFileHandle b = Open(first.Path_);
        using SafeFileHandle c = Open(second.Path_);
        Assert.True(FileInode.TryGet(a, FileLength, out FileInode ofA));
        Assert.True(FileInode.TryGet(b, FileLength, out FileInode ofB));
        Assert.True(FileInode.TryGet(c, FileLength, out FileInode ofC));
        Assert.Equal(ofA, ofB);
        Assert.NotEqual(ofA, ofC);

        // The platform's answer must repeat the length, or the identity is not trusted.
        Assert.False(FileInode.TryGet(a, FileLength + 1, out _));
    }

    [Fact]
    public async Task ASecondOpenOfTheFileTakesOverItsMapping()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileCache cache = new MappedFileCache(4);

        MappedFileOwner first = await MappedAndReadAsync(file.Path_, cache, content);
        MappedFileOwner second = await MappedAndReadAsync(file.Path_, cache, content);

        Assert.Same(first, second);
        Assert.Equal(1, cache.Count);
        cache.Clear();
    }

    [Fact]
    public async Task AKeptFileReadFromItsNameOpensOnItsMappingAndReadsItsTail()
    {
        // Where the platform reads a file's identity from its name, an open of a kept file takes
        // the mapping over before any scan, and serves the tail an open reads from it.
        Assert.SkipUnless(ReadsIdentityFromName(), "the platform reads no identity from a name");
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileCache cache = new MappedFileCache(4);
        MappedFileOwner first = await MappedAndReadAsync(file.Path_, cache, content);

        await using (LocalFileSource again = LocalFileSource.Open(file.Path_, cache))
        {
            Assert.Same(first, again.Mapping);
            using SegmentOwner tail = await ((ISegmentReader)again).ReadRangeAsync(FileLength - 1_000, 1_000, 1, CancellationToken.None);
            Assert.True(tail.Buffer.Span.SequenceEqual(content.AsSpan(FileLength - 1_000)));
        }

        // Replaced under its name, it is another file: opened, and mapped by its first scan.
        byte[] after = Pattern(FileLength);
        after[0] ^= 0xFF;
        global::System.IO.File.WriteAllBytes(file.Path_ + ".next", after);
        global::System.IO.File.Move(file.Path_ + ".next", file.Path_, overwrite: true);
        await using (LocalFileSource replaced = LocalFileSource.Open(file.Path_, cache))
        {
            Assert.Null(replaced.Mapping);
        }

        cache.Clear();

        static bool ReadsIdentityFromName()
        {
            string probe = Path.GetTempFileName();
            try
            {
                return FileInode.TryGetByName(probe, out _, out _);
            }
            finally
            {
                global::System.IO.File.Delete(probe);
            }
        }
    }

    [Fact]
    public async Task AFileReplacedUnderItsNameIsMappedAnew()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        byte[] before = Pattern(FileLength);
        using TempFile file = new TempFile(before);
        MappedFileCache cache = new MappedFileCache(4);
        MappedFileOwner first = await MappedAndReadAsync(file.Path_, cache, before);

        byte[] after = new byte[FileLength];
        for (int i = 0; i < after.Length; i++)
        {
            after[i] = (byte)~before[i];
        }

        string replacement = file.Path_ + ".next";
        global::System.IO.File.WriteAllBytes(replacement, after);
        global::System.IO.File.Move(replacement, file.Path_, overwrite: true);

        MappedFileOwner second = await MappedAndReadAsync(file.Path_, cache, after);
        Assert.NotSame(first, second);
        Assert.Equal(1, cache.Count);
        cache.Clear();
    }

    [Fact]
    public async Task AFileWhoseLengthChangedIsMappedAnew()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        byte[] before = Pattern(FileLength);
        using TempFile file = new TempFile(before);
        MappedFileCache cache = new MappedFileCache(4);
        MappedFileOwner first = await MappedAndReadAsync(file.Path_, cache, before);

        byte[] after = Pattern(FileLength + 5_000);
        using (FileStream append = new FileStream(file.Path_, FileMode.Append, FileAccess.Write))
        {
            append.Write(after, FileLength, 5_000);
        }

        MappedFileOwner second = await MappedAndReadAsync(file.Path_, cache, after);
        Assert.NotSame(first, second);
        Assert.Equal(1, cache.Count);
        cache.Clear();
    }

    [Fact]
    public async Task AFileRewrittenInPlaceReadsAsItNowIs()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        byte[] before = Pattern(FileLength);
        using TempFile file = new TempFile(before);
        MappedFileCache cache = new MappedFileCache(4);
        MappedFileOwner first = await MappedAndReadAsync(file.Path_, cache, before);

        byte[] after = new byte[FileLength];
        for (int i = 0; i < after.Length; i++)
        {
            after[i] = (byte)(before[i] + 1);
        }

        using (FileStream rewrite = new FileStream(file.Path_, FileMode.Open, FileAccess.Write))
        {
            rewrite.Write(after);
        }

        // Same file, same length: the kept mapping is taken over, and it shows the new bytes.
        MappedFileOwner second = await MappedAndReadAsync(file.Path_, cache, after);
        Assert.Same(first, second);
        cache.Clear();
    }

    [Fact]
    public async Task TheFileOpenedLongestAgoLeavesAndAnOpenFileKeepsReading()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        byte[] content = Pattern(FileLength);
        using TempFile older = new TempFile(content);
        using TempFile newer = new TempFile(content);
        MappedFileCache cache = new MappedFileCache(1);

        await using LocalFileSource open = LocalFileSource.Open(older.Path_, cache);
        open.AnticipateData();
        _ = await MappedAndReadAsync(newer.Path_, cache, content);
        Assert.Equal(1, cache.Count);

        // The older file left the cache while its reader was open; the reader still reads.
        using SegmentOwner owner = await ((ISegmentReader)open).ReadAsync(Spec(100, 2_000), CancellationToken.None);
        Assert.True(owner.Buffer.Span.SequenceEqual(content.AsSpan(100, 2_000)));
        cache.Clear();
        Assert.Equal(0, cache.Count);

        using SegmentOwner again = await ((ISegmentReader)open).ReadAsync(Spec(30_000, 1_000), CancellationToken.None);
        Assert.True(again.Buffer.Span.SequenceEqual(content.AsSpan(30_000, 1_000)));
    }

    [Fact]
    public async Task AFileKeptMappedDoesNotKeepAWriterOut()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        byte[] content = Pattern(FileLength);
        using TempFile file = new TempFile(content);
        MappedFileCache cache = new MappedFileCache(4);
        _ = await MappedAndReadAsync(file.Path_, cache, content);
        Assert.Equal(1, cache.Count);

        // What an append opens with: the kept mapping must hold neither the handle nor its lock.
        using (SafeFileHandle writer = global::System.IO.File.OpenHandle(file.Path_, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.False(writer.IsInvalid);
        }

        cache.Clear();
    }

    [Fact]
    public void ASessionKeepsNoNegativeCountOfFiles()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VortexSession.Create(options => options.MappedFileCacheCount = -1));
    }

    // A file deleted once closed keeps its disk space for as long as its mapping is kept.
    [Fact]
    public async Task ASessionLetsGoOfTheFilesItKeepsMappedWhenAsked()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        string directory = Path.Combine(AppContext.BaseDirectory, "kept-mappings");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"ids-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        long expected = await WriteSpreadIdsAsync(path, CancellationToken.None);

        await using VortexSession session = VortexSession.Create(options => options.MappedFileCacheCount = 4);
        long sum = 0;
        await using (VortexFile file = await session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken))
        {
            await foreach (BatchView batch in file.Scan("id").WithCancellation(TestContext.Current.CancellationToken))
            {
                foreach (long id in batch.Column<long>("id").Values)
                {
                    sum += id;
                }
            }
        }

        Assert.Equal(expected, sum);
        Assert.Equal(1, session.Mappings!.Count);
        global::System.IO.File.Delete(path);
        session.ReleaseMappedFiles();
        Assert.Equal(0, session.Mappings.Count);
    }

    [Fact]
    public async Task AnOpenThatEndsAfterItsSessionIsDisposedClosesItsFileAndKeepsNoMapping()
    {
        Assert.SkipUnless(FileInode.IsSupported, "the platform does not tell one file from another");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(AppContext.BaseDirectory, "kept-mappings");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"late-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await WriteSpreadIdsAsync(path, ct);

        VortexSession session = VortexSession.Create(options => options.MappedFileCacheCount = 4);
        await using (VortexFile first = await session.OpenAsync(path, cancellationToken: ct))
        {
            long rows = 0;
            await foreach (BatchView batch in first.Scan("id").WithCancellation(ct))
            {
                rows += batch.RowCount;
            }

            Assert.Equal(SpreadIds, rows);
            Assert.Equal(1, session.Mappings!.Count);
        }

        // An open that passed its check before the session was disposed, and ends after: it took
        // over the mapping the first open left, and gives it back as it closes.
        VortexFile late = await VortexFile.OpenAsync(path, VortexOpenOptions.Default, session, ct);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await session.AttachAsync(late, path));
        Assert.Throws<ObjectDisposedException>(() => late.SegmentSpecs.Length);
        Assert.Equal(0, session.Mappings!.Count);
        global::System.IO.File.Delete(path);
    }

    /// <summary>Ids <see cref="WriteSpreadIdsAsync"/> writes: eight bytes each, 128 KiB.</summary>
    private const int SpreadIds = 16_384;

    /// <summary>
    /// Writes ids that do not compress, so that the file outgrows what an open reads of it whole
    /// and its scan maps it; returns their sum.
    /// </summary>
    private static async Task<long> WriteSpreadIdsAsync(string path, CancellationToken ct)
    {
        long[] ids = new long[SpreadIds];
        long sum = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = (long)((ulong)i * 0x9E3779B97F4A7C15UL);
            sum += ids[i];
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(path, [("id", VortexType.Int64)]))
        {
            ColumnsBuilder builder = writer.Builder();
            builder.Column<long>(0).Append(ids);
            await writer.WriteAsync(builder, ct);
            await writer.CompleteAsync(ct);
        }

        Assert.True(new FileInfo(path).Length > VortexOpenOptions.DefaultInitialReadSize);
        return sum;
    }

    /// <summary>Opens the file through the cache, maps it, checks every byte, and returns the mapping it read.</summary>
    private static async Task<MappedFileOwner> MappedAndReadAsync(string path, MappedFileCache cache, byte[] expected)
    {
        await using LocalFileSource source = LocalFileSource.Open(path, cache);
        source.AnticipateData();
        MappedFileOwner mapping = source.Mapping ?? throw new InvalidOperationException("the scan did not map the file");
        using SegmentOwner owner = await ((ISegmentReader)source).ReadAsync(Spec(0, (uint)expected.Length), CancellationToken.None);
        Assert.True(owner.Buffer.Span.SequenceEqual(expected));
        return mapping;
    }

    private static SafeFileHandle Open(string path) =>
        global::System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
