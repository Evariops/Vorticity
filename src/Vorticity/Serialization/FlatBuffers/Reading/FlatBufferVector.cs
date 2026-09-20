using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Serialization.FlatBuffers;

/// <summary>
/// A vector of tables or strings inside an untrusted FlatBuffer, laid out as
/// <c>[u32 count][uoffset elements...]</c>. Vectors of scalars and of inline structs do not come
/// through here - <c>FlatBufferTable.GetStructVector</c> reinterprets those in place.
/// </summary>
/// <remarks>
/// The element count is validated against the real buffer length when the vector is resolved, so
/// <see cref="Count"/> can never promise more elements than the buffer holds. Each element is a
/// forward uoffset that is bounds-checked again when it is dereferenced.
/// </remarks>
public readonly ref struct FlatBufferVector
{
    private readonly ReadOnlySpan<byte> _buffer;

    // The traversal's remaining table budget, shared by reference with the table this vector came
    // from. A null ref means the traversal carries no budget - see FlatBufferTable.
    private readonly ref int _tableBudget;

    private readonly int _elementsPos;
    private readonly int _count;
    private readonly int _depth;

    internal FlatBufferVector(ReadOnlySpan<byte> buffer, int vectorPos, int depth, ref int tableBudget)
    {
        uint count = FlatBufferAccess.ReadUInt32(buffer, vectorPos);
        long elementsPos = (long)vectorPos + 4;
        // 64-bit multiply: a hostile count of uint.MaxValue is 16 GiB of uoffsets, which must be
        // rejected rather than wrapped into a plausible-looking range.
        FlatBufferAccess.CheckRange(buffer, elementsPos, (long)count * sizeof(uint), "offset vector");

        _buffer = buffer;
        _tableBudget = ref tableBudget;
        _elementsPos = (int)elementsPos;
        // The range check above proves count * 4 fits in the buffer, so count fits in an int.
        _count = (int)count;
        _depth = depth;
    }

    /// <summary>Number of elements. 0 for an absent vector.</summary>
    public int Count => _count;

    /// <summary>Reads the table at <paramref name="index"/>.</summary>
    /// <exception cref="VortexFormatException">
    /// The index is out of range, the element uoffset is zero or out of range, the table's vtable
    /// is malformed, or the traversal exceeds <see cref="VortexLimits.MaxFlatBufferDepth"/>.
    /// </exception>
    public FlatBufferTable GetTable(int index)
    {
        int pos = ElementPos(index);
        int target = ResolveElement(_buffer, pos);
        int depth = _depth + 1;
        VortexLimits.CheckDepth(depth, VortexLimits.MaxFlatBufferDepth, "FlatBuffers table");
        return new FlatBufferTable(_buffer, target, depth, ref _tableBudget);
    }

    /// <summary>
    /// Reads the UTF-8 bytes of the string at <paramref name="index"/>, excluding the trailing NUL.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The index is out of range, or the string escapes the buffer.
    /// </exception>
    public ReadOnlySpan<byte> GetStringUtf8(int index) =>
        FlatBufferTable.ReadString(_buffer, ResolveElement(_buffer, ElementPos(index)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ElementPos(int index)
    {
        // Out of range is reported as a format error like every other out-of-range access in this
        // reader: a hostile file must never be able to surface a different exception type.
        if ((uint)index >= (uint)_count)
        {
            ThrowIndex(index, _count);
        }

        return _elementsPos + (index * sizeof(uint));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResolveElement(ReadOnlySpan<byte> buffer, int pos)
    {
        uint relative = FlatBufferAccess.ReadUInt32(buffer, pos);
        long target = (long)pos + relative;
        if (relative == 0 || target > buffer.Length - 4)
        {
            ThrowElementOffset(pos, relative, buffer.Length);
        }

        return (int)target;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndex(int index, int count) =>
        throw new VortexFormatException(
            $"FlatBuffers vector index {index} is outside the {count} elements it declares.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowElementOffset(int pos, uint relative, int length) =>
        throw new VortexFormatException(
            $"FlatBuffers vector element uoffset {relative} at {pos} is zero or points outside the " +
            $"{length}-byte buffer.");
}
