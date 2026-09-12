// Phase 1 contract §8.2. ArrayDecoderTable's static constructor now names every decoder itself, so
// this type installs nothing: each Register below finds its slot already filled and returns.
//
// It survives integration as the public "make sure the canonical decoders are there" entry point
// that wave C1 published and several test harnesses still call, and because touching
// ArrayDecoderTable at all is what forces its type initializer to run. Registration stays
// idempotent, and this is deliberately NOT a [ModuleInitializer]: contract §8.2 forbids
// module-initializer ordering games in the decoder files.
//
// It must not call into ArrayDecoderTable's constructor and be called from it: the dependency runs
// one way only, from here to there.
using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Installs the fourteen canonical decoders into <see cref="ArrayDecoderTable"/>. Thread-safe and
/// idempotent: the work happens in a type initializer, which the runtime runs exactly once.
/// </summary>
public static class CanonicalDecoders
{
    /// <summary>How many decoders this component owns.</summary>
    public const int Count = 14;

    /// <summary>
    /// Ensures every canonical decoder is registered. Safe to call from any thread, any number of
    /// times, and safe to call after something else has already filled some of the slots.
    /// </summary>
    public static void RegisterAll() => Registration.Ensure();

    /// <summary>
    /// A separate type so the registration runs in a type initializer: the CLR guarantees it
    /// executes once and blocks every other thread until it finishes, which is the same guarantee
    /// <see cref="ArrayDecoderTable"/>'s own static constructor relies on.
    /// </summary>
    private static class Registration
    {
        internal static readonly bool Done = Install();

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Ensure()
        {
            if (!Done)
            {
                throw new InvalidOperationException("Canonical decoder registration failed.");
            }
        }

        private static bool Install()
        {
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
            return true;
        }

        private static void Register(ArrayDecoder decoder)
        {
            if (!ArrayDecoderTable.IsImplemented(decoder.EncodingId))
            {
                ArrayDecoderTable.Register(decoder);
            }
        }
    }
}
