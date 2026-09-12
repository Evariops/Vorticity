// TEST SCAFFOLDING, and it is now a no-op.
//
// ArrayDecoderTable's static constructor is the wave-C integration point of Phase 1 contract §8.2
// and §15 item 1, and it names all twenty-three decoders. IsImplemented is therefore true for every
// slot below and Fill installs nothing; the call remains as the one place the column tests reach for
// "the decoders exist", and as the guard that would refill the table if that constructor ever lost
// a line.
using System.Threading;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;

namespace Vorticity.Tests.Columns;

internal static class DecoderBootstrap
{
    private static readonly Lock Gate = new Lock();
    private static bool _done;

    /// <summary>Registers any decoder the table is still missing. Idempotent and thread-safe.</summary>
    internal static void Ensure()
    {
        if (Volatile.Read(ref _done))
        {
            return;
        }

        lock (Gate)
        {
            if (_done)
            {
                return;
            }

            Fill(NullDecoder.Instance);
            Fill(BoolDecoder.Instance);
            Fill(PrimitiveDecoder.Instance);
            Fill(DecimalDecoder.Instance);
            Fill(VarBinDecoder.Instance);
            Fill(VarBinViewDecoder.Instance);
            Fill(StructDecoder.Instance);
            Fill(ListDecoder.Instance);
            Fill(ListViewDecoder.Instance);
            Fill(FixedSizeListDecoder.Instance);
            Fill(ExtensionDecoder.Instance);
            Fill(ChunkedDecoder.Instance);
            Fill(ConstantDecoder.Instance);
            Fill(MaskedDecoder.Instance);
            Fill(ForDecoder.Instance);
            Fill(BitPackedDecoder.Instance);
            Fill(FastLanesRleDecoder.Instance);
            Fill(ZigZagDecoder.Instance);
            Fill(RunEndDecoder.Instance);
            Fill(DictDecoder.Instance);
            Fill(SparseDecoder.Instance);
            Fill(SequenceDecoder.Instance);
            Fill(ByteBoolDecoder.Instance);

            Volatile.Write(ref _done, true);
        }
    }

    private static void Fill(ArrayDecoder decoder)
    {
        if (!ArrayDecoderTable.IsImplemented(decoder.EncodingId))
        {
            ArrayDecoderTable.Register(decoder);
        }
    }
}
