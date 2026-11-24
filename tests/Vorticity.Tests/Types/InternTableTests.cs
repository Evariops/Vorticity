using Xunit;

namespace Vorticity.Tests.Types;

public sealed class InternTableTests
{
    [Fact]
    public void AnEqualValueIsTheOneInterned()
    {
        InternTable<string> table = new InternTable<string>(budget: 100);
        string first = new string('a', 3);
        Assert.Same(first, table.Intern(first, 4));
        Assert.Same(first, table.Intern(new string('a', 3), 4));
        Assert.Equal(4, table.Weight);
    }

    [Fact]
    public void PastItsBudgetATableInternsNothingNewAndKeepsWhatItHas()
    {
        InternTable<string> table = new InternTable<string>(budget: 10);
        string a = new string('a', 1);
        string b = new string('b', 1);
        Assert.Same(a, table.Intern(a, 4));
        Assert.Same(b, table.Intern(b, 4));

        // Twelve would pass the budget of ten: the value is handed back, and an equal one after it is
        // handed back too, since nothing was kept.
        string c = new string('c', 1);
        string twin = new string('c', 1);
        Assert.Same(c, table.Intern(c, 4));
        Assert.Same(twin, table.Intern(twin, 4));
        Assert.Equal(8, table.Weight);

        // What it has is still served.
        Assert.Same(a, table.Intern(new string('a', 1), 4));
    }

    /// <summary>
    /// A type kept for reuse weighs the bytes it holds on the heap: a fixed-size list of a new size, a unit under a weight
    /// of one a type, weighs 176 bytes, so that a family of such types fills the budget at megabytes and not at a million
    /// of them. Every other test interns beside this one, which only adds to what is weighed.
    /// </summary>
    [Fact]
    public void ATypeKeptWeighsTheBytesItHolds()
    {
        long before = VortexType.InternedBytes;
        int kept = 0;
        for (int size = 7_000_000; size < 7_001_000; size++)
        {
            VortexType type = VortexType.FixedSizeList(VortexType.UInt8, size);
            if (ReferenceEquals(type, VortexType.FixedSizeList(VortexType.UInt8, size)))
            {
                kept++;
            }
        }

        long weighed = VortexType.InternedBytes - before;
        Assert.True(weighed >= kept * 176L, $"{kept} types kept weigh {weighed} bytes");
        Assert.True(kept > 0 || VortexType.InternedBytes > (8L << 20) - 176, $"none of the types was kept under {VortexType.InternedBytes} bytes");
    }
}
