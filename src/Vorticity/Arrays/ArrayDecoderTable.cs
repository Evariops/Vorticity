// Phase 1 contract §2.3 and §8.2. This is ONE of exactly three places in the library that may
// throw VortexUnsupportedException, and the only one that may throw it with kind "array".
//
// Registration is AOT- and trim-safe and free of module-initializer ordering games: the static
// constructor news up every decoder explicitly, by name, in one place. No reflection, no assembly
// scanning, no [ModuleInitializer] in a decoder file.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Arrays;

/// <summary>
/// Maps a resolved <see cref="ArrayEncodingId"/> to its decoder. Built once at type-init and
/// shared by every scan.
/// </summary>
public static class ArrayDecoderTable
{
    private static readonly ArrayDecoder?[] Decoders = new ArrayDecoder?[EncodingRegistry.MaxArrayEncodingId + 1];

    static ArrayDecoderTable()
    {
        // Phase 1 contract §8.2, plus Phase 2 decoders as they land: every decoder this build owns,
        // named explicitly, in one place, in encoding-id order. Nothing else in the library
        // registers anything, and nothing outside it has to: a caller who opens a file and scans it
        // in a process with no test harness gets a working decoder table.
        //
        // Each decoder exposes a shared, stateless `Instance`; `Register` asserts that its
        // `IdUtf8` resolves back to the slot its `EncodingId` names, so a transposition here is a
        // type-init failure rather than a wrong-encoding decode.
        //
        // The fourteen canonical decoders (Decoders/Canonical).
        Register(NullDecoder.Instance);
        Register(BoolDecoder.Instance);
        Register(PrimitiveDecoder.Instance);
        Register(DecimalDecoder.Instance);
        Register(VarBinDecoder.Instance);
        Register(VarBinViewDecoder.Instance);
        Register(StructDecoder.Instance);
        Register(ListDecoder.Instance);
        Register(ListViewDecoder.Instance);
        Register(FixedSizeListDecoder.Instance);
        Register(ExtensionDecoder.Instance);
        Register(ChunkedDecoder.Instance);
        Register(ConstantDecoder.Instance);
        Register(MaskedDecoder.Instance);

        // The nine compressed decoders (Decoders/Compressed).
        Register(ForDecoder.Instance);
        Register(DeltaDecoder.Instance);
        Register(MapDecoder.Instance);
        Register(PatchedArrayDecoder.Instance);
        Register(ZstdBuffersDecoder.Instance);
        Register(Vorticity.Arrays.Decoders.Compressed.Pco.PcoDecoder.Instance);
        Register(BitPackedDecoder.Instance);
        Register(FastLanesRleDecoder.Instance);
        Register(ZigZagDecoder.Instance);
        Register(RunEndDecoder.Instance);
        Register(DictDecoder.Instance);
        Register(SparseDecoder.Instance);
        Register(SequenceDecoder.Instance);
        Register(ByteBoolDecoder.Instance);

        // Phase 2, as each lands.
        Register(DecimalBytePartsDecoder.Instance);
        Register(DateTimePartsDecoder.Instance);
        Register(ZstdDecoder.Instance);
        Register(AlpDecoder.Instance);
        Register(AlpRdDecoder.Instance);
        Register(FsstDecoder.Instance);
        Register(OnPairDecoder.Instance);
        Register(VariantDecoder.Instance);
        Register(ParquetVariantDecoder.Instance);
    }

    /// <summary>
    /// The decoder for <paramref name="id"/>. <b>The only place a
    /// <see cref="VortexUnsupportedException"/> with kind <c>"array"</c> is thrown</b> (contract §2.3).
    /// </summary>
    /// <param name="id">The resolved id; <see cref="ArrayEncodingId.Unknown"/> always throws.</param>
    /// <param name="idText">
    /// The id exactly as the file spells it, for the exception message. It is required even on the
    /// success path because a caller cannot know in advance which call will fail.
    /// </param>
    /// <returns>The shared, stateless decoder instance.</returns>
    /// <exception cref="VortexUnsupportedException">This build does not decode that encoding.</exception>
    public static ArrayDecoder Get(ArrayEncodingId id, string idText)
    {
        ArrayDecoder? decoder = Lookup(id);
        return decoder ?? ThrowUnsupported(idText);
    }

