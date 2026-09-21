using System;
using System.Diagnostics.CodeAnalysis;

namespace Vorticity.Types;

/// <summary>
/// A typed scalar: a <see cref="Types.DType"/> paired with a <see cref="ScalarValue"/>. Both halves
/// are handles into their own arena, so the pair is small and copies freely.
/// </summary>
internal readonly struct Scalar : IEquatable<Scalar>
{
    /// <summary>Pairs a dtype with a value. Neither is validated against the other.</summary>
    /// <remarks>
    /// A default <paramref name="dtype"/> is accepted: a codec reading a malformed
    /// <c>Scalar</c> message decides for itself whether a missing dtype is fatal, and this model
    /// must be able to represent what it read either way.
    /// </remarks>
    public Scalar(DType dtype, ScalarValue value)
    {
        DType = dtype;
        Value = value;
    }

    /// <summary>The declared type of the value.</summary>
    public DType DType { get; }

    /// <summary>The value itself.</summary>
    public ScalarValue Value { get; }

    /// <summary>
    /// True when the value is the null value. False when it is merely
    /// <see cref="ScalarValueKind.Absent"/>: absence is not nullity.
    /// </summary>
    public bool IsNull => Value.IsNull;

    /// <summary>Structural equality of both halves.</summary>
    public bool Equals(Scalar other) => DType.Equals(other.DType) && Value.Equals(other.Value);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Scalar other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DType.GetHashCode(), Value.GetHashCode());

    /// <summary>
    /// Culture-invariant rendering of the form <c>"i32? = 42"</c>: the dtype, then the value.
    /// </summary>
    public override string ToString()
    {
        Span<char> stack = stackalloc char[128];
        ValueStringWriter writer = new(stack, DTypeFormatter.MaxRenderedLength);
        try
        {
            Append(ref writer, DType, Value);
            return writer.Build();
        }
        finally
        {
            writer.Dispose();
        }
    }

    internal static void Append(ref ValueStringWriter w, DType dtype, ScalarValue value)
    {
        DTypeFormatter.Append(ref w, dtype);
        w.Append(" = ");
        ScalarValue.Append(ref w, value);
    }
}
