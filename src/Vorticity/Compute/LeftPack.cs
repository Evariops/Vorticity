using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Vorticity.Compute;

/// <summary>
/// The tables of a left-pack, the compress of a machine that has none: for each mask of a vector's lanes, the permutation
/// that puts the kept lanes first, in order, the lanes past them left to whatever the next store writes over them.
/// </summary>
/// <remarks>
/// AVX-512 compresses a register's lanes by a mask in one instruction. AVX2 has no compress, but a permutation of eight
/// 32-bit lanes (<c>vpermd</c>) chosen by the mask's eight bits does the same for eight lanes, and for four 64-bit lanes as
/// pairs of 32-bit ones; a 128-bit byte shuffle (<c>pshufb</c>, <c>tbl</c>) does it for four 32-bit lanes. Built once.
/// </remarks>
internal static class LeftPack
{
    /// <summary>For each mask of eight 32-bit lanes, eight lane numbers: 256 permutations, 8 KB.</summary>
    internal static readonly int[] Lanes8 = Build(8, 1);

    /// <summary>For each mask of four 64-bit lanes, the eight 32-bit lane numbers that move them as pairs: 16 permutations.</summary>
    internal static readonly int[] Pairs4 = Build(4, 2);

    /// <summary>For each mask of four 32-bit lanes, the sixteen byte numbers of a 128-bit shuffle: 256 bytes.</summary>
    internal static readonly byte[] Bytes4 = BuildBytes4();

    /// <summary>
    /// The 32-bit lanes of <paramref name="values"/> whose bits <paramref name="mask"/> sets, first and in order: eight lanes
    /// by <see cref="Lanes8"/>, or four 64-bit lanes by <see cref="Pairs4"/> when <typeparamref name="T"/> is 8 bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<T> Pack256<T>(Vector256<T> values, uint mask)
    {
        ref int table = ref MemoryMarshal.GetArrayDataReference(Unsafe.SizeOf<T>() == 8 ? Pairs4 : Lanes8);
        return Avx2.PermuteVar8x32(values.AsInt32(), Vector256.LoadUnsafe(ref table, mask * 8)).As<int, T>();
    }

    private static int[] Build(int lanes, int width)
    {
        int[] table = new int[(1 << lanes) * 8];
        for (int mask = 0; mask < 1 << lanes; mask++)
        {
            int kept = 0;
            for (int lane = 0; lane < lanes; lane++)
            {
                if (((mask >> lane) & 1) == 0)
                {
                    continue;
                }

                for (int part = 0; part < width; part++)
                {
                    table[(mask * 8) + (kept * width) + part] = (lane * width) + part;
                }

                kept++;
            }
        }

        return table;
    }

    private static byte[] BuildBytes4()
    {
        byte[] table = new byte[16 * 16];
        for (int mask = 0; mask < 16; mask++)
        {
            int kept = 0;
            for (int lane = 0; lane < 4; lane++)
            {
                if (((mask >> lane) & 1) != 0)
                {
                    for (int b = 0; b < 4; b++)
                    {
                        table[(mask * 16) + (kept * 4) + b] = (byte)((lane * 4) + b);
                    }

                    kept++;
                }
            }
        }

        return table;
    }
}
