using System;
using System.Globalization;
using System.Threading;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// <c>ToString</c> is what <c>vxdump</c> prints and what a failing assertion shows, so it is part
/// of the contract, not a debugging convenience. Every example of that format is asserted
/// literally here.
/// </summary>
public sealed class DTypeFormatTests
{
    [Fact]
    public void LeafExamplesMatchTheContract()
    {
        DTypeArena a = new();
        Assert.Equal("null", a.Null(Nullability.Nullable).ToString());
        Assert.Equal("bool", a.Bool(Nullability.NonNullable).ToString());
        Assert.Equal("bool?", a.Bool(Nullability.Nullable).ToString());
        Assert.Equal("i32", a.Primitive(PType.I32, Nullability.NonNullable).ToString());
        Assert.Equal("f64?", a.Primitive(PType.F64, Nullability.Nullable).ToString());
        Assert.Equal("decimal(10,2)", a.Decimal(10, 2, Nullability.NonNullable).ToString());
        Assert.Equal("utf8", a.Utf8(Nullability.NonNullable).ToString());
        Assert.Equal("binary?", a.Binary(Nullability.Nullable).ToString());
        Assert.Equal("variant", a.Variant(Nullability.NonNullable).ToString());
    }

    [Fact]
    public void CompositeExamplesMatchTheContract()
    {
        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        DType utf8n = a.Utf8(Nullability.Nullable);
        DType utf8 = a.Utf8(Nullability.NonNullable);
        DType f32 = a.Primitive(PType.F32, Nullability.NonNullable);
        DType i64 = a.Primitive(PType.I64, Nullability.NonNullable);

        Assert.Equal(
            "struct{a: i32, b: utf8?}",
            a.Struct(["a", "b"], [i32, utf8n], Nullability.NonNullable).ToString());
        Assert.Equal("list(i32)", a.List(i32, Nullability.NonNullable).ToString());
        Assert.Equal("fsl(f32, 3)", a.FixedSizeList(f32, 3, Nullability.NonNullable).ToString());
        Assert.Equal("ext(vortex.date, i32)", a.Extension("vortex.date", i32, ReadOnlySpan<byte>.Empty).ToString());
        Assert.Equal("map(utf8, i64)", a.Map(utf8, i64, keysSorted: false, Nullability.NonNullable).ToString());

        int na = a.InternName("a"u8);
        int[] handles = [na];
        DType[] fields = [i32];
        Assert.Equal("union{a: i32 = 0}", a.Union(handles, fields, [(byte)0], Nullability.NonNullable).ToString());
    }

