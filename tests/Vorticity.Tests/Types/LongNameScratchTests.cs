// The four places that encode a caller's string into a transient buffer take the pool when the
// string is past the 256-byte stack buffer. They rented and returned by hand, with no finally, so
// a throw between the two lost the rental; they take `Scratch<T>` now, whose `using` is the
// finally. These cases drive each one past that boundary, which is what the hand-written pairs
// were never exercised at.
using System;
using System.Linq;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

public sealed class LongNameScratchTests
{
    /// <summary>Past the 256 bytes every one of the four keeps on the stack.</summary>
    private static string Long(char fill) => new string(fill, 300);

    [Fact]
    public void A_field_name_past_the_stack_buffer_is_found()
    {
        DTypeArena types = new DTypeArena();
        string name = Long('f');
        DType schema = types.Struct(
            ["short", name],
            [
                types.Primitive(PType.I64, Nullability.NonNullable),
                types.Primitive(PType.I32, Nullability.NonNullable),
            ],
            Nullability.NonNullable);

        Assert.Equal(1, schema.IndexOfField(name));
        Assert.Equal(0, schema.IndexOfField("short"));
        Assert.Equal(-1, schema.IndexOfField(Long('g')));
    }

    [Fact]
    public void Looking_a_field_up_on_an_unnamed_dtype_throws_and_keeps_going()
    {
        DTypeArena types = new DTypeArena();
        DType primitive = types.Primitive(PType.I64, Nullability.NonNullable);

        // The throw lands between the rent and the return the old code did by hand.
        Assert.Throws<InvalidOperationException>(() => primitive.IndexOfField(Long('h')));

        // The arena is still usable, and so is the pool it borrowed from.
        DType schema = types.Struct(
            [Long('h')], [primitive], Nullability.NonNullable);
        Assert.Equal(0, schema.IndexOfField(Long('h')));
    }

    [Fact]
    public void Interning_a_name_past_the_stack_buffer_is_stable()
    {
        DTypeArena types = new DTypeArena();
        string name = Long('i');

        int first = types.InternName(name);
        int again = types.InternName(name);

        Assert.Equal(first, again);
        Assert.NotEqual(first, types.InternName(Long('j')));
        Assert.Equal(300, types.GetName(first).Length);
    }

    [Fact]
    public void An_extension_id_past_the_stack_buffer_round_trips()
    {
        DTypeArena types = new DTypeArena();
        string id = Long('k');
        DType storage = types.Primitive(PType.I64, Nullability.NonNullable);

        DType extension = types.Extension(id, storage, ReadOnlySpan<byte>.Empty);

        Assert.Equal(DTypeKind.Extension, extension.Kind);
        Assert.Equal(id, extension.ExtensionId);
    }

    [Fact]
    public void A_rejected_extension_id_past_the_stack_buffer_leaves_nothing_interned()
    {
        // The comment on that overload is the reason it encodes before interning: a rejected
        // storage dtype must not leave the id behind. The throw crosses the rented buffer.
        DTypeArena types = new DTypeArena();
        int before = types.NameCount;

        Assert.ThrowsAny<Exception>(() => types.Extension(Long('l'), default, ReadOnlySpan<byte>.Empty));

        Assert.Equal(before, types.NameCount);
    }

    [Fact]
    public void A_string_scalar_past_the_stack_buffer_round_trips()
    {
        ScalarStore store = new ScalarStore();
        string value = Long('m');

        ScalarValue scalar = store.String(value);

        Assert.Equal(ScalarValueKind.String, scalar.Kind);
        Assert.Equal(300, scalar.AsBytes.Length);
        Assert.True(scalar.AsBytes.ToArray().All(b => b == (byte)'m'));
    }
}
