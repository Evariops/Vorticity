using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class VortexBufferTests
{
    /// <summary>
    /// The 16-byte inline struct of docs/02-format.md §3, the exact shape
    /// <c>VortexBuffer.Cast</c> exists to serve. Field order and widths are transcribed from
    /// spec/flatbuffers/footer.fbs.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SegmentSpecLike
    {
        public ulong Offset;              //  0..8
        public uint SegmentLength;        //  8..12
        public byte AlignmentExponent;    // 12..13
        public byte Compression;          // 13..14
        public ushort Encryption;         // 14..16
    }

    /// <summary>The 8-byte <c>Buffer</c> struct of spec/flatbuffers/array.fbs.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BufferLike
    {
        public ushort Padding;            // 0..2
        public byte AlignmentExponent;    // 2..3
        public byte Compression;          // 3..4
        public uint BufferLength;         // 4..8
    }

    [Fact]
    public void The_wire_structs_are_the_sizes_the_format_promises()
    {
        // docs/09-contracts.md §7: "A static assert verifies sizeof(SegmentSpec) == 16 and
        // sizeof(BufferSpec) == 8."
        Assert.Equal(16, Unsafe.SizeOf<SegmentSpecLike>());
        Assert.Equal(8, Unsafe.SizeOf<BufferLike>());
    }

    [Fact]
    public void VortexBuffer_stays_a_pointer_plus_two_ints()
    {
        // A fat view would be copied into every array node of every batch. If this grows, the
        // arena design of docs/03-architecture.md §3.3 pays for it on every column.
        if (IntPtr.Size == 8)
        {
            Assert.Equal(16, Unsafe.SizeOf<VortexBuffer>());
        }
    }

    [Fact]
    public void Empty_is_a_valid_zero_length_view()
    {
        VortexBuffer empty = VortexBuffer.Empty;

        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.Length);
        Assert.Equal(1, empty.Alignment);
        Assert.Equal(0, empty.AlignmentExponent);
        Assert.True(empty.IsAligned);
        Assert.True(empty.Span.IsEmpty);
        Assert.True(empty.Cast<SegmentSpecLike>().IsEmpty);
        Assert.Equal(default, empty);
    }

    [Fact]
    public unsafe void FromPointer_rejects_a_negative_length() =>
        Assert.Throws<VortexFormatException>(static () =>
            VortexBuffer.FromPointer(null, -1, 0));

    [Fact]
    public unsafe void FromPointer_rejects_int_MinValue_length() =>
        Assert.Throws<VortexFormatException>(static () =>
            VortexBuffer.FromPointer(null, int.MinValue, 0));

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(255)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public unsafe void FromPointer_rejects_an_alignment_exponent_past_the_cap(int exponent)
    {
        // docs/08-semantics.md §6: alignment_exponent is a u8 on the wire, so an unchecked file
        // can demand 2^255 and 1 << 255 would silently be 1 << 31 after the shift is masked.
        Assert.Throws<VortexFormatException>(() => VortexBuffer.FromPointer(null, 0, exponent));
    }

    [Fact]
    public unsafe void FromPointer_refuses_a_null_base_with_a_non_zero_length() =>
        Assert.Throws<VortexFormatException>(static () => VortexBuffer.FromPointer(null, 1, 0));

    [Fact]
    public unsafe void FromPointer_accepts_a_null_base_with_a_zero_length()
    {
        VortexBuffer buffer = VortexBuffer.FromPointer(null, 0, 6);

        Assert.True(buffer.IsEmpty);
        Assert.Equal(64, buffer.Alignment);
    }

    [Fact]
    public void FromPinned_views_the_bytes_without_copying()
    {
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf([1, 2, 3, 4], 64);
        VortexBuffer buffer = VortexBuffer.FromPinned(owner.Buffer.Span, 6);

        Assert.Equal(4, buffer.Length);
        Assert.Equal(64, buffer.Alignment);
        Assert.True(buffer.IsAligned);
        Assert.True(buffer.Span.SequenceEqual(owner.Buffer.Span));
    }

    [Fact]
    public void FromPinned_of_an_empty_span_keeps_the_declared_alignment()
    {
        VortexBuffer buffer = VortexBuffer.FromPinned(default, 4);

        Assert.True(buffer.IsEmpty);
        Assert.Equal(16, buffer.Alignment);
        Assert.Equal(4, buffer.AlignmentExponent);
    }

    [Fact]
    public void FromPinned_rejects_an_alignment_exponent_past_the_cap() =>
        Assert.Throws<VortexFormatException>(static () => VortexBuffer.FromPinned(default, 7));

    [Fact]
    public void Slice_accepts_both_endpoints()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(64, 64);
        VortexBuffer buffer = owner.Buffer;

        Assert.Equal(64, buffer.Slice(0).Length);
        Assert.Equal(0, buffer.Slice(64).Length);
        Assert.Equal(1, buffer.Slice(63).Length);
        Assert.Equal(0, buffer.Slice(0, 0).Length);
        Assert.Equal(64, buffer.Slice(0, 64).Length);
        Assert.Equal(0, buffer.Slice(64, 0).Length);
        Assert.Equal(1, buffer.Slice(63, 1).Length);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Slice_rejects_an_offset_outside_the_buffer(int offset)
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(64, 64);
        Assert.Throws<VortexFormatException>(() => owner.Buffer.Slice(offset));
    }

    [Theory]
    [InlineData(0, 65)]
    [InlineData(1, 64)]
    [InlineData(64, 1)]
    [InlineData(0, -1)]
    [InlineData(-1, 0)]
    [InlineData(32, int.MaxValue)]      // offset + length overflows int if added naively
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MinValue)]
    [InlineData(int.MinValue, int.MinValue)]
    public void Slice_rejects_a_range_outside_the_buffer(int offset, int length)
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(64, 64);
        Assert.Throws<VortexFormatException>(() => owner.Buffer.Slice(offset, length));
    }

    [Fact]
    public void Slice_keeps_the_declared_alignment_and_tells_the_truth_about_the_address()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(128, 64);
        VortexBuffer buffer = owner.Buffer;

        Assert.True(buffer.IsAligned);

        VortexBuffer misaligned = buffer.Slice(1);
        Assert.Equal(6, misaligned.AlignmentExponent);
        Assert.Equal(64, misaligned.Alignment);
        Assert.False(misaligned.IsAligned);

        VortexBuffer realigned = buffer.Slice(64);
        Assert.True(realigned.IsAligned);
    }

    [Fact]
    public void Slice_of_a_slice_composes()
    {
        using PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.Allocate(16, 16);
        for (int i = 0; i < 16; i++)
        {
            owner.WritableSpan[i] = (byte)i;
        }

        VortexBuffer inner = owner.Buffer.Slice(4).Slice(2, 3);

        Assert.Equal(3, inner.Length);
        Assert.Equal(6, inner.Span[0]);
        Assert.Equal(8, inner.Span[2]);
        Assert.Throws<VortexFormatException>(() => inner.Slice(4));
    }

    [Fact]
    public void Cast_reinterprets_the_bytes_in_place()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(32, 64);
        Span<byte> writable = owner.WritableSpan;
        MemoryMarshal.Write(writable[..8], (ulong)0x0102030405060708UL);
        MemoryMarshal.Write(writable.Slice(8, 4), 0x11223344u);
        writable[12] = 6;
        writable[13] = 0;
        MemoryMarshal.Write(writable.Slice(14, 2), (ushort)0);
        writable[16..].Clear();

        ReadOnlySpan<SegmentSpecLike> specs = owner.Buffer.Cast<SegmentSpecLike>();

        Assert.Equal(2, specs.Length);
        Assert.Equal(0x0102030405060708UL, specs[0].Offset);
        Assert.Equal(0x11223344u, specs[0].SegmentLength);
        Assert.Equal(6, specs[0].AlignmentExponent);
        Assert.Equal(0UL, specs[1].Offset);
    }

    [Fact]
    public void Cast_is_actually_zero_copy()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(32, 64);
        ReadOnlySpan<SegmentSpecLike> specs = owner.Buffer.Cast<SegmentSpecLike>();

        // Performance invariant #2 of docs/03-architecture.md §4, checked by pointer identity.
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(owner.Buffer.Span),
            ref Unsafe.As<SegmentSpecLike, byte>(ref MemoryMarshal.GetReference(specs))));
    }

    [Fact]
    public void Cast_rejects_a_ragged_length()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(40, 64);

        // 40 bytes is two whole specs plus 8 stray bytes. Truncating would silently drop a
        // segment from the map; that is the bug this rejection exists to prevent.
        Assert.Throws<VortexFormatException>(() => { _ = owner.Buffer.Cast<SegmentSpecLike>(); });
        Assert.False(owner.Buffer.TryCast(out ReadOnlySpan<SegmentSpecLike> _));

        Assert.Equal(5, owner.Buffer.Cast<BufferLike>().Length);
    }

    [Fact]
    public void Cast_rejects_a_misaligned_base()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(64, 64);

        Assert.Throws<VortexFormatException>(() => { _ = owner.Buffer.Slice(1, 32).Cast<uint>(); });
        Assert.Throws<VortexFormatException>(() => { _ = owner.Buffer.Slice(2, 32).Cast<uint>(); });
        Assert.Throws<VortexFormatException>(() => { _ = owner.Buffer.Slice(8, 32).Cast<SegmentSpecLike>(); });

        // ...but a genuinely aligned slice is accepted.
        Assert.Equal(8, owner.Buffer.Slice(4, 32).Cast<uint>().Length);
        Assert.Equal(2, owner.Buffer.Slice(16, 32).Cast<SegmentSpecLike>().Length);
    }

    [Fact]
    public void TryCast_reports_failure_instead_of_throwing()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(64, 64);

        Assert.False(owner.Buffer.Slice(1, 32).TryCast(out ReadOnlySpan<uint> misaligned));
        Assert.True(misaligned.IsEmpty);

        Assert.False(owner.Buffer.Slice(0, 33).TryCast(out ReadOnlySpan<uint> ragged));
        Assert.True(ragged.IsEmpty);

        Assert.True(owner.Buffer.Slice(0, 32).TryCast(out ReadOnlySpan<uint> good));
        Assert.Equal(8, good.Length);
    }

    [Fact]
    public void Cast_to_byte_always_succeeds()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(37, 64);

        Assert.Equal(36, owner.Buffer.Slice(1).Cast<byte>().Length);
        Assert.Equal(37, owner.Buffer.Cast<byte>().Length);
    }

    [Fact]
    public void Cast_of_an_empty_buffer_is_an_empty_span()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(64, 64);
        VortexBuffer empty = owner.Buffer.Slice(64);

        Assert.True(empty.Cast<SegmentSpecLike>().IsEmpty);
        Assert.True(empty.Cast<ulong>().IsEmpty);
        Assert.True(empty.TryCast(out ReadOnlySpan<SegmentSpecLike> _));
    }

    [Fact]
    public void Written_bytes_are_visible_through_the_read_only_view()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(16, 64);
        MemoryMarshal.Write(owner.WritableSpan, 0xDEADBEEFu);

        Assert.Equal(0xDEADBEEFu, owner.Buffer.Cast<uint>()[0]);

        // Little-endian, as docs/02-format.md §1 mandates and VortexRuntimeChecks asserts.
        Assert.Equal(0xEF, owner.Buffer.Span[0]);
        Assert.Equal(0xDE, owner.Buffer.Span[3]);
    }
}
