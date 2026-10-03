using System.Runtime.CompilerServices;

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
    /// counted, <paramref name="count"/> cleared up to 255. Four sets of counters, one for each byte of
    /// a 32-bit word, so that a run of equal bytes does not chain each count on the last.
    /// </summary>
    /// <returns>The largest count; <paramref name="maxSymbolValue"/> is then the largest byte present.</returns>
    public static uint CountFast(uint* count, out uint maxSymbolValue, byte* source, nuint size)
    {
        if (size < 1500)
        {
            maxSymbolValue = 255;
            return CountSimple(count, ref maxSymbolValue, source, size);
        }

        uint* counters = stackalloc uint[4 * 256];
        Unsafe.InitBlockUnaligned(counters, 0, 4 * 256 * sizeof(uint));
        uint* c1 = counters;
        uint* c2 = counters + 256;
        uint* c3 = counters + 512;
        uint* c4 = counters + 768;
        byte* p = source;
        byte* end = source + size;
        for (; p + 4 <= end; p += 4)
        {
            uint word = Unsafe.ReadUnaligned<uint>(p);
            c1[word & 0xFF]++;
            c2[(word >> 8) & 0xFF]++;
            c3[(word >> 16) & 0xFF]++;
            c4[word >> 24]++;
        }

        for (; p < end; p++)
        {
            c1[*p]++;
        }

        uint largest = 0;
        for (int s = 0; s < 256; s++)
        {
            uint total = c1[s] + c2[s] + c3[s] + c4[s];
            count[s] = total;
            if (total > largest)
            {
                largest = total;
            }
        }

        uint max = 255;
        while (count[max] == 0)
        {
            max--;
        }

        maxSymbolValue = max;
        return largest;
    }
}
