using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Vorticity.Layouts;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Scanning;

/// <summary>
/// The size of a batch when only the reading decides it: the zone length, doubled for as long as
/// a batch of the projection's decoded rows fits what a core's L2 cache can spare for it.
/// </summary>
/// <remarks>
/// <para>
/// A batch is decoded, then read by the caller, and it is meant to still be in the cache when it
/// is read. Half of a core's share of its L2 is the batch's; the other half is left to what the
/// batch is decoded from and to what the caller does with it. Growing a batch within that
/// divides the fixed cost of a batch -- the plan's step, the readers' walk, the batch itself --
/// over more rows. The zone is the floor: below it that fixed cost grows faster than evictions
/// cost, even for a caller that reads every value it is handed. A window is the ceiling, since a
/// window holds whole batches.
/// </para>
/// <para>
/// The share is read from the system once: on macOS the performance cores' L2 and how many cores
/// share it, on Linux the first CPU's L2 and the CPUs listed as sharing it. Elsewhere, or when the
/// system does not answer, a core is taken to have a megabyte to itself, the smallest L2 of a
/// current core: that never evicts, and only costs a machine with more the larger batches it could
/// have had.
/// </para>
/// </remarks>
internal static partial class BatchBudget
{
    /// <summary>The L2 a core is taken to have to itself when the system does not say.</summary>
    internal const long FallbackShare = 1 << 20;

    /// <summary>A string's view: its length and up to twelve of its bytes, or where they are.</summary>
    private const int ViewBytes = 16;

    /// <summary>A string's bytes past its view, which a dtype does not tell: a guess.</summary>
    private const int GuessedHeapBytes = 16;

    /// <summary>A list's offset and size, at their widest.</summary>
    private const int ListBytes = 16;

    /// <summary>A list's elements per row, which a dtype does not tell: a guess.</summary>
    private const int GuessedElements = 4;

    /// <summary>The decoded bytes a batch may hold: half of a core's share of its L2 cache.</summary>
    internal static long Bytes { get; } = CoreShare() / 2;

    /// <summary>
    /// The rows of a batch of <paramref name="mask"/> over <paramref name="schema"/>: the zone
    /// length, doubled while a batch stays within <paramref name="budget"/> bytes and a window.
    /// </summary>
    /// <param name="schema">The file's dtype.</param>
    /// <param name="mask">The projection.</param>
    /// <param name="natural">The zone length.</param>
    /// <param name="windowRows">The most rows a window holds.</param>
    /// <param name="budget">The decoded bytes a batch may hold.</param>
    internal static long Rows(DType schema, in FieldMask mask, long natural, int windowRows, long budget)
    {
        double bits = Math.Max(BitsPerRow(schema, mask), 1);
        long rows = natural;
        while (rows <= windowRows / 2 && rows * 2 * bits <= budget * 8.0)
        {
            rows *= 2;
        }

        return rows;
    }

    /// <summary>
    /// The bits a decoded row of <paramref name="dtype"/> holds under <paramref name="mask"/>, as
    /// its canonical form keeps them: exact for fixed widths, guessed past a string's view and for
    /// a list's elements, which the dtype does not tell.
    /// </summary>
    internal static double BitsPerRow(DType dtype, FieldMask mask)
    {
        double validity = dtype.IsNullable ? 1 : 0;
        return validity + dtype.Kind switch
        {
            DTypeKind.Null => 0,
            DTypeKind.Bool => 1,
            DTypeKind.Primitive => 8 * dtype.PType.ByteWidth(),
            DTypeKind.Decimal => 8 * DecimalStorage.ByteWidth(DecimalStorage.ForPrecision(dtype.Precision)),
            DTypeKind.Struct => FieldBits(dtype, mask),
            DTypeKind.Extension => BitsPerRow(dtype.StorageType, mask),
            DTypeKind.FixedSizeList => dtype.FixedSize * BitsPerRow(dtype.ElementType, FieldMask.All),
            DTypeKind.List => (8 * ListBytes) + (GuessedElements * BitsPerRow(dtype.ElementType, FieldMask.All)),
            DTypeKind.Map => (8 * ListBytes) + (GuessedElements
                * (BitsPerRow(dtype.KeyType, FieldMask.All) + BitsPerRow(dtype.ValueType, FieldMask.All))),

            // Strings, binaries, variants and unions: a view, and bytes past it.
            _ => 8 * (ViewBytes + GuessedHeapBytes),
        };
    }

