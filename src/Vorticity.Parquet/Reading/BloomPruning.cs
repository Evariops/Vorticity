using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types.Numerics;

namespace Vorticity.Parquet.Reading;

/// <summary>
/// The row groups a scan's filter cannot select, proven by the Bloom filters of the columns its
/// equalities ask about: an equality, or an IN, every value of which a column's filter proves
/// absent from the group selects none of its rows.
/// </summary>
/// <remarks>
/// <para>
/// A value is probed as the filter hashed it: its PLAIN bytes at the column's physical width, a byte
/// array's without its length, by xxHash64. A float's zero is probed as both zeros, a NaN never,
/// a boolean never; a value the column cannot hold exactly is not claimed absent, which is all a
/// probe that fails to encode means.
/// </para>
/// <para>
/// Absence is combined as the filter combines its rows: an AND is absent where either side is, an
/// OR where both are, and a NOT claims nothing, since a filter proves what a column lacks and never
/// what it holds.
/// </para>
/// </remarks>
internal static class BloomPruning
{
    /// <summary>Whether <paramref name="filter"/> asks an equality a Bloom filter may answer.</summary>
    internal static bool Asks(VortexExpr filter) => filter.Kind switch
    {
        ExprKind.Comparison => filter is ComparisonExpr { Op: ComparisonOp.Equal, Field: not FunctionFieldExpr },
        ExprKind.In => filter is InExpr { Field: not FunctionFieldExpr },
        ExprKind.Logical => Asks(((LogicalExpr)filter).Left) || Asks(((LogicalExpr)filter).Right),
        _ => false,
    };

    /// <summary>
    /// Whether the Bloom filters of row group <paramref name="group"/> prove the filter selects none
    /// of its rows, the filters of the columns it asks about read in one request.
    /// </summary>
    internal static async ValueTask<bool> RulesOutAsync(FilterColumns filter, int group, SegmentRequestSet requests, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        ParquetFile file = filter.File;
        ParquetFooter footer = file.Footer;
        int columns = filter.Columns.Length;
        int[] slots = new int[columns];
        int asked = 0;
        long bytes = 0;
        requests.Release();
        for (int i = 0; i < columns; i++)
        {
            slots[i] = -1;
            ColumnChunkMetadata chunk = footer.Chunk(group, filter.Columns[i]);
            if (chunk.BloomFilterOffset < 0 || chunk.IsEncrypted || !Asks(filter.Filter, filter.Fields[i].Path))
            {
                continue;
            }

            long length = Length(file, chunk);
            if (!file.Holds(chunk.BloomFilterOffset, length))
            {
                continue;
            }

            slots[i] = requests.Add(new SegmentSpec((ulong)chunk.BloomFilterOffset, (uint)length, 0, 0, 0));
            bytes += length;
            asked++;
        }

        if (asked == 0)
        {
            return false;
        }

        ScanCounters.Note(metrics, asked, bytes);
        await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        uint[]?[] words = new uint[columns][];
        for (int i = 0; i < columns; i++)
        {
            if (slots[i] < 0)
            {
                continue;
            }

            words[i] = await WordsAsync(file, footer.Chunk(group, filter.Columns[i]), requests.GetBuffer(slots[i]), metrics, cancellationToken).ConfigureAwait(false);
        }

        return Absent(filter.Filter, filter, words);
    }

    /// <summary>The words of a chunk's filter, read alone; null when it has none this build reads.</summary>
    internal static async ValueTask<uint[]?> ReadFilterAsync(ParquetFile file, ColumnChunkMetadata chunk, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        long length = Length(file, chunk);
        if (chunk.BloomFilterOffset < 0 || !file.Holds(chunk.BloomFilterOffset, length))
        {
            return null;
        }

        using SegmentRequestSet requests = new();
        int slot = requests.Add(new SegmentSpec((ulong)chunk.BloomFilterOffset, (uint)length, 0, 0, 0));
        ScanCounters.Note(metrics, 1, length);
        await file.Reader.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
        uint[]? words = await WordsAsync(file, chunk, requests.GetBuffer(slot), metrics, cancellationToken).ConfigureAwait(false);
        requests.Release();
        return words;
    }

    /// <summary>
    /// Whether a chunk's filter may hold <paramref name="value"/>: its PLAIN bytes, as a writer hashes
    /// them, found; null when the value has no such bytes this build names.
    /// </summary>
    internal static bool? Holds(ParquetColumn column, FilterLiteral value, uint[] words, Span<byte> plain)
    {
        if (column.Physical == PhysicalType.ByteArray)
        {
            return column.Form is LeafForm.Binary or LeafForm.Utf8 && value.Kind == FilterLiteralKind.Bytes
                ? SplitBlockBloom.Contains(words, SplitBlockBloom.Hash(value.BytesValue, BloomHash.XxHash64))
                : null;
        }

        return TryPlain(column, value, plain, out int length, out _)
            ? SplitBlockBloom.Contains(words, SplitBlockBloom.Hash(plain[..length], BloomHash.XxHash64))
            : null;
    }

