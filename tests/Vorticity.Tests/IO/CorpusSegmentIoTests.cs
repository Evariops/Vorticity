using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Xunit;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>
/// The I/O layer against real files written by Vortex 0.86.1. Value-by-value conformance belongs
/// to the conformance component; what is proved here is narrower and is
/// the I/O layer's own claim: the three sources return the same bytes as the file itself, for
/// ranges shaped and aligned the way a real footer's <c>segment_specs</c> are.
/// </summary>
public sealed class CorpusSegmentIoTests
{
    /// <summary>
    /// Files chosen for their sizes: under one page, several pages, and past a 32 KiB boundary.
    /// </summary>
    public static TheoryData<string> Files => new TheoryData<string>
    {
        "encodings/map.vortex",
        "types/binary_nonnull_r1.vortex",
        "encodings/alp_r1025.vortex",
        "containers/chunked_layout_rowblock1024.vortex",
    };

    private static string CorpusRoot([CallerFilePath] string thisFile = "")
    {
        // tests/Vorticity.Tests/IO/<this file> -> tests/Vorticity.Conformance/corpus
        string testsRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        return Path.Combine(testsRoot, "Vorticity.Conformance", "corpus");
    }

    private static string Resolve(string relative)
    {
        string path = Path.Combine(CorpusRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(global::System.IO.File.Exists(path), $"corpus file missing: {path}");
        return path;
    }

    /// <summary>
    /// Deterministically spreads segment-shaped ranges over a file, using the alignment exponents
    /// the corpus contains (3 and 4 in the footers, 0 and 3 in the postscripts).
    /// </summary>
    private static SegmentSpec[] SyntheticSegments(long fileLength)
    {
        byte[] exponents = [0, 3, 4, 6, 3, 0];
        int count = 0;
        SegmentSpec[] buffer = new SegmentSpec[64];

        long offset = 0;
        int step = 0;
        while (offset < fileLength && count < buffer.Length)
        {
            byte exponent = exponents[step % exponents.Length];
            int alignment = 1 << exponent;

            // Honest specs: a writer aligns a segment's offset to its own exponent.
            long aligned = (offset + alignment - 1) & ~((long)alignment - 1);
            if (aligned >= fileLength)
            {
                break;
            }

            int length = (int)Math.Min(((step * 37) % 512) + 1, fileLength - aligned);
            buffer[count++] = new SegmentSpec((ulong)aligned, (uint)length, exponent, 0, 0);

            offset = aligned + length + ((step * 101) % 700);
            step++;
        }

        return buffer[..count];
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task All_three_sources_return_the_file_length(string relative)
    {
        string path = Resolve(relative);
        long expected = new FileInfo(path).Length;

        await using MemoryMappedSegmentSource mapped = MemoryMappedSegmentSource.Open(path);
        await using RandomAccessSegmentSource random = RandomAccessSegmentSource.Open(path);
        await using HttpRangeSegmentSource http =
            new HttpRangeSegmentSource(new InMemoryRangeTransport(global::System.IO.File.ReadAllBytes(path)));

        Assert.Equal(expected, mapped.Length);
        Assert.Equal(expected, random.Length);
        Assert.Equal(expected, http.Length);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task All_three_sources_agree_with_the_file_on_a_coalesced_split(string relative)
    {
        string path = Resolve(relative);
        byte[] expected = global::System.IO.File.ReadAllBytes(path);
        SegmentSpec[] specs = SyntheticSegments(expected.Length);

        Assert.True(specs.Length > 3, "the fixture should produce a real split");

        foreach (SegmentSourceKind kind in
                 (SegmentSourceKind[])Enum.GetValues(typeof(SegmentSourceKind)))
        {
            ISegmentSource source = kind switch
            {
                SegmentSourceKind.MemoryMapped => MemoryMappedSegmentSource.Open(path),
                SegmentSourceKind.RandomAccess => RandomAccessSegmentSource.Open(path),
                _ => new HttpRangeSegmentSource(new InMemoryRangeTransport(expected)),
            };

            await using (source)
            {
                using SegmentRequestSet set = new SegmentRequestSet();

                int[] slots = new int[specs.Length];
                for (int i = 0; i < specs.Length; i++)
                {
                    slots[i] = set.Add(specs[i]);
                }

                await source.ReadManyAsync(set, CancellationToken.None);

                for (int i = 0; i < specs.Length; i++)
                {
                    VortexBuffer buffer = set.GetBuffer(slots[i]);

                    Assert.True(
                        expected.AsSpan((int)specs[i].Offset, (int)specs[i].Length)
                            .SequenceEqual(buffer.Span),
                        $"{kind} {relative} segment {i} at {specs[i].Offset}+{specs[i].Length}");

                    // The alignment proof, on real offsets from a real file.
                    Assert.Equal(
                        (nuint)0,
                        AddressOf(buffer) % (nuint)(1 << specs[i].AlignmentExponent));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task The_open_path_tail_read_matches_the_file(string relative)
    {
        // What VortexFile.OpenAsync does first: 64 KiB off the end, aligned for a FlatBuffers root.
        string path = Resolve(relative);
        byte[] expected = global::System.IO.File.ReadAllBytes(path);

        const int tailWanted = 65536;
        long start = Math.Max(0, expected.Length - tailWanted);
        int tailLength = (int)(expected.Length - start);

        await using MemoryMappedSegmentSource mapped = MemoryMappedSegmentSource.Open(path);
        await using RandomAccessSegmentSource random = RandomAccessSegmentSource.Open(path);

        SegmentOwner a = await mapped.ReadRangeAsync(start, tailWanted, 8, CancellationToken.None);
        SegmentOwner b = await random.ReadRangeAsync(start, tailWanted, 8, CancellationToken.None);

        try
        {
            Assert.Equal(tailLength, a.Buffer.Length);
            Assert.Equal(tailLength, b.Buffer.Length);
            Assert.True(expected.AsSpan((int)start).SequenceEqual(a.Buffer.Span));
            Assert.True(a.Buffer.Span.SequenceEqual(b.Buffer.Span));
            Assert.Equal((nuint)0, AddressOf(a.Buffer) % 8);
            Assert.Equal((nuint)0, AddressOf(b.Buffer) % 8);
        }
        finally
        {
            a.Release();
            b.Release();
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Reading_past_the_end_of_a_real_file_is_a_format_error(string relative)
    {
        string path = Resolve(relative);
        long length = new FileInfo(path).Length;

        await using MemoryMappedSegmentSource mapped = MemoryMappedSegmentSource.Open(path);
        await using RandomAccessSegmentSource random = RandomAccessSegmentSource.Open(path);

        SegmentSpec overrun = Spec((ulong)length - 8, 64, 3);

        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await mapped.ReadAsync(overrun, CancellationToken.None));
        await Assert.ThrowsAsync<VortexFormatException>(
            async () => await random.ReadAsync(overrun, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Coalescing_a_real_file_costs_far_fewer_requests_than_segments(string relative)
    {
        string path = Resolve(relative);
        byte[] content = global::System.IO.File.ReadAllBytes(path);
        SegmentSpec[] specs = SyntheticSegments(content.Length);

        InMemoryRangeTransport transport = new InMemoryRangeTransport(content);
        await using HttpRangeSegmentSource source = new HttpRangeSegmentSource(transport);
        using SegmentRequestSet set = new SegmentRequestSet();

        foreach (SegmentSpec spec in specs)
        {
            set.Add(spec);
        }

        await source.ReadManyAsync(set, CancellationToken.None);

        // Every gap here is well under the 1 MiB default, so the whole file is one round trip -
        // the property that makes or breaks reading from object storage.
        Assert.Equal(1, transport.RequestCount);
        Assert.True(set.Count > 3);
    }
}
