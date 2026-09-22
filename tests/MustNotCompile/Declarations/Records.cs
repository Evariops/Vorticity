using Vorticity;

namespace MustNotCompile.Declarations;

// What the generator refuses, and says why, rather than emitting a record that cannot bind.
[VortexRecord]
internal partial record struct WithAChar(int Day, char Initial); // expect: VX1005

[VortexRecord]
internal record struct NotPartial(int Day); // expect: VX1006

[VortexRecord]
internal partial record struct Generic<T>(T Value); // expect: VX1007

[VortexRecord]
internal sealed partial class GetOnly
{
    public int Day { get; } // expect: VX1008
}

// A borrowed column lives on the stack, inside the loop body that received it.
internal sealed class Holder
{
    internal Column<int> Days; // expect: CS8345
}
