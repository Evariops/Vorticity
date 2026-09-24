using System;
using System.IO;
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
