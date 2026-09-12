// What this build actually implements, asked of the build itself.
//
// The registration below is now a NO-OP, and `ScopeSplitTests.TheShippedDecoderTableIsWiredUp`
// asserts that it is. `ArrayDecoderTable`'s static constructor names all twenty-three decoders
// (PHASE1-CONTRACTS.md §15.1), so every slot is already filled by the time anything here looks, and
// `ShippedTableWasEmpty` reads false. It is kept, still guarded by IsImplemented, for exactly one
// reason: it is the measurement that proves the corpus numbers below are the library's and not the
// harness's. Delete it and a regression that empties the static constructor again turns 616
// passing files into 616 files the harness quietly rescued.
using System;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Layouts;

namespace Vorticity.Conformance.Corpus;

/// <summary>Component-support queries, answered by the library's own registries.</summary>
internal static class Phase1Components
{
    /// <summary>
    /// The nine compressed decoders. Named here so the wiring assertion can check each one by its
    /// wire id rather than by a count.
    /// </summary>
    internal static readonly string[] CompressedDecodersRegisteredByTheHarness =
    [
        "fastlanes.bitpacked",
        "vortex.bytebool",
        "vortex.dict",
        "fastlanes.rle",
        "fastlanes.for",
        "vortex.runend",
        "vortex.sequence",
        "vortex.sparse",
        "vortex.zigzag",
        "vortex.decimal_byte_parts",
        "vortex.datetimeparts",
        "vortex.zstd",
        "vortex.alp",
    ];

    private static readonly object Gate = new object();
    private static bool s_registered;
    private static bool s_tableWasEmpty;

    /// <summary>
    /// <see langword="true"/> when <see cref="ArrayDecoderTable"/> held no decoder at all before the
    /// harness registered any. Expected to be <see langword="false"/>: it is the observable form of
    /// the §15.1 integration gap, and the gap is closed.
    /// </summary>
    internal static bool ShippedTableWasEmpty
    {
        get
        {
            EnsureRegistered();
            return s_tableWasEmpty;
        }
    }

    /// <summary>Installs every decoder this build owns, once. Thread-safe and idempotent.</summary>
    internal static void EnsureRegistered()
    {
        // A lock, not an Interlocked flag: with a flag the SECOND caller returns while the first is
        // still registering, and xunit runs these test classes in parallel. A scan that starts one
        // instruction too early sees an empty decoder table and fails with an exception that has
        // nothing to do with the file it was reading.
        lock (Gate)
        {
            if (s_registered)
            {
                return;
            }

            s_registered = true;
            RegisterAll();
        }
    }

    private static void RegisterAll()
    {
        s_tableWasEmpty = !ArrayDecoderTable.IsImplemented(ArrayEncodingId.Primitive) &&
                          !ArrayDecoderTable.IsImplemented(ArrayEncodingId.Bool) &&
                          !ArrayDecoderTable.IsImplemented(ArrayEncodingId.Struct);

        CanonicalDecoders.RegisterAll();

        Register(BitPackedDecoder.Instance);
        Register(ByteBoolDecoder.Instance);
        Register(DictDecoder.Instance);
        Register(FastLanesRleDecoder.Instance);
        Register(ForDecoder.Instance);
        Register(RunEndDecoder.Instance);
        Register(SequenceDecoder.Instance);
        Register(SparseDecoder.Instance);
        Register(ZigZagDecoder.Instance);
        Register(DecimalBytePartsDecoder.Instance);
        Register(DateTimePartsDecoder.Instance);
        Register(ZstdDecoder.Instance);
        Register(AlpDecoder.Instance);
    }

    /// <summary>Whether this build has a decoder for the array encoding <paramref name="id"/>.</summary>
    /// <param name="id">The wire id, e.g. <c>vortex.alp</c>.</param>
    internal static bool DecodesArray(string id)
    {
        EnsureRegistered();
        return ArrayDecoderTable.IsImplemented(EncodingRegistry.ResolveArray(Utf8(id)));
    }

    /// <summary>Whether this build has a reader for the layout <paramref name="id"/>.</summary>
    /// <param name="id">The wire id, e.g. <c>vortex.zoned</c>.</param>
    internal static bool ReadsLayout(string id) =>
        LayoutReaderTable.IsImplemented(EncodingRegistry.ResolveLayout(Utf8(id)));

    /// <summary>Whether this build resolves the extension dtype <paramref name="id"/>.</summary>
    /// <param name="id">The wire id, e.g. <c>vortex.timestamp</c>.</param>
    internal static bool ResolvesExtensionDType(string id) =>
        ExtensionDTypeRegistry.Resolve(Utf8(id)) != ExtensionKind.Unknown;

    private static void Register(ArrayDecoder decoder)
    {
        if (!ArrayDecoderTable.IsImplemented(decoder.EncodingId))
        {
            ArrayDecoderTable.Register(decoder);
        }
    }

    private static byte[] Utf8(string id) => Encoding.UTF8.GetBytes(id);
}
