using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
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
using Vorticity.File;

namespace Vorticity;

/// <summary>
/// An open Vortex file: schema, row count, footer dictionaries and the root layout.
/// </summary>
/// <remarks>
/// <para>
/// Immutable after open and therefore thread-safe: concurrent scans on one open file are supported
/// and expected. The file holds the tail buffer for its whole life because
/// <see cref="SegmentSpecs"/> points into it.
/// </para>
/// <para>
/// Opening costs a length probe, one read of the file's last 65535 bytes, and at most one further
/// read. The tail size is chosen so that it always covers the postscript, whatever the file, so
/// locating the footer never needs a second read. Only a dtype, layout, statistics or footer
/// segment that begins before that window costs one, and it is issued once, for the whole gap, and
/// prepended to the window. Supplying a DType through <see cref="VortexOpenOptions.DType"/> takes
/// the dtype segment out of that decision, which is what makes it a saving. User metadata segments
/// never enter it, because metadata values are read lazily.
/// </para>
/// <para>
/// A scan of a file whose source does I/O reads from that window every segment lying inside it, so
/// a file shorter than the window is read once, by its open. Over such a source the window starts
/// on a 64-byte boundary, which every segment's alignment divides, and so runs up to 63 bytes longer.
/// </para>
/// <para>
/// <b>After disposal</b>, a member throws <see cref="ObjectDisposedException"/> when, and only
/// when, it reads the retained tail, whose buffer has gone back to the pool:
/// <see cref="StoredIdentity"/>, <see cref="SegmentSpecs"/>, <see cref="Indexes"/>,
/// <see cref="GetArrayEncodingId"/>, <see cref="GetLayoutEncodingId"/>,
/// <see cref="ReadMetadataAsync"/> and <see cref="ReadIndexDirectoryAsync"/>. The others answer
/// from state captured at the open -- <see cref="DType"/>, <see cref="RowCount"/>,
/// <see cref="FileLength"/>, <see cref="FileStatistics"/>, the metadata keys and specs -- and go on
/// answering, because the answer is still true and a guard on them would be a branch on a member
/// a scan reads. A scan built from a disposed file fails at its first read instead: its source is
/// gone. The check is made on entry, so it turns a use after the dispose into that exception, not a
/// use that races it: dispose a file once nothing uses it, no scan running and no member being
/// read, as with any disposable. A read that races the dispose may read the tail from a buffer
/// already given back.
/// </para>
/// </remarks>
public sealed partial class VortexFile : IAsyncDisposable
{
    private readonly ISegmentReader _source;
    private readonly bool _ownsSource;

    /// <summary>
    /// Whether the file serves from its tail the segments lying there: when its source fetches its
    /// bytes, or when the tail is the whole file and its source would otherwise map it for a scan.
    /// </summary>
    private readonly bool _servesTail;
    private readonly SegmentOwner _tail;
    private readonly long _tailOffset;
    private readonly DType _schema;
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
        _servesTail = !state.Source.ReadsInPlace || (state.TailOffset == 0 && state.Source is IReadAnticipation);
        _tail = state.Tail;
        _tailOffset = state.TailOffset;
        FileLength = state.FileLength;
        ReadOptions = state.ReadOptions;
        _schema = state.Schema;
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
        VortexSession.Default.OpenAsync(path, null, cancellationToken);

    /// <summary>The session the file was opened in, whose pool, cache and parallelism its scans use.</summary>
    public VortexSession Session { get; internal set; } = VortexSession.Default;

    /// <summary>Opens a Vortex file from a path, in <see cref="VortexSession.Default"/>.</summary>
    /// <param name="path">A local file path.</param>
    /// <param name="options">Open-time policy.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file. The caller disposes it.</returns>
    internal static ValueTask<VortexFile> OpenAsync(
        string path, VortexOpenOptions options, CancellationToken cancellationToken = default) =>
        OpenAsync(path, options, VortexSession.Default, cancellationToken);

