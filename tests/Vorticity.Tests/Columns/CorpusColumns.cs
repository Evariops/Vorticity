// A deliberately minimal, test-only path from a .vortex file to a RecordBatch. The layout
// readers and the scan do this properly; this walks the three layout encodings that get
// us to real decoded values in 496 of the corpus's 819 files - vortex.zoned, vortex.struct and
// vortex.flat - and refuses everything else. It exists because agreeing with our own fixtures
// proves nothing: these values were written by Vortex 0.86.1 and the expectations come from the
// sidecar.
//
// Deliberately NOT supported: vortex.chunked (needs concatenation across chunks) and vortex.dict
// (needs a take). Both are left to the layout readers, and SidecarValues.IsWalkable filters out
// every file that would reach one, along with every file whose array encodings this build does
// not decode.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.Tests.Columns;

/// <summary>One corpus file, opened and decoded into a single <see cref="RecordBatch"/>.</summary>
internal sealed class CorpusColumns : IAsyncDisposable
{
    private readonly VortexFile _file;
    private readonly ScanContext _context;
    private readonly PinnedArraySegmentOwner _layoutBytes;

    private CorpusColumns(VortexFile file, ScanContext context, PinnedArraySegmentOwner layoutBytes, RecordBatch batch)
    {
        _file = file;
        _context = context;
        _layoutBytes = layoutBytes;
        Batch = batch;
    }

    /// <summary>The whole file as one batch.</summary>
    internal RecordBatch Batch { get; }

    /// <summary>The file's schema, straight off the dtype segment.</summary>
    internal DType Schema => _file.Schema;

    /// <summary>The corpus root, located from this source file's compile-time path.</summary>
    internal static string CorpusRoot { get; } = LocateCorpus();

    /// <summary>Absolute path of a corpus entry, e.g. <c>"types/i32_nonnull_r1024"</c>.</summary>
    internal static string PathOf(string entry, string extension) =>
        Path.Combine(CorpusRoot, entry.Replace('/', Path.DirectorySeparatorChar) + extension);

    internal static async Task<CorpusColumns> LoadAsync(string entry)
    {
        DecoderBootstrap.Ensure();
        VortexFile file = await VortexFile.OpenAsync(PathOf(entry, ".vortex")).ConfigureAwait(false);
        PinnedArraySegmentOwner? layoutBytes = null;
        ScanContext? context = null;
        try
        {
            // The FlatBuffers reader reinterprets Layout.segments in place and tests the element
            // address, so the layout blob has to be handed to it aligned.
            layoutBytes = PinnedArraySegmentOwner.CopyOf(file.RootLayoutBytes.Span, 64);
            context = new ScanContext(file);

            string[] layoutIds = new string[file.LayoutEncodingCount];
            for (int i = 0; i < layoutIds.Length; i++)
            {
                layoutIds[i] = file.GetLayoutEncodingId(i);
            }

            // A LayoutView is a ref struct and cannot cross an await (CS4007), so the register
            // and decode walks each open their own root - which is also exactly the shape a scan
            // follows: register, one ReadManyAsync, then a fully synchronous execute.
            RegisterAll(file, context, layoutIds, layoutBytes);
            await file.Segments.ReadManyAsync(context.Segments, CancellationToken.None).ConfigureAwait(false);
            context.Segments.Complete();

            int canonicalRoot = DecodeAll(file, context, layoutIds, layoutBytes);
            RecordBatch batch = new RecordBatch(context, canonicalRoot, 0);
            return new CorpusColumns(file, context, layoutBytes, batch);
        }
        catch
        {
            context?.Dispose();
            layoutBytes?.Dispose();
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Batch.Dispose();
        _context.Dispose();
        _layoutBytes.Dispose();
        await _file.DisposeAsync().ConfigureAwait(false);
    }

    private static void RegisterAll(
        VortexFile file, ScanContext context, string[] layoutIds, PinnedArraySegmentOwner layoutBytes)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        Register(file, context, layoutIds, LayoutView.Root(layoutBytes.Buffer.Span, ref budget), 0);
    }

    private static int DecodeAll(
        VortexFile file, ScanContext context, string[] layoutIds, PinnedArraySegmentOwner layoutBytes)
    {
        int budget = VortexLimits.MaxFlatBufferTables;
        return Decode(
            file, context, layoutIds, LayoutView.Root(layoutBytes.Buffer.Span, ref budget), file.Schema, 0);
    }

    private static void Register(
        VortexFile file, ScanContext context, string[] layoutIds, LayoutView node, int depth)
    {
        if (depth >= VortexLimits.MaxLayoutDepth)
        {
            throw new InvalidDataException("Corpus walk: layout depth exceeded.");
        }

        switch (IdOf(layoutIds, node))
        {
            case "vortex.flat":
                context.Segments.Add(SpecOf(file, node));
                break;
            case "vortex.zoned":
                Register(file, context, layoutIds, node.GetChild(0), depth + 1);
                break;
            case "vortex.struct":
                for (int i = 0; i < node.ChildCount; i++)
                {
                    Register(file, context, layoutIds, node.GetChild(i), depth + 1);
                }

                break;
            default:
                throw new InvalidDataException(
                    $"Corpus walk: layout '{IdOf(layoutIds, node)}' is out of this walker's scope.");
        }
    }

