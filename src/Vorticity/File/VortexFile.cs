// The file open path. Transcribed from vortex-file-0.86.1/src/open.rs (`read_footer`),
// src/footer/deserializer.rs (`parse_postscript`, `deserialize`, `checked_segment_slice`),
// src/footer/postscript.rs and src/footer/mod.rs (`from_flatbuffer`,
// `validate_segments_within_file`).
//
// THE ROUND-TRIP GUARANTEE (docs/02-format.md §1, PHASE1-CONTRACTS.md §7.1). One length probe, one
// 65535-byte tail read, and at most one more read:
//
//   * 65535 = MAX_POSTSCRIPT_SIZE + EOF_SIZE, so the tail ALWAYS covers the postscript by
//     construction - there is no file for which locating the footer needs a second read;
//   * only a dtype / layout / statistics / footer segment that starts before the tail window costs
//     the second read, and it is issued once, for the whole gap, and prepended;
//   * supplying a DType removes the dtype segment from that decision entirely, which is the whole
//     point of VortexOpenOptions.DType;
//   * user metadata segment offsets are never part of it: metadata values are lazy.
//
// Measured over all 819 golden corpus files, zero need the second read. The synthetic file in
// VortexFileRoundTripTests is the one that does, and an instrumented ISegmentSource counts both.
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.File;

/// <summary>
/// An open Vortex file: schema, row count, footer dictionaries and the root layout.
/// </summary>
/// <remarks>
/// Immutable after open and therefore thread-safe: concurrent scans on one open file are supported
/// and expected (docs/09-contracts.md §1). The file holds the tail buffer for its whole life
/// because <see cref="SegmentSpecs"/> points into it.
/// </remarks>
public sealed partial class VortexFile : IAsyncDisposable
{
    private readonly ISegmentSource _source;
    private readonly bool _ownsSource;
    private readonly SegmentOwner _tail;
    private readonly long _tailOffset;
    private readonly DType _schema;

    // Held for their lifetime, not for their API: the parsed schema, the widened sum DTypes and
    // every statistic's ScalarValue are handles into these two, and a handle whose backing store
    // has been collected is a null dereference waiting to happen.
    private readonly DTypeArena _derivedTypes;
    private readonly ScalarStore _scalars;
    private readonly ArrayEncodingId[] _arrayEncodings;
    private readonly IdLocation[] _arrayEncodingIds;
    private readonly LayoutEncodingId[] _layoutEncodings;
    private readonly IdLocation[] _layoutEncodingIds;
    private readonly int _segmentSpecsByteOffset;
    private readonly int _segmentSpecCount;
    private readonly byte[] _rootLayoutBytes;
    private readonly FileStatistics? _statistics;

    /// <summary>Lazily parsed by <see cref="LayoutTree"/>; see its remarks on the benign race.</summary>
    private Layouts.LayoutTree? _layoutTree;
    private readonly string[] _metadataKeys;
    private readonly byte[][] _metadataKeysUtf8;
    private readonly SegmentSpec[] _metadataSegments;
    private int _disposed;

    private VortexFile(in OpenState state)
    {
        _source = state.Source;
        _ownsSource = state.OwnsSource;
        _tail = state.Tail;
        _tailOffset = state.TailOffset;
        FileLength = state.FileLength;
        ReadOptions = state.ReadOptions;
        _schema = state.Schema;
        _derivedTypes = state.DerivedTypes;
        _scalars = state.Scalars;
        RowCount = state.RowCount;
        _arrayEncodings = state.ArrayEncodings;
        _arrayEncodingIds = state.ArrayEncodingIds;
        _layoutEncodings = state.LayoutEncodings;
        _layoutEncodingIds = state.LayoutEncodingIds;
        _segmentSpecsByteOffset = state.SegmentSpecsByteOffset;
        _segmentSpecCount = state.SegmentSpecCount;
        _rootLayoutBytes = state.RootLayoutBytes;
        _statistics = state.Statistics;
        _metadataKeys = state.MetadataKeys;
        _metadataKeysUtf8 = state.MetadataKeysUtf8;
        _metadataSegments = state.MetadataSegments;
    }

    // ------------------------------------------------------------------------------------ open

    /// <summary>Opens a Vortex file from a path with the default options.</summary>
    /// <param name="path">A local file path.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file. The caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    public static ValueTask<VortexFile> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, VortexOpenOptions.Default, cancellationToken);

