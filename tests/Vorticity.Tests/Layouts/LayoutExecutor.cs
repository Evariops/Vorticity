// The scan's two-phase execution, reduced to what a layout test needs: plan, register, ONE
// ReadManyAsync, execute. The scan owns the real one; this harness tests the layout readers
// against real files without it.
using System;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.File;
using Vorticity.Layouts;

namespace Vorticity.Tests.Layouts;

internal static class LayoutExecutor
{
    private static int s_registered;

    /// <summary>
    /// Makes sure every decoder is present. <see cref="ArrayDecoderTable"/>'s static constructor
    /// already installs all of them, so every call below is a no-op guarded by
    /// <c>IsImplemented</c> and this method only forces that type initializer to run.
    /// </summary>
    internal static void EnsureDecoders()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1)
        {
            return;
        }

        Register(BitPackedDecoder.Instance);
        Register(ByteBoolDecoder.Instance);
        Register(DictDecoder.Instance);
        Register(FastLanesRleDecoder.Instance);
        Register(ForDecoder.Instance);
        Register(RunEndDecoder.Instance);
        Register(SequenceDecoder.Instance);
        Register(SparseDecoder.Instance);
        Register(ZigZagDecoder.Instance);
    }

    /// <summary>
    /// Whether every array and layout encoding this file uses is one this build decodes. A file
    /// that needs any other encoding is left out of the layout tests rather than failed.
    /// </summary>
    internal static bool IsFullyDecodable(LayoutCorpusEntry entry)
    {
        EnsureDecoders();

        foreach (string id in entry.ArrayIds)
        {
            if (!ArrayDecoderTable.IsImplemented(EncodingRegistry.ResolveArray(Utf8(id))))
            {
                return false;
            }
        }

        foreach (string id in entry.LayoutIds)
        {
            if (!LayoutReaderTable.IsImplemented(EncodingRegistry.ResolveLayout(Utf8(id))))
            {
                return false;
            }
        }

        // CanonicalFill has no zeroed form for Map, Union or Variant, and a ZERO-ROW file of one
        // of those dtypes declares no array ids at all, so the encoding check above cannot see it.
        // The manifest's dtype display is the cheap discriminator.
        if (entry.DType.Contains("map(", StringComparison.Ordinal)
            || entry.DType.Contains("variant", StringComparison.Ordinal)
            || entry.DType.Contains("union", StringComparison.Ordinal))
        {
            return false;
        }

        return entry.HasDTypeSegment;
    }

    private static byte[] Utf8(string id) => System.Text.Encoding.UTF8.GetBytes(id);

    /// <summary>Opens a corpus file with its decoders registered.</summary>
    /// <summary>Opens a FORGED fixture by its path under <c>forged/</c>.</summary>
    /// <param name="relative">e.g. <c>negative/unknown_layout_id.vortex</c>.</param>
    /// <returns>The open file.</returns>
    internal static async ValueTask<VortexFile> OpenForgedAsync(string relative) =>
        await VortexFile.OpenAsync(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(LayoutCorpus.Root)!, "forged", relative));

    internal static async ValueTask<VortexFile> OpenAsync(string id)
    {
        EnsureDecoders();
        return await VortexFile.OpenAsync(LayoutCorpus.FullPath(LayoutCorpus.Find(id)));
    }

    /// <summary>Reads one batch: register, one read, execute.</summary>
    /// <returns>The canonical root's index in <paramref name="context"/>.</returns>
    internal static async ValueTask<int> ReadAsync(
        VortexFile file,
        LayoutTree tree,
        ScanContext context,
        RowRange rows,
        FieldMask fields,
        CancellationToken cancellationToken = default)
    {
        LayoutNode root = tree.Root;
        LayoutReader reader = LayoutReaderTable.Get(root.Encoding, root.EncodingIdText);

        reader.RegisterSegments(in root, rows, in fields, context.Segments);
        await file.Segments.ReadManyAsync(context.Segments, cancellationToken);
        return reader.Execute(in root, rows, in fields, context);
    }

    private static void Register(ArrayDecoder decoder)
    {
        if (!ArrayDecoderTable.IsImplemented(decoder.EncodingId))
        {
            ArrayDecoderTable.Register(decoder);
        }
    }
}