    [Fact]
    public void AWideStructElidesItsFields()
    {
        // "struct{...}?" from the contract: past MaxRenderedFields the field list is elided so a
        // 400-column schema does not drown a test failure. The "?" still lands outside the brace.
        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        int count = DTypeFormatter.MaxRenderedFields + 1;
        string[] names = new string[count];
        DType[] fields = new DType[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = "c" + i.ToString(CultureInfo.InvariantCulture);
            fields[i] = i32;
        }

        Assert.Equal("struct{...}?", a.Struct(names, fields, Nullability.Nullable).ToString());
        Assert.Equal("struct{...}", a.Struct(names, fields, Nullability.NonNullable).ToString());

        // Exactly at the limit the fields are still rendered.
        DType atLimit = a.Struct(
            names.AsSpan(0, DTypeFormatter.MaxRenderedFields),
            fields.AsSpan(0, DTypeFormatter.MaxRenderedFields),
            Nullability.NonNullable);
        Assert.StartsWith("struct{c0: i32,", atLimit.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullabilityGoesOutsideTheBraces()
    {
        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        Assert.Equal("struct{a: i32}?", a.Struct(["a"], [i32], Nullability.Nullable).ToString());
        Assert.Equal("list(i32)?", a.List(i32, Nullability.Nullable).ToString());
        Assert.Equal("fsl(i32, 0)?", a.FixedSizeList(i32, 0, Nullability.Nullable).ToString());
        Assert.Equal("map(i32, i32)?", a.Map(i32, i32, false, Nullability.Nullable).ToString());
        Assert.Equal("variant?", a.Variant(Nullability.Nullable).ToString());
        Assert.Equal("struct{}", a.Struct(ReadOnlySpan<string>.Empty, ReadOnlySpan<DType>.Empty, Nullability.NonNullable).ToString());
    }

    [Fact]
    public void ExtensionShowsNullabilityThroughItsStorage()
    {
        // Extension has no "?" of its own; the storage dtype already carries it.
        DTypeArena a = new();
        DType i32n = a.Primitive(PType.I32, Nullability.Nullable);
        DType ext = a.Extension("vortex.date", i32n, ReadOnlySpan<byte>.Empty);
        Assert.Equal("ext(vortex.date, i32?)", ext.ToString());
        Assert.DoesNotContain("?)?", ext.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultRendersWithoutThrowing() => Assert.Equal("<default>", default(DType).ToString());

    [Fact]
    public void RenderingIsCultureInvariant()
    {
        // A negative decimal scale is the trap: several cultures use U+2212, not '-', as the
        // negative sign, and a few use ',' as the decimal separator.
        CultureInfo hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "!";
        hostile.NumberFormat.NumberDecimalSeparator = ",";
        hostile.NumberFormat.PositiveSign = "@";

        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = hostile;
            DTypeArena a = new();
            DType f32 = a.Primitive(PType.F32, Nullability.NonNullable);
            Assert.Equal("decimal(10,-2)", a.Decimal(10, -2, Nullability.NonNullable).ToString());
            Assert.Equal("decimal(38,-38)", a.Decimal(38, -38, Nullability.NonNullable).ToString());
            Assert.Equal("fsl(f32, 4294967295)", a.FixedSizeList(f32, uint.MaxValue, Nullability.NonNullable).ToString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PathologicalNestingStaysBounded()
    {
        // 64 levels of a 16-field struct is a legal dtype whose full rendering has 16^63 leaves.
        // The renderer must terminate and stay inside its budget.
        DTypeArena a = new();
        DType d = a.Primitive(PType.I8, Nullability.NonNullable);
        string[] names = new string[16];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = "f" + i.ToString(CultureInfo.InvariantCulture);
        }

        DType[] fields = new DType[16];
        for (int level = 1; level < VortexLimits.MaxDTypeDepth; level++)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = d;
            }

            d = a.Struct(names, fields, Nullability.NonNullable);
        }

        string text = d.ToString();
        Assert.True(text.Length <= DTypeFormatter.MaxRenderedLength + 3, $"length was {text.Length}");
        Assert.EndsWith("...", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing caps the length of a struct field name: it is interned verbatim from the file
    /// bytes, so it is attacker-sized. The renderer's budget bounds the OUTPUT, and it must bound
    /// the work and the buffer too — decoding the whole name and clamping afterwards rents (and
    /// decodes) the full length to produce 2051 characters, an allocation sized by a value from
    /// the file with no cap.
    /// </summary>
    [Fact]
    public void AHugeFieldNameIsRenderedWithoutAllocatingItsLength()
    {
        const int NameBytes = 4 * 1024 * 1024;

        byte[] name = new byte[NameBytes];
        Array.Fill(name, (byte)'x');

        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        int[] handles = [a.InternName(name)];
        DType[] fields = [i32];
        DType d = a.Struct(handles, fields, Nullability.NonNullable);

        // Warm the render path on a SMALL dtype: enough to JIT it, but it must not put a
        // name-sized array into ArrayPool<char>.Shared, which would hide the very rental this
        // measures. For the same reason no other test may render a name in this size class.
        DType warm = a.Struct(["a"], [i32], Nullability.NonNullable);
        for (int i = 0; i < 20; i++)
        {
            _ = warm.ToString();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        string text = d.ToString();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            text.Length <= DTypeFormatter.MaxRenderedLength + 3,
            $"length was {text.Length.ToString(CultureInfo.InvariantCulture)}");
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.True(
            allocated < 64 * 1024,
            $"rendering a {NameBytes.ToString(CultureInfo.InvariantCulture)}-byte name allocated " +
            $"{allocated.ToString(CultureInfo.InvariantCulture)} bytes");
    }

    /// <summary>
    /// The other half of the same rule: the cap is on the RENTAL, never on the input bytes. A
    /// multi-byte name that decodes to fewer characters than the budget must still render whole —
    /// clamping the input to `budget` bytes would cut it short, mangle the sequence it lands in
    /// and append a "..." that is not true.
    /// </summary>
    [Fact]
    public void AMultiByteNameUnderTheBudgetIsNotTruncated()
    {
        const int Chars = 1000;   // 3000 UTF-8 bytes, well over MaxRenderedLength bytes

        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        string name = new string('\u4E2D', Chars);
        int[] handles = [a.InternName(name)];
        DType[] fields = [i32];

        string text = a.Struct(handles, fields, Nullability.NonNullable).ToString();

        Assert.Equal("struct{" + name + ": i32}", text);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.DoesNotContain("...", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NonUtf8FieldNamesDoNotThrow()
    {
        // Field names come from a file. An invalid UTF-8 sequence must degrade to U+FFFD rather
        // than turning a diagnostic into a second exception.
        DTypeArena a = new();
        DType i32 = a.Primitive(PType.I32, Nullability.NonNullable);
        byte[] invalid = [0xC3, 0x28];
        int handle = a.InternName(invalid);
        int[] handles = [handle];
        DType[] fields = [i32];

        string text = a.Struct(handles, fields, Nullability.NonNullable).ToString();

        Assert.StartsWith("struct{", text, StringComparison.Ordinal);
        Assert.EndsWith(": i32}", text, StringComparison.Ordinal);
    }
}
