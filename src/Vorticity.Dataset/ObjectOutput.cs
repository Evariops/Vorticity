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
    /// The tree's keys of the object's first and last rows (<see cref="DatasetSchema.Tree"/>), which its writer took them
    /// in the order of: what its tree key leads with and its entry's last key; null when the dataset has no key, the
    /// object's position leading its tree key then.
    /// </summary>
    internal (byte[] First, byte[] Last)? Keys { get; set; }

    /// <summary>The partition the object's rows lie in, row-encoded, which leads its tree key before its position; null for none.</summary>
    internal byte[]? Partition { get; set; }

    /// <summary>When the object's rows arrived, in seconds since the Unix epoch; 0 for when it is sealed.</summary>
    internal long Arrival { get; set; }

    /// <summary>Abandons the object: its writer's buffers released, nothing put.</summary>
    public async ValueTask DisposeAsync()
    {
        Writer.Abandon();
        await Writer.DisposeAsync().ConfigureAwait(false);
        Sink.Discard();
    }
}
