// The forged fixtures, and the malformed-input invariant of docs/09-contracts.md §4.1:
//
//   "a file that violates the format - bad offsets, truncation, out-of-range Class I fields,
//    exceeded caps - is guaranteed to produce a clean VortexFormatException or
//    VortexUnsupportedException. No out-of-bounds access, no unbounded allocation, no hang."
//
// WHAT SHIPS ON DISK AND WHAT DOES NOT. forged/ holds exactly one fixture,
// negative/unknown_encoding_id, and it is NOT malformed: it is a structurally valid file carrying
// an encoding id no registry resolves, which is the lazy-resolution pair's fixture
// (LazyResolutionTests) and belongs to the "unsupported" half of the invariant. Its manifest says
// so. So the malformed half is generated here, deterministically, from corpus files: truncations at
// every structurally interesting length, a broken EOF magic, a broken version, and a postscript
// length that points outside the file. Every one of them must come back as VortexFormatException -
// not IndexOutOfRange, not ArgumentOutOfRange, not OutOfMemory, and not a file that reads.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Conformance.Corpus;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Conformance;

public sealed class ForgedFixtureTests
{
    /// <summary>The corpus files the malformed variants are derived from - small and structurally varied.</summary>
    private static readonly string[] MutationSources =
    [
        "containers/uncompressed_canonical",
        "containers/zoned_layout",
        "containers/chunked_layout_rowblock1024",
        "containers/dict_layout",
        "containers/postscript_max_metadata",
        "types/struct_nested_deep_nonnull_r1024",
    ];

    public static TheoryData<string> Fixtures()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (ForgedFixture fixture in ForgedCatalog.Fixtures)
        {
            data.Add(fixture.Id);
        }

