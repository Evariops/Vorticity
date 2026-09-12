// Transcribed from spec/flatbuffers/dtype.fbs and spec/proto/dtype.proto: every parameterised
// dtype carries a single `nullable: bool` field. Modelling it as a two-valued enum rather than a
// bool keeps call sites self-describing (`Nullability.Nullable` vs a bare `true`) at zero cost:
// the enum is a byte and converts to/from the wire bool with a cast.
namespace Vorticity.Types;

/// <summary>
/// Whether a dtype admits nulls. The wire representation is the <c>nullable</c> boolean field of
/// each dtype variant, so <see cref="NonNullable"/> is <c>false</c> and <see cref="Nullable"/> is
/// <c>true</c>.
/// </summary>
public enum Nullability : byte
{
    /// <summary>The dtype does not admit nulls (<c>nullable = false</c>).</summary>
    NonNullable = 0,

    /// <summary>The dtype admits nulls (<c>nullable = true</c>).</summary>
    Nullable = 1,
}
