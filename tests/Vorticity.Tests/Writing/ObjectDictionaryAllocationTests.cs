// An object's dictionary of a column's values collects them in working arrays from the process's shelf and gives them back:
// once the shelf holds them, a dictionary allocates what it is, its values, their offsets and their hashes, and no more.
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class ObjectDictionaryAllocationTests
{
    /// <summary>
    /// The dictionary of 60 000 texts of 10 000 values: past the first, it allocates its values' bytes, their offsets,
    /// their hashes and its table, and a kibibyte at most beside them, where the collector's heap, its table and its order,
    /// grown as the values came, came new every time.
    /// </summary>
    [Fact]
    public void ADictionaryAllocatesWhatItIsOnceTheShelfHoldsItsWorkingArrays()
    {
        const int Rows = 60_000;
        const int Values = 10_000;
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        VortexBuffer heap = arena.Allocate(Rows * 24, 8, out Span<byte> heapBytes);
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> value = heapBytes.Slice(row * 24, 24);
            Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"text-{row * 7_919 % Values:D19}"), value);
            Span<byte> view = viewBytes.Slice(row * 16, 16);
            MemoryMarshal.Write(view, 24);
            value[..4].CopyTo(view[4..]);
            MemoryMarshal.Write(view[8..], 0);
            MemoryMarshal.Write(view[12..], row * 24);
        }

        int column = arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, [heap]);
        for (int warm = 0; warm < 3; warm++)
        {
            Assert.Equal(Values, ObjectDictionary.Of(arena, column)!.Count);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        ObjectDictionary dictionary = ObjectDictionary.Of(arena, column)!;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long itself = dictionary.ValueBytes + ((Values + 1) * sizeof(int)) + (Values * sizeof(ulong))
            + (System.Numerics.BitOperations.RoundUpToPowerOf2((2 * Values) + 1) * sizeof(int));
        Assert.Equal(Values, dictionary.Count);
        Assert.True(allocated <= itself + 1_024, $"{allocated} bytes allocated by a dictionary of {itself} bytes");
    }
}
