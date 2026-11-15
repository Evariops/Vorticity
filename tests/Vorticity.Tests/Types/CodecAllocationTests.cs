using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;
using static Vorticity.Tests.Types.AllocationTests;

namespace Vorticity.Tests.Types;

/// <summary>
/// No managed allocation per batch in steady state is a rule about the decode path above all, and the three decoders
/// are exactly where it is easy to break: the FlatBuffers reader depends on its pooled blocks plus interning names
/// straight from the file bytes, and both Protobuf readers depend on <c>ProtoScratchList</c> staying a pooled rental.
/// Every one of those regressions round-trips correctly, so nothing else in the suite would notice.
/// </summary>
/// <remarks>
/// Measured alone, as every allocation ceiling that rents from the process-wide pool is. The decoders give their blocks
/// back to one locked stack per size class, which every thread of the process shares; beside the rest of the suite, a
/// neighbour pops the block a read just gave back, and the next read makes a new one. The aggregation's tables rent the
/// same small classes, so the parallel suite reaches it on every run.
/// </remarks>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class CodecAllocationTests
{
    [Fact]
    public void DTypeFlatBuffersReadAllocatesNothing()
    {
        byte[] buffer = DTypeFlatBuffers.Serialize(WideStruct(new DTypeArena()));

        // The arena is deliberately reused rather than reconstructed: node dedup makes the re-read
        // free, which is the steady state of a chunked scan re-reading one schema per chunk.
        // `new DTypeArena()` per call allocates its backing arrays by design (~9 KB) and would
        // measure the arena instead of the codec - do not "fix" this test that way.
        DTypeArena arena = new();
        long bytes = Measure(() => Consume(DTypeFlatBuffers.Read(buffer, arena).NodeIndex));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void DTypeProtobufReadAllocatesNothing()
    {
        byte[] message = DTypeProtobuf.Serialize(WideStruct(new DTypeArena()));

        DTypeArena arena = new();
        long bytes = Measure(() => Consume(DTypeProtobuf.Read(message, arena).NodeIndex));
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    public void ScalarProtobufReadValueAllocatesNothing(int elements)
    {
        ScalarStore source = new();
        ScalarValue[] values = new ScalarValue[elements];
        for (int i = 0; i < elements; i++)
        {
            values[i] = source.Int64(i);
        }

        byte[] message = ScalarProtobuf.SerializeValue(source.List(values));

        ScalarStore store = new();
        DTypeArena dtypes = new();
        long bytes = Measure(() =>
        {
            // Clearing is mandatory here, not tidiness: unlike DTypeArena the store does not
            // deduplicate, so every read appends fresh nodes and its arrays keep doubling. A store
            // left to grow measures its own resizes, not the codec.
            store.Clear();
            Consume(ScalarProtobuf.ReadValue(message, store, dtypes).Kind);
        });

        Assert.Equal(0, bytes);
    }
}