        return data;
    }

    /// <summary>
    /// A forged fixture is re-derived, not trusted: its bytes must be the source file's with the
    /// declared patch applied, and its hash must be the declared one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryForgedFixtureMatchesItsDeclaredPatchAndHash(string id)
    {
        ForgedFixture fixture = ForgedCatalog.Find(id);
        byte[] bytes = System.IO.File.ReadAllBytes(fixture.FullPath);

        Assert.Equal(fixture.SizeBytes, bytes.LongLength);
        Assert.Equal(fixture.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        if (fixture.Source is null || fixture.Patch is null)
        {
            return;
        }

        byte[] source = System.IO.File.ReadAllBytes(CorpusCatalog.Resolve(fixture.Source));
        if (fixture.SourceSha256 is not null)
        {
            Assert.Equal(fixture.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(source)));
        }

        ForgedPatch patch = fixture.Patch;
        byte[] from = Encoding.UTF8.GetBytes(patch.From);
        byte[] to = Encoding.UTF8.GetBytes(patch.To);
        Assert.Equal(patch.Length, from.Length);
        Assert.Equal(patch.Length, to.Length);

        // Length-preserving, so every offset in the file stays valid: that is the property that
        // makes the fixture a test of component resolution and not of the bounds checks.
        Assert.Equal(source.LongLength, bytes.LongLength);
        Assert.True(source.AsSpan((int)patch.Offset, patch.Length).SequenceEqual(from),
            $"{id}: the source does not carry '{patch.From}' at {patch.Offset}");

        byte[] rederived = (byte[])source.Clone();
        to.CopyTo(rederived.AsSpan((int)patch.Offset));
        Assert.True(rederived.AsSpan().SequenceEqual(bytes), $"{id}: the fixture is not the source plus the patch");
    }

    /// <summary>
    /// Truncation, at every length where the container changes shape. Nothing here may escape as
    /// anything but <see cref="VortexFormatException"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(MutationSourceFiles))]
    public async Task TruncationProducesOnlyAFormatException(string id)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(CorpusCatalog.Verdict(id).Entry.FullPath);

        foreach (int length in TruncationLengths(bytes.Length))
        {
            byte[] truncated = bytes.AsSpan(0, length).ToArray();
            await AssertFormatExceptionAsync($"{id} truncated to {length}", truncated);
        }
    }

    /// <summary>The EOF marker: broken magic, wrong version, and a postscript length that lies.</summary>
    [Theory]
    [MemberData(nameof(MutationSourceFiles))]
    public async Task ABrokenEofMarkerProducesOnlyAFormatException(string id)
    {
        byte[] original = System.IO.File.ReadAllBytes(CorpusCatalog.Verdict(id).Entry.FullPath);

        byte[] magic = (byte[])original.Clone();
        magic[^1] ^= 0xFF;
        await AssertFormatExceptionAsync($"{id} with a corrupted EOF magic", magic);

        byte[] version = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(version.Length - 8, 2), 2);
        await AssertFormatExceptionAsync($"{id} with format version 2", version);

        byte[] zeroVersion = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroVersion.AsSpan(zeroVersion.Length - 8, 2), 0);
        await AssertFormatExceptionAsync($"{id} with format version 0", zeroVersion);

        // A postscript longer than the file: the classic offset-based-format failure, and the one a
        // reader that trusts a u16 turns into an out-of-bounds read.
        byte[] huge = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(huge.AsSpan(huge.Length - 6, 2), 65527);
        await AssertFormatExceptionAsync($"{id} with a 65527-byte postscript", huge);

        byte[] empty = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(empty.AsSpan(empty.Length - 6, 2), 0);
        await AssertFormatExceptionAsync($"{id} with a zero-length postscript", empty);

        // One byte shorter than the truth: the postscript now starts mid-field.
        int declared = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(original.Length - 6, 2));
        if (declared > 1)
        {
            byte[] shifted = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(shifted.AsSpan(shifted.Length - 6, 2), (ushort)(declared - 1));
            await AssertFormatExceptionAsync($"{id} with a postscript one byte short", shifted);
        }
    }

    public static TheoryData<string> MutationSourceFiles()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (string id in MutationSources)
        {
            data.Add(id);
        }

        return data;
    }

    /// <summary>
    /// Opens and fully scans <paramref name="bytes"/> and asserts the only thing that escapes is
    /// <see cref="VortexFormatException"/>. A file that reads without error is also a failure: a
    /// truncated file that produces values is producing them out of thin air.
    /// </summary>
    private static async Task AssertFormatExceptionAsync(string what, byte[] bytes)
    {
        Phase1Components.EnsureRegistered();

        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(
                new MemorySegmentSource(bytes), VortexOpenOptions.Default, TestContext.Current.CancellationToken);

            await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                .WithCancellation(TestContext.Current.CancellationToken))
            {
                batch.Dispose();
            }
        }
        catch (VortexFormatException)
        {
            return;
        }
        catch (Exception error)
        {
            Assert.Fail(
                $"{what}: expected VortexFormatException, got {error.GetType().FullName}: {error.Message}");
            return;
        }

        Assert.Fail($"{what}: the malformed file was read without error");
    }

    /// <summary>
    /// The lengths worth truncating at: zero, inside the EOF marker, just inside the postscript,
    /// halfway, and one byte short.
    /// </summary>
    private static IEnumerable<int> TruncationLengths(int length)
    {
        int postscript = 8 + 32;
        int[] candidates =
        [
            0,
            1,
            7,
            8,
            9,
            16,
            64,
            length / 2,
            Math.Max(0, length - postscript),
            Math.Max(0, length - 8),
            Math.Max(0, length - 1),
        ];

        SortedSet<int> unique = new SortedSet<int>();
        foreach (int candidate in candidates)
        {
            if (candidate >= 0 && candidate < length)
            {
                unique.Add(candidate);
            }
        }

        return unique;
    }

    /// <summary>
    /// The one fixture that ships is not malformed, and the harness says so out loud rather than
    /// letting a reader assume the malformed half is covered by a file on disk.
    /// </summary>
    [Fact]
    public void TheForgedDirectoryShipsOneUnsupportedEncodingFixtureAndNoMalformedOnes()
    {
        Assert.Single(ForgedCatalog.Fixtures);
        ForgedFixture fixture = ForgedCatalog.Fixtures[0];
        Assert.Equal("negative/unknown_encoding_id", fixture.Id);

        Console.Out.Write(
            "forged/: 1 fixture (" + fixture.Id + "), structurally valid and carrying an " +
            "unregistered encoding id. No malformed fixture ships on disk; the malformed-input " +
            "invariant is exercised by " + MutationSources.Length.ToString(CultureInfo.InvariantCulture) +
            " corpus files mutated in memory.\n");
    }
}