    /// <summary>Opens a Vortex file from a path.</summary>
    /// <param name="path">A local file path.</param>
    /// <param name="options">Open-time policy.</param>
    /// <param name="session">The session whose mappings, or whose bound on reads and segment cache, the file reads through.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file. The caller disposes it.</returns>
    /// <remarks>
    /// The open reads the tail positionally. In a session that maps files, the file is mapped when a
    /// scan's plan announces enough bytes to pay for it, or its kept mapping taken over, and from
    /// then on segment reads are zero-copy; in one that does not, every read is positional. The
    /// source created here is always owned by the returned file:
    /// <see cref="VortexOpenOptions.LeaveSourceOpen"/> applies only to a source the caller supplied.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    internal static ValueTask<VortexFile> OpenAsync(
        string path, VortexOpenOptions options, VortexSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ISegmentReader source = session.Options.MapFiles
            ? LocalFileSource.Open(path, session.Mappings)
            : SessionReader.Wrap(new FileSegmentSource(path), session);
        ValueTask<VortexFile> open = OpenCoreAsync(source, options, ownsSource: true, cancellationToken);
        string? tokenPath = options.ReadsIndexFragments ? path : null;
        return tokenPath is null && !options.PreloadIndexes
            ? open
            : FinishOpenAsync(open, tokenPath, options.PreloadIndexes, cancellationToken);
    }

    /// <summary>
    /// What an open does after the tail: the store token of a file opened from a path for its
    /// fragments, which is what binds a file that carries no identity and has to be taken now; then
    /// the index directory when the options preload it.
    /// </summary>
    private static async ValueTask<VortexFile> FinishOpenAsync(
        ValueTask<VortexFile> open, string? tokenPath, bool preload, CancellationToken cancellationToken)
    {
        VortexFile file = await open.ConfigureAwait(false);
        try
        {
            if (tokenPath is not null)
            {
                global::Vorticity.Indexes.IndexContainer.RememberToken(file, tokenPath);
            }

            // A file without a directory is answered without a read, and then describes none.
            if (preload)
            {
                await file.ReadIndexDirectoryAsync(cancellationToken).ConfigureAwait(false);
            }

            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a Vortex file over an already-constructed segment source.</summary>
    /// <param name="source">The source. Owned by the file unless <see cref="VortexOpenOptions.LeaveSourceOpen"/>.</param>
    /// <param name="options">Open-time policy.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open file. The caller disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="VortexFormatException">The file is not a well-formed Vortex file.</exception>
    internal static ValueTask<VortexFile> OpenAsync(
        ISegmentReader source, VortexOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ValueTask<VortexFile> open = OpenCoreAsync(source, options, ownsSource: !options.LeaveSourceOpen, cancellationToken);
        return options.PreloadIndexes ? FinishOpenAsync(open, null, preload: true, cancellationToken) : open;
    }

    /// <summary>
    /// Opens the file, or, when its tail does not parse and the policy allows it, the last whole
    /// version before the tear. The source is disposed on failure when the file would own it.
    /// </summary>
    private static async ValueTask<VortexFile> OpenCoreAsync(
        ISegmentReader source, VortexOpenOptions options, bool ownsSource, CancellationToken cancellationToken)
    {
        options = options.Resolved();
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
    /// begin as a Vortex file, ends with a whole record of another version, or no prefix of it opens.
    /// </summary>
    private static async ValueTask<VortexFile?> OpenPreviousAsync(
        ISegmentReader source, VortexOpenOptions options, bool ownsSource, VortexFormatException torn,
        CancellationToken cancellationToken)
    {
        long length = options.FileLength >= 0
            ? options.FileLength
            : await source.GetLengthAsync(cancellationToken).ConfigureAwait(false);
        if (!await VortexFileRepair.BeginsAsVortexAsync(source, length, cancellationToken).ConfigureAwait(false)
            || await VortexFileRepair.ForeignVersionAsync(source, length, cancellationToken).ConfigureAwait(false) is not null)
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
        ISegmentReader source, VortexOpenOptions options, bool ownsSource, long fileLength, CancellationToken cancellationToken)
    {
        VortexRuntimeChecks.Require();
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

            // A file whose scans are served from the tail reads it from a boundary of the alignment
            // its segments declare, and at that alignment, so that a segment inside keeps it. One
            // whose source reads in place slices only structures out of it, from a boundary of
            // theirs, so that a source in memory hands out a view of it rather than a copy.
            long startMask = source.ReadsInPlace ? -(long)VortexFileFormat.TailAlignment : -(long)VortexLimits.MaxAlignment;
            int alignment = source.ReadsInPlace ? VortexFileFormat.TailAlignment : VortexLimits.MaxAlignment;

            // A file longer than the window is read first over what its source asks for, unless the
            // caller raised the window; the window holds any postscript a shorter read misses.
            int window = InitialReadSize(options.InitialReadSize, fileLength);
            int wanted = window < fileLength && options.InitialReadSize <= VortexOpenOptions.DefaultInitialReadSize
                ? Math.Min(window, source.TailReadSize)
                : window;
            long tailOffset;
            while (true)
            {
                tailOffset = (fileLength - wanted) & startMask;
                int initialReadSize = (int)(fileLength - tailOffset);
                tail = await source
                    .ReadRangeAsync(tailOffset, initialReadSize, alignment, cancellationToken)
                    .ConfigureAwait(false);
                if (tail.Length != initialReadSize)
                {
                    FileThrow.ShortRead(tailOffset, initialReadSize, tail.Length);
                }

                if (wanted == window || HoldsPostscript(tail.Buffer.Span))
                {
                    break;
                }

                SegmentOwner partial = tail;
                tail = null;
                partial.Release();
                wanted = window;
            }

            PostscriptInfo postscript = ParsePostscript(tail.Buffer.Span, tailOffset, fileLength, options);

            // The window is extended at most once, which the arithmetic guarantees; the check after
            // the extension stays because "one suffices" is a claim about that arithmetic and not
            // about the file, and a file may say anything.
            long required = RequiredOffset(in postscript, tailOffset) & startMask;
            if (required < tailOffset)
            {
                long gap = tailOffset - required;
                if (gap > int.MaxValue - tail.Length)
                {
                    FileThrow.FooterRegionTooLarge(gap);
                }

                if (source.ReadsInPlace)
                {
                    // A source that reads in place reads the region whole: a view of its memory, or
                    // one copy where its first read was a copy too, rather than the gap and a third
                    // buffer the two are copied into.
                    int whole = (int)(fileLength - required);
                    SegmentOwner region = await source
                        .ReadRangeAsync(required, whole, alignment, cancellationToken)
                        .ConfigureAwait(false);
                    SegmentOwner previous = tail;
                    tail = region;
                    previous.Release();
                    if (region.Length != whole)
                    {
                        FileThrow.ShortRead(required, whole, region.Length);
                    }
                }
                else
                {
                    SegmentOwner prefix = await source
                        .ReadRangeAsync(required, (int)gap, alignment, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        if (prefix.Length != (int)gap)
                        {
                            FileThrow.ShortRead(required, (int)gap, prefix.Length);
                        }

                        SegmentOwner combined = Concatenate(prefix, tail, alignment);
                        SegmentOwner previous = tail;
                        tail = combined;
                        previous.Release();
                    }
                    finally
                    {
                        prefix.Release();
                    }
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

    /// <summary>Whether <paramref name="window"/> holds the end record and the postscript it declares.</summary>
    private static bool HoldsPostscript(ReadOnlySpan<byte> window) =>
        window.Length >= VortexFileFormat.EofSize
        && window.Length - VortexFileFormat.EofSize >= BinaryPrimitives.ReadUInt16LittleEndian(
            window.Slice(window.Length - VortexFileFormat.EofSize + VortexFileFormat.EofPostscriptLengthOffset, 2));

    private static int InitialReadSize(int configured, long fileLength)
    {
        // A caller may raise the initial read size, never lower it, and it is capped at the file.
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

    private static SegmentOwner Concatenate(SegmentOwner prefix, SegmentOwner suffix, int alignment)
    {
        NativeSegmentOwner combined = NativeSegmentOwner.Allocate(prefix.Length + suffix.Length, alignment);
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
    /// The EOF marker and the postscript, checked in this order. The order is the point:
    /// <c>postscript_length</c> slices the buffer, so every check that could reject it runs first.
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

        // 4. The declared length, bounded by the format before it slices anything. Upstream only
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
        //    eagerly - at most 16 entries, every key present, non-empty, no two equal, and no
        //    longer than 64 bytes, a limit on encoded bytes and not on characters - before any of
        //    it can be read.
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

        // No embedded DType and none supplied is malformed, and it is raised here, before any
        // offset arithmetic.
        bool supplied = !options.DType.IsDefault;
        if (!info.HasDType && !supplied)
        {
            FileThrow.MissingDType();
        }

        // A supplied DType wins over the embedded one. The segment is then never read, never parsed,
        // and excluded from the second-read decision, which is what makes it a saving.
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

        // Range-check every postscript segment before any of them slices the window.
        //
        // Offset alignment is deliberately not checked here. The postscript's four segments all
        // declare an alignment exponent of 3 while their offsets are routinely unaligned in files
        // real writers produce, so enforcing it would reject almost everything. It is enforced
        // where it does hold and where memory safety depends on it: the footer's segment_specs,
        // out of which buffers are cast, and the metadata segments.
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
        // The exponent is validated for every segment without exception, because it is later cast
        // into a shift; the bound belongs with the other resource limits, not in a local cap here.
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
        public required ISegmentReader Source { get; init; }
        public required bool OwnsSource { get; init; }
        public required SegmentOwner Tail { get; init; }
        public required long TailOffset { get; init; }
        public required long FileLength { get; init; }
        public required VortexReadOptions ReadOptions { get; init; }
        public required DType Schema { get; init; }
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
        ISegmentReader source,
        bool ownsSource,
        VortexOpenOptions options,
        long fileLength,
        SegmentOwner tail,
        long tailOffset,
        in PostscriptInfo postscript)
    {
        ReadOnlySpan<byte> window = tail.Buffer.Span;

        // The schema and the sum types of its statistics: nine distinct nodes or fewer for 99
        // files in 100, and the arena grows for the others.
        DTypeArena derivedTypes = new DTypeArena(8);

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

        // Every declared id is resolved at open, and one this library does not implement maps to
        // Unknown rather than failing: a writer pre-populates array_specs with every id its
        // editions permit, so entries nothing in the file uses are routine.
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

        // Absent segment_specs is an error, unlike every other footer vector.
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

        // Copied, not borrowed: one bounded allocation at open buys a ReadOnlyMemory the layouts
        // component can hold without wrapping native memory in a MemoryManager.
        byte[] rootLayoutBytes = layoutBytes.ToArray();

        // --- file statistics
        FileStatistics? statistics = null;
        if (postscript.HasStatistics)
        {
            ReadOnlySpan<byte> statisticsBytes =
                SliceSegment(window, tailOffset, in postscript.StatisticsSegment, "Statistics segment");
            statistics = ParseStatistics(statisticsBytes, schema, derivedTypes);
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

            // Non-decreasing rather than increasing, precisely because zero-length segments are
            // legal and two of them share an offset.
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

    private static FileStatistics ParseStatistics(ReadOnlySpan<byte> bytes, DType schema, DTypeArena types)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        FileStatisticsView view = FileStatisticsView.Root(bytes, ref budget);

        // A struct root needs exactly one entry per top-level field, shallow: a nested struct gets
        // one entry and its own fields none. Any other root needs exactly one. An absent
        // field_stats vector is therefore an error for everything except a zero-field struct.
        bool isStruct = schema.Kind == DTypeKind.Struct;
        int expected = isStruct ? schema.FieldCount : 1;
        int actual = view.FieldStatsCount;
        if (actual != expected)
        {
            FileThrow.StatisticsFieldCount(actual, expected, isStruct ? "struct" : "non-struct");
        }

        DType[] sumDTypes = expected == 0 ? Array.Empty<DType>() : new DType[expected];
        FieldStatistics[] fields =
            expected == 0 ? Array.Empty<FieldStatistics>() : new FieldStatistics[expected];

        // A minimum and a maximum a field: a nested value takes more nodes, and the store grows.
        ScalarStore scalars = new ScalarStore(2 * expected);
        for (int i = 0; i < expected; i++)
        {
            DType fieldDType = isStruct ? schema.GetField(i) : schema;
            DType sumDType = SumDType(fieldDType, types);
            sumDTypes[i] = sumDType;
            fields[i] = ReadFieldStatistics(view.GetFieldStats(i), fieldDType, sumDType, types, scalars);
        }

        return new FileStatistics(schema, sumDTypes, fields);
    }

    private static FieldStatistics ReadFieldStatistics(
        ArrayStatsView stats, DType fieldDType, DType sumDType, DTypeArena types, ScalarStore scalars)
    {
        // A statistic with no DType of its own is skipped entirely, which is the case for min and
        // max on a Null column and for sum on anything that is not bool, primitive or decimal. The
        // boolean and u64 statistics are read straight off the FlatBuffer and are gated on nothing.
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
    /// The DType a <c>sum</c> statistic is typed against: a <c>u64</c> count for a bool, the 64-bit
    /// type of the same family for an integer, <c>f64</c> for a float, and ten more digits of
    /// precision, up to the maximum, for a decimal. Every result is nullable because an overflowing
    /// sum is recorded as null. <c>default</c> means the field has no summable DType and the statistic is skipped.
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
    /// The file's root DType. It may be <em>any</em> DType, not necessarily a struct.
    /// </summary>
    internal DType DType => _schema;

    /// <summary>
    /// The parsed root layout tree, parsed at most once per open file and shared by every scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree is a function of the file and of nothing else, so parsing it once per open file
    /// rather than once per scan costs nothing in correctness and saves the whole parse for a
    /// caller that scans the same open file repeatedly, which is a supported way to use it.
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

    /// <summary>
    /// Tells the file's reader, before a scan reads any data segment, that its plan will read some:
    /// a reader that chooses how to read from it is told, and no other.
    /// </summary>
    /// <param name="share">The share of the file's rows the plan reads, from 0 to 1.</param>
    internal void AnticipateReads(double share)
    {
        // A file the open read whole serves every segment from that read: nothing is left to map.
        if (share > 0 && !_servesTail && _source is IReadAnticipation reader)
        {
            reader.AnticipateData();
        }
    }

    /// <summary>True when <see cref="DType"/> is a struct and the file therefore reads as a table.</summary>
    internal bool IsTabular => _schema.Kind == DTypeKind.Struct;

    /// <summary>Rows in the file: the root layout's <c>row_count</c>, narrowed once, here.</summary>
    public long RowCount { get; }

    /// <summary>The container format version. Always 1.</summary>
    internal int FormatVersion => VortexFileFormat.Version;

    /// <summary>The file length in bytes.</summary>
    internal long FileLength { get; }

    /// <summary>
    /// The identity of this version of the file's bytes, or null when the file carries none.
    /// </summary>
    /// <remarks>
    /// Sixteen bytes every postscript this library writes carries, minted anew by every write,
    /// append and post-hoc indexing, read from the tail the open already read. A file written by
    /// another writer has none. An index or a dataset bound to a file
    /// compares it, and never the file's content, to prove it describes these bytes.
    /// <para>
    /// Computed from the retained tail on every call, so a file that is never asked costs nothing
    /// for it.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    internal Guid? StoredIdentity
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

    /// <summary>The arena that owns <see cref="DType"/>'s nodes. Lives as long as the file.</summary>
    internal DTypeArena Types => _schema.Arena;

    /// <summary>
    /// The reader this file's scans read through: the source, or, when the source fetches its
    /// bytes or would map a file the open read whole, the file itself, which serves from the tail
    /// the open read every segment lying there.
    /// </summary>
    internal ISegmentReader Segments => _servesTail ? this : _source;

    /// <summary>The reader the file was opened over.</summary>
    internal ISegmentReader Source => _source;

    /// <summary>Read-time policy, copied into every scan context.</summary>
    internal VortexReadOptions ReadOptions { get; }

    /// <summary>Number of entries in the footer's <c>array_specs</c> dictionary.</summary>
    internal int ArrayEncodingCount => _arrayEncodings.Length;

    /// <summary>The footer's <c>array_specs</c>, resolved, which an array tree's nodes index.</summary>
    internal ReadOnlySpan<ArrayEncodingId> ResolvedArrayEncodings => _arrayEncodings;

    /// <summary>What scans decoded once and the file keeps for the next, made at the first of them.</summary>
    private DecodedStructures? _decoded;

    /// <summary>
    /// The structures a scan decodes once and the file keeps: the index runs its cursors read, the
    /// zone maps its filters prune with. One holder for both, made when the first is: a file read
    /// without a filter or an index pays for neither, not even a field each.
    /// </summary>
    private sealed class DecodedStructures
    {
        /// <summary>The decoded index runs, made at the first index read.</summary>
        internal Indexes.IndexRunCache? Runs;

        /// <summary>
        /// The zone maps decoded so far: a <see cref="ZoneEntry"/> array of at most
        /// <see cref="FewZones"/>, by the index of their zoned layout node, replaced whole and never
        /// written in place; past them a map by node of every one, a wide file's, filtered on many
        /// columns, which takes the list's place for good.
        /// </summary>
        /// <remarks>One field for both, so that a file filtered on a handful of columns holds no more than the list.</remarks>
        internal object? Zones;
    }

    /// <summary>One decoded zone map: the zoned node it belongs to, and its bounds.</summary>
    private readonly record struct ZoneEntry(int Node, Compute.ZoneColumn Column);

    /// <summary>The zone maps a file keeps in a list, walked, before it keeps them by node in a map.</summary>
    private const int FewZones = 8;

    private DecodedStructures Decoded
    {
        get
        {
            DecodedStructures? decoded = Volatile.Read(ref _decoded);
            if (decoded is null)
            {
                Interlocked.CompareExchange(ref _decoded, new DecodedStructures(), null);
                decoded = _decoded!;
            }

            return decoded;
        }
    }

    /// <summary>The zone map of zoned node <paramref name="node"/>, when a scan has decoded it.</summary>
    /// <param name="node">The zoned layout node's index.</param>
    /// <remarks>
    /// A zone map is a property of the file, so it is read and decoded once for every scan that
    /// filters on the column: the next one reads nothing. A file filtered on a handful of columns
    /// walks a short list; a wide one filtered on many finds each map by its node.
    /// </remarks>
    internal Compute.ZoneColumn? DecodedZones(int node)
    {
        DecodedStructures? decoded = Volatile.Read(ref _decoded);
        if (decoded is null)
        {
            return null;
        }

        object? zones = Volatile.Read(ref decoded.Zones);
        if (zones is ZoneEntry[] entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Node == node)
                {
                    return entries[i].Column;
                }
            }

            return null;
        }

        return zones is ConcurrentDictionary<int, Compute.ZoneColumn> many && many.TryGetValue(node, out Compute.ZoneColumn? found)
            ? found
            : null;
    }

    /// <summary>Keeps the zone map a scan decoded for zoned node <paramref name="node"/>, for the scans after it.</summary>
    /// <param name="node">The zoned layout node's index.</param>
    /// <param name="column">Its bounds.</param>
    /// <remarks>
    /// Two scans that decode the same map at once keep the first: both are the same. The first
    /// <see cref="FewZones"/> maps go to a list copied at every one, the rest to a map by node,
    /// which takes the list's place whole in the one exchange that also keeps the new map: a keep
    /// racing it finds the map on its next try, so nothing the list held is missed. The map is
    /// keyed by node rather than laid out by it, since a file of many chunks has many more nodes
    /// than zoned columns.
    /// </remarks>
    internal void KeepZones(int node, Compute.ZoneColumn column)
    {
        DecodedStructures decoded = Decoded;
        while (true)
        {
            object? seen = Volatile.Read(ref decoded.Zones);
            if (seen is ConcurrentDictionary<int, Compute.ZoneColumn> many)
            {
                many.TryAdd(node, column);
                return;
            }

            ZoneEntry[]? entries = (ZoneEntry[]?)seen;
            int count = entries?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                if (entries![i].Node == node)
                {
                    return;
                }
            }

            object next;
            if (count < FewZones)
            {
                ZoneEntry[] grown = new ZoneEntry[count + 1];
                entries?.CopyTo(grown, 0);
                grown[count] = new ZoneEntry(node, column);
                next = grown;
            }
            else
            {
                ConcurrentDictionary<int, Compute.ZoneColumn> moved = new ConcurrentDictionary<int, Compute.ZoneColumn>(
                    concurrencyLevel: 1, capacity: 4 * FewZones);
                foreach (ZoneEntry entry in entries!)
                {
                    moved.TryAdd(entry.Node, entry.Column);
                }

                moved.TryAdd(node, column);
                next = moved;
            }

            if (Interlocked.CompareExchange(ref decoded.Zones, next, seen) == seen)
            {
                return;
            }
        }
    }

    private int _largestArrayTree;

    /// <summary>
    /// The largest array tree a context of this file has loaded, in bytes, for the contexts made
    /// after it to start their tree buffer at: a lane whose first decode of its own comes late in a
    /// scan then grows nothing in the middle of it.
    /// </summary>
    internal int LargestArrayTree => Volatile.Read(ref _largestArrayTree);

    /// <summary>Records the size of a tree buffer a context grew to.</summary>
    /// <param name="bytes">The buffer's size.</param>
    internal void NoteArrayTree(int bytes)
    {
        int seen = Volatile.Read(ref _largestArrayTree);
        while (bytes > seen)
        {
            int previous = Interlocked.CompareExchange(ref _largestArrayTree, bytes, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    /// <summary>
    /// The resolved array encoding for a <c>u16</c> <c>ArrayNode.encoding</c> index.
    /// <see cref="ArrayEncodingId.Unknown"/> for an id this library does not decode, which is not
    /// an error until a projected column actually needs it.
    /// </summary>
    /// <param name="specIndex">The index carried by the array node.</param>
    /// <returns>The resolved id.</returns>
    /// <exception cref="VortexFormatException">
    /// The index is outside the dictionary. It comes from the file, so it is a format error and not
    /// a caller error.
    /// </exception>
    internal ArrayEncodingId GetArrayEncoding(int specIndex)
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
    internal string GetArrayEncodingId(int specIndex)
    {
        ThrowIfDisposed();
        if ((uint)specIndex >= (uint)_arrayEncodingIds.Length)
        {
            FileThrow.SpecIndexOutOfRange("ArraySpec", specIndex, _arrayEncodingIds.Length);
        }

        return _arrayEncodingIds[specIndex].Materialize(_tail.Buffer.Span);
    }

    /// <summary>Number of entries in the footer's <c>layout_specs</c> dictionary.</summary>
    internal int LayoutEncodingCount => _layoutEncodings.Length;

    /// <summary>The resolved layout encoding for a <c>u16</c> <c>Layout.encoding</c> index.</summary>
    /// <param name="specIndex">The index carried by the layout node.</param>
    /// <returns>The resolved id, or <see cref="LayoutEncodingId.Unknown"/>.</returns>
    /// <exception cref="VortexFormatException">The index is outside the dictionary.</exception>
    internal LayoutEncodingId GetLayoutEncoding(int specIndex)
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
    internal string GetLayoutEncodingId(int specIndex)
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
    internal ReadOnlySpan<SegmentSpec> SegmentSpecs
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
    internal ReadOnlyMemory<byte> RootLayoutBytes => _rootLayoutBytes;

    /// <summary>True when the file carries a statistics segment.</summary>
    internal bool HasFileStatistics => _statistics is not null;

    /// <summary>The file-level statistics.</summary>
    /// <exception cref="InvalidOperationException">The file carries no statistics segment.</exception>
    internal FileStatistics FileStatistics =>
        _statistics ?? throw new InvalidOperationException(
            "This Vortex file carries no statistics segment; check HasFileStatistics first.");

    /// <summary>Number of user metadata segments named by the postscript.</summary>
    internal int MetadataCount => _metadataKeys.Length;

    /// <summary>The key of one user metadata segment, in stored order.</summary>
    /// <param name="index">0-based index, below <see cref="MetadataCount"/>.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    internal string GetMetadataKey(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _metadataKeys.Length);
        return _metadataKeys[index];
    }

    /// <summary>Finds a user metadata segment by key, without allocating.</summary>
    /// <param name="keyUtf8">The key as UTF-8 bytes.</param>
    /// <param name="index">The entry's index when found.</param>
    /// <returns>Whether the key is present.</returns>
    internal bool TryGetMetadataIndex(ReadOnlySpan<byte> keyUtf8, out int index)
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
    internal SegmentSpec GetMetadataSegment(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _metadataSegments.Length);
        return _metadataSegments[index];
    }

    /// <summary>
    /// Reads one user metadata value. Metadata values are lazy: this issues a targeted read unless
    /// the initial tail already covered the segment, in which case it hands back a view into the
    /// tail buffer and performs no reading at all.
    /// </summary>
    /// <param name="index">0-based index, below <see cref="MetadataCount"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>An owner the caller releases exactly once.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The file has been disposed.</exception>
    internal ValueTask<SegmentOwner> ReadMetadataAsync(int index, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _metadataSegments.Length);

        SegmentSpec spec = _metadataSegments[index];
        if (TryViewInTail(in spec, out VortexBuffer view))
        {
            return new ValueTask<SegmentOwner>(new SliceSegmentOwner(_tail, view));
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
    /// Releases the tail buffer and disposes the segment source the file owns, which is every
    /// source handed to an open. Idempotent. A file of its session must be disposed before the
    /// session is.
    /// </summary>
    /// <returns>A task that completes when the file is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Session.Detach(this);
        _tail.Release();
        if (Volatile.Read(ref _indexState) is { } indexes)
        {
            await DisposeIndexSourcesAsync(indexes.Origins).ConfigureAwait(false);
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
