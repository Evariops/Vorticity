using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Compute;

/// <summary>
/// A text column against a literal, decided from each row's view where the view holds enough.
/// </summary>
/// <remarks>
/// <para>
/// A view is two little-endian words: the value's length and its first four bytes, then either the
/// next eight bytes, for a value of twelve bytes or fewer, or where the rest lives. So a row whose
/// length or first bytes differ from the literal's is decided without its bytes being looked up,
/// and a row held inline is decided whatever it holds. Only a row held out of line whose first
/// bytes agree reads its value, which for a selective literal is a few rows.
/// </para>
/// <para>
/// The bytes of a view past its value's length are masked out rather than trusted to be zero: a
/// view built elsewhere need not have cleared them. A null row's view is not trusted at all: its
/// value is only looked up once its validity says it has one, and its state is overwritten with
/// <see cref="Trilean.Unknown"/> after the pass.
/// </para>
/// </remarks>
internal static partial class ComparisonKernels
{
    private const int ViewBytes = 16;

    private const int InlineLength = 12;

    /// <summary>
    /// The high <c>k</c> bytes of a big-endian key of four bytes, at index <c>k</c>: a value's first
    /// bytes, as far as its length reaches.
    /// </summary>
    private static ReadOnlySpan<uint> HeadKeyMasks => [0, 0xFF00_0000, 0xFFFF_0000, 0xFFFF_FF00, 0xFFFF_FFFF];

    /// <summary>The high <c>k</c> bytes of a big-endian key of eight bytes, at index <c>k</c>.</summary>
    private static ReadOnlySpan<ulong> TailKeyMasks =>
    [
        0,
        0xFF00_0000_0000_0000,
        0xFFFF_0000_0000_0000,
        0xFFFF_FF00_0000_0000,
        0xFFFF_FFFF_0000_0000,
        0xFFFF_FFFF_FF00_0000,
        0xFFFF_FFFF_FFFF_0000,
        0xFFFF_FFFF_FFFF_FF00,
        0xFFFF_FFFF_FFFF_FFFF,
    ];