    /// <summary><see langword="true"/> when this build has a decoder for <paramref name="id"/>.</summary>
    /// <param name="id">The resolved id.</param>
    public static bool IsImplemented(ArrayEncodingId id) => Lookup(id) is not null;

    /// <summary>
    /// The decoder for a node's resolved encoding, without materializing its id text on the
    /// success path.
    /// </summary>
    /// <remarks>
    /// <see cref="ScanContext.GetArrayEncodingIdText"/> allocates a string - it reads the id out of
    /// the file's footer window on demand, precisely so that a footer full of shared spec ids does
    /// not cost O(bytes squared) at open. The contract's <see cref="Get(ArrayEncodingId, string)"/>
    /// takes the text by value, so calling it per node per batch would allocate on a decode path,
    /// which §1.3 forbids. The failing case still goes through <c>Get</c>, so the throw site is
    /// unchanged. The exact mirror of <c>LayoutReaderTable.Require</c>.
    /// </remarks>
    /// <param name="scan">The flow whose encoding dictionary names <paramref name="specIndex"/>.</param>
    /// <param name="id">The resolved id carried by the node.</param>
    /// <param name="specIndex">The wire <c>u16</c>, used only to build the failure message.</param>
    /// <returns>The shared, stateless decoder instance.</returns>
    /// <exception cref="VortexUnsupportedException">This build does not decode that encoding.</exception>
    internal static ArrayDecoder Require(ScanContext scan, ArrayEncodingId id, int specIndex)
    {
        ArrayDecoder? decoder = Lookup(id);
        return decoder ?? Get(id, scan.GetArrayEncodingIdText(specIndex));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ArrayDecoder? Lookup(ArrayEncodingId id)
    {
        ArrayDecoder?[] decoders = Decoders;
        return (uint)id < (uint)decoders.Length ? decoders[(int)id] : null;
    }

    /// <summary>
    /// Installs <paramref name="decoder"/> in its declared slot. Called only from this type's
    /// static constructor, so it needs no synchronization and none is provided.
    /// </summary>
    /// <param name="decoder">The decoder to install.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The decoder's <see cref="ArrayDecoder.EncodingId"/> is <see cref="ArrayEncodingId.Unknown"/>
    /// or out of range, its <see cref="ArrayDecoder.IdUtf8"/> does not resolve back to that slot,
    /// or the slot is already occupied. All three are build-time mistakes, not file problems.
    /// </exception>
    internal static void Register(ArrayDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        ArrayEncodingId id = decoder.EncodingId;
        if (id == ArrayEncodingId.Unknown || !EncodingRegistry.IsDefined(id))
        {
            throw new ArgumentException(
                $"Decoder {decoder.GetType().Name} declares encoding id {(ushort)id}, which is not " +
                "a registrable slot.",
                nameof(decoder));
        }

        // Catches the transposition that would otherwise be silent: a decoder whose IdUtf8 and
        // EncodingId disagree would decode the wrong encoding with a plausible-looking error.
        if (EncodingRegistry.ResolveArray(decoder.IdUtf8) != id)
        {
            throw new ArgumentException(
                $"Decoder {decoder.GetType().Name} declares EncodingId {id} but an IdUtf8 that " +
                "resolves elsewhere.",
                nameof(decoder));
        }

        if (Decoders[(int)id] is not null)
        {
            throw new ArgumentException($"A decoder for {id} is already registered.", nameof(decoder));
        }

        Decoders[(int)id] = decoder;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static ArrayDecoder ThrowUnsupported(string idText)
    {
        string id = idText ?? "<unnamed>";
        string? detail = EncodingRegistry.DescribeUnsupported(id);
        throw detail is null
            ? new VortexUnsupportedException(id, VortexComponentKind.Array)
            : new VortexUnsupportedException(id, VortexComponentKind.Array, detail);
    }
}
