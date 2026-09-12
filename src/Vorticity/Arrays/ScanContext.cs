// Phase 1 contract §2.2 and §8.3: everything one decode flow owns, with one owner and one
// lifetime. A ScanContext is AFFINE TO A SINGLE CONSUMER (docs/09-contracts.md §1) and is never
// shared - WithDegreeOfParallelism gives each concurrent split its own, with its own arenas.
//
// ResetBatch() releases segments BEFORE resetting the arenas. The other order would let a decoder
// that ran during the reset read freed memory.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// Everything one decode flow owns: the three per-batch arenas, the scalar store, the dtype
/// arena, the read options and the file's encoding table.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe, deliberately. Concurrent scans over one open <see cref="VortexFile"/> are
/// supported and expected, and each of them holds its own <see cref="ScanContext"/>.
/// </para>
/// <para>
/// Every index this context hands out - an <see cref="ArrayNodeArena"/> node index, a
/// <see cref="CanonicalArena"/> node index - is meaningful only until the next
/// <see cref="ResetBatch"/>. Never store one in a field or across a batch boundary.
/// </para>
/// </remarks>
public sealed class ScanContext : IDisposable
{
    private readonly VortexFile? _file;
    private readonly ArrayEncodingId[] _arrayEncodings;
    private readonly string[] _arrayEncodingIds;
    private bool _disposed;

    /// <summary>Creates a scan context over an open file.</summary>
    /// <param name="file">The file being scanned; its encoding table and read options are copied in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    public ScanContext(VortexFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        Options = file.ReadOptions;

        int count = file.ArrayEncodingCount;
        _arrayEncodings = count == 0 ? [] : new ArrayEncodingId[count];

        // Deliberately NOT materialized. VortexFile keeps only each id's UTF-8 location for the
        // reason its GetArrayEncodingId documents: FlatBuffers strings may be shared, so a 300 KB
        // footer can legally point 20 000 spec entries at one 40 KB id, and interning them all
        // would allocate 1.6 GB from a file that costs a single read. Interning them HERE instead
        // would simply move that cost onto the first scan - and multiply it by the degree of
        // parallelism, because every lane builds its own context. The four-byte resolved ids below
        // are bounded; the text is fetched from the file only by a throw site.
        _arrayEncodingIds = [];
        for (int i = 0; i < count; i++)
        {
            _arrayEncodings[i] = file.GetArrayEncoding(i);
        }

