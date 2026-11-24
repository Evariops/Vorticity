using System;
using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>
/// What a test watches a writer's chooser with, handed to the writer before its first write (<c>VortexFileWriter.Probes</c>).
/// The writer gives it to each workspace it compresses in, for as long as it holds the workspace, and compresses its chunks
/// on its own flow while one is set, so that a probe sees every chunk, one at a time, and no other writer's. None in
/// production.
/// </summary>
internal sealed class WriteProbes
{
    /// <summary>The probe that runs the reference chooser beside the one that decides, and reports where they differ.</summary>
    internal ColumnCompressor.DifferentialProbe? Differential { get; init; }

    /// <summary>
    /// What sees every list chunk's elements beside the summary the chooser is handed for them: the arena, the elements node,
    /// and that summary, which is absent when the chooser has to walk the elements itself.
    /// </summary>
    internal Action<CanonicalArena, int, BlockStats>? ElementsHanded { get; init; }
}
