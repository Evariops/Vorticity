// The lazy-resolution rule of PHASE1-CONTRACTS.md §2.3, on a real file.
//
// Opening a file must NEVER fail because of an unknown component id: resolution happens at open,
// failure happens at use, and the three throw sites are all downstream of this component. The
// fixture is forged in memory by a LENGTH-PRESERVING byte patch of one `array_specs` id, so every
// offset in the file stays valid and the only thing that changed is a name the registry cannot
// resolve - the same trick §2.7 uses to turn a `vortex.zoned` layout into a `vortex.stats` one.
using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.File;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class VortexFileForgedEncodingTests
{
    private const string BaseFile = "containers/all_null_i64_explicit_validity_r1025";

    [Fact]
    public async Task AnUnknownArrayEncodingIdOpensAndIsReportedVerbatim()
    {
        ReadOnlySpan<byte> known = "vortex.primitive"u8;
        ReadOnlySpan<byte> forged = "vortex.notreal!!"u8;
        Assert.Equal(known.Length, forged.Length);

        byte[] bytes = CorpusManifest.Bytes(BaseFile);
        int at = LastFlatBufferString(bytes, known);
        forged.CopyTo(bytes.AsSpan(at));

        TestSegmentSource source = new TestSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(
            source, VortexOpenOptions.Default, CancellationToken.None);

        // Opening succeeded: an unresolvable id is not an error here and never will be.
        Assert.Equal(1, source.TotalReads);
        Assert.Equal(1025, file.RowCount);

        bool sawForged = false;
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            if (string.Equals(file.GetArrayEncodingId(i), "vortex.notreal!!", StringComparison.Ordinal))
            {
                sawForged = true;
                Assert.Equal(ArrayEncodingId.Unknown, file.GetArrayEncoding(i));
            }

            // And nothing else was disturbed: every other id still resolves as it did.
            Assert.NotEqual("vortex.primitive", file.GetArrayEncodingId(i));
        }

        Assert.True(sawForged, "the forged id must appear in the dictionary verbatim");
    }

    [Fact]
    public async Task AnUnknownLayoutEncodingIdOpensToo()
    {
        // vortex.flat -> vortex.flip, same 11 bytes. The root layout's encoding index now names an
        // id no reader implements, and the open path must still not care: the layout tree is the
        // layouts component's business and the throw belongs to LayoutReaderTable.Get.
        ReadOnlySpan<byte> known = "vortex.flat"u8;
        ReadOnlySpan<byte> forged = "vortex.flip"u8;

        byte[] bytes = CorpusManifest.Bytes(BaseFile);
        int at = LastFlatBufferString(bytes, known);
        forged.CopyTo(bytes.AsSpan(at));

        await using VortexFile file = await VortexFile.OpenAsync(
            new TestSegmentSource(bytes), VortexOpenOptions.Default, CancellationToken.None);

        Assert.Equal(1, file.LayoutEncodingCount);
        Assert.Equal("vortex.flip", file.GetLayoutEncodingId(0));
        Assert.Equal(LayoutEncodingId.Unknown, file.GetLayoutEncoding(0));
        Assert.Equal(1025, file.RowCount);
    }

    /// <summary>
    /// Finds the last FlatBuffers string in <paramref name="bytes"/> whose payload equals
    /// <paramref name="value"/>: a <c>u32</c> length prefix, the bytes, and the trailing NUL. The
    /// footer is at the end of the file, so searching backwards finds the encoding dictionary
    /// rather than a coincidence in the data segments.
    /// </summary>
    private static int LastFlatBufferString(byte[] bytes, ReadOnlySpan<byte> value)
    {
        for (int at = bytes.Length - value.Length - 1; at >= 4; at--)
        {
            if (!bytes.AsSpan(at, value.Length).SequenceEqual(value))
            {
                continue;
            }

            if (bytes[at + value.Length] != 0)
            {
                continue;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at - 4, 4)) != (uint)value.Length)
            {
                continue;
            }

            return at;
        }

        throw new InvalidOperationException(
            $"No FlatBuffers string '{Encoding.UTF8.GetString(value)}' in the fixture.");
    }
}
