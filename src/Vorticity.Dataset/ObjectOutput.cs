using System;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// One data object being written for a dataset: a core writer over a fresh key in the store, which the dataset completes,
/// puts and describes (<see cref="VortexDataset.SealAsync"/>). What an append writes each partition's rows through, and a
/// compaction and a rewrite their outputs.
/// </summary>
/// <remarks>Disposing an object that was not sealed abandons it: nothing reaches the store.</remarks>
internal sealed class ObjectOutput : IAsyncDisposable
{
    internal ObjectOutput(VortexDataset owner, Guid identity, string key, ObjectSegmentSink sink, VortexFileWriter writer)
    {
        Owner = owner;
        Identity = identity;
        Key = key;
        Sink = sink;
        Writer = writer;
    }

    /// <summary>The key the object will have in the store.</summary>
    internal string Key { get; }

    /// <summary>The writer the rows go through, with the dataset's schema and write options.</summary>
    internal VortexFileWriter Writer { get; }

    /// <summary>The dataset the object is written for.</summary>
    internal VortexDataset Owner { get; }

    /// <summary>The identity minted for the object before a byte of it was written.</summary>
    internal Guid Identity { get; }

    /// <summary>Where the bytes wait until the object is put.</summary>
    internal ObjectSegmentSink Sink { get; }

    /// <summary>
    /// Whether the object is written with no run on the clustering key, its rows coming in key order:
    /// its seal checks that the statistics say so, since a key cursor then walks the column itself.
    /// </summary>
    internal bool BySortedColumn { get; init; }

    /// <summary>Abandons the object: its writer's buffers released, nothing put.</summary>
    public async ValueTask DisposeAsync()
    {
        Writer.Abandon();
        await Writer.DisposeAsync().ConfigureAwait(false);
        Sink.Discard();
    }
}
