// The key bytes every value index shares: their order is the total order `KeyOrder.Total` defines,
// and a literal becomes a key only when the conversion is exact.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Keys;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class KeyLayoutTests
{
    // PType is internal, so the theories take its name and parse it.
    public static TheoryData<string> PTypes() =>
    [
        nameof(PType.I8), nameof(PType.I16), nameof(PType.I32), nameof(PType.I64),
        nameof(PType.U8), nameof(PType.U16), nameof(PType.U32), nameof(PType.U64),
        nameof(PType.F16), nameof(PType.F32), nameof(PType.F64),
    ];

    [Theory]
    [MemberData(nameof(PTypes))]
    public void TheByteOrderIsTheTotalOrder(string name)
    {
        PType ptype = Enum.Parse<PType>(name);
        DTypeArena types = new DTypeArena();
        Assert.True(KeyLayout.TryOf(types.Primitive(ptype, Nullability.NonNullable), out KeyLayout layout));
        Random random = new Random(7 + (int)ptype);
        List<(byte[] Bytes, FilterLiteral Literal)> values = [];
        foreach (ulong bits in Specials(ptype))
        {
            values.Add(Value(ptype, bits));
        }

        for (int i = 0; i < 300; i++)
        {
            values.Add(Value(ptype, (ulong)random.NextInt64() ^ ((ulong)random.Next() << 32)));
        }

        foreach ((byte[] a, FilterLiteral la) in values)
        {
            foreach ((byte[] b, FilterLiteral lb) in values)
            {
                Assert.Equal(KeyOrder.Total(la, lb), layout.Compare(a, b));
            }
        }
    }

    [Fact]
    public void StringsOrderBytewise()
    {
        DTypeArena types = new DTypeArena();
        Assert.True(KeyLayout.TryOf(types.Utf8(Nullability.NonNullable), out KeyLayout layout));
        Assert.True(layout.Compare("a"u8, "ab"u8) < 0);
        Assert.True(layout.Compare("b"u8, "ab"u8) > 0);
        Assert.Equal(0, layout.Compare(""u8, ""u8));
        Assert.True(layout.Compare([0xFF], "z"u8) > 0);
    }

    [Fact]
    public void DecimalsBoolsAndNestedKindsHaveNoLayout()
    {
        DTypeArena types = new DTypeArena();
        Assert.False(KeyLayout.TryOf(types.Decimal(10, 2, Nullability.NonNullable), out _));
        Assert.False(KeyLayout.TryOf(types.Bool(Nullability.NonNullable), out _));
        Assert.False(KeyLayout.TryOf(types.Struct(Array.Empty<string>(), [], Nullability.NonNullable), out _));
    }

    public static TheoryData<string, string, bool> Encodings() => new()
    {
        { nameof(PType.I8), "s:127", true },
        { nameof(PType.I8), "s:128", false },
        { nameof(PType.I8), "s:-128", true },
        { nameof(PType.I8), "s:-129", false },
        { nameof(PType.U8), "s:-1", false },
        { nameof(PType.U8), "u:255", true },
        { nameof(PType.U8), "u:256", false },
        { nameof(PType.I64), "u:9223372036854775808", false },
        { nameof(PType.U64), "u:18446744073709551615", true },
        { nameof(PType.I32), "f:5", true },
        { nameof(PType.I32), "f:5.5", false },
        { nameof(PType.I64), "f:9007199254740992", false },
        { nameof(PType.I64), "f:9007199254740991", true },
        { nameof(PType.U32), "f:-1", false },
        { nameof(PType.F32), "f:0.1", false },
        { nameof(PType.F32), "f:0.5", true },
        { nameof(PType.F32), "s:16777217", false },
        { nameof(PType.F64), "s:16777217", true },
        { nameof(PType.F16), "f:65504", true },
        { nameof(PType.F16), "f:0.1", false },
        { nameof(PType.F64), "f:NaN", false },
        { nameof(PType.I32), "b:x", false },
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void ALiteralBecomesAKeyOnlyWhenTheConversionIsExact(string name, string literal, bool encodes)
    {
        PType ptype = Enum.Parse<PType>(name);
        DTypeArena types = new DTypeArena();
        Assert.True(KeyLayout.TryOf(types.Primitive(ptype, Nullability.NonNullable), out KeyLayout layout));
        Span<byte> scratch = stackalloc byte[8];
        Span<byte> zero = stackalloc byte[8];
        Assert.Equal(encodes, layout.TryEncode(Parse(literal), scratch, out ReadOnlySpan<byte> key, zero, out _));
        if (encodes)
        {
            Assert.Equal(ptype.ByteWidth(), key.Length);
        }
    }

    [Theory]
    [InlineData(PType.F16)]
    [InlineData(PType.F32)]
    [InlineData(PType.F64)]
    internal void AFloatZeroAsksForBothZeros(PType ptype)
    {
        DTypeArena types = new DTypeArena();
        Assert.True(KeyLayout.TryOf(types.Primitive(ptype, Nullability.NonNullable), out KeyLayout layout));
        Span<byte> scratch = stackalloc byte[8];
        Span<byte> zero = stackalloc byte[8];
        Assert.True(layout.TryEncode(FilterLiteral.From(0.0), scratch, out ReadOnlySpan<byte> key, zero, out bool other));
        Assert.True(other);
        int width = ptype.ByteWidth();
        Assert.Equal(0, layout.Compare(key, Value(ptype, 0).Bytes));
        Assert.Equal(0, layout.Compare(zero[..width], Value(ptype, 1UL << ((width * 8) - 1)).Bytes));

        Assert.True(layout.TryEncode(FilterLiteral.From(1.0), scratch, out _, zero, out bool none));
        Assert.False(none);
    }

    private static FilterLiteral Parse(string literal)
    {
        string value = literal[2..];
        return literal[0] switch
        {
            's' => FilterLiteral.From(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            'u' => FilterLiteral.From(ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            'f' => FilterLiteral.From(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => FilterLiteral.From(value),
        };
    }

    private static IEnumerable<ulong> Specials(PType ptype)
    {
        int bits = ptype.ByteWidth() * 8;
        ulong sign = 1UL << (bits - 1);
        ulong all = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        yield return 0;
        yield return sign;
        yield return all;
        yield return sign - 1;
        yield return 1;
        yield return sign + 1;
    }

    /// <summary>The key bytes and the literal of the value whose raw bits are <paramref name="bits"/>.</summary>
    private static (byte[] Bytes, FilterLiteral Literal) Value(PType ptype, ulong bits)
    {
        int width = ptype.ByteWidth();
        byte[] bytes = new byte[width];
        Span<byte> all = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(all, bits);
        all[..width].CopyTo(bytes);
        FilterLiteral literal = ptype switch
        {
            PType.I8 => FilterLiteral.From((long)(sbyte)bytes[0]),
            PType.I16 => FilterLiteral.From((long)BinaryPrimitives.ReadInt16LittleEndian(bytes)),
            PType.I32 => FilterLiteral.From((long)BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            PType.I64 => FilterLiteral.From(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
            PType.U8 => FilterLiteral.From((ulong)bytes[0]),
            PType.U16 => FilterLiteral.From((ulong)BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
            PType.U32 => FilterLiteral.From((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes)),
            PType.U64 => FilterLiteral.From(BinaryPrimitives.ReadUInt64LittleEndian(bytes)),
            PType.F16 => FilterLiteral.From((double)BinaryPrimitives.ReadHalfLittleEndian(bytes)),
            PType.F32 => FilterLiteral.From((double)BinaryPrimitives.ReadSingleLittleEndian(bytes)),
            _ => FilterLiteral.From(BinaryPrimitives.ReadDoubleLittleEndian(bytes)),
        };
        return (bytes, literal);
    }
}