    /// <summary>
    /// The bytes of an out-of-line row from <paramref name="from"/> on, read from the buffer and
    /// offset of the view in hand when the value lies in the first buffer, else resolved by row.
    /// </summary>
    /// <param name="values">The column's values, for a row in another buffer or out of bounds.</param>
    /// <param name="heap">The column's first data buffer, or empty.</param>
    /// <param name="view">The row's view.</param>
    /// <param name="row">The row, for the resolution.</param>
    /// <param name="from">The bytes the view already settled, at most four.</param>
    /// <remarks>
    /// A value running past its buffer takes the resolution, which reports the malformed view.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> OutOfLine(
        in ViewValues values, ReadOnlySpan<byte> heap, ref byte view, int row, int from)
    {
        uint size = Unsafe.ReadUnaligned<uint>(ref view);
        uint buffer = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 8));
        uint offset = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12));
        if (buffer == 0 && (ulong)offset + size <= (ulong)heap.Length)
        {
            return MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), (nint)offset + from), (int)size - from);
        }

        return values.At(row)[from..];
    }

    /// <summary>
    /// Where an out-of-line row's bytes from <paramref name="from"/> on start in the first buffer,
    /// when its value lies there and <see cref="Window.Size"/> bytes can be read from that point,
    /// its own and whatever follows it; otherwise a null reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte Readable(ReadOnlySpan<byte> heap, ref byte view, int from)
    {
        uint size = Unsafe.ReadUnaligned<uint>(ref view);
        uint buffer = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 8));
        ulong offset = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 12));
        if (buffer == 0 && offset + size <= (ulong)heap.Length && offset + (uint)from + Window.Size <= (ulong)heap.Length)
        {
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), (nint)offset + from);
        }

        return ref Unsafe.NullRef<byte>();
    }

    /// <summary>
    /// -1, 0 or 1 as <paramref name="left"/> is below, equal to or above <paramref name="right"/>,
    /// from two flags rather than a branch: which side of a literal a row falls follows the data,
    /// and a branch on it mispredicts as often as the data is irregular.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sign<T>(T left, T right)
        where T : IComparisonOperators<T, T, bool> =>
        (left > right ? 1 : 0) - (left < right ? 1 : 0);

    /// <summary>The low <paramref name="count"/> bits of a word of 32, up to all of them.</summary>
    private static uint LowBits(int count) => count >= 32 ? uint.MaxValue : (1u << count) - 1;

    /// <summary>
    /// The first <see cref="Size"/> bytes of a literal past those a view settles, zero-padded, for
    /// testing a row read out of line for equality or a prefix in two vector compares rather than
    /// a library call: a column of values alike far into their bytes, URLs or paths, leaves every
    /// row to this. An order is left to the library's compare, which measured faster there.
    /// </summary>
    private readonly struct Window
    {
        internal const int Size = 32;

        private readonly Vector128<byte> _low;
        private readonly Vector128<byte> _high;

        internal Window(ReadOnlySpan<byte> bytes)
        {
            Span<byte> padded = stackalloc byte[Size];
            padded.Clear();
            bytes[..Math.Min(bytes.Length, Size)].CopyTo(padded);
            _low = Vector128.Create<byte>(padded[..16]);
            _high = Vector128.Create<byte>(padded[16..]);
        }

        /// <summary>Bit <c>i</c> set where byte <c>i</c> from <paramref name="at"/> differs from the literal's.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint Differences(ref byte at) =>
            (~Vector128.Equals(Vector128.LoadUnsafe(ref at), _low)).ExtractMostSignificantBits()
            | ((~Vector128.Equals(Vector128.LoadUnsafe(ref at, 16), _high)).ExtractMostSignificantBits() << 16);
    }

    /// <summary>The low <paramref name="count"/> bytes of a word.</summary>
    private static ulong LowBytes(int count) => count >= 8 ? ulong.MaxValue : (1UL << (8 * count)) - 1;

    /// <summary>
    /// <c>column = literal</c>, or <c>column &lt;&gt; literal</c>, over a varbinview column: the length and
    /// the first four bytes in one masked compare of the view's first word, then the next eight in
    /// one of its second when the literal fits a view, else the rest of the value.
    /// </summary>
    /// <param name="node">The column.</param>
    /// <param name="mask">Its validity.</param>
    /// <param name="wanted">The literal.</param>
    /// <param name="equal">Whether a row equal to the literal is true, or false.</param>
    /// <param name="destination">One state per row.</param>
    private static void EqualViews(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<byte> wanted, bool equal, Span<byte> destination)
    {
        int rows = destination.Length;
        Span<byte> padded = stackalloc byte[InlineLength];
        padded.Clear();
        wanted[..Math.Min(wanted.Length, InlineLength)].CopyTo(padded);

        // The length is the view's low half, so a row of another length fails the first compare.
        ulong head = (uint)wanted.Length | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(padded) << 32);
        ulong headMask = uint.MaxValue | (LowBytes(Math.Min(wanted.Length, 4)) << 32);
        ulong tail = BinaryPrimitives.ReadUInt64LittleEndian(padded[4..]);
        ulong tailMask = LowBytes(Math.Clamp(wanted.Length - 4, 0, 8));
        bool inline = wanted.Length <= InlineLength;
        ReadOnlySpan<byte> rest = wanted[Math.Min(wanted.Length, 4)..];
        Window window = new Window(rest);
        uint restBits = LowBits(rest.Length);

        ViewValues values = new ViewValues(node);
        ReadOnlySpan<byte> heap = node.DataBufferCount >= 1 ? node.GetDataBuffer(0).Span : default;
        ref byte view0 = ref MemoryMarshal.GetReference(node.Views.Span[..(rows * ViewBytes)]);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        byte hit = equal ? Trilean.True : Trilean.False;
        byte miss = equal ? Trilean.False : Trilean.True;
        bool allValid = mask.AllValid;
        for (int i = 0; i < rows; i++)
        {
            ref byte view = ref Unsafe.Add(ref view0, i * ViewBytes);
            bool same;
            if (((Unsafe.ReadUnaligned<ulong>(ref view) ^ head) & headMask) != 0)
            {
                same = false;
            }
            else if (inline)
            {
                same = ((Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref view, 8)) ^ tail) & tailMask) == 0;
            }
            else if (allValid || mask.IsValid(i))
            {
                // The length and the first four bytes agree, and the value is out of line.
                ref byte at = ref Readable(heap, ref view, 4);
                same = Unsafe.IsNullRef(ref at)
                    ? OutOfLine(values, heap, ref view, i, 4).SequenceEqual(rest)
                    : (window.Differences(ref at) & restBits) == 0
                        && (rest.Length <= Window.Size || OutOfLine(values, heap, ref view, i, 4 + Window.Size).SequenceEqual(rest[Window.Size..]));
            }
            else
            {
                same = false;
            }

            Unsafe.Add(ref into, i) = same ? hit : miss;
        }

        if (!allValid)
        {
            MarkUnknown(mask.Bits, mask.BitOffset, destination);
        }
    }

    /// <summary>
    /// <c>column &lt; literal</c> and the other orders, over a varbinview column: the first four
    /// bytes as a big-endian key decide a row whose first bytes differ, and the next eight a row
    /// held inline; only a row out of line whose first four bytes agree reads its value.
    /// </summary>
    /// <remarks>
    /// Ordinal byte order, which for utf8 is also code-point order: UTF-8 is designed so that
    /// memcmp of the encoded bytes equals comparison of the code points. Never a culture-aware
    /// string comparison.
    /// Each key keeps the bytes its value has and zeros past them. Two keys that differ order their
    /// values as bytewise order does: at the first byte they differ, either both values have it,
    /// or one has ended and the other's byte, not being the zero that stands for the end, is
    /// larger, which is the order of a prefix before what extends it. Two values whose keys agree
    /// over all twelve bytes differ only in zeros past the shorter one, so the longer is larger.
    /// </remarks>
    private static void OrderViews<TOp>(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<byte> wanted, Span<byte> destination)
        where TOp : struct, IOrderOp
    {
        int rows = destination.Length;
        Span<byte> padded = stackalloc byte[InlineLength];
        padded.Clear();
        wanted[..Math.Min(wanted.Length, InlineLength)].CopyTo(padded);
        uint headKey = BinaryPrimitives.ReadUInt32BigEndian(padded);
        ulong tailKey = BinaryPrimitives.ReadUInt64BigEndian(padded[4..]);
        uint length = (uint)wanted.Length;

        // A row read out of line agrees with the literal on its first bytes, as far as either
        // reaches into four: the rest decides.
        int shared = Math.Min(wanted.Length, 4);
        ReadOnlySpan<byte> after = wanted[shared..];

        ViewValues values = new ViewValues(node);
        ReadOnlySpan<byte> heap = node.DataBufferCount >= 1 ? node.GetDataBuffer(0).Span : default;
        ref byte view0 = ref MemoryMarshal.GetReference(node.Views.Span[..(rows * ViewBytes)]);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        ref uint headMasks = ref MemoryMarshal.GetReference(HeadKeyMasks);
        ref ulong tailMasks = ref MemoryMarshal.GetReference(TailKeyMasks);
        bool allValid = mask.AllValid;
        for (int i = 0; i < rows; i++)
        {
            ref byte view = ref Unsafe.Add(ref view0, i * ViewBytes);
            uint size = Unsafe.ReadUnaligned<uint>(ref view);
            uint key = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref view, 4)))
                & Unsafe.Add(ref headMasks, (nint)Math.Min(size, 4u));
            int order;
            if (key != headKey)
            {
                order = Sign(key, headKey);
            }
            else if (size <= InlineLength)
            {
                ulong tail = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref view, 8)))
                    & Unsafe.Add(ref tailMasks, (nint)(size <= 4 ? 0 : size - 4));
                int byTail = Sign(tail, tailKey);
                order = byTail != 0 ? byTail : Sign(size, length);
            }
            else if (allValid || mask.IsValid(i))
            {
                order = OutOfLine(values, heap, ref view, i, shared).SequenceCompareTo(after);
            }
            else
            {
                order = 0;
            }

            Unsafe.Add(ref into, i) = TOp.Holds(order, 0) ? Trilean.True : Trilean.False;
        }

        if (!allValid)
        {
            MarkUnknown(mask.Bits, mask.BitOffset, destination);
        }
    }

    /// <summary>
    /// <c>StartsWith</c> over a varbinview column: a row shorter than the pattern, or whose first
    /// bytes differ from it, fails from its view, a row held inline is decided by its view, and a
    /// row out of line is decided by its view when the pattern is four bytes or fewer.
    /// </summary>
    private static void StartsWithViews(
        CanonicalNode node, ValidityMask mask, ReadOnlySpan<byte> pattern, Span<byte> destination)
    {
        int rows = destination.Length;
        Span<byte> padded = stackalloc byte[InlineLength];
        padded.Clear();
        pattern[..Math.Min(pattern.Length, InlineLength)].CopyTo(padded);
        ulong head = BinaryPrimitives.ReadUInt32LittleEndian(padded);
        ulong headMask = LowBytes(Math.Min(pattern.Length, 4));
        ulong tail = BinaryPrimitives.ReadUInt64LittleEndian(padded[4..]);
        ulong tailMask = LowBytes(Math.Clamp(pattern.Length - 4, 0, 8));
        uint length = (uint)pattern.Length;
        ReadOnlySpan<byte> rest = pattern[Math.Min(pattern.Length, 4)..];
        Window window = new Window(rest);
        uint restBits = LowBits(rest.Length);

        ViewValues values = new ViewValues(node);
        ReadOnlySpan<byte> heap = node.DataBufferCount >= 1 ? node.GetDataBuffer(0).Span : default;
        ref byte view0 = ref MemoryMarshal.GetReference(node.Views.Span[..(rows * ViewBytes)]);
        ref byte into = ref MemoryMarshal.GetReference(destination);
        bool allValid = mask.AllValid;
        for (int i = 0; i < rows; i++)
        {
            ref byte view = ref Unsafe.Add(ref view0, i * ViewBytes);
            ulong first = Unsafe.ReadUnaligned<ulong>(ref view);
            uint size = (uint)first;
            bool holds;
            if (size < length || (((first >> 32) ^ head) & headMask) != 0)
            {
                holds = false;
            }
            else if (size <= InlineLength)
            {
                // The pattern is no longer than the value, so it fits the view too.
                holds = ((Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref view, 8)) ^ tail) & tailMask) == 0;
            }
            else if (rest.IsEmpty)
            {
                holds = true;
            }
            else if (allValid || mask.IsValid(i))
            {
                // The value, out of line, is at least as long as the pattern.
                ref byte at = ref Readable(heap, ref view, 4);
                holds = Unsafe.IsNullRef(ref at)
                    ? OutOfLine(values, heap, ref view, i, 4)[..rest.Length].SequenceEqual(rest)
                    : (window.Differences(ref at) & restBits) == 0
                        && (rest.Length <= Window.Size || OutOfLine(values, heap, ref view, i, 4 + Window.Size)[..(rest.Length - Window.Size)].SequenceEqual(rest[Window.Size..]));
            }
            else
            {
                holds = false;
            }

            Unsafe.Add(ref into, i) = holds ? Trilean.True : Trilean.False;
        }

        if (!allValid)
        {
            MarkUnknown(mask.Bits, mask.BitOffset, destination);
        }
    }
}
