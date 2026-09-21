// A u8 column, written and read back.
//
// Found while writing something else: giving VortexFileWriter a `u8` primitive column produced a
// file whose own reader rejected it with "fastlanes.bitpacked bit width 9 exceeds the 8 bits of u8".
// NOTHING IN THE CORPUS CAUSES A u8 PRIMITIVE COLUMN TO BE WRITTEN, so `BitPackPlan`'s narrowest
// path had never run - the 774-file round-trip sweep and the Rust cross-check both read files the
// REFERENCE wrote, and the reference does not produce this shape either.
//
// So this is the smallest case that exercises it, and it is a unit test rather than a corpus entry
// for the same reason the bug survived: the corpus cannot be relied on to contain every shape the
// writer can emit.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>Narrow primitive columns survive a write and a read back.</summary>
public sealed class BitPackedWidthTests
{
    [Theory]
    [InlineData(PType.U8)]
    [InlineData(PType.I8)]
    [InlineData(PType.U16)]
    [InlineData(PType.I16)]
    [InlineData(PType.U32)]
    [InlineData(PType.I64)]
    public async Task AColumnOfEveryNarrowWidthReadsBackWhatWasWritten(PType ptype)
    {
        Decoders.EnsureRegistered();

        const int Rows = 4096;
        int width = ptype.ByteWidth();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType dtype = types.Primitive(ptype, Nullability.NonNullable);

        VortexBuffer values = arena.Allocate(Rows * width, width, out Span<byte> destination);
        long[] expected = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            // The full range of the type, so the bit width the planner picks is the widest the type
            // allows. A narrower spread would let a u8 column pack into 3 bits and never reach the
            // boundary the defect sits on.
            long value = ptype switch
            {
                PType.U8 => i % 256,
                PType.I8 => (i % 256) - 128,
                PType.U16 => i % 65536,
                PType.I16 => (i % 65536) - 32768,
                _ => i * 7,
            };

            expected[i] = value;
            WriteInteger(destination, ptype, i, value);
        }

        int root = arena.AddPrimitive(dtype, Rows, Validity.NonNullable, ptype, values);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-width-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, dtype))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch, CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            long[] actual = new long[Rows];
            int seen = 0;
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                seen += Read(batch, ptype, actual.AsSpan(seen));
            }

            Assert.Equal(Rows, seen);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>Writes one value of <paramref name="ptype"/>, little-endian.</summary>
    private static void WriteInteger(Span<byte> bytes, PType ptype, int index, long value)
    {
        switch (ptype)
        {
            case PType.U8:
            case PType.I8:
                bytes[index] = unchecked((byte)value);
                break;
            case PType.U16:
            case PType.I16:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                    bytes.Slice(index * 2, 2), unchecked((ushort)value));
                break;
            case PType.U32:
            case PType.I32:
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.Slice(index * 4, 4), unchecked((uint)value));
                break;
            default:
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                    bytes.Slice(index * 8, 8), value);
                break;
        }
    }

    /// <summary>Reads a batch's values back as <see cref="long"/>, whatever the width.</summary>
    private static int Read(RecordBatch batch, PType ptype, Span<long> destination)
    {
        switch (ptype)
        {
            case PType.U8: return Copy(batch.Root.AsPrimitive<byte>(), destination);
            case PType.I8: return Copy(batch.Root.AsPrimitive<sbyte>(), destination);
            case PType.U16: return Copy(batch.Root.AsPrimitive<ushort>(), destination);
            case PType.I16: return Copy(batch.Root.AsPrimitive<short>(), destination);
            case PType.U32: return Copy(batch.Root.AsPrimitive<uint>(), destination);
            default: return Copy(batch.Root.AsPrimitive<long>(), destination);
        }
    }

    private static int Copy<T>(PrimitiveColumn<T> column, Span<long> destination)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        for (int i = 0; i < column.Length; i++)
        {
            destination[i] = long.CreateTruncating(column[i]);
        }

        return column.Length;
    }

    /// <summary>
    /// The same widths, but with a narrow range and a few outliers - the shape that produces
    /// PATCHES rather than a plain packing.
    /// </summary>
    /// <remarks>
    /// Bit-width defects live here rather than in the uniform case: the planner picks a width from
    /// the bulk of the values and carries whatever does not fit as patches, so the width it chooses
    /// and the width the type allows can disagree. The uniform test above passes for every width,
    /// which is exactly why it is not enough on its own.
    /// </remarks>
    [Theory]
    [InlineData(PType.U8, false)]
    [InlineData(PType.U8, true)]
    [InlineData(PType.I8, false)]
    [InlineData(PType.U16, true)]
    [InlineData(PType.I64, true)]
    public async Task ANarrowColumnWithOutliersReadsBackWhatWasWritten(PType ptype, bool nullable)
    {
        Decoders.EnsureRegistered();

        const int Rows = 4096;
        int width = ptype.ByteWidth();
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType dtype = types.Primitive(ptype, nullable ? Nullability.Nullable : Nullability.NonNullable);

        VortexBuffer values = arena.Allocate(Rows * width, width, out Span<byte> destination);
        long[] expected = new long[Rows];
        for (int i = 0; i < Rows; i++)
        {
            // Mostly three bits' worth, with one outlier every 512 rows at the type's ceiling.
            long value = i % 512 == 511
                ? ptype switch
                {
                    PType.U8 => 255,
                    PType.I8 => 127,
                    PType.U16 => 65535,
                    _ => 1L << 40,
                }
                : i % 7;

            expected[i] = value;
            WriteInteger(destination, ptype, i, value);
        }

        Validity validity = Validity.NonNullable;
        if (nullable)
        {
            // Every 64th row null, so the validity child is a real bitmap rather than a constant.
            int bytes = (Rows + 7) / 8;
            VortexBuffer bits = arena.Allocate(bytes, 1, out Span<byte> bitmap);
            for (int i = 0; i < Rows; i++)
            {
                if (i % 64 != 63)
                {
                    bitmap[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            int bitsNode = arena.AddBool(
                types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0);
            validity = Validity.Bitmap(bitsNode);
        }

        int root = arena.AddPrimitive(dtype, Rows, validity, ptype, values);

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-patch-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, dtype))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch, CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            int seen = 0;
            long[] actual = new long[Rows];
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                seen += Read(batch, ptype, actual.AsSpan(seen));
            }

            Assert.Equal(Rows, seen);
            for (int i = 0; i < Rows; i++)
            {
                if (nullable && i % 64 == 63)
                {
                    continue;
                }

                Assert.Equal(expected[i], actual[i]);
            }
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
