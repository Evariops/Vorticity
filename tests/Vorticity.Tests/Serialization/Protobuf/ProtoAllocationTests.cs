using System;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Protobuf;
using Xunit;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// docs/03-architecture.md §1 and §4: the parse path allocates nothing. Metadata messages are
/// decoded once per array node per chunk, so a single boxed enum or a defensive copy here shows up
/// as per-batch garbage on a wide scan.
/// </summary>
public sealed class ProtoAllocationTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ParseEverything(ReadOnlySpan<byte> body)
    {
        ulong accumulator = 0;

        ProtoReader reader = new ProtoReader(body);
        while (reader.TryReadTag(out int fieldNumber, out ProtoWireType wireType))
        {
            switch (fieldNumber)
            {
                case 1:
                    accumulator += reader.ReadVarint32();
                    break;
                case 2:
                    accumulator += unchecked((ulong)reader.ReadSInt64());
                    break;
                case 3:
                    accumulator += BitConverter.DoubleToUInt64Bits(reader.ReadDouble());
                    break;
                case 4:
                    accumulator += (ulong)reader.ReadLengthDelimited().Length;
                    break;
                case 5:
                    {
                        ProtoReader nested = reader.ReadMessage();
                        while (nested.TryReadTag(out _, out ProtoWireType nestedWireType))
                        {
                            nested.SkipField(nestedWireType);
                            accumulator++;
                        }

                        break;
                    }

                default:
                    reader.SkipField(wireType);
                    accumulator++;
                    break;
            }
        }

        return accumulator;
    }

    [Fact]
    public void Parsing_a_metadata_message_allocates_nothing()
    {
        byte[] body = ProtoTestHelpers.Write((ref ProtoWriter w) =>
        {
            w.WriteUInt32Always(1, 1024);
            w.WriteSInt64Always(2, -987654321);
            w.WriteDoubleAlways(3, 2.5);
            w.WriteBytesAlways(4, ProtoTestHelpers.Payload(64));
            using (ProtoWriter.MessageScope scope = w.BeginMessage(5))
            {
                w.WriteUInt32Always(1, 1);
                w.WriteFloatAlways(2, 1f);
            }

            w.WriteUInt64Always(600, ulong.MaxValue);   // an unknown field, skipped
        });

        // Warm up so the JIT has settled before the measured window.
        ulong warm = 0;
        for (int i = 0; i < 64; i++)
        {
            warm += ParseEverything(body);
        }

        Assert.NotEqual(0UL, warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong measured = 0;
        for (int i = 0; i < 1000; i++)
        {
            measured += ParseEverything(body);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(0UL, measured);
        Assert.Equal(0L, allocated);
    }
}
