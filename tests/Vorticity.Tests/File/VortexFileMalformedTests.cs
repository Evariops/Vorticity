// Malformed input. The promise here is absolute: every one of these must
// produce a VortexFormatException and nothing else - never an IndexOutOfRange, never an
// unbounded allocation, never a hang, never a plausible wrong answer.
//
// The base file is a real corpus file, mutated byte for byte, so the mutation is the only thing
// that differs from a file the reference reads happily.
using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class VortexFileMalformedTests
{
    private const string BaseFile = "containers/all_null_i64_explicit_validity_r1025";

    private static byte[] Original() => CorpusManifest.Bytes(BaseFile);

    private static async Task<VortexFormatException> Rejects(byte[] bytes) =>
        await Assert.ThrowsAsync<VortexFormatException>(async () => await Open(bytes));

    private static async Task<VortexFile> Open(byte[] bytes) =>
        await VortexFile.OpenAsync(
            new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None);

    // ------------------------------------------------------------------------------ truncation

    [Fact]
    public async Task ZeroLengthInputIsRejected()
    {
        VortexFormatException error = await Rejects([]);
        Assert.Contains("at least 8 bytes", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task AFileShorterThanTheEofMarkerIsRejected(int length) =>
        await Rejects(new byte[length]);

    [Fact]
    public async Task TruncatingTheLastByteIsRejected()
    {
        // length - 1 destroys the trailing magic.
        byte[] bytes = Original();
        VortexFormatException error = await Rejects(bytes.AsSpan(0, bytes.Length - 1).ToArray());
        Assert.Contains("magic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TruncatingTheWholeEofMarkerIsRejected()
    {
        // length - 8 leaves the postscript's last bytes where the EOF marker should be.
        byte[] bytes = Original();
        await Rejects(bytes.AsSpan(0, bytes.Length - 8).ToArray());
    }

    [Fact]
    public async Task TruncatingOneByteBeforeTheEofMarkerIsRejected()
    {
        byte[] bytes = Original();
        await Rejects(bytes.AsSpan(0, bytes.Length - 9).ToArray());
    }

    [Fact]
    public async Task AFileShorterThanItsOwnPostscriptIsRejected()
    {
        // The file is cut to `postscript_length + 8 - 1` bytes: the EOF marker survives, its
        // declared length does not fit, and the length must be rejected BEFORE it is used to
        // slice. That ordering is the whole point of the test.
        byte[] bytes = Original();
        int postscriptLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 6, 2));
        byte[] truncated = bytes.AsSpan(bytes.Length - (postscriptLength + 7)).ToArray();

        VortexFormatException error = await Rejects(truncated);
        Assert.Contains("shorter than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EverySingleByteMutationOfTheFooterRegionIsRejectedOrOpensCleanly()
    {
        // The blunt instrument that backs the absolute promise on malformed input: a
        // deterministic sweep of single-byte mutations over the last 4 KB - postscript, footer,
        // layout and statistics - asserting that the ONLY two outcomes are a clean open and a
        // VortexFormatException. No IndexOutOfRange, no OverflowException, no hang.
        byte[] original = Original();
        int start = Math.Max(0, original.Length - 4096);
        Random random = new Random(20260912);
        int rejected = 0;
        int accepted = 0;

        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] bytes = (byte[])original.Clone();
            int at = start + random.Next(original.Length - start);
            bytes[at] ^= (byte)(1 << random.Next(8));

            try
            {
                VortexFile file = await Open(bytes);
                await file.DisposeAsync();
                accepted++;
            }
            catch (VortexFormatException)
            {
                // The only acceptable failure.
                rejected++;
            }
            catch (Exception ex)
            {
                Assert.Fail($"mutation of byte {at} produced {ex.GetType().FullName}: {ex.Message}");
            }
        }

        // Self-check: the sweep must actually reach both outcomes, or it is measuring nothing.
        // Most mutations land in padding or in a field nobody reads and the file still opens.
        Assert.True(rejected > 100, $"only {rejected} of 3000 mutations were rejected");
        Assert.True(accepted > 100, $"only {accepted} of 3000 mutations still opened");
    }

    // ----------------------------------------------------------------------------- EOF marker

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AFlippedTrailingMagicByteIsRejected(int index)
    {
        byte[] bytes = Original();
        bytes[bytes.Length - 4 + index] ^= 0xff;
        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("magic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFlippedLeadingMagicByteIsRejectedWhenTheWindowReachesOffsetZero()
    {
        // The leading 'VTXF' is never verified upstream. We verify it whenever the tail read
        // happens to reach offset 0, which it does for every file below 65535 bytes.
        byte[] bytes = Original();
        Assert.True(bytes.Length < VortexFileFormat.InitialReadSize);
        bytes[2] ^= 0xff;
        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("leading magic", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)2)]
    [InlineData(ushort.MaxValue)]
    public async Task AnUnsupportedVersionIsRejected(ushort version)
    {
        byte[] bytes = Original();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 8, 2), version);
        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("unsupported version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APostscriptLengthAboveTheFormatMaximumIsRejected()
    {
        // 65528 is MAX_POSTSCRIPT_SIZE + 1. Upstream never checks this on read and discovers it as
        // a short buffer instead; catching it here names the real problem.
        byte[] bytes = Original();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 6, 2), 65528);
        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("exceeds the format maximum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APostscriptLengthLongerThanTheFileIsRejected()
    {
        byte[] bytes = Original();
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(bytes.Length - 6, 2), (ushort)VortexFileFormat.MaxPostscriptSize);
        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("shorter than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorruptPostscriptFlatBufferIsRejected()
    {
        byte[] bytes = Original();
        int postscriptLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 6, 2));
        int start = bytes.Length - 8 - postscriptLength;

        // The postscript's root uoffset, pointed past the end of its own buffer.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start, 4), 0xffff_fff0u);
        await Rejects(bytes);
    }

    [Fact]
    public async Task EveryPrefixOfTheFileIsRejectedOrOpens()
    {
        // A blunt sweep: no prefix of a valid file may produce anything but a clean rejection.
        byte[] bytes = Original();
        for (int length = 0; length < bytes.Length; length += 37)
        {
            byte[] prefix = bytes.AsSpan(0, length).ToArray();
            await Assert.ThrowsAsync<VortexFormatException>(async () => await Open(prefix));
        }
    }

    // ------------------------------------------------------------------------ postscript rules

    [Fact]
    public async Task DuplicateMetadataKeysAreRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            Metadata = [("same", [1]), ("same", [2])],
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("share a key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyMetadataKeyIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            Metadata = [("placeholder", [1])],
            MetadataKeysUtf8 = [[]],
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMetadataKeyOfSixtyFiveBytesIsRejectedAndSixtyFourIsAccepted()
    {
        DTypeArena arena = new DTypeArena();

        byte[] tooLong = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            Metadata = [("placeholder", [1])],
            MetadataKeysUtf8 = [Key(65)],
        });
        await Rejects(tooLong);

        byte[] exactly = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            Metadata = [("placeholder", [1])],
            MetadataKeysUtf8 = [Key(64)],
        });
        await using VortexFile file = await Open(exactly);
        Assert.Equal(1, file.MetadataCount);

        static byte[] Key(int length)
        {
            byte[] key = new byte[length];
            key.AsSpan().Fill((byte)'k');
            return key;
        }
    }

    [Fact]
    public async Task AMultiByteKeyIsMeasuredInBytesNotCharacters()
    {
        // Upstream's own test uses repeated "é": 33 of them is 66 UTF-8 bytes and must be rejected
        // even though it is only 33 characters.
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            Metadata = [(new string('é', 33), [1])],
        });

        await Rejects(bytes);
    }

    [Fact]
    public async Task SeventeenMetadataSegmentsAreRejectedAndSixteenAreAccepted()
    {
        DTypeArena arena = new DTypeArena();
        DType schema = SyntheticVortexFile.SmallSchema(arena);

        await using (VortexFile sixteen = await Open(
            SyntheticVortexFile.Build(new SyntheticFileSpec { Schema = schema, Metadata = Entries(16) })))
        {
            Assert.Equal(16, sixteen.MetadataCount);
        }

        // The writer refuses to emit 17, so the 17-entry postscript is assembled by hand.
        await Rejects(SyntheticFileWithSeventeenMetadataEntries(schema));

        static (string, byte[])[] Entries(int count)
        {
            (string, byte[])[] entries = new (string, byte[])[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = ("key" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), [(byte)i]);
            }

            return entries;
        }
    }

    private static byte[] SyntheticFileWithSeventeenMetadataEntries(DType schema)
    {
        // PostscriptWriter.Write enforces the 16-entry ceiling, so the vector is built directly.
        (string, byte[])[] sixteen = new (string, byte[])[16];
        for (int i = 0; i < 16; i++)
        {
            sixteen[i] = ("key" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), [(byte)i]);
        }

        byte[] valid = SyntheticVortexFile.Build(new SyntheticFileSpec { Schema = schema, Metadata = sixteen });

        // Bump the metadata vector's element count from 16 to 17 in place. The 17th element offset
        // now reads whatever follows, which the reader must reject on the count alone - before it
        // ever dereferences it. That ordering is the point of the test.
        int postscriptLength = BinaryPrimitives.ReadUInt16LittleEndian(valid.AsSpan(valid.Length - 6, 2));
        int start = valid.Length - 8 - postscriptLength;
        Span<byte> postscript = valid.AsSpan(start, postscriptLength);

        for (int i = 0; i + 4 <= postscript.Length; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(postscript.Slice(i, 4)) == 16u)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(postscript.Slice(i, 4), 17u);
                return valid;
            }
        }

        throw new InvalidOperationException("Could not find the metadata vector length to patch.");
    }

    // ---------------------------------------------------------------------------- footer rules

    [Fact]
    public async Task SegmentSpecsOutOfOrderAreRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideSegmentSpecs =
            [
                new SegmentSpec(256, 8, 0, 0, 0),
                new SegmentSpec(128, 8, 0, 0, 0),
            ],
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("not ordered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EqualSegmentOffsetsAreAcceptedBecauseZeroLengthSegmentsExist()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideSegmentSpecs =
            [
                new SegmentSpec(128, 0, 0, 0, 0),
                new SegmentSpec(128, 8, 0, 0, 0),
                new SegmentSpec(128, 0, 0, 0, 0),
            ],
        });

        await using VortexFile file = await Open(bytes);
        Assert.Equal(3, file.SegmentSpecs.Length);
        Assert.Equal(0u, file.SegmentSpecs[0].Length);
    }

    [Fact]
    public async Task ASegmentExtendingPastTheEndOfTheFileIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideSegmentSpecs = [new SegmentSpec(64, uint.MaxValue, 0, 0, 0)],
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("past the end", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASegmentWhoseOffsetPlusLengthOverflowsIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideSegmentSpecs = [new SegmentSpec(ulong.MaxValue, 1, 0, 0, 0)],
        });

        await Rejects(bytes);
    }

    [Fact]
    public async Task ASegmentMisalignedForItsOwnExponentIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideSegmentSpecs = [new SegmentSpec(65, 8, 6, 0, 0)],
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("not aligned", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlignmentExponentAboveTheCapIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        foreach (byte exponent in new byte[] { 7, 16, 255 })
        {
            byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
            {
                Schema = SyntheticVortexFile.SmallSchema(arena),
                OverrideSegmentSpecs = [new SegmentSpec(0, 8, exponent, 0, 0)],
            });

            await Rejects(bytes);
        }
    }

    // --------------------------------------------------------------- postscript segment locators

    [Fact]
    public async Task AZeroLengthLayoutSegmentIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideLayoutSegment = new SegmentSpec(64, 0, 3, 0, 0),
        });

        await Rejects(bytes);
    }

    [Fact]
    public async Task AZeroLengthDTypeSegmentIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideDTypeSegment = new SegmentSpec(64, 0, 3, 0, 0),
        });

        await Rejects(bytes);
    }

    [Fact]
    public async Task AFooterSegmentPastTheEndOfTheFileIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideFooterSegment = new SegmentSpec(64, uint.MaxValue, 3, 0, 0),
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("past the end", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFooterSegmentWhoseOffsetPlusLengthOverflowsIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideFooterSegment = new SegmentSpec(ulong.MaxValue, 8, 3, 0, 0),
        });

        await Rejects(bytes);
    }

    [Fact]
    public async Task ALayoutSegmentPointingAtDataBytesIsRejectedRatherThanMisread()
    {
        // Inside the file, correctly ranged, but not a Layout FlatBuffer. The reader must reject
        // it rather than return a plausible row count from whatever the bytes happen to say.
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            OverrideLayoutSegment = new SegmentSpec(64, 96, 3, 0, 0),
        });

        await Rejects(bytes);
    }

    [Fact]
    public async Task ARowCountThatDoesNotFitALongIsRejected()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            RowCount = ulong.MaxValue,
        });

        VortexFormatException error = await Rejects(bytes);
        Assert.Contains("does not fit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowCountAtLongMaxValueIsAccepted()
    {
        DTypeArena arena = new DTypeArena();
        byte[] bytes = SyntheticVortexFile.Build(new SyntheticFileSpec
        {
            Schema = SyntheticVortexFile.SmallSchema(arena),
            RowCount = long.MaxValue,
        });

        await using VortexFile file = await Open(bytes);
        Assert.Equal(long.MaxValue, file.RowCount);
    }
}
