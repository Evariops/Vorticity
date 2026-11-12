// The distinct trigrams a text predicate requires, in the order they first come, against those of its literal runs told
// apart by their bytes: repeats kept once, trigrams that differ in one byte both kept, folded to lower case when asked.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class TrigramsRequiredTests
{
    [Theory]
    [InlineData("%abcabcabd%", false)]
    [InlineData("%aaaaaaaa%", false)]
    [InlineData("%xyZ_xyz%ABC%abc%", true)]
    [InlineData("%xyZ_xyz%ABC%abc%", false)]
    [InlineData("%été été%", false)]
    public void EachRequiredTrigramComesOnce(string pattern, bool fold)
    {
        StringMatchExpr like = new StringMatchExpr(Expr.Field("t"), StringMatchOp.Like, FilterLiteral.From(Encoding.UTF8.GetBytes(pattern)), 0x5C);
        List<byte[]> found = Trigrams.Required(like, fold);

        List<string> expected = [];
        foreach (string run in pattern.Split('%', '_'))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(run);
            for (int i = 0; i + 3 <= bytes.Length; i++)
            {
                byte[] trigram = new byte[3];
                Trigrams.Copy(bytes.AsSpan(i, 3), fold, trigram);
                string key = Convert.ToHexString(trigram);
                if (!expected.Contains(key))
                {
                    expected.Add(key);
                }
            }
        }

        Assert.Equal(expected, found.Select(Convert.ToHexString).ToList());
    }
}
