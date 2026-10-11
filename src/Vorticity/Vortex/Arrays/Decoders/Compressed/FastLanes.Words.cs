using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Threading;

namespace Vorticity.Arrays.Decoders.Compressed;

internal static partial class FastLanes
{
    /// <summary>Bytes of one row of a block, and of one packed word of all its lanes, whatever the element type.</summary>
    private const int LineBytes = 128;

    /// <summary>
    /// Unpacks whole blocks a packed word at a time: each word's line is loaded once, every row
    /// inside the word is written from the registers holding it, and so is the one row that reaches
    /// its last bit, together with the next word's line, which the next step starts from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row of a block is 128 bytes of output for every element type, and so is each packed word of
    /// all the lanes: a row is eight vectors shifted, masked and stored as one line. No row is as
    /// wide as a word, so a word ends inside exactly one row, which either runs into the next word
    /// or ends with this one; one formula serves both, the next word's bits landing above the mask
    /// when the row ends with the word. Every word is therefore the same step, and a block loads
    /// each of its packed bytes once.
    /// </para>
    /// <para>
    /// What each step does depends on the bit width alone, so the steps are laid out once per width
    /// and the loops only read them: no branch looks at the data.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The unsigned element type.</typeparam>
    /// <param name="packed">The first packed word of the run.</param>
    /// <param name="bitWidth">Bits per value, strictly between 0 and the element width.</param>
    /// <param name="output">The first of <c>blocks * 1024</c> values; every one is written.</param>
    /// <param name="blocks">How many consecutive blocks to unpack.</param>
    private static void UnpackWords<T>(ref T packed, int bitWidth, ref T output, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        WordPlan plan = WordPlanOf<T>(bitWidth);
        Vector128<T> mask = Vector128.Create(Mask<T>(bitWidth));
        nuint packedBytes = (nuint)(LineBytes * bitWidth);
        nuint outputBytes = (nuint)(BlockSize * Unsafe.SizeOf<T>());
        for (int block = 0; block < blocks; block++)
        {
            WordsOfBlock(ref packed, ref output, plan, mask);
            packed = ref Unsafe.AddByteOffset(ref packed, packedBytes);
            output = ref Unsafe.AddByteOffset(ref output, outputBytes);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WordsOfBlock<T>(ref T packed, ref T output, WordPlan plan, Vector128<T> mask)
        where T : unmanaged
    {
        nuint step = (nuint)Vector128<T>.Count;
        ref WordStep word = ref MemoryMarshal.GetArrayDataReference(plan.Steps);
        ref WordStep lastWord = ref Unsafe.Add(ref word, plan.Steps.Length);
        ref WholeRow rows = ref MemoryMarshal.GetArrayDataReference(plan.Rows);

        Vector128<T> c0 = Vector128.LoadUnsafe(ref packed);
        Vector128<T> c1 = Vector128.LoadUnsafe(ref packed, step);
        Vector128<T> c2 = Vector128.LoadUnsafe(ref packed, 2 * step);
        Vector128<T> c3 = Vector128.LoadUnsafe(ref packed, 3 * step);
        Vector128<T> c4 = Vector128.LoadUnsafe(ref packed, 4 * step);
        Vector128<T> c5 = Vector128.LoadUnsafe(ref packed, 5 * step);
        Vector128<T> c6 = Vector128.LoadUnsafe(ref packed, 6 * step);
        Vector128<T> c7 = Vector128.LoadUnsafe(ref packed, 7 * step);

        for (; Unsafe.IsAddressLessThan(ref word, ref lastWord); word = ref Unsafe.Add(ref word, 1))
        {
            Word(
                ref packed, ref output, in word, ref rows, mask, c0, c1, c2, c3, c4, c5, c6, c7,
                out c0, out c1, out c2, out c3, out c4, out c5, out c6, out c7);
        }
    }

    /// <summary>One packed word's rows: the whole ones from its line, then the one reaching its last bit, with the next word's line, which it hands back.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Word<T>(
        ref T packed, ref T output, in WordStep word, ref WholeRow rows, Vector128<T> mask,
        Vector128<T> c0, Vector128<T> c1, Vector128<T> c2, Vector128<T> c3,
        Vector128<T> c4, Vector128<T> c5, Vector128<T> c6, Vector128<T> c7,
        out Vector128<T> n0, out Vector128<T> n1, out Vector128<T> n2, out Vector128<T> n3,
        out Vector128<T> n4, out Vector128<T> n5, out Vector128<T> n6, out Vector128<T> n7)
        where T : unmanaged
    {
        nuint step = (nuint)Vector128<T>.Count;
        ref T next = ref Unsafe.AddByteOffset(ref packed, word.Next);
        n0 = Vector128.LoadUnsafe(ref next);
        n1 = Vector128.LoadUnsafe(ref next, step);
        n2 = Vector128.LoadUnsafe(ref next, 2 * step);
        n3 = Vector128.LoadUnsafe(ref next, 3 * step);
        n4 = Vector128.LoadUnsafe(ref next, 4 * step);
        n5 = Vector128.LoadUnsafe(ref next, 5 * step);
        n6 = Vector128.LoadUnsafe(ref next, 6 * step);
        n7 = Vector128.LoadUnsafe(ref next, 7 * step);

        ref WholeRow row = ref Unsafe.Add(ref rows, word.FirstRow);
        ref WholeRow lastRow = ref Unsafe.Add(ref rows, word.EndRow);
        for (; Unsafe.IsAddressLessThan(ref row, ref lastRow); row = ref Unsafe.Add(ref row, 1))
        {
            ref T into = ref Unsafe.AddByteOffset(ref output, row.Destination);
            Vector128<byte> count = row.RightCount;
            int shift = row.Right;
            (ShiftBy(c0, count, shift) & mask).StoreUnsafe(ref into);
            (ShiftBy(c1, count, shift) & mask).StoreUnsafe(ref into, step);
            (ShiftBy(c2, count, shift) & mask).StoreUnsafe(ref into, 2 * step);
            (ShiftBy(c3, count, shift) & mask).StoreUnsafe(ref into, 3 * step);
            (ShiftBy(c4, count, shift) & mask).StoreUnsafe(ref into, 4 * step);
            (ShiftBy(c5, count, shift) & mask).StoreUnsafe(ref into, 5 * step);
            (ShiftBy(c6, count, shift) & mask).StoreUnsafe(ref into, 6 * step);
            (ShiftBy(c7, count, shift) & mask).StoreUnsafe(ref into, 7 * step);
        }

        ref T last = ref Unsafe.AddByteOffset(ref output, word.Destination);
        Vector128<byte> right = word.RightCount;
        Vector128<byte> left = word.LeftCount;
        Vector128<T> upper = word.Upper.As<byte, T>();
        int down = word.Right;
        int up = word.Left;
        Across(c0, n0, right, left, upper, down, up, mask).StoreUnsafe(ref last);
        Across(c1, n1, right, left, upper, down, up, mask).StoreUnsafe(ref last, step);
        Across(c2, n2, right, left, upper, down, up, mask).StoreUnsafe(ref last, 2 * step);
        Across(c3, n3, right, left, upper, down, up, mask).StoreUnsafe(ref last, 3 * step);
        Across(c4, n4, right, left, upper, down, up, mask).StoreUnsafe(ref last, 4 * step);
        Across(c5, n5, right, left, upper, down, up, mask).StoreUnsafe(ref last, 5 * step);
        Across(c6, n6, right, left, upper, down, up, mask).StoreUnsafe(ref last, 6 * step);
        Across(c7, n7, right, left, upper, down, up, mask).StoreUnsafe(ref last, 7 * step);
    }

    // On Arm a shift by a vector of counts takes the count as data, laid out ahead; elsewhere the
    // count is broadcast, once a row since every vector of the row shares it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftBy<T>(Vector128<T> value, Vector128<byte> count, int right)
        where T : unmanaged
    {
        if (!AdvSimd.IsSupported)
        {
            return ShiftRight(value, right);
        }

        if (typeof(T) == typeof(byte))
        {
            return AdvSimd.ShiftLogical(value.AsByte(), count.AsSByte()).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return AdvSimd.ShiftLogical(value.AsUInt16(), count.AsInt16()).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return AdvSimd.ShiftLogical(value.AsUInt32(), count.AsInt32()).As<uint, T>();
        }

        return AdvSimd.ShiftLogical(value.AsUInt64(), count.AsInt64()).As<ulong, T>();
    }

    // The row reaching a word's last bit: its low bits from this word shifted down, whose bits above
    // them are already clear, and from the next word shifted up only the bits `upper` selects, up to
    // the width, so that no mask is needed after; on Arm the selection is one instruction.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> Across<T>(
        Vector128<T> low, Vector128<T> high, Vector128<byte> rightCount, Vector128<byte> leftCount,
        Vector128<T> upper, int right, int left, Vector128<T> mask)
        where T : unmanaged =>
        AdvSimd.IsSupported
            ? Vector128.ConditionalSelect(upper, ShiftBy(high, leftCount, left), ShiftBy(low, rightCount, right))
            : (ShiftRight(low, right) | ShiftLeft(high, left)) & mask;