        Types = new DTypeArena();
        Scalars = new ScalarStore();
        Nodes = new ArrayNodeArena();
        Canonical = new CanonicalArena();
        Segments = new SegmentRequestSet();
        Decode = new ArrayDecodeContext(this);
    }

    /// <summary>
    /// Creates a scan context detached from any file, over an explicit encoding table.
    /// </summary>
    /// <param name="arrayEncodingIds">
    /// The footer's <c>array_specs</c>, in order: entry <c>i</c> is what a node's
    /// <c>encoding == i</c> names. Over-reporting is normal and never an error - the writer
    /// pre-populates every id its edition permits (corpus manifest, last caveat).
    /// </param>
    /// <param name="options">Read-time policy; <see cref="VortexReadOptions.Default"/> when null.</param>
    /// <remarks>
    /// This is the constructor tests and tools use. <see cref="File"/> throws for a context built
    /// this way; everything else behaves identically.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An entry of <paramref name="arrayEncodingIds"/> is null.</exception>
    public ScanContext(ReadOnlySpan<string> arrayEncodingIds, VortexReadOptions? options = null)
    {
        _file = null;
        Options = options ?? VortexReadOptions.Default;

        int count = arrayEncodingIds.Length;
        _arrayEncodings = count == 0 ? [] : new ArrayEncodingId[count];
        _arrayEncodingIds = count == 0 ? [] : new string[count];

        // Hoisted out of the loop: a stackalloc inside one would grow the frame per iteration.
        Span<byte> scratch = stackalloc byte[64];
        for (int i = 0; i < count; i++)
        {
            string id = arrayEncodingIds[i] ?? throw new ArgumentNullException(nameof(arrayEncodingIds));
            _arrayEncodingIds[i] = id;

            // One transcode per entry, once per scan: the ids are tens of entries and the matcher
            // itself never allocates.
            int byteCount = Encoding.UTF8.GetByteCount(id);
            Span<byte> utf8 = byteCount <= scratch.Length ? scratch : new byte[byteCount];
            int written = Encoding.UTF8.GetBytes(id, utf8);
            _arrayEncodings[i] = EncodingRegistry.ResolveArray(utf8[..written]);
        }

        Types = new DTypeArena();
        Scalars = new ScalarStore();
        Nodes = new ArrayNodeArena();
        Canonical = new CanonicalArena();
        Segments = new SegmentRequestSet();
        Decode = new ArrayDecodeContext(this);
    }

    /// <summary><see langword="true"/> when this context was built over a <see cref="VortexFile"/>.</summary>
    public bool HasFile => _file is not null;

    /// <summary>The file being scanned.</summary>
    /// <exception cref="InvalidOperationException">The context was built detached from a file.</exception>
    public VortexFile File => _file ?? ThrowDetached();

    /// <summary>Read-time policy for this scan.</summary>
    public VortexReadOptions Options { get; }

    /// <summary>
    /// The arena every DType derived during decoding is built in: the validity child's
    /// non-nullable <c>Bool</c>, a <c>Primitive(ptype, NonNullable)</c> read out of metadata, and
    /// so on.
    /// </summary>
    /// <remarks>
    /// It is deliberately <em>not</em> the file's arena. A <see cref="DTypeArena"/> mutates when it
    /// is asked for a node it does not already hold, and two concurrent scans deriving into one
    /// arena would race on its arrays. Structural equality and hashing work across arenas, so a
    /// DType built here compares equal to the schema's. It is not cleared per batch: the arena
    /// deduplicates, and the set of derivable dtypes is bounded by the schema's shape.
    /// </remarks>
    public DTypeArena Types { get; }

    /// <summary>
    /// The batch's scalar store. Cleared by <see cref="ResetBatch"/>, so a
    /// <see cref="ScalarValue"/> read out of one batch is meaningless in the next - the same
    /// lifetime rule the arenas follow.
    /// </summary>
    public ScalarStore Scalars { get; }

    /// <summary>One <see cref="ArrayNodeRecord"/> per serialized node, plus the resolved buffer table.</summary>
    public ArrayNodeArena Nodes { get; }

    /// <summary>One record per decoded node.</summary>
    public CanonicalArena Canonical { get; }

    /// <summary>
    /// The <see cref="Vorticity.Buffers.SegmentOwner"/> references for this batch's segments. Exactly one refcount
    /// per segment per batch: the source hands back an owner whose count already includes this
    /// reference, and <see cref="SegmentRequestSet.Release"/> drops it once. Decoders never
    /// <c>Retain</c> or <c>Release</c>.
    /// </summary>
    public SegmentRequestSet Segments { get; }

    /// <summary>The decode facade handed to every decoder.</summary>
    public ArrayDecodeContext Decode { get; }

    /// <summary>Maps a wire <c>u16</c> encoding spec index to a resolved id.</summary>
    public ReadOnlySpan<ArrayEncodingId> ArrayEncodings => _arrayEncodings;

    /// <summary>How many entries the footer's <c>array_specs</c> declared.</summary>
    public int ArrayEncodingCount => _arrayEncodings.Length;

    /// <summary>
    /// The id text of encoding spec <paramref name="specIndex"/>, for a diagnostic message.
    /// </summary>
    /// <remarks>
    /// Materialized on demand, never at construction: see the file-backed constructor. The file
    /// owns its footer window for its whole life, so a throw site can always ask it for one id -
    /// which is exactly what <see cref="VortexFile.GetArrayEncodingId"/> exists to do. Callers on
    /// a decode path must therefore reach the decoder through
    /// <c>ArrayDecoderTable.Require</c>, which asks for the text only when it is about to throw.
    /// </remarks>
    /// <param name="specIndex">The wire <c>u16</c>.</param>
    /// <returns>The id, or a placeholder when the index is outside the declared table.</returns>
    public string GetArrayEncodingIdText(int specIndex)
    {
        if (_file is not null)
        {
            return (uint)specIndex < (uint)_arrayEncodings.Length
                ? _file.GetArrayEncodingId(specIndex)
                : Placeholder(specIndex);
        }

        return (uint)specIndex < (uint)_arrayEncodingIds.Length
            ? _arrayEncodingIds[specIndex]
            : Placeholder(specIndex);
    }

    private static string Placeholder(int specIndex) =>
        "<encoding spec " + specIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">";

    /// <summary>
    /// Releases every segment held for the previous batch and resets every arena.
    /// </summary>
    /// <remarks>
    /// The order is fixed: release segments, then the node arena, then the canonical arena. A
    /// canonical node holds non-owning views into segment memory, so resetting the arenas first
    /// would leave a window in which a live view names released pages.
    /// </remarks>
    public void ResetBatch()
    {
        Segments.Release();
        Nodes.Reset();
        Canonical.Reset();
        Scalars.Clear();
        Decode.ResetBatch();
    }

    /// <summary>Releases the batch's segments and the blocks the canonical arena rented.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Canonical.Reset();
        Segments.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static VortexFile ThrowDetached() =>
        throw new InvalidOperationException(
            "This ScanContext was created without a VortexFile; only its arenas and encoding " +
            "table are available.");
}
