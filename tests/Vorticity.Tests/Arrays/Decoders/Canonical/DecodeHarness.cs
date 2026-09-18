// One decode of one synthetic blob, with the segment kept rooted for as long as the canonical
// nodes that point into it. Contract §2.2 rule 3: spans borrowed from a batch are invalid once the
// batch is disposed, and these tests honour that by keeping the harness alive around every read.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

internal sealed class DecodeHarness : IDisposable
{
    private readonly List<PinnedSegment> _segments = new List<PinnedSegment>();

    internal DecodeHarness() => Scan = new ScanContext(TestEncodings.Ids);

    internal ScanContext Scan { get; }

    internal DTypeArena Types => Scan.Types;

    internal CanonicalArena Canonical => Scan.Canonical;

    /// <summary>Loads <paramref name="root"/> into the node arena and decodes it.</summary>
    internal int Decode(BlobBuilder builder, BlobNode root, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PinnedSegment segment = builder.Build(root);
        _segments.Add(segment);
        ArrayBlobReader.Load(Scan.Nodes, segment.Buffer, Scan.ArrayEncodings);
        return Scan.Decode.Decode(Scan.Nodes.Root, dtype, length);
    }

    internal CanonicalNode Node(int index) => Canonical.GetNode(index);

    public void Dispose() => Scan.Dispose();
}
