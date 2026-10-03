using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's <c>hist.c</c>: byte histograms, their largest count and their last symbol.</summary>
internal static unsafe class Histogram
{
    /// <summary>
    /// libzstd's <c>HIST_count_simple</c>: counts the bytes of <paramref name="source"/> into
    /// <paramref name="count"/>, whose first <paramref name="maxSymbolValue"/> + 1 counters it clears.
    /// On return <paramref name="maxSymbolValue"/> is the largest byte present (0 for no byte).
    /// </summary>
    /// <returns>The largest count.</returns>
    public static uint CountSimple(uint* count, ref uint maxSymbolValue, byte* source, nuint size)
    {
        uint max = maxSymbolValue;
        Unsafe.InitBlockUnaligned(count, 0, (max + 1) * sizeof(uint));
        if (size == 0)
        {
            maxSymbolValue = 0;
            return 0;
        }

        byte* end = source + size;
        for (byte* p = source; p < end; p++)
        {
            count[*p]++;
        }

        while (count[max] == 0)
        {
            max--;
        }

        maxSymbolValue = max;
        uint largest = 0;
        for (uint s = 0; s <= max; s++)
        {
            if (count[s] > largest)
            {
                largest = count[s];
            }
        }

        return largest;
    }

    /// <summary>
    /// libzstd's <c>HIST_countFast_wksp</c> (and <c>HIST_count_wksp</c> for 256 symbols): every byte
    /// counted, <paramref name="count"/> cleared up to 255. Four sets of counters, a byte's set its
    /// place in its four (see <see cref="CountWords"/>), so that a run of equal bytes does not chain
    /// each count on the last.
    /// </summary>
    /// <returns>The largest count; <paramref name="maxSymbolValue"/> is then the largest byte present.</returns>
    public static uint CountFast(uint* count, out uint maxSymbolValue, byte* source, nuint size)
    {
        if (size < 1500)
        {
            maxSymbolValue = 255;
            return CountSimple(count, ref maxSymbolValue, source, size);
        }

        uint* counters = stackalloc uint[4 * 512];
        Unsafe.InitBlockUnaligned(counters, 0, 4 * 512 * sizeof(uint));
        byte* p = source;
        byte* end = source + size;
        p = CountWords(p, end, counters, counters);
        for (; p < end; p++)
        {
            counters[*p]++;
        }

        Vector128<uint> largestLanes = Vector128<uint>.Zero;
        for (nuint s = 0; s < 256; s += 4)
        {
            uint* c = counters + s;
            Vector128<uint> total =
                (Vector128.Load(c) + Vector128.Load(c + 256)) + (Vector128.Load(c + 512) + Vector128.Load(c + 768)) +
                (Vector128.Load(c + 1024) + Vector128.Load(c + 1280)) + (Vector128.Load(c + 1536) + Vector128.Load(c + 1792));
            total.Store(count + s);
            largestLanes = Vector128.Max(largestLanes, total);
        }

        uint largest = Math.Max(
            Math.Max(largestLanes.GetElement(0), largestLanes.GetElement(1)),
            Math.Max(largestLanes.GetElement(2), largestLanes.GetElement(3)));

        uint max = 255;
        while (count[max] == 0)
        {
            max--;
        }

        maxSymbolValue = max;
        return largest;
    }

    /// <summary>
    /// The bytes of <paramref name="source"/>, eight a read, into four sets of 512 counters (whose two
    /// halves are one byte's): a byte's field is nine bits wide, the stray bit the next byte's lowest,
    /// so that the JIT extracts it in one instruction. With a mask of eight bits, it masked in 32 bits
    /// and widened again.
    /// </summary>
    /// <remarks>
    /// The counters are read through <paramref name="read"/> and written through
    /// <paramref name="write"/>, the same memory: not knowing it, the JIT folds each address into its
    /// load and its store, instead of computing it once for both.
    /// </remarks>
    /// <returns>Where the bytes left, under eight, start.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte* CountWords(byte* source, byte* end, uint* read, uint* write)
    {
        uint* read1 = read;
        uint* read2 = read + 512;
        uint* read3 = read + 1024;
        uint* read4 = read + 1536;
        uint* write1 = write;
        uint* write2 = write + 512;
        uint* write3 = write + 1024;
        uint* write4 = write + 1536;
        byte* p = source;
        for (; p + 8 <= end; p += 8)
        {
            ulong word = Unsafe.ReadUnaligned<ulong>(p);
            nuint b0 = (nuint)(word & 0x1FF);
            nuint b1 = (nuint)((word >> 8) & 0x1FF);
            nuint b2 = (nuint)((word >> 16) & 0x1FF);
            nuint b3 = (nuint)((word >> 24) & 0x1FF);
            nuint b4 = (nuint)((word >> 32) & 0x1FF);
            nuint b5 = (nuint)((word >> 40) & 0x1FF);
            nuint b6 = (nuint)((word >> 48) & 0x1FF);
            nuint b7 = (nuint)(word >> 56);
            write1[b0] = read1[b0] + 1;
            write2[b1] = read2[b1] + 1;
            write3[b2] = read3[b2] + 1;
            write4[b3] = read4[b3] + 1;
            write1[b4] = read1[b4] + 1;
            write2[b5] = read2[b5] + 1;
            write3[b6] = read3[b6] + 1;
            write4[b7] = read4[b7] + 1;
        }

        return p;
    }
}
