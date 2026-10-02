using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// A prefix as <see cref="FsstDecoder.TryStartsWith"/> matches it against a row's codes: the codes
/// every row starting with it shares, and the bytes of it past those.
/// </summary>
/// <remarks>
/// Compression takes the longest symbol matching at each position, and a symbol is at most eight
/// bytes, so the code chosen at a position depends on the eight bytes from it and on nothing else
/// while eight remain. A row that starts with the prefix has the prefix's own bytes at each of its
/// positions that end eight bytes or more before the prefix does: the same codes as the prefix
/// compressed alone, in the same place. So the prefix's codes up to the first that starts within
/// its last seven bytes open that row's codes, and past them fewer than eight bytes are left to
/// decode and compare.
/// </remarks>
internal readonly ref struct FsstPrefix
{
    private readonly FsstSymbolTable _table;
    private readonly ReadOnlySpan<byte> _shared;
    private readonly ulong _head;
    private readonly ulong _tail;
    private readonly Vector128<byte> _wideHead;
    private readonly Vector128<byte> _wideTail;
    private readonly int _rest;
    private readonly ulong _restBits;

    private FsstPrefix(FsstSymbolTable table, ReadOnlySpan<byte> shared, ReadOnlySpan<byte> rest)
    {
        _table = table;
        _shared = shared;
        if (shared.Length >= sizeof(ulong))
        {
            _head = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(shared));
            _tail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(shared), shared.Length - sizeof(ulong)));
        }

        if (shared.Length >= Vector128<byte>.Count)
        {
            _wideHead = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(shared));
            _wideTail = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(shared), (nuint)(shared.Length - Vector128<byte>.Count));
        }

        _rest = rest.Length;
        for (int k = 0; k < rest.Length; k++)
        {
            _restBits |= (ulong)rest[k] << (8 * k);
        }
    }

    /// <summary>The prefix's codes that a row starting with it has first.</summary>
    internal ReadOnlySpan<byte> Shared => _shared;

    /// <summary>
    /// The matcher of <paramref name="prefix"/> under <paramref name="table"/>, its codes compressed
    /// into <paramref name="scratch"/>; false where they do not fit.
    /// </summary>
    /// <param name="table">The column's symbol table.</param>
    /// <param name="prefix">The bytes a row must start with.</param>
    /// <param name="scratch">At least <see cref="FsstSymbolTable.MaxCompressedLength"/> of the prefix, and one more.</param>
    /// <param name="matcher">The matcher, over <paramref name="scratch"/> and <paramref name="prefix"/>.</param>
    internal static bool TryCreate(FsstSymbolTable table, ReadOnlySpan<byte> prefix, Span<byte> scratch, out FsstPrefix matcher)
    {
        matcher = default;
        if (!table.TryCompress(prefix, scratch, out int compressed))
        {
            return false;
        }

        // The codes that start eight bytes or more before the prefix's end, and the bytes they cover.
        int shared = 0;
        int covered = 0;
        while (shared < compressed && covered + FsstSymbolTable.SymbolSize <= prefix.Length)
        {
            byte code = scratch[shared];
            shared += code == FsstSymbolTable.EscapeCode ? 2 : 1;
            covered += code == FsstSymbolTable.EscapeCode ? 1 : table.Width(code);
        }

        matcher = new FsstPrefix(table, scratch[..shared], prefix[covered..]);
        return true;
    }

    /// <summary>Whether <paramref name="codes"/> open with the shared codes.</summary>
    /// <remarks>
    /// Eight bytes or more are compared as their first and last words, which overlap under sixteen,
    /// and sixteen or more as their first and last vectors and the vectors between, the last one
    /// overlapping: most rows of a column that shares a prefix pass, and a call to compare spans
    /// costs a row more than the compare.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Opens(ReadOnlySpan<byte> codes)
    {
        int length = _shared.Length;
        if (length < sizeof(ulong))
        {
            return codes.StartsWith(_shared);
        }

        if (codes.Length < length)
        {
            return false;
        }

        ref byte first = ref MemoryMarshal.GetReference(codes);
        if (length < Vector128<byte>.Count)
        {
            return Unsafe.ReadUnaligned<ulong>(ref first) == _head &&
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, length - sizeof(ulong))) == _tail;
        }

        nuint last = (nuint)(length - Vector128<byte>.Count);
        if (Vector128.LoadUnsafe(ref first) != _wideHead || Vector128.LoadUnsafe(ref first, last) != _wideTail)
        {
            return false;
        }

        ref byte shared = ref MemoryMarshal.GetReference(_shared);
        for (nuint at = (nuint)Vector128<byte>.Count; at < last; at += (nuint)Vector128<byte>.Count)
        {
            if (Vector128.LoadUnsafe(ref first, at) != Vector128.LoadUnsafe(ref shared, at))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a row whose codes are <paramref name="codes"/> starts with the prefix.</summary>
    /// <exception cref="VortexFormatException">A code this reads names no symbol, or an escape ends the row.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Match(ReadOnlySpan<byte> codes)
    {
        if (!Opens(codes))
        {
            return false;
        }

        // Decoded into a word: fewer than eight bytes are wanted, so each code's bytes go in at
        // the bytes before it, and what a symbol carries past them falls off the top.
        ulong decoded = 0;
        int produced = 0;
        int i = _shared.Length;
        while (produced < _rest)
        {
            if (i >= codes.Length)
            {
                return false;
            }

            byte code = codes[i++];
            if (code == FsstSymbolTable.EscapeCode)
            {
                if (i >= codes.Length)
                {
                    Truncated();
                }

                decoded |= (ulong)codes[i++] << (8 * produced);
                produced++;
                continue;
            }

            if (code >= _table.Count)
            {
                Unknown(code, _table.Count);
            }

            // A symbol's word past its width is padding, so it is cleared before it goes in.
            int width = _table.Width(code);
            ulong symbol = _table.SymbolBits(code);
            decoded |= (width == FsstSymbolTable.SymbolSize ? symbol : symbol & ((1UL << (8 * width)) - 1)) << (8 * produced);
            produced += width;
        }

        return (decoded & ((1UL << (8 * _rest)) - 1)) == _restBits;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Truncated() =>
        CompressedThrow.Format($"{FsstDecoder.Id}: truncated compressed string, escape code at end of input.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unknown(byte code, int count) =>
        CompressedThrow.Format($"{FsstDecoder.Id}: code {code} names no symbol of a {count}-symbol table.");
}
