using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// A boolean column appended a span at a time packs its values straight into the bitmap once the
/// count reaches a whole byte: held to the values appended, whatever the count was before the span,
/// at every span length around a byte and a vector, with booleans whose byte is not 1.
/// </summary>
public sealed class BoolStoreTests
{
    [Fact]
    public void SpansPackIntoTheBitmapAtAnyCount()
    {
        Random random = new Random(20260927);
        foreach (int before in new[] { 0, 1, 3, 7, 8, 9 })
        {
            foreach (int length in new[] { 0, 1, 7, 8, 9, 63, 64, 65, 200 })
            {
                AlignedBufferPool pool = new AlignedBufferPool();
                BoolStore store = new BoolStore(new DTypeArena().Bool(Nullability.NonNullable), pool);
                List<bool> expected = [];
                for (int i = 0; i < before; i++)
                {
                    bool value = random.Next(2) == 0;
                    store.Append(value);
                    expected.Add(value);
                }

                bool[] span = new bool[length];
                for (int i = 0; i < length; i++)
                {
                    // A CLR bool is any byte; true here is sometimes 2, which must still read true.
                    byte raw = (byte)(random.Next(3) switch { 0 => 0, 1 => 1, _ => 2 });
                    span[i] = Unsafe.As<byte, bool>(ref raw);
                    expected.Add(raw != 0);
                }

                store.Append(span);
                CanonicalArena arena = new CanonicalArena();
                int rows = expected.Count;
                CanonicalNode node = arena.GetNode(store.Build(arena, rows));
                ReadOnlySpan<byte> bits = node.Bits.Span;
                for (int i = 0; i < rows; i++)
                {
                    int at = node.BitOffset + i;
                    Assert.True(expected[i] == (((bits[at >> 3] >> (at & 7)) & 1) != 0), $"before {before}, length {length}, row {i}");
                }

                store.Release();
            }
        }
    }
}
