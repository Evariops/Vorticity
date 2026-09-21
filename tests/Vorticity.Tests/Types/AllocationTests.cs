using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// "Structural equality and hashing, allocation-free" is a stated requirement of the type model,
/// and the read paths a parser uses must not allocate once the arena has stopped growing. These
/// assertions measure it rather than trusting the comment.
/// </summary>
public sealed class AllocationTests
{
    private const int Warmup = 200;
    private const int Iterations = 500;

    /// <summary>Runs <paramref name="body"/> after a warm-up and returns the bytes it allocated.</summary>
    private static long Measure(Action body)
    {
        for (int i = 0; i < Warmup; i++)
        {
            body();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            body();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static (DTypeArena A, DTypeArena B, DType X, DType Y) TwoArenas()
    {
        DTypeArena a = new();
        DTypeArena b = new();
        return (a, b, Build(a), Build(b));

        static DType Build(DTypeArena arena)
        {
            DType i64 = arena.Primitive(PType.I64, Nullability.NonNullable);
            DType utf8 = arena.Utf8(Nullability.Nullable);
            DType inner = arena.Struct(["k", "v"], [utf8, i64], Nullability.NonNullable);
            DType list = arena.List(inner, Nullability.Nullable);
            DType ext = arena.Extension("vortex.timestamp", i64, [3, 0]);
            return arena.Struct(["id", "rows", "ts"], [i64, list, ext], Nullability.NonNullable);
        }
    }

    /// <summary>
    /// Both handles are a reference plus two ints, and both were already padded to two words by the
    /// reference's alignment. Anything that pushes either past 16 bytes makes every schema array,
    /// every statistics table and every copy of a handle cost half again as much.
    /// </summary>
    [Fact]
    public void HandlesAreTwoWords()
    {
        Assert.Equal(16, Unsafe.SizeOf<DType>());
        Assert.Equal(16, Unsafe.SizeOf<ScalarValue>());
    }

    [Fact]
    public void CrossArenaEqualityAllocatesNothing()
    {
        (_, _, DType x, DType y) = TwoArenas();
        long bytes = Measure(() => Consume(x.Equals(y)));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void StructuralHashingAllocatesNothing()
    {
        (_, _, DType x, DType y) = TwoArenas();
        long bytes = Measure(() =>
        {
            Consume(x.GetHashCode());
            Consume(y.GetHashCode());
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void FieldLookupByUtf8AllocatesNothing()
    {
        (_, _, DType x, _) = TwoArenas();
        long bytes = Measure(() =>
        {
            Consume(x.IndexOfField("rows"u8));
            Consume(x.IndexOfField("absent-name"u8));
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void ChildAndNameTraversalAllocatesNothing()
    {
        (_, _, DType x, _) = TwoArenas();
        long bytes = Measure(() =>
        {
            for (int i = 0; i < x.ChildCount; i++)
            {
                Consume(x.GetChild(i).Kind);
                Consume(x.GetFieldNameUtf8(i).Length);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void InterningAKnownNameAllocatesNothing()
    {
        // The parse path interns every field name of every schema; a hit must be free.
        DTypeArena arena = new();
        arena.InternName("already_here"u8);
        long bytes = Measure(() => Consume(arena.InternName("already_here"u8)));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void RebuildingAnExistingNodeAllocatesNothing()
    {
        // A chunked file re-reads the same schema per chunk. Dedup must make that free once the
        // arena has seen it, otherwise the arena grows without bound over a long scan.
        DTypeArena arena = new();
        DType i64 = arena.Primitive(PType.I64, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        int k = arena.InternName("k"u8);
        int v = arena.InternName("v"u8);
        int[] names = [k, v];
        DType[] fields = [utf8, i64];

        long bytes = Measure(() =>
        {
            Consume(arena.Primitive(PType.I64, Nullability.NonNullable).NodeIndex);
            Consume(arena.List(i64, Nullability.Nullable).NodeIndex);
            Consume(arena.Struct(names, fields, Nullability.NonNullable).NodeIndex);
        });

        Assert.Equal(0, bytes);
        // And the arena really did stop growing.
        int before = arena.NodeCount;
        arena.Struct(names, fields, Nullability.NonNullable);
        Assert.Equal(before, arena.NodeCount);
    }

    [Fact]
    public void ScalarEqualityAndHashingAllocateNothing()
    {
        ScalarStore a = new();
        ScalarStore b = new();
        ScalarValue x = a.List([a.Int64(1), a.String("z"u8), a.F64(2.5)]);
        ScalarValue y = b.List([b.Int64(1), b.String("z"u8), b.F64(2.5)]);

        long bytes = Measure(() =>
        {
            Consume(x.Equals(y));
            Consume(x.GetHashCode());
            Consume(x.GetListElement(1).AsBytes.Length);
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void ToStringAllocatesButStaysBounded()
    {
        // ToString is the one member allowed to allocate; it should still be a handful of objects,
        // not one per node.
        (_, _, DType x, _) = TwoArenas();
        string rendered = x.ToString();
        Assert.Equal(
            "struct{id: i64, rows: list(struct{k: utf8?, v: i64})?, ts: ext(vortex.timestamp, i64)}",
            rendered);

        long bytes = Measure(() => Consume(x.ToString().Length));
        long perCall = bytes / Iterations;
        Assert.True(perCall < 1024, $"ToString allocated {perCall} bytes per call");
    }

    // ------------------------------------------------------------------ codec parse paths

    /// <summary>
    /// A 100-field struct: wide enough that the pooled scratch the codecs use for the parallel
    /// name/dtype vectors actually has to grow, which is where a <c>List&lt;T&gt;</c> or an
    /// intermediate <c>string</c> per field would show up.
    /// </summary>
    private static DType WideStruct(DTypeArena arena)
    {
        const int Fields = 100;
        string[] names = new string[Fields];
        DType[] fields = new DType[Fields];
        DType i32 = arena.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8 = arena.Utf8(Nullability.Nullable);
        for (int i = 0; i < Fields; i++)
        {
            names[i] = "column_" + i.ToString(CultureInfo.InvariantCulture);
            fields[i] = i % 2 == 0 ? i32 : utf8;
        }

        return arena.Struct(names, fields, Nullability.NonNullable);
    }

    /// <summary>
    /// No managed allocation per batch in steady state is a rule about the decode path above all,
    /// and the three decoders are exactly where it is easy to break: the FlatBuffers reader
    /// depends on two <c>ArrayPool</c> rentals plus interning names straight from the file bytes,
    /// and both Protobuf readers depend on <c>ProtoScratchList</c> staying a pooled rental. Every
    /// one of those regressions round-trips correctly, so nothing else in the suite would notice.
    /// </summary>
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume<T>(T value) => _ = value;

    [Fact]
    public void MeasureItselfDetectsAnAllocation()
    {
        // Guards against the measurement silently returning zero for everything.
        long bytes = Measure(() => Consume(new object()));
        Assert.True(bytes > 0, $"expected allocations, measured {bytes.ToString(CultureInfo.InvariantCulture)}");
    }
}
