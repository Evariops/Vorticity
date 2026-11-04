using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Indexes;

/// <summary>
/// What one question read and parsed of its files' indexes, kept for its other scans and dropped with it: the scans of a
/// question's parts, an object's each, read its variant directory again, a frame decompressed and walked, some 1.7 times an
/// object of a question of the services under 5xx (2026-11-02). Nothing outlives the question: an object open across
/// questions keeps none of it.
/// </summary>
internal sealed class QuestionReads
{
    // Made at the first directory kept, a question that reads none holding nothing; the dictionary its own lock, the lanes
    // of the question's scans planning their pruning side by side.
    private Dictionary<(VortexFile File, int Field, string Paths), VariantDirectory?>? _directories;

    /// <summary>The variant directory of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="paths"/> the question read, if any.</summary>
    internal bool TryGetDirectory(VortexFile file, int field, string paths, out VariantDirectory? directory)
    {
        directory = null;
        if (Volatile.Read(ref _directories) is not { } kept)
        {
            return false;
        }

        lock (kept)
        {
            return kept.TryGetValue((file, field, paths), out directory);
        }
    }

    /// <summary>Keeps <paramref name="directory"/>, the variant directory of column <paramref name="field"/> of <paramref name="file"/> for <paramref name="paths"/>, for the question's other scans.</summary>
    internal void KeepDirectory(VortexFile file, int field, string paths, VariantDirectory? directory)
    {
        Dictionary<(VortexFile File, int Field, string Paths), VariantDirectory?> kept = Volatile.Read(ref _directories)
            ?? Interlocked.CompareExchange(ref _directories, [], null) ?? _directories!;
        lock (kept)
        {
            kept.TryAdd((file, field, paths), directory);
        }
    }
}
