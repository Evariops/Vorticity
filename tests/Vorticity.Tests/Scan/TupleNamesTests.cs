// The names of a tuple's elements, which name the columns of a selection that returns a tuple: the
// first ones made once for the process, the others formatted as they are asked for, the same names
// either way.
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class TupleNamesTests
{
    [Theory]
    [InlineData(0, "Item1")]
    [InlineData(8, "Item9")]
    [InlineData(31, "Item32")]
    [InlineData(32, "Item33")]
    [InlineData(99, "Item100")]
    public void AnElementIsNamedByItsPositionFromOne(int index, string name) => Assert.Equal(name, TupleNames.Item(index));

    [Fact]
    public void TheFirstNamesAreMadeOnce() => Assert.Same(TupleNames.Item(2), TupleNames.Item(2));
}
