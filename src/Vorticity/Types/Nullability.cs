namespace Vorticity.Types;

/// <summary>
/// Whether a dtype admits nulls. The wire representation is the <c>nullable</c> boolean field of
/// each dtype variant, so <see cref="NonNullable"/> is <c>false</c> and <see cref="Nullable"/> is
/// <c>true</c>. A two-valued enum rather than a bool, which keeps call sites self-describing at
/// no cost: it is a byte and converts to and from the wire boolean with a cast.
/// </summary>
public enum Nullability : byte
{
    /// <summary>The dtype does not admit nulls (<c>nullable = false</c>).</summary>
    NonNullable = 0,

    /// <summary>The dtype admits nulls (<c>nullable = true</c>).</summary>
    Nullable = 1,
}