    /// <summary>The bytes a filter's first read takes: its length, or, where a writer left it out, its header's worth short of the footer.</summary>
    private static long Length(ParquetFile file, ColumnChunkMetadata chunk) =>
        chunk.BloomFilterLength > 0 ? chunk.BloomFilterLength : Math.Min(64, file.Length - 8 - chunk.BloomFilterOffset);

    /// <summary>
    /// The words of the filter whose first bytes <paramref name="read"/> holds, read at the chunk's
    /// offset: the bitset read again whole where the first read stopped short of it, as it does where
    /// a writer left the filter's length out; null when the bytes are no filter this build reads, or
    /// its bitset would lie past the file's pages.
    /// </summary>
    private static async ValueTask<uint[]?> WordsAsync(ParquetFile file, ColumnChunkMetadata chunk, VortexBuffer read, ScanCounters? metrics, CancellationToken cancellationToken)
    {
        if (!BloomFilterHeader.TryRead(read.Span, out int header, out int bitset))
        {
            return null;
        }

        if (read.Length >= header + bitset)
        {
            return SplitBlockBloom.Words(read.Span.Slice(header, bitset)).ToArray();
        }

        if (!file.Holds(chunk.BloomFilterOffset + header, bitset))
        {
            return null;
        }

        using SegmentRequestSet again = new();
        int slot = again.Add(new SegmentSpec((ulong)chunk.BloomFilterOffset + (ulong)header, (uint)bitset, 0, 0, 0));
        ScanCounters.Note(metrics, 1, bitset);
        await file.Reader.ReadManyAsync(again, cancellationToken).ConfigureAwait(false);
        uint[] words = SplitBlockBloom.Words(again.GetBuffer(slot).Span).ToArray();
        again.Release();
        return words;
    }

    /// <summary>Whether <paramref name="path"/> is a column an equality of <paramref name="filter"/> asks about.</summary>
    private static bool Asks(VortexExpr filter, string path) => filter.Kind switch
    {
        ExprKind.Comparison => filter is ComparisonExpr { Op: ComparisonOp.Equal, Field: not FunctionFieldExpr } comparison && comparison.Field.Path == path,
        ExprKind.In => filter is InExpr { Field: not FunctionFieldExpr } @in && @in.Field.Path == path,
        ExprKind.Logical => Asks(((LogicalExpr)filter).Left, path) || Asks(((LogicalExpr)filter).Right, path),
        _ => false,
    };

    /// <summary>Whether the filters prove <paramref name="expr"/> selects no row.</summary>
    private static bool Absent(VortexExpr expr, FilterColumns filter, uint[]?[] words)
    {
        switch (expr.Kind)
        {
            case ExprKind.Comparison:
                return expr is ComparisonExpr { Op: ComparisonOp.Equal, Field: not FunctionFieldExpr } comparison
                    && AbsentValue(comparison.Field.Path, comparison.Value, filter, words);
            case ExprKind.In:
                if (expr is not InExpr { Field: not FunctionFieldExpr } @in || @in.Literals.Length == 0)
                {
                    return false;
                }

                foreach (FilterLiteral value in @in.Literals)
                {
                    if (!AbsentValue(@in.Field.Path, value, filter, words))
                    {
                        return false;
                    }
                }

                return true;
            case ExprKind.Logical:
                LogicalExpr logical = (LogicalExpr)expr;
                return logical.IsAnd
                    ? Absent(logical.Left, filter, words) || Absent(logical.Right, filter, words)
                    : Absent(logical.Left, filter, words) && Absent(logical.Right, filter, words);
            default:
                return false;
        }
    }