    /// <summary>Opens a Vortex file from a path.</summary>
    /// <param name="path">A local file path.</param>
    /// <param name="options">Open-time policy.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file. The caller disposes it.</returns>
    /// <remarks>
    /// The file is memory-mapped, so segment reads are zero-copy. The source created here is always
    /// owned by the returned file: <see cref="VortexOpenOptions.LeaveSourceOpen"/> applies only to
    /// a source the caller supplied.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    public static ValueTask<VortexFile> OpenAsync(
        string path, VortexOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(path);
        ValueTask<VortexFile> open = OpenCoreAsync(source, options, ownsSource: true, cancellationToken);
        return options.Read.IndexSidecarPath is null ? open : RememberTokenAsync(open, path);
    }

    /// <summary>
    /// The store token of a file opened from a path for a sidecar (13 §7): the binding of a file
    /// without an identity, taken at the open.
    /// </summary>
    private static async ValueTask<VortexFile> RememberTokenAsync(ValueTask<VortexFile> open, string path)
    {
        VortexFile file = await open.ConfigureAwait(false);
        Indexes.IndexSidecar.RememberToken(file, path);
        return file;
    }

    /// <summary>Opens a Vortex file over an already-constructed segment source.</summary>
    /// <param name="source">The source. Owned by the file unless <see cref="VortexOpenOptions.LeaveSourceOpen"/>.</param>
    /// <param name="options">Open-time policy.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file. The caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    public static ValueTask<VortexFile> OpenAsync(
        ISegmentSource source, VortexOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        return OpenCoreAsync(source, options, ownsSource: !options.LeaveSourceOpen, cancellationToken);
    }

    /// <summary>
    /// Opens the file, or, when its tail does not parse and the policy allows it, the last whole
    /// version before the tear (13 §12). The source is disposed on failure when the file would own it.
    /// </summary>
    private static async ValueTask<VortexFile> OpenCoreAsync(
        ISegmentSource source, VortexOpenOptions options, bool ownsSource, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                return await OpenTailAsync(source, options, ownsSource, fileLength: -1, cancellationToken).ConfigureAwait(false);
            }
            catch (VortexFormatException torn) when (options.TornTail == VortexTornTailPolicy.ReadPrevious)
            {
                VortexFile? previous = await OpenPreviousAsync(source, options, ownsSource, torn, cancellationToken).ConfigureAwait(false);
                if (previous is null)
                {
                    throw;
                }

                return previous;
            }
        }
        catch
        {
            if (ownsSource)
            {
                try
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ignored: the open is already failing, and its diagnosis is worth more.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// The last whole version of a file whose tail did not parse, or null when the file does not
    /// begin as a Vortex file or no prefix of it opens.
    /// </summary>
    private static async ValueTask<VortexFile?> OpenPreviousAsync(
        ISegmentSource source, VortexOpenOptions options, bool ownsSource, VortexFormatException torn,
        CancellationToken cancellationToken)
    {
        long length = options.FileLength >= 0
            ? options.FileLength
            : await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (!await VortexFileRepair.BeginsAsVortexAsync(source, length, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long end = await VortexFileRepair.PreviousEndAsync(source, length, cancellationToken).ConfigureAwait(false);
        if (end <= 0)
        {
            return null;
        }

        // The prefix owns the source when the file would have: the file disposes the prefix.
        VortexFileRepair.PrefixSource prefix = new VortexFileRepair.PrefixSource(source, end, ownsInner: ownsSource);
        VortexFile file = await OpenTailAsync(prefix, options.ForPrefix(end), ownsSource, end, cancellationToken).ConfigureAwait(false);
        file.TornTail = new VortexTornTail(length, end, torn.Message);
        return file;
    }

    /// <summary>Parses the tail of the file as it stands; leaves the source to the caller on failure.</summary>
    private static async ValueTask<VortexFile> OpenTailAsync(
        ISegmentSource source, VortexOpenOptions options, bool ownsSource, long fileLength, CancellationToken cancellationToken)
    {
        SegmentOwner? tail = null;
        try
        {
            if (fileLength < 0)
            {
                fileLength = options.FileLength >= 0
                    ? options.FileLength
                    : await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
            }

            if (fileLength < VortexFileFormat.EofSize)
            {
                FileThrow.FileTooShort(fileLength);
            }

            int initialReadSize = InitialReadSize(options.InitialReadSize, fileLength);
            long tailOffset = fileLength - initialReadSize;
            tail = await source
                .ReadRangeAsync(tailOffset, initialReadSize, VortexFileFormat.TailAlignment, cancellationToken)
                .ConfigureAwait(false);
            if (tail.Length != initialReadSize)
            {
                FileThrow.ShortRead(tailOffset, initialReadSize, tail.Length);
            }

            PostscriptInfo postscript = ParsePostscript(tail.Buffer.Span, tailOffset, fileLength, options);

            // The second-read rule of PHASE1-CONTRACTS.md §7.1, verbatim in effect from
            // deserializer.rs. Bounded at one extension by construction, but written as a loop and
            // asserted, because "exactly one suffices" is a claim about the arithmetic and not
            // about the file.
            long required = RequiredOffset(in postscript, tailOffset);
            if (required < tailOffset)
            {
                long gap = tailOffset - required;
                if (gap > int.MaxValue - tail.Length)
                {
                    FileThrow.FooterRegionTooLarge(gap);
                }

                SegmentOwner prefix = await source
                    .ReadRangeAsync(required, (int)gap, VortexFileFormat.TailAlignment, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (prefix.Length != (int)gap)
                    {
                        FileThrow.ShortRead(required, (int)gap, prefix.Length);
                    }

                    SegmentOwner combined = Concatenate(prefix, tail);
                    SegmentOwner previous = tail;
                    tail = combined;
                    previous.Release();
                }
                finally
                {
                    prefix.Release();
                }

                tailOffset = fileLength - tail.Length;
                if (tailOffset < 0)
                {
                    FileThrow.BufferLongerThanFile(tail.Length, fileLength);
                }

                if (RequiredOffset(in postscript, tailOffset) < tailOffset)
                {
                    FileThrow.ExtraReadNeeded();
                }
            }

            VortexFile file = Build(source, ownsSource, options, fileLength, tail, tailOffset, in postscript);
            tail = null;
            return file;
        }
        catch
        {
            // Clean-up failures must not replace the diagnosis. A VortexFormatException naming the
            // byte that is wrong is worth more than whatever a doomed unmap says on the way out.
            try
            {
                tail?.Release();
            }
            catch (Exception)
            {
                // Ignored: the open is already failing.
            }

            throw;
        }
    }

    private static int InitialReadSize(int configured, long fileLength)
    {
        // "The effective size is min(max(configured, 65535), fileLength); a caller may raise it,
        // never lower it." PHASE1-CONTRACTS.md §7.1.
        int wanted = Math.Max(configured, VortexFileFormat.InitialReadSize);
        return fileLength < wanted ? (int)fileLength : wanted;
    }

    private static long RequiredOffset(in PostscriptInfo postscript, long windowOffset)
    {
        long required = windowOffset;
        if (postscript.NeedsDTypeSegment)
        {
            required = Math.Min(required, (long)postscript.DTypeSegment.Offset);
        }

        if (postscript.HasStatistics)
        {
            required = Math.Min(required, (long)postscript.StatisticsSegment.Offset);
        }

        required = Math.Min(required, (long)postscript.LayoutSegment.Offset);
        return Math.Min(required, (long)postscript.FooterSegment.Offset);
    }

    private static SegmentOwner Concatenate(SegmentOwner prefix, SegmentOwner suffix)
    {
        NativeSegmentOwner combined = NativeSegmentOwner.Allocate(
            prefix.Length + suffix.Length, VortexFileFormat.TailAlignment);
        try
        {
            Span<byte> destination = combined.WritableSpan;
            prefix.Buffer.Span.CopyTo(destination);
            suffix.Buffer.Span.CopyTo(destination.Slice(prefix.Length));
            return combined;
        }
        catch
        {
            combined.Release();
            throw;
        }
    }

    // ------------------------------------------------------------------------- postscript parse

    /// <summary>
    /// The postscript, flattened out of the FlatBuffer so it survives the <c>await</c> of a second
    /// read. Parsed exactly once: prefixing more data never moves the postscript, which is why
    /// upstream caches it in the deserializer too.
    /// </summary>
    private struct PostscriptInfo
    {
        public bool HasDType;
        public bool NeedsDTypeSegment;
        public SegmentSpec DTypeSegment;
        public SegmentSpec LayoutSegment;
        public bool HasStatistics;
        public SegmentSpec StatisticsSegment;
        public SegmentSpec FooterSegment;
        public string[] MetadataKeys;
        public byte[][] MetadataKeysUtf8;
        public SegmentSpec[] MetadataSegments;
    }

    /// <summary>
    /// PHASE1-CONTRACTS.md §7.3: the EOF marker and the postscript, checked in this order. The
    /// order is the point - <c>postscript_length</c> is used to slice, so every check that could
    /// reject it runs first.
    /// </summary>
    private static PostscriptInfo ParsePostscript(
        ReadOnlySpan<byte> window, long windowOffset, long fileLength, VortexOpenOptions options)
    {
        // 1. The EOF marker must be there at all.
        if (window.Length < VortexFileFormat.EofSize)
        {
            FileThrow.FileTooShort(window.Length);
        }

        int eof = window.Length - VortexFileFormat.EofSize;

        // 2. Magic first, exactly as parse_postscript does: a file that is not ours is diagnosed
        //    as such rather than as an unsupported version.
        ReadOnlySpan<byte> trailingMagic = window.Slice(eof + VortexFileFormat.EofMagicOffset, 4);
        if (!trailingMagic.SequenceEqual(VortexFileFormat.MagicBytes))
        {
            FileThrow.BadTrailingMagic(trailingMagic[0], trailingMagic[1], trailingMagic[2], trailingMagic[3]);
        }

        // 3. Version, exact equality. There is no `<=` forward compatibility.
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(
            window.Slice(eof + VortexFileFormat.EofVersionOffset, 2));
        if (version != VortexFileFormat.Version)
        {
            FileThrow.UnsupportedVersion(version);
        }

        // 4. The declared length, bounded by the format before it is used to slice. Upstream only
        //    checks that the buffer is long enough; catching an over-large length here is cheaper
        //    than discovering it as a short buffer, and is strictly stronger.
        int postscriptLength = BinaryPrimitives.ReadUInt16LittleEndian(
            window.Slice(eof + VortexFileFormat.EofPostscriptLengthOffset, 2));
        if (postscriptLength > VortexFileFormat.MaxPostscriptSize)
        {
            FileThrow.PostscriptTooLarge(postscriptLength);
        }

        // 5. Only now may it index the buffer.
        if (window.Length - VortexFileFormat.EofSize < postscriptLength)
        {
            FileThrow.PostscriptTruncated(postscriptLength, window.Length);
        }

        // The leading 'VTXF' is written but never verified upstream. Verify it when the window
        // happens to reach offset 0 - free, and never a reason for a second read.
        if (windowOffset == 0 && window.Length >= 4)
        {
            ReadOnlySpan<byte> leading = window.Slice(0, 4);
            if (!leading.SequenceEqual(VortexFileFormat.MagicBytes))
            {
                FileThrow.BadLeadingMagic(leading[0], leading[1], leading[2], leading[3]);
            }
        }

        ReadOnlySpan<byte> postscriptBytes = window.Slice(eof - postscriptLength, postscriptLength);

        // 6. The FlatBuffer itself. PostscriptView.Root validates the whole user-metadata vector
        //    eagerly - at most 16 entries, every key present, non-empty, <= 64 UTF-8 BYTES, no two
        //    equal - before any of it can be read (spec/flatbuffers/footer.fbs; upstream's own
        //    test uses repeated "é" to prove the limit is bytes and not characters).
        int tableBudget = VortexLimits.MaxFlatBufferTables;
        PostscriptView view = PostscriptView.Root(postscriptBytes, ref tableBudget);

        PostscriptInfo info = default;

        // `layout` and `footer` are required; `dtype` and `statistics` are optional. Every
        // alignment exponent goes through VortexLimits.CheckAlignmentExponent inside ToSegmentSpec.
        info.LayoutSegment = view.Layout.ToSegmentSpec();
        info.FooterSegment = view.Footer.ToSegmentSpec();
        info.HasDType = view.HasDType;
        if (info.HasDType)
        {
            info.DTypeSegment = view.DType.ToSegmentSpec();
        }

        info.HasStatistics = view.HasStatistics;
        if (info.HasStatistics)
        {
            info.StatisticsSegment = view.Statistics.ToSegmentSpec();
        }

        // PHASE1-CONTRACTS.md §7.4 row 4: no embedded DType and none supplied is malformed, and
        // upstream raises it here, before any offset arithmetic.
        bool supplied = !options.DType.IsDefault;
        if (!info.HasDType && !supplied)
        {
            FileThrow.MissingDType();
        }

        // Row 2 of the same table: a supplied DType WINS. The segment is never read, never parsed,
        // and is excluded from the second-read decision - that is what makes it an I/O saving.
        info.NeedsDTypeSegment = info.HasDType && !supplied;

        int metadataCount = view.MetadataCount;
        info.MetadataKeys = metadataCount == 0 ? Array.Empty<string>() : new string[metadataCount];
        info.MetadataKeysUtf8 = metadataCount == 0 ? Array.Empty<byte[]>() : new byte[metadataCount][];
        info.MetadataSegments = metadataCount == 0 ? Array.Empty<SegmentSpec>() : new SegmentSpec[metadataCount];
        for (int i = 0; i < metadataCount; i++)
        {
            PostscriptMetadataView entry = view.GetMetadata(i);
            ReadOnlySpan<byte> key = entry.KeyUtf8;
            info.MetadataKeysUtf8[i] = key.ToArray();
            info.MetadataKeys[i] = Encoding.UTF8.GetString(key);
            info.MetadataSegments[i] = entry.Segment.ToSegmentSpec();
        }

        // Range-check every postscript segment before any of them is used to slice.
        //
        // PHASE1-CONTRACTS.md §7.3 check 11 asks for an offset-alignment check on ALL of them,
        // calling it "free and strictly stronger" than upstream's metadata-only check. Measured
        // over the corpus it is neither: the postscript's four segments all declare
        // alignment_exponent 3 and their offsets are routinely NOT 8-aligned - 687 of 819 dtype
        // segments, 578 layout, 383 statistics, 548 footer. Applying it there rejects every real
        // file. It IS applied where it holds and where memory safety depends on it: the footer's
        // segment_specs (all 2236 in the corpus are aligned to their own exponent, and buffers are
        // cast out of them) and the metadata segments, which is exactly upstream's rule
        // (vortex-file-0.86.1/src/footer/deserializer.rs). Reported as a contract defect.
        CheckSegmentRange(in info.LayoutSegment, fileLength, "Layout segment", -1);
        CheckSegmentRange(in info.FooterSegment, fileLength, "Footer segment", -1);
        if (info.HasDType)
        {
            CheckSegmentRange(in info.DTypeSegment, fileLength, "DType segment", -1);
        }

        if (info.HasStatistics)
        {
            CheckSegmentRange(in info.StatisticsSegment, fileLength, "Statistics segment", -1);
        }

        for (int i = 0; i < metadataCount; i++)
        {
            CheckSegmentRange(in info.MetadataSegments[i], fileLength, MetadataSegmentName, i);
            CheckSegmentOffsetAlignment(in info.MetadataSegments[i], MetadataSegmentName, i);
        }

        return info;
    }

    private const string MetadataSegmentName = "Metadata segment";

    private const string MapSegmentName = "Segment";

    private static void CheckSegmentRange(in SegmentSpec spec, long fileLength, string what, int index)
    {
        // The exponent is validated for every segment without exception: it is cast into a shift
        // later and PHASE1-CONTRACTS.md §0a C4 forbids a local cap.
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);

        ulong end = spec.End;
        if (end > (ulong)fileLength)
        {
            FileThrow.SegmentPastEndOfFile(what, index, spec.Offset, spec.Length, fileLength);
        }
    }

    private static void CheckSegmentOffsetAlignment(in SegmentSpec spec, string what, int index)
    {
        int alignment = VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);
        if ((spec.Offset & (ulong)(alignment - 1)) != 0)
        {
            FileThrow.SegmentMisaligned(what, index, spec.Offset, alignment);
        }
    }

    // ----------------------------------------------------------------------------- footer parse

    private readonly ref struct OpenState
    {
        public required ISegmentSource Source { get; init; }
        public required bool OwnsSource { get; init; }
        public required SegmentOwner Tail { get; init; }
        public required long TailOffset { get; init; }
        public required long FileLength { get; init; }
        public required VortexReadOptions ReadOptions { get; init; }
        public required DType Schema { get; init; }
        public required DTypeArena DerivedTypes { get; init; }
        public required ScalarStore Scalars { get; init; }
        public required long RowCount { get; init; }
        public required ArrayEncodingId[] ArrayEncodings { get; init; }
        public required IdLocation[] ArrayEncodingIds { get; init; }
        public required LayoutEncodingId[] LayoutEncodings { get; init; }
        public required IdLocation[] LayoutEncodingIds { get; init; }
        public required int SegmentSpecsByteOffset { get; init; }
        public required int SegmentSpecCount { get; init; }
        public required byte[] RootLayoutBytes { get; init; }
        public required FileStatistics? Statistics { get; init; }
        public required string[] MetadataKeys { get; init; }
        public required byte[][] MetadataKeysUtf8 { get; init; }
        public required SegmentSpec[] MetadataSegments { get; init; }
    }

    private static VortexFile Build(
        ISegmentSource source,
        bool ownsSource,
        VortexOpenOptions options,
        long fileLength,
        SegmentOwner tail,
        long tailOffset,
        in PostscriptInfo postscript)
    {
        ReadOnlySpan<byte> window = tail.Buffer.Span;
        DTypeArena derivedTypes = new DTypeArena();
        ScalarStore scalars = new ScalarStore();

        DType schema;
        if (!options.DType.IsDefault)
        {
            schema = options.DType;
        }
        else
        {
            ReadOnlySpan<byte> dtypeBytes =
                SliceSegment(window, tailOffset, in postscript.DTypeSegment, "DType segment");
            schema = DTypeFlatBuffers.Read(dtypeBytes, derivedTypes);
        }

        // --- footer dictionaries and the segment map
        ReadOnlySpan<byte> footerBytes =
            SliceSegment(window, tailOffset, in postscript.FooterSegment, "Footer segment");
        int footerBudget = VortexLimits.MaxFlatBufferTables;
        FooterView footer = FooterView.Root(footerBytes, ref footerBudget);

        // Resolving every declared id at open is PHASE1-CONTRACTS.md §2.3: an id we do not
        // implement maps to Unknown and THAT IS NOT AN ERROR here. The writer pre-populates
        // array_specs with every id its editions permit, so unused and unresolvable entries are
        // routine (40 declared against 36 serialized across the golden corpus).
        int arrayCount = footer.ArraySpecCount;
        ArrayEncodingId[] arrayEncodings =
            arrayCount == 0 ? Array.Empty<ArrayEncodingId>() : new ArrayEncodingId[arrayCount];
        IdLocation[] arrayEncodingIds =
            arrayCount == 0 ? Array.Empty<IdLocation>() : new IdLocation[arrayCount];
        for (int i = 0; i < arrayCount; i++)
        {
            ReadOnlySpan<byte> id = footer.GetArraySpecIdUtf8(i);
            arrayEncodings[i] = EncodingRegistry.ResolveArray(id);
            arrayEncodingIds[i] = IdLocation.Of(window, id);
        }

        int layoutCount = footer.LayoutSpecCount;
        LayoutEncodingId[] layoutEncodings =
            layoutCount == 0 ? Array.Empty<LayoutEncodingId>() : new LayoutEncodingId[layoutCount];
        IdLocation[] layoutEncodingIds =
            layoutCount == 0 ? Array.Empty<IdLocation>() : new IdLocation[layoutCount];
        for (int i = 0; i < layoutCount; i++)
        {
            ReadOnlySpan<byte> id = footer.GetLayoutSpecIdUtf8(i);
            layoutEncodings[i] = EncodingRegistry.ResolveLayout(id);
            layoutEncodingIds[i] = IdLocation.Of(window, id);
        }

        // Absent segment_specs is an error, unlike every other footer vector: upstream
        // "FileLayout missing segment specs" (vortex-file-0.86.1/src/footer/mod.rs).
        ReadOnlySpan<SegmentSpec> specs = footer.SegmentSpecs;
        ValidateSegmentSpecs(specs, fileLength);

        // Reading it triggers the vector's own ceiling check (<= VortexLimits.MaxCompressionSpecs).
        _ = footer.CompressionSpecCount;

        int specsByteOffset = specs.IsEmpty ? 0 : ByteOffsetWithin(window, specs);

        // --- the root layout
        ReadOnlySpan<byte> layoutBytes =
            SliceSegment(window, tailOffset, in postscript.LayoutSegment, "Layout segment");
        int layoutBudget = VortexLimits.MaxFlatBufferTables;
        LayoutView rootLayout = LayoutView.Root(layoutBytes, ref layoutBudget);
        ulong wireRowCount = rootLayout.RowCount;
        if (wireRowCount > long.MaxValue)
        {
            FileThrow.RowCountTooLarge(wireRowCount);
        }

        // Copied, not borrowed. Upstream copies the layout bytes too (FlatBuffer::copy_from in
        // deserializer.rs) and it costs one bounded allocation at open in exchange for a
        // ReadOnlyMemory the layouts component can hold without a MemoryManager over native memory.
        byte[] rootLayoutBytes = layoutBytes.ToArray();

        // --- file statistics
        FileStatistics? statistics = null;
        if (postscript.HasStatistics)
        {
            ReadOnlySpan<byte> statisticsBytes =
                SliceSegment(window, tailOffset, in postscript.StatisticsSegment, "Statistics segment");
            statistics = ParseStatistics(statisticsBytes, schema, derivedTypes, scalars);
        }

        OpenState state = new OpenState
        {
            Source = source,
            OwnsSource = ownsSource,
            Tail = tail,
            TailOffset = tailOffset,
            FileLength = fileLength,
            ReadOptions = options.Read,
            Schema = schema,
            DerivedTypes = derivedTypes,
            Scalars = scalars,
            RowCount = (long)wireRowCount,
            ArrayEncodings = arrayEncodings,
            ArrayEncodingIds = arrayEncodingIds,
            LayoutEncodings = layoutEncodings,
            LayoutEncodingIds = layoutEncodingIds,
            SegmentSpecsByteOffset = specsByteOffset,
            SegmentSpecCount = specs.Length,
            RootLayoutBytes = rootLayoutBytes,
            Statistics = statistics,
            MetadataKeys = postscript.MetadataKeys,
            MetadataKeysUtf8 = postscript.MetadataKeysUtf8,
            MetadataSegments = postscript.MetadataSegments,
        };
        return new VortexFile(in state);
    }

    private static void ValidateSegmentSpecs(ReadOnlySpan<SegmentSpec> specs, long fileLength)
    {
        ulong previous = 0;
        for (int i = 0; i < specs.Length; i++)
        {
            ref readonly SegmentSpec spec = ref specs[i];

            // Non-decreasing, with `<=`, precisely because zero-length segments are legal
            // (vortex-file-0.86.1/src/footer/mod.rs: "this assertion is `<=`").
            if (i > 0 && spec.Offset < previous)
            {
                FileThrow.SegmentsOutOfOrder(i, previous, spec.Offset);
            }

            previous = spec.Offset;
            CheckSegmentRange(in spec, fileLength, MapSegmentName, i);
            CheckSegmentOffsetAlignment(in spec, MapSegmentName, i);
        }
    }

    /// <summary>
    /// Where one encoding id's UTF-8 bytes sit inside the tail window, so the string can be
    /// materialized on demand instead of interned at open.
    /// </summary>
    private readonly struct IdLocation
    {
        private readonly int _offset;
        private readonly int _length;

        private IdLocation(int offset, int length)
        {
            _offset = offset;
            _length = length;
        }

        internal static IdLocation Of(ReadOnlySpan<byte> window, ReadOnlySpan<byte> id)
        {
            if (id.IsEmpty)
            {
                return default;
            }

            ref byte origin = ref MemoryMarshal.GetReference(window);
            ref byte target = ref MemoryMarshal.GetReference(id);
            nint delta = Unsafe.ByteOffset(ref origin, ref target);
            if (delta < 0 || (long)delta + id.Length > window.Length)
            {
                ThrowIdOutsideWindow();
            }

            return new IdLocation((int)delta, id.Length);
        }

        internal string Materialize(ReadOnlySpan<byte> window)
        {
            if (_length == 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(window.Slice(_offset, _length));
        }
    }

    private static int ByteOffsetWithin(ReadOnlySpan<byte> window, ReadOnlySpan<SegmentSpec> specs)
    {
        ref byte origin = ref MemoryMarshal.GetReference(window);
        ref byte target = ref Unsafe.As<SegmentSpec, byte>(ref MemoryMarshal.GetReference(specs));
        nint delta = Unsafe.ByteOffset(ref origin, ref target);
        long bytes = (long)specs.Length * Unsafe.SizeOf<SegmentSpec>();
        if (delta < 0 || (long)delta + bytes > window.Length)
        {
            ThrowSpecsOutsideWindow();
        }

        return (int)delta;
    }

    private static ReadOnlySpan<byte> SliceSegment(
        ReadOnlySpan<byte> window, long windowOffset, in SegmentSpec spec, string what)
    {
        if (spec.Offset < (ulong)windowOffset)
        {
            FileThrow.SegmentBeforeWindow(what, spec.Offset, windowOffset);
        }

        ulong relative = spec.Offset - (ulong)windowOffset;
        if (relative > (ulong)window.Length || (ulong)window.Length - relative < spec.Length)
        {
            FileThrow.SegmentOutsideWindow(what, spec.Offset, spec.Length, window.Length);
        }

        return window.Slice((int)relative, (int)spec.Length);
    }

    // ------------------------------------------------------------------------- file statistics

    private static FileStatistics ParseStatistics(
        ReadOnlySpan<byte> bytes, DType schema, DTypeArena types, ScalarStore scalars)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        FileStatisticsView view = FileStatisticsView.Root(bytes, ref budget);

        // vortex-file-0.86.1/src/footer/file_statistics.rs: a struct root needs exactly one entry
        // per TOP-LEVEL field (shallow - a nested struct gets one entry, its own fields none), and
        // any other root needs exactly one. An absent field_stats vector is therefore an error for
        // everything except a zero-field struct.
        bool isStruct = schema.Kind == DTypeKind.Struct;
        int expected = isStruct ? schema.FieldCount : 1;
        int actual = view.FieldStatsCount;
        if (actual != expected)
        {
            FileThrow.StatisticsFieldCount(actual, expected, isStruct ? "struct" : "non-struct");
        }

        DType[] fieldDTypes = expected == 0 ? Array.Empty<DType>() : new DType[expected];
        DType[] sumDTypes = expected == 0 ? Array.Empty<DType>() : new DType[expected];
        FieldStatistics[] fields =
            expected == 0 ? Array.Empty<FieldStatistics>() : new FieldStatistics[expected];

        for (int i = 0; i < expected; i++)
        {
            DType fieldDType = isStruct ? schema.GetField(i) : schema;
            DType sumDType = SumDType(fieldDType, types);
            fieldDTypes[i] = fieldDType;
            sumDTypes[i] = sumDType;
            fields[i] = ReadFieldStatistics(view.GetFieldStats(i), fieldDType, sumDType, types, scalars);
        }

        return new FileStatistics(fieldDTypes, sumDTypes, fields);
    }

    private static FieldStatistics ReadFieldStatistics(
        ArrayStatsView stats, DType fieldDType, DType sumDType, DTypeArena types, ScalarStore scalars)
    {
        // Stat::dtype(array_dtype) in vortex-array-0.86.1/src/expr/stats/mod.rs returns None for
        // min/max on a Null column and for sum on anything that is not bool/primitive/decimal;
        // upstream then skips the statistic entirely. The boolean and u64 statistics are read
        // straight off the FlatBuffer and are not gated on any dtype.
        bool minMaxTyped = fieldDType.Kind != DTypeKind.Null;

        ScalarValue min = default;
        StatPrecision minPrecision = StatPrecision.Inexact;
        bool hasMin = false;
        ReadOnlySpan<byte> minBytes = stats.MinBytes;
        if (minMaxTyped && !minBytes.IsEmpty)
        {
            ScalarValue value = ScalarProtobuf.ReadValue(minBytes, scalars, types);
            if (!value.IsAbsent)
            {
                minPrecision = CheckPrecision(stats.MinPrecision, "min_precision");
                min = value;
                hasMin = true;
            }
        }

        ScalarValue max = default;
        StatPrecision maxPrecision = StatPrecision.Inexact;
        bool hasMax = false;
        ReadOnlySpan<byte> maxBytes = stats.MaxBytes;
        if (minMaxTyped && !maxBytes.IsEmpty)
        {
            ScalarValue value = ScalarProtobuf.ReadValue(maxBytes, scalars, types);
            if (!value.IsAbsent)
            {
                maxPrecision = CheckPrecision(stats.MaxPrecision, "max_precision");
                max = value;
                hasMax = true;
            }
        }

        ScalarValue sum = default;
        bool hasSum = false;
        ReadOnlySpan<byte> sumBytes = stats.SumBytes;
        if (!sumDType.IsDefault && !sumBytes.IsEmpty)
        {
            ScalarValue value = ScalarProtobuf.ReadValue(sumBytes, scalars, types);
            if (!value.IsAbsent)
            {
                sum = value;
                hasSum = true;
            }
        }

        bool? isSorted = stats.TryGetIsSorted(out bool sorted) ? sorted : null;
        bool? isStrictSorted = stats.TryGetIsStrictSorted(out bool strict) ? strict : null;
        bool? isConstant = stats.TryGetIsConstant(out bool constant) ? constant : null;
        ulong? nullCount = stats.TryGetNullCount(out ulong nulls) ? nulls : null;
        ulong? uncompressed =
            stats.TryGetUncompressedSizeInBytes(out ulong size) ? size : null;
        ulong? nanCount = stats.TryGetNanCount(out ulong nans) ? nans : null;

        return new FieldStatistics(
            min, minPrecision, hasMin,
            max, maxPrecision, hasMax,
            sum, hasSum,
            isSorted, isStrictSorted, isConstant, nullCount, uncompressed, nanCount);
    }

    private static StatPrecision CheckPrecision(StatPrecision precision, string field)
    {
        // "Corrupted min_precision field" upstream: a value other than Exact/Inexact is malformed,
        // not an unknown-but-forward-compatible one.
        if ((byte)precision > (byte)StatPrecision.Exact)
        {
            FileThrow.CorruptedPrecision(field, (byte)precision);
        }

        return precision;
    }

    /// <summary>
    /// The DType a <c>sum</c> statistic is typed against, from
    /// vortex-array-0.86.1/src/aggregate_fn/fns/sum/mod.rs <c>return_dtype</c>. Every result is
    /// nullable because an overflowing sum is recorded as null. <c>default</c> means the field has
    /// no summable DType and the statistic is skipped.
    /// </summary>
    private static DType SumDType(DType fieldDType, DTypeArena types)
    {
        switch (fieldDType.Kind)
        {
            case DTypeKind.Bool:
                return types.Primitive(PType.U64, Nullability.Nullable);

            case DTypeKind.Primitive:
                PType widened = fieldDType.PType switch
                {
                    PType.U8 or PType.U16 or PType.U32 or PType.U64 => PType.U64,
                    PType.I8 or PType.I16 or PType.I32 or PType.I64 => PType.I64,
                    _ => PType.F64,
                };
                return types.Primitive(widened, Nullability.Nullable);

            case DTypeKind.Decimal:
                int precision = Math.Min(DTypeArena.MaxDecimalPrecision, fieldDType.Precision + 10);
                return types.Decimal((byte)precision, fieldDType.Scale, Nullability.Nullable);

            default:
                return default;
        }
    }

    // -------------------------------------------------------------------------- public surface

    /// <summary>
    /// The file's root DType. It may be <em>any</em> DType, not necessarily a struct
    /// (docs/02-format.md §4, docs/07-dotnet-mapping.md §5).
    /// </summary>
    public DType Schema => _schema;

    /// <summary>
    /// The parsed root layout tree, parsed at most once per open file and shared by every scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE TREE IS A FUNCTION OF THE FILE AND NOTHING ELSE, so re-parsing it per
    /// <c>ScanBuilder.ExecuteAsync</c> re-derived the same immutable object from the same
    /// immutable bytes. A caller that scans one open file repeatedly - which
    /// docs/09-contracts.md §1 exists to permit - paid for that every time.
    /// </para>
    /// <para>
    /// The race is benign and deliberately left unlocked: two scans starting at once may both
    /// parse, and because the result is immutable and derived only from <c>_rootLayoutBytes</c>,
    /// either instance is equally correct and the loser is ordinary garbage. A lock here would
    /// serialize the one thing this property exists to make concurrent.
    /// </para>
    /// </remarks>
    internal Layouts.LayoutTree LayoutTree
    {
        get
        {
            Layouts.LayoutTree? tree = Volatile.Read(ref _layoutTree);
            if (tree is not null)
            {
                return tree;
            }

            tree = Layouts.LayoutTree.Parse(this);
            Volatile.Write(ref _layoutTree, tree);
            return tree;
        }
    }

    /// <summary>True when <see cref="Schema"/> is a struct and the file therefore reads as a table.</summary>
    public bool IsTabular => _schema.Kind == DTypeKind.Struct;

    /// <summary>Rows in the file: the root layout's <c>row_count</c>, narrowed once, here.</summary>
    public long RowCount { get; }

    /// <summary>The container format version. Always 1.</summary>
    public int FormatVersion => VortexFileFormat.Version;

    /// <summary>The file length in bytes.</summary>
    public long FileLength { get; }

    /// <summary>
    /// The identity of this version of the file's bytes, or null when the file carries none.
    /// </summary>
    /// <remarks>
    /// Sixteen bytes every postscript this library writes carries, minted anew by every write,
    /// append and post-hoc indexing (docs/13-dataset.md §7), read from the tail the open already
    /// read. A file written by another writer has none. An index or a dataset bound to a file
    /// compares it, and never the file's content, to prove it describes these bytes.
    /// <para>
    /// Computed from the retained tail on every call, so a file that is never asked costs nothing
    /// for it.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public Guid? Identity
    {
        get
        {
            ThrowIfDisposed();
            return FileIdentity.Find(_metadataKeysUtf8, _metadataSegments, _tail.Buffer.Span, _tailOffset);
        }
    }

    /// <summary>
    /// When the file's tail did not parse and the open fell back to the last whole version before
    /// it (<see cref="VortexOpenOptions.TornTail"/>): the file's length, the version's, and why;
    /// <see langword="null"/> for a file that opened as it stands.
    /// </summary>
    /// <remarks>
    /// Such a file reads as the version it was before the torn append, and everything it answers is
    /// that version's: rows, statistics, indexes, <see cref="FileLength"/>. An append or an
    /// indexing pass refuses it; <see cref="VortexFileRepair.RepairAsync"/> truncates the torn bytes.
    /// Kept beside the file rather than in it, so that a file that opened whole pays nothing.
    /// </remarks>
    public VortexTornTail? TornTail
    {
        get => TornTails.TryGetValue(this, out VortexTornTail? torn) ? torn : null;
        private set
        {
            if (value is not null)
            {
                TornTails.AddOrUpdate(this, value);
            }
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VortexFile, VortexTornTail> TornTails = [];

    /// <summary>The arena that owns <see cref="Schema"/>'s nodes. Lives as long as the file.</summary>
    public DTypeArena Types => _schema.Arena;

    /// <summary>The segment source this file reads through.</summary>
    public ISegmentSource Segments => _source;

    /// <summary>Read-time policy, copied into every scan context.</summary>
    public VortexReadOptions ReadOptions { get; }

    /// <summary>Number of entries in the footer's <c>array_specs</c> dictionary.</summary>
    public int ArrayEncodingCount => _arrayEncodings.Length;

    /// <summary>
    /// The resolved array encoding for a <c>u16</c> <c>ArrayNode.encoding</c> index.
    /// <see cref="ArrayEncodingId.Unknown"/> for an id this library does not decode, which is not
    /// an error until a projected column actually needs it (PHASE1-CONTRACTS.md §2.3).
    /// </summary>
    /// <param name="specIndex">The index carried by the array node.</param>
    /// <returns>The resolved id.</returns>
    /// <exception cref="VortexFormatException">
    /// The index is outside the dictionary. It comes from the file, so it is a format error and not
    /// a caller error.
    /// </exception>
    public ArrayEncodingId GetArrayEncoding(int specIndex)
    {
        if ((uint)specIndex >= (uint)_arrayEncodings.Length)
        {
            FileThrow.SpecIndexOutOfRange("ArraySpec", specIndex, _arrayEncodings.Length);
        }

        return _arrayEncodings[specIndex];
    }

    /// <summary>The raw id text of one <c>array_specs</c> entry, for a diagnostic message.</summary>
    /// <param name="specIndex">The index carried by the array node.</param>
    /// <returns>The id as written in the file.</returns>
    /// <remarks>
    /// Materialized on demand rather than at open. FlatBuffers strings may be shared between
    /// tables, so a 300 KB footer can legally point 20 000 spec entries at one 40 KB id and
    /// interning them all at open would allocate 1.6 GB from a file that never touches the disk
    /// twice. Only the UTF-8 location is kept, and only the exception path pays for a string.
    /// </remarks>
    /// <exception cref="VortexFormatException">The index is outside the dictionary.</exception>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public string GetArrayEncodingId(int specIndex)
    {
        ThrowIfDisposed();
        if ((uint)specIndex >= (uint)_arrayEncodingIds.Length)
        {
            FileThrow.SpecIndexOutOfRange("ArraySpec", specIndex, _arrayEncodingIds.Length);
        }

        return _arrayEncodingIds[specIndex].Materialize(_tail.Buffer.Span);
    }

    /// <summary>Number of entries in the footer's <c>layout_specs</c> dictionary.</summary>
    public int LayoutEncodingCount => _layoutEncodings.Length;

    /// <summary>The resolved layout encoding for a <c>u16</c> <c>Layout.encoding</c> index.</summary>
    /// <param name="specIndex">The index carried by the layout node.</param>
    /// <returns>The resolved id, or <see cref="LayoutEncodingId.Unknown"/>.</returns>
    /// <exception cref="VortexFormatException">The index is outside the dictionary.</exception>
    public LayoutEncodingId GetLayoutEncoding(int specIndex)
    {
        if ((uint)specIndex >= (uint)_layoutEncodings.Length)
        {
            FileThrow.SpecIndexOutOfRange("LayoutSpec", specIndex, _layoutEncodings.Length);
        }

        return _layoutEncodings[specIndex];
    }

    /// <summary>The raw id text of one <c>layout_specs</c> entry, for a diagnostic message.</summary>
    /// <param name="specIndex">The index carried by the layout node.</param>
    /// <returns>The id as written in the file.</returns>
    /// <remarks>Materialized on demand; see <see cref="GetArrayEncodingId"/>.</remarks>
    /// <exception cref="VortexFormatException">The index is outside the dictionary.</exception>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public string GetLayoutEncodingId(int specIndex)
    {
        ThrowIfDisposed();
        if ((uint)specIndex >= (uint)_layoutEncodingIds.Length)
        {
            FileThrow.SpecIndexOutOfRange("LayoutSpec", specIndex, _layoutEncodingIds.Length);
        }

        return _layoutEncodingIds[specIndex].Materialize(_tail.Buffer.Span);
    }

    /// <summary>
    /// The segment map, zero-copy over the footer bytes the file keeps alive. Already validated:
    /// non-decreasing by offset, every range inside the file, every offset aligned to its own
    /// exponent.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public ReadOnlySpan<SegmentSpec> SegmentSpecs
    {
        get
        {
            ThrowIfDisposed();
            if (_segmentSpecCount == 0)
            {
                return default;
            }

            return MemoryMarshal.Cast<byte, SegmentSpec>(_tail.Buffer.Span.Slice(
                _segmentSpecsByteOffset, _segmentSpecCount * Unsafe.SizeOf<SegmentSpec>()));
        }
    }

    /// <summary>
    /// The root layout FlatBuffer bytes. Parsed by the layouts component; this component never
    /// interprets a layout.
    /// </summary>
    public ReadOnlyMemory<byte> RootLayoutBytes => _rootLayoutBytes;

    /// <summary>True when the file carries a statistics segment.</summary>
    public bool HasFileStatistics => _statistics is not null;

    /// <summary>The file-level statistics.</summary>
    /// <exception cref="InvalidOperationException">The file carries no statistics segment.</exception>
    public FileStatistics Statistics =>
        _statistics ?? throw new InvalidOperationException(
            "This Vortex file carries no statistics segment; check HasFileStatistics first.");

    /// <summary>Number of user metadata segments named by the postscript.</summary>
    public int MetadataCount => _metadataKeys.Length;

    /// <summary>The key of one user metadata segment, in stored order.</summary>
    /// <param name="index">0-based index, below <see cref="MetadataCount"/>.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public string GetMetadataKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _metadataKeys.Length);
        return _metadataKeys[index];
    }

    /// <summary>Finds a user metadata segment by key, without allocating.</summary>
    /// <param name="keyUtf8">The key as UTF-8 bytes.</param>
    /// <param name="index">The entry's index when found.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryGetMetadataIndex(ReadOnlySpan<byte> keyUtf8, out int index)
    {
        // Linear over at most VortexLimits.MaxMetadataSegments (16) entries: a dictionary keyed on
        // a string would allocate per lookup and hash file-controlled bytes for 16 comparisons.
        byte[][] keys = _metadataKeysUtf8;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keyUtf8.SequenceEqual(keys[i]))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>The locator of one user metadata segment.</summary>
    /// <param name="index">0-based index, below <see cref="MetadataCount"/>.</param>
    /// <returns>The segment locator, already range- and alignment-checked.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public SegmentSpec GetMetadataSegment(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _metadataSegments.Length);
        return _metadataSegments[index];
    }

    /// <summary>
    /// Reads one user metadata value. Metadata values are lazy (docs/02-format.md §2): this issues
    /// a targeted read unless the initial tail already covered the segment, in which case it hands
    /// back a view into the tail buffer and performs no I/O at all.
    /// </summary>
    /// <param name="index">0-based index, below <see cref="MetadataCount"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>An owner the caller releases exactly once.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    public ValueTask<SegmentOwner> ReadMetadataAsync(int index, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _metadataSegments.Length);

        SegmentSpec spec = _metadataSegments[index];
        if (TryViewInTail(in spec, out VortexBuffer view))
        {
            return new ValueTask<SegmentOwner>(new TailSliceSegmentOwner(_tail, view));
        }

        return _source.ReadAsync(spec, cancellationToken);
    }

    private bool TryViewInTail(in SegmentSpec spec, out VortexBuffer view)
    {
        view = default;
        int windowLength = _tail.Length;
        if (spec.Offset < (ulong)_tailOffset)
        {
            return false;
        }

        ulong relative = spec.Offset - (ulong)_tailOffset;
        if (relative > (ulong)windowLength || (ulong)windowLength - relative < spec.Length)
        {
            return false;
        }

        view = spec.Length == 0
            ? VortexBuffer.Empty
            : _tail.Buffer.Slice((int)relative, (int)spec.Length);
        return true;
    }

    /// <summary>
    /// Releases the tail buffer and, unless <see cref="VortexOpenOptions.LeaveSourceOpen"/> was
    /// set, disposes the segment source. Idempotent. Spans previously returned by
    /// <see cref="SegmentSpecs"/> are invalid afterwards.
    /// </summary>
    /// <returns>A task that completes when the file is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tail.Release();
        if (_indexState?.Sidecar is { } sidecar)
        {
            await sidecar.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsSource)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(VortexFile));

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIdOutsideWindow() =>
        throw new VortexFormatException(
            "A footer encoding id does not lie inside the buffer the footer was read from.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSpecsOutsideWindow() =>
        throw new VortexFormatException(
            "The footer's segment_specs vector does not lie inside the buffer it was read from.");
}