    private static int Decode(
        VortexFile file, ScanContext context, string[] layoutIds, LayoutView node, DType dtype, int depth)
    {
        if (depth >= VortexLimits.MaxLayoutDepth)
        {
            throw new InvalidDataException("Corpus walk: layout depth exceeded.");
        }

        int rows = checked((int)node.RowCount);
        switch (IdOf(layoutIds, node))
        {
            case "vortex.zoned":
                // Child 0 is the data, child 1 the zone map, which only pruning reads.
                return Decode(file, context, layoutIds, node.GetChild(0), dtype, depth + 1);

            case "vortex.flat":
            {
                int slot = context.Segments.Add(SpecOf(file, node));
                VortexBuffer segment = context.Segments.GetBuffer(slot);
                ReadOnlySpan<byte> metadata = node.Metadata;
                if (!metadata.IsEmpty)
                {
                    FlatLayoutMetadata parsed = FlatLayoutMetadata.Read(metadata);
                    if (parsed.HasArrayEncodingTree)
                    {
                        context.Decode.LoadBlob(parsed.ArrayEncodingTree, segment);
                        return context.Decode.DecodeRoot(context.Nodes.Root, dtype, rows);
                    }
                }

                context.Decode.LoadBlob(segment);
                return context.Decode.DecodeRoot(context.Nodes.Root, dtype, rows);
            }

            case "vortex.struct":
            {
                // Validity FIRST when the struct dtype is nullable, then the fields in dtype
                // order. The validity child's dtype is Bool NON-nullable.
                bool nullable = dtype.Nullability == Nullability.Nullable;
                int offset = nullable ? 1 : 0;
                int fields = dtype.FieldCount;
                if (node.ChildCount != fields + offset)
                {
                    throw new InvalidDataException(
                        $"Corpus walk: struct layout has {node.ChildCount} children, expected {fields + offset}.");
                }

                Validity validity = Validity.NonNullable;
                if (nullable)
                {
                    int bits = Decode(
                        file, context, layoutIds, node.GetChild(0),
                        context.Types.Bool(Nullability.NonNullable), depth + 1);
                    validity = Collapse(context, bits, rows);
                }

                int[] children = new int[fields];
                for (int i = 0; i < fields; i++)
                {
                    DType field = dtype.GetField(i);

                    // Map, Union and Variant are out of Phase 1 scope and reach the caller as
                    // VortexUnsupportedException("dtype"). The all-dtypes corpus structs each carry
                    // one map field, so stand a Null node in its place rather than lose the whole
                    // file; SidecarValues skips those fields on the expectation side too.
                    if (field.Kind is DTypeKind.Map or DTypeKind.Union or DTypeKind.Variant)
                    {
                        children[i] = context.Canonical.AddNull(field, rows);
                        continue;
                    }

                    children[i] = Decode(
                        file, context, layoutIds, node.GetChild(i + offset), field, depth + 1);
                }

                return context.Canonical.AddStruct(dtype, rows, validity, children);
            }

            default:
                throw new InvalidDataException(
                    $"Corpus walk: layout '{IdOf(layoutIds, node)}' is out of this walker's scope.");
        }
    }

    /// <summary>An all-true or all-false bitmap collapses to the enum.</summary>
    private static Validity Collapse(ScanContext context, int boolNode, int rows)
    {
        if (rows == 0)
        {
            return Validity.AllValid;
        }

        CanonicalNode bits = context.Canonical.GetNode(boolNode);
        int nulls = RecordBatch.CountClearBits(bits.Bits.Span, bits.BitOffset, rows);
        if (nulls == 0)
        {
            return Validity.AllValid;
        }

        return nulls == rows ? Validity.AllInvalid : Validity.Bitmap(boolNode);
    }

    private static SegmentSpec SpecOf(VortexFile file, LayoutView node)
    {
        ReadOnlySpan<uint> segments = node.Segments;
        if (segments.Length != 1)
        {
            throw new InvalidDataException(
                $"Corpus walk: a vortex.flat layout has {segments.Length} segments, expected 1.");
        }

        ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
        uint index = segments[0];
        if (index >= (uint)specs.Length)
        {
            throw new InvalidDataException($"Corpus walk: segment {index} of {specs.Length}.");
        }

        return specs[(int)index];
    }

    private static string IdOf(string[] layoutIds, LayoutView node)
    {
        ushort encoding = node.Encoding;
        return encoding < layoutIds.Length ? layoutIds[encoding] : "?";
    }

    private static string LocateCorpus([CallerFilePath] string thisFile = "")
    {
        // Walk up from this source file rather than from AppContext.BaseDirectory: the check
        // project that builds this component lives outside the repository.
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "Vorticity.Conformance", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests/Vorticity.Conformance/corpus above '{thisFile}'.");
    }

    /// <summary>Every corpus entry name, without the extension, in a stable order.</summary>
    internal static IEnumerable<string> Entries()
    {
        foreach (string directory in new[] { "containers", "distributions", "editions", "encodings", "types" })
        {
            string full = Path.Combine(CorpusRoot, directory);
            if (!Directory.Exists(full))
            {
                continue;
            }

            string[] files = Directory.GetFiles(full, "*.vortex");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string path in files)
            {
                yield return directory + "/" + Path.GetFileNameWithoutExtension(path);
            }
        }
    }
}