    /// <summary>The steps of one bit width, built at the first use of the width and kept for the process.</summary>
    private static WordPlan WordPlanOf<T>(int bitWidth)
        where T : unmanaged
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        WordPlan?[] widths = Volatile.Read(ref WordPlans<T>.ByWidth)
            ?? Interlocked.CompareExchange(ref WordPlans<T>.ByWidth, new WordPlan?[elementBits + 1], null)
            ?? WordPlans<T>.ByWidth!;
        WordPlan? plan = Volatile.Read(ref widths[bitWidth]);
        if (plan is null)
        {
            plan = new WordPlan(bitWidth, elementBits);
            Volatile.Write(ref widths[bitWidth], plan);
        }

        return plan;
    }

    /// <summary>The word plans of each bit width of one element type; no initializer, so no static constructor.</summary>
    private static class WordPlans<T>
        where T : unmanaged
    {
        internal static WordPlan?[]? ByWidth;
    }

    /// <summary>A row that lies inside one packed word: how far its word is shifted, and where it lands.</summary>
    private readonly struct WholeRow(nint destination, int right, Vector128<byte> rightCount)
    {
        /// <summary>The row's first value, in bytes from the block's first.</summary>
        internal readonly nint Destination = destination;

        /// <summary>The bit the row starts at in its word.</summary>
        internal readonly int Right = right;

        /// <summary><see cref="Right"/> as a vector of negative counts, a shift right on Arm.</summary>
        internal readonly Vector128<byte> RightCount = rightCount;
    }

    /// <summary>One packed word: its whole rows, then the row that reaches its last bit, read with the next word.</summary>
    private readonly struct WordStep(
        int firstRow, int endRow, nint next, nint destination, int right, int left,
        Vector128<byte> rightCount, Vector128<byte> leftCount, Vector128<byte> upper)
    {
        /// <summary>The word's first whole row in the plan's rows.</summary>
        internal readonly int FirstRow = firstRow;

        /// <summary>One past the word's last whole row.</summary>
        internal readonly int EndRow = endRow;

        /// <summary>The next word's line, in bytes from the block's first packed word: this word's own for the last word.</summary>
        internal readonly nint Next = next;

        /// <summary>The row reaching the word's last bit, its first value in bytes from the block's first.</summary>
        internal readonly nint Destination = destination;

        /// <summary>The bit that row starts at in this word.</summary>
        internal readonly int Right = right;

        /// <summary>How far the next word is shifted up under it: the element width less <see cref="Right"/>.</summary>
        internal readonly int Left = left;

        internal readonly Vector128<byte> RightCount = rightCount;

        internal readonly Vector128<byte> LeftCount = leftCount;

        /// <summary>The bits of the row the next word supplies, from <see cref="Left"/> up to the width; none when the row ends with this word.</summary>
        internal readonly Vector128<byte> Upper = upper;
    }

    /// <summary>The steps of a bit width, and the whole rows they point into.</summary>
    private sealed class WordPlan
    {
        internal WordPlan(int bitWidth, int elementBits)
        {
            int elementBytes = elementBits / 8;
            Steps = new WordStep[bitWidth];
            Rows = new WholeRow[elementBits - bitWidth];
            int row = 0;
            int whole = 0;
            for (int word = 0; word < bitWidth; word++)
            {
                int first = whole;
                while ((row * bitWidth) + bitWidth < (word + 1) * elementBits)
                {
                    int shift = row * bitWidth % elementBits;
                    Rows[whole++] = new WholeRow(Destination(row, elementBytes), shift, Counts(-shift, elementBits));
                    row++;
                }

                int right = row * bitWidth % elementBits;
                int left = elementBits - right;
                nint next = LineBytes * (word + 1 < bitWidth ? word + 1 : word);
                ulong upper = LowBits(bitWidth) & ~LowBits(left);
                Steps[word] = new WordStep(
                    first, whole, next, Destination(row, elementBytes), right, left,
                    Counts(-right, elementBits), Counts(left, elementBits), Broadcast(upper, elementBits));
                row++;
            }
        }

        internal WordStep[] Steps { get; }

        internal WholeRow[] Rows { get; }

        private static nint Destination(int row, int elementBytes) =>
            ((Order[row / 8] * 16) + (row % 8 * 128)) * elementBytes;

        // A shift count in every element of the element type's width, as an Arm shift by vector reads it.
        private static Vector128<byte> Counts(int count, int elementBits) => elementBits switch
        {
            8 => Vector128.Create((sbyte)count).AsByte(),
            16 => Vector128.Create((short)count).AsByte(),
            32 => Vector128.Create(count).AsByte(),
            _ => Vector128.Create((long)count).AsByte(),
        };

        private static Vector128<byte> Broadcast(ulong value, int elementBits) => elementBits switch
        {
            8 => Vector128.Create((byte)value),
            16 => Vector128.Create((ushort)value).AsByte(),
            32 => Vector128.Create((uint)value).AsByte(),
            _ => Vector128.Create(value).AsByte(),
        };

        // Both counts stay under 64: a width is under its element's bits, and so is a shift up.
        private static ulong LowBits(int count) => (1UL << count) - 1;
    }
}
