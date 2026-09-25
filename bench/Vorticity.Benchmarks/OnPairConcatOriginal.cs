using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Benchmarks;

/// <summary>
/// The four-codes-a-step OnPair concatenation, kept here unchanged as the baseline every change to
/// <c>OnPairDecoder.Concatenate</c> is measured against in the same process.
/// </summary>
/// <remarks>
/// Self-contained on purpose: nothing here calls into the library, so editing the library's kernel
/// can never move this arm.
/// </remarks>
internal static class OnPairConcatOriginal
{
    private const int MaxTokenSize = 16;

    /// <summary>Concatenates the tokens <paramref name="codes"/> names; the bytes written.</summary>
    internal static int Concatenate(ReadOnlySpan<ushort> typed, ReadOnlySpan<long> tokens, ReadOnlySpan<byte> dictionary, Span<byte> destination)
    {
        int written = 0;
        int wideLimit = destination.Length - MaxTokenSize;
        int wideStart = dictionary.Length - MaxTokenSize;
        ref byte output = ref MemoryMarshal.GetReference(destination);
        ref byte source = ref MemoryMarshal.GetReference(dictionary);
        int blockLimit = destination.Length - (4 * MaxTokenSize);
        int tokenLimit = tokens.Length;
        int codeEnd = typed.Length;
        int i = 0;
        while (true)
        {
            if (i + 4 > codeEnd || written > blockLimit)
            {
                if (i >= codeEnd)
                {
                    break;
                }

                i = One(typed, i, tokens, dictionary, destination, ref output, ref source, wideLimit, wideStart, ref written);
                continue;
            }

            uint a = typed[i];
            uint b = typed[i + 1];
            uint c = typed[i + 2];
            uint d = typed[i + 3];
            if (a >= (uint)tokenLimit || b >= (uint)tokenLimit || c >= (uint)tokenLimit || d >= (uint)tokenLimit)
            {
                i = One(typed, i, tokens, dictionary, destination, ref output, ref source, wideLimit, wideStart, ref written);
                continue;
            }

            long pa = tokens[(int)a];
            long pb = tokens[(int)b];
            long pc = tokens[(int)c];
            long pd = tokens[(int)d];
            int sa = (int)pa;
            int sb = (int)pb;
            int sc = (int)pc;
            int sd = (int)pd;
            if (Math.Max(Math.Max(sa, sb), Math.Max(sc, sd)) > wideStart)
            {
                i = One(typed, i, tokens, dictionary, destination, ref output, ref source, wideLimit, wideStart, ref written);
                continue;
            }

            int oa = written;
            int ob = oa + (int)(pa >> 32);
            int oc = ob + (int)(pb >> 32);
            int od = oc + (int)(pc >> 32);
            written = od + (int)(pd >> 32);
            Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sa)), ref Unsafe.Add(ref output, (uint)oa));
            Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sb)), ref Unsafe.Add(ref output, (uint)ob));
            Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sc)), ref Unsafe.Add(ref output, (uint)oc));
            Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)sd)), ref Unsafe.Add(ref output, (uint)od));
            i += 4;
        }

        return written;
    }

    private static int One(
        ReadOnlySpan<ushort> typed, int i, ReadOnlySpan<long> tokens, ReadOnlySpan<byte> dictionary, Span<byte> destination,
        ref byte output, ref byte source, int wideLimit, int wideStart, ref int written)
    {
        uint code = typed[i];
        if (code >= (uint)tokens.Length)
        {
            throw new InvalidOperationException($"code {code} names no token");
        }

        long packed = tokens[(int)code];
        int start = (int)packed;
        int size = (int)(packed >> 32);
        if (written <= wideLimit && start <= wideStart)
        {
            Vector128.StoreUnsafe(Vector128.LoadUnsafe(ref Unsafe.Add(ref source, (uint)start)), ref Unsafe.Add(ref output, (uint)written));
            written += size;
            return i + 1;
        }

        if (written + size > destination.Length)
        {
            throw new InvalidOperationException("the codes decode past the destination");
        }

        dictionary.Slice(start, size).CopyTo(destination.Slice(written, size));
        written += size;
        return i + 1;
    }
}
