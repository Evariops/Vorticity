// A float column's dictionary is held to what its ALP writes, as an integer column's is to its bit-packing. Priced
// against the plain column alone, 100,000 prices in cents over 262,144 rows took the chunk as a dictionary of 957 KB,
// its codes bit-packed at 17 bits, where ALP writes 558 KB; a few prices are still a dictionary.
using System;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Tests.Columns;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class FloatDictionaryTests
{
    [Fact]
    public void FloatsTheirAlpWritesSmallerAreNotADictionary()
    {
        const int Rows = 262_144;
        using ColumnFixture fixture = new ColumnFixture();
        float[] prices = new float[Rows];
        for (int row = 0; row < Rows; row++)
        {
            prices[row] = ((row * 7919L) % 100_000) * 0.01f;
        }

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, FloatNode(fixture, prices));
        try
        {
            Assert.Equal(ColumnScheme.Alp, plan.Scheme);
        }
        finally
        {
            plan.Release();
        }
    }

    [Fact]
    public void FloatsOfAFewValuesAreStillADictionary()
    {
        const int Rows = 262_144;
        using ColumnFixture fixture = new ColumnFixture();
        float[] menu = [1.99f, 2.49f, 3.75f, 4.10f, 5.95f, 7.25f, 9.99f, 12.5f];
        float[] prices = new float[Rows];
        for (int row = 0; row < Rows; row++)
        {
            prices[row] = menu[(row * 5) % menu.Length];
        }

        ColumnPlan plan = ColumnCompressor.Choose(fixture.Arena, FloatNode(fixture, prices));
        try
        {
            Assert.Equal(ColumnScheme.Dict, plan.Scheme);
            Assert.Equal(menu.Length, plan.Entries);
        }
        finally
        {
            plan.Release();
        }
    }

    private static int FloatNode(ColumnFixture fixture, float[] values) =>
        fixture.Arena.AddPrimitive(
            fixture.Types.Primitive(PType.F32, Nullability.NonNullable), values.Length, Validity.NonNullable, PType.F32,
            fixture.Bytes(MemoryMarshal.AsBytes(values.AsSpan())));
}
