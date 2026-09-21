using System;
using System.Collections.Generic;
using Vorticity.Expressions;
using Xunit;

namespace Vorticity.Tests.Indexes;

/// <summary>
/// <see cref="FilterLiteral"/> is a dictionary key: the pruner puts the literals of an <c>IN</c>
/// into one to find each one's slot, and the collector puts them into a
/// set to drop the repeats. Both degrade to a linear scan when the hash cannot separate them, and a
/// key column's literals are identifiers, all of one width.
/// </summary>
public sealed class FilterLiteralHashTests
{
    [Fact]
    public void Byte_strings_of_one_width_do_not_share_a_hash_code()
    {
        HashSet<int> codes = [];
        for (int i = 0; i < 4_096; i++)
        {
            codes.Add(FilterLiteral.From(Identifier(i)).GetHashCode());
        }

        // Not "all distinct": a thirty-two bit hash over four thousand values collides by the
        // birthday bound alone. What matters is that it separates them at all.
        Assert.True(codes.Count > 4_000, $"4096 distinct identifiers produced {codes.Count} hash codes");
    }

    [Fact]
    public void Equal_byte_strings_still_agree_on_their_hash_code()
    {
        FilterLiteral one = FilterLiteral.From(Identifier(7));
        FilterLiteral other = FilterLiteral.From(Identifier(7));

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void A_text_literal_hashes_on_its_text()
    {
        HashSet<int> codes =
        [
            FilterLiteral.From("alpha").GetHashCode(),
            FilterLiteral.From("bravo").GetHashCode(),
            FilterLiteral.From("charlie").GetHashCode(),
        ];

        Assert.Equal(3, codes.Count);
        Assert.Equal(
            FilterLiteral.From("alpha").GetHashCode(), FilterLiteral.From("alpha").GetHashCode());
    }

    [Fact]
    public void Literals_of_other_kinds_keep_agreeing_with_equality()
    {
        Assert.Equal(FilterLiteral.From(42L).GetHashCode(), FilterLiteral.From(42L).GetHashCode());
        Assert.NotEqual(FilterLiteral.From(42L).GetHashCode(), FilterLiteral.From(43L).GetHashCode());
        Assert.Equal(FilterLiteral.From(true).GetHashCode(), FilterLiteral.From(true).GetHashCode());
    }

    /// <summary>Sixteen bytes that differ only in their content, as identifiers do.</summary>
    private static byte[] Identifier(int seed)
    {
        byte[] bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), unchecked((ulong)seed * 0x9E3779B97F4A7C15UL));
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), unchecked((ulong)seed));
        return bytes;
    }
}