    /// <summary>Whether the column at <paramref name="path"/> has a filter that proves <paramref name="value"/> absent.</summary>
    private static bool AbsentValue(string path, FilterLiteral value, FilterColumns filter, uint[]?[] words)
    {
        for (int i = 0; i < filter.Fields.Length; i++)
        {
            if (filter.Fields[i].Path != path || words[i] is not { } bits)
            {
                continue;
            }

            ParquetColumn column = filter.File.Compiled.Columns[filter.Columns[i]];
            if (column.Physical == PhysicalType.ByteArray)
            {
                // A byte array is hashed as its bytes, which the literal holds whole.
                return column.Form is LeafForm.Binary or LeafForm.Utf8
                    && value.Kind == FilterLiteralKind.Bytes
                    && !SplitBlockBloom.Contains(bits, SplitBlockBloom.Hash(value.BytesValue, BloomHash.XxHash64));
            }

            Span<byte> plain = stackalloc byte[32];
            if (!TryPlain(column, value, plain, out int length, out bool zero))
            {
                return false;
            }

            if (SplitBlockBloom.Contains(bits, SplitBlockBloom.Hash(plain[..length], BloomHash.XxHash64)))
            {
                return false;
            }

            if (zero)
            {
                // The other zero, whose bits the filter hashed apart: the sign bit of the value's width.
                plain[length - 1] ^= 0x80;
                return !SplitBlockBloom.Contains(bits, SplitBlockBloom.Hash(plain[..length], BloomHash.XxHash64));
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The PLAIN bytes a filter hashed for the column's value equal to <paramref name="literal"/>,
    /// at most 32, and whether it is a float's zero, whose other zero is probed too; false when no
    /// value of the column is equal to it by bytes the filter can name.
    /// </summary>
    internal static bool TryPlain(ParquetColumn column, FilterLiteral literal, Span<byte> into, out int length, out bool zero)
    {
        length = 0;
        zero = false;
        bool unsigned = column.Logical.Kind == LogicalTypeKind.Integer && !column.Logical.IsSigned;
        bool decimals = column.Logical.Kind == LogicalTypeKind.Decimal;
        switch (column.Physical)
        {
            case PhysicalType.Int32:
            case PhysicalType.Int64:
                bool wide = column.Physical == PhysicalType.Int64;
                long signed;
                if (decimals)
                {
                    if (!ComparisonKernels.TryDecimal(literal, out Int256 unscaled) || !unscaled.TryToInt64(out signed))
                    {
                        return false;
                    }
                }
                else if (literal.Kind == FilterLiteralKind.Signed)
                {
                    signed = literal.SignedValue;
                    if (unsigned && signed < 0)
                    {
                        return false;
                    }
                }
                else if (literal.Kind == FilterLiteralKind.Unsigned)
                {
                    ulong value = literal.UnsignedValue;
                    if (!unsigned && value > long.MaxValue)
                    {
                        return false;
                    }

                    signed = (long)value;
                }
                else
                {
                    return false;
                }

                if (!wide)
                {
                    bool fits = unsigned ? (ulong)signed <= uint.MaxValue : signed is >= int.MinValue and <= int.MaxValue;
                    if (!fits)
                    {
                        return false;
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(into, unchecked((int)signed));
                    length = sizeof(int);
                    return true;
                }

                BinaryPrimitives.WriteInt64LittleEndian(into, signed);
                length = sizeof(long);
                return true;

            case PhysicalType.Float:
                if (literal.Kind != FilterLiteralKind.Float || double.IsNaN(literal.FloatValue) || (float)literal.FloatValue != literal.FloatValue)
                {
                    return false;
                }

                BinaryPrimitives.WriteSingleLittleEndian(into, (float)literal.FloatValue);
                length = sizeof(float);
                zero = literal.FloatValue == 0;
                return true;

            case PhysicalType.Double:
                if (literal.Kind != FilterLiteralKind.Float || double.IsNaN(literal.FloatValue))
                {
                    return false;
                }

                BinaryPrimitives.WriteDoubleLittleEndian(into, literal.FloatValue);
                length = sizeof(double);
                zero = literal.FloatValue == 0;
                return true;

            case PhysicalType.FixedLenByteArray:
                return TryFixed(column, literal, into, out length, out zero);

            default:
                // BOOLEAN is never probed, INT96 has no literal, and a byte array is its own bytes.
                return false;
        }
    }

    private static bool TryFixed(ParquetColumn column, FilterLiteral literal, Span<byte> into, out int length, out bool zero)
    {
        length = column.TypeLength;
        zero = false;
        if (length <= 0 || length > into.Length)
        {
            return false;
        }

        switch (column.Form)
        {
            case LeafForm.Float16:
                if (literal.Kind != FilterLiteralKind.Float || double.IsNaN(literal.FloatValue) || (double)(Half)literal.FloatValue != literal.FloatValue)
                {
                    return false;
                }

                BinaryPrimitives.WriteHalfLittleEndian(into, (Half)literal.FloatValue);
                zero = literal.FloatValue == 0;
                return true;

            case LeafForm.BigEndianDecimal:
                if (!ComparisonKernels.TryDecimal(literal, out Int256 unscaled))
                {
                    return false;
                }

                // Big-endian at the column's length: what is cut must be the sign's extension.
                Span<byte> full = stackalloc byte[32];
                unscaled.WriteBigEndianBytes(full);
                byte sign = unscaled.IsNegative ? (byte)0xFF : (byte)0;
                for (int b = 0; b < 32 - length; b++)
                {
                    if (full[b] != sign)
                    {
                        return false;
                    }
                }

                if (((full[32 - length] & 0x80) != 0) != unscaled.IsNegative)
                {
                    return false;
                }

                full[(32 - length)..].CopyTo(into);
                return true;

            case LeafForm.FixedBytes:
                if (literal.Kind != FilterLiteralKind.Bytes || literal.BytesValue.Length != length)
                {
                    return false;
                }

                literal.BytesValue.CopyTo(into);
                return true;

            default:
                return false;
        }
    }
}