    private static double FieldBits(DType dtype, FieldMask mask)
    {
        double bits = 0;
        for (int i = 0; i < dtype.FieldCount; i++)
        {
            if (mask.Includes(i))
            {
                bits += BitsPerRow(dtype.GetField(i), mask.Descend(i));
            }
        }

        return bits;
    }

    private static long CoreShare()
    {
        (long size, long sharers) = default((long, long));
        try
        {
            (size, sharers) = OperatingSystem.IsMacOS() ? Darwin()
                : OperatingSystem.IsLinux() ? Linux("/sys/devices/system/cpu/cpu0/cache")
                : default;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A system without the call is a system that does not say.
        }

        return size > 0 && sharers > 0 ? size / sharers : FallbackShare;
    }

    /// <summary>
    /// The performance cores' L2 and how many cores share it, which is where a scan's thread runs;
    /// a Mac with one kind of core has no performance levels, and each of its cores has an L2 of
    /// its own.
    /// </summary>
    internal static (long Size, long Sharers) Darwin()
    {
        long size = Sysctl("hw.perflevel0.l2cachesize\0"u8);
        return size > 0
            ? (size, Sysctl("hw.perflevel0.cpusperl2\0"u8))
            : (Sysctl("hw.l2cachesize\0"u8), 1);
    }

    /// <summary>A numeric <c>sysctl</c>, or 0 when the system has no such name.</summary>
    /// <param name="name">The name, NUL-terminated.</param>
    private static unsafe long Sysctl(ReadOnlySpan<byte> name)
    {
        // A four-byte answer lands in the low half of the zeroed word, which is little-endian.
        long value = 0;
        nuint length = sizeof(long);
        fixed (byte* pointer = name)
        {
            return SysctlByName(pointer, &value, &length, null, 0) == 0 && (length == 4 || length == 8)
                ? value
                : 0;
        }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sysctlbyname")]
    private static unsafe partial int SysctlByName(byte* name, long* value, nuint* length, void* newValue, nuint newLength);

    /// <summary>
    /// The first unified or data cache of level 2 under a sysfs cache directory: its size, and how
    /// many CPUs its <c>shared_cpu_list</c> names; nothing when there is none or it cannot be read.
    /// </summary>
    /// <param name="directory">A CPU's <c>cache</c> directory, whose <c>index*</c> directories describe its caches.</param>
    internal static (long Size, long Sharers) Linux(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return default;
            }

            foreach (string index in Directory.EnumerateDirectories(directory, "index*"))
            {
                if (Line(index, "level") == "2" && Line(index, "type") is "Unified" or "Data")
                {
                    return (Size(Line(index, "size")), CpuCount(Line(index, "shared_cpu_list")));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is unknown, and unknown is the fallback's.
        }

        return default;
    }

    private static string Line(string directory, string name) =>
        System.IO.File.ReadAllText(Path.Combine(directory, name)).Trim();

    /// <summary>A sysfs size, <c>2048K</c> or <c>1M</c>, in bytes; 0 when it is not one.</summary>
    internal static long Size(string text)
    {
        long unit = text.EndsWith('K') ? 1L << 10 : text.EndsWith('M') ? 1L << 20 : text.EndsWith('G') ? 1L << 30 : 1;
        string digits = unit == 1 ? text : text[..^1];
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value * unit
            : 0;
    }

    /// <summary>How many CPUs a sysfs list names, <c>0-3,8-11</c> being eight; 0 when it is not one.</summary>
    internal static long CpuCount(string list)
    {
        long count = 0;
        foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    return 0;
                }

                count++;
                continue;
            }

            if (!int.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out int first)
                || !int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int last)
                || last < first)
            {
                return 0;
            }

            count += last - first + 1;
        }

        return count;
    }
}
