using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// The four validity states an array can be in. <c>true</c> means <em>valid</em>.
/// </summary>
internal enum ValidityKind : byte
{
    /// <summary>The dtype forbids nulls; there is no validity information and none is needed.</summary>
    NonNullable = 0,

    /// <summary>The dtype admits nulls but every row in this array is valid.</summary>
    AllValid = 1,

    /// <summary>Every row in this array is null.</summary>
    AllInvalid = 2,

    /// <summary>Validity is a per-row bitmap held by a canonical Bool node.</summary>
    Bitmap = 3,
}

/// <summary>
/// One array's validity. 8 bytes; the <see cref="ValidityKind.Bitmap"/> case names a canonical
/// Bool node in the same <see cref="CanonicalArena"/> by index rather than holding a buffer,
/// because an index is four bytes and cannot outlive the arena it points into.
/// </summary>
/// <remarks>
/// A <see cref="Validity"/> carries no length of its own: the parent array's length is the length.
/// </remarks>
internal readonly struct Validity : IEquatable<Validity>
{
    private readonly int _canonicalNodeIndex;
    private readonly ValidityKind _kind;

    private Validity(ValidityKind kind, int canonicalNodeIndex)
    {
        _kind = kind;
        _canonicalNodeIndex = canonicalNodeIndex;
    }

    /// <summary>The dtype forbids nulls.</summary>
    public static Validity NonNullable => new(ValidityKind.NonNullable, -1);

    /// <summary>Nulls are admitted but none are present.</summary>
    public static Validity AllValid => new(ValidityKind.AllValid, -1);

    /// <summary>Every row is null.</summary>
    public static Validity AllInvalid => new(ValidityKind.AllInvalid, -1);

    /// <summary>Validity comes from the canonical Bool node at <paramref name="canonicalNodeIndex"/>.</summary>
    /// <param name="canonicalNodeIndex">A non-negative index into the batch's
    /// <see cref="CanonicalArena"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is negative.</exception>
    public static Validity Bitmap(int canonicalNodeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(canonicalNodeIndex);
        return new Validity(ValidityKind.Bitmap, canonicalNodeIndex);
    }

    /// <summary>
    /// <see cref="Nullability.NonNullable"/> maps to <see cref="NonNullable"/> and
    /// <see cref="Nullability.Nullable"/> to <see cref="AllValid"/>.
    /// </summary>
    /// <param name="nullability">The inherited dtype's nullability.</param>
    /// <exception cref="VortexFormatException"><paramref name="nullability"/> is not 0 or 1.</exception>
    public static Validity FromNullability(Nullability nullability) => nullability switch
    {
        Nullability.NonNullable => NonNullable,
        Nullability.Nullable => AllValid,
        _ => ThrowNullability(nullability),
    };

    /// <summary>The state this validity is in.</summary>
    public ValidityKind Kind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _kind;
    }

    /// <summary>
    /// <see langword="true"/> for <see cref="ValidityKind.NonNullable"/> and
    /// <see cref="ValidityKind.AllValid"/>: no row needs a per-row check.
    /// </summary>
    public bool IsAllValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _kind is ValidityKind.NonNullable or ValidityKind.AllValid;
    }

    /// <summary>
    /// The canonical Bool node holding the bitmap, or -1 when <see cref="Kind"/> is not
    /// <see cref="ValidityKind.Bitmap"/>.
    /// </summary>
    public int CanonicalNodeIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _kind == ValidityKind.Bitmap ? _canonicalNodeIndex : -1;
    }

    /// <inheritdoc/>
    public bool Equals(Validity other) =>
        _kind == other._kind && CanonicalNodeIndex == other.CanonicalNodeIndex;

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Validity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine((byte)_kind, CanonicalNodeIndex);

    /// <summary>Structural equality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(Validity left, Validity right) => left.Equals(right);

    /// <summary>Structural inequality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(Validity left, Validity right) => !left.Equals(right);

    /// <summary>Culture-invariant rendering, for diagnostics.</summary>
    public override string ToString() =>
        _kind == ValidityKind.Bitmap
            ? "Bitmap(#" + _canonicalNodeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
            : _kind.ToString();

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static Validity ThrowNullability(Nullability nullability) =>
        throw new VortexFormatException(
            $"Nullability value {(byte)nullability} is not 0 (NonNullable) or 1 (Nullable).");
}
