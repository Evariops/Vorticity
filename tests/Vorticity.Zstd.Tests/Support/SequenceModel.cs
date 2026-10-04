using System;
using System.Collections.Generic;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// RFC 8878's sequence execution, written as plainly as the text states it: the model a hand-built
/// frame's expected content is computed with.
/// </summary>
internal sealed class SequenceModel
{
    private readonly byte[] _history;

    public SequenceModel(byte[]? history = null)
    {
        _history = history ?? [];
    }

    public List<byte> Output { get; } = [];

    /// <summary>Repeated offsets 1, 2 and 3, initialized to 1, 4 and 8.</summary>
    public uint[] Reps { get; } = [1, 4, 8];

    /// <summary>The offset an <c>Offset_Value</c> resolves to, and the repeated offsets after it (3.1.1.5).</summary>
    public long Resolve(uint offsetValue, int literalLength)
    {
        uint offset;
        if (offsetValue > 3)
        {
            offset = offsetValue - 3;
            Reps[2] = Reps[1];
            Reps[1] = Reps[0];
            Reps[0] = offset;
            return offset;
        }

        // With no literal before the match, the codes shift by one and 3 means Repeated_Offset1 - 1.
        int index = literalLength == 0 ? (int)offsetValue : (int)offsetValue - 1;
        if (index == 0)
        {
            return Reps[0];
        }

        offset = index == 3 ? Reps[0] - 1 : Reps[index];
        if (index == 3 || index == 2)
        {
            Reps[2] = Reps[1];
        }

        Reps[1] = Reps[0];
        Reps[0] = offset;
        return offset;
    }

    /// <summary>Copies the literals, then the match, byte by byte from the combined history.</summary>
    public void Execute(ReadOnlySpan<byte> literals, Seq sequence)
    {
        foreach (byte b in literals.Slice(0, sequence.LiteralLength))
        {
            Output.Add(b);
        }

        long offset = Resolve(sequence.OffsetValue, sequence.LiteralLength);
        for (int i = 0; i < sequence.MatchLength; i++)
        {
            long from = Output.Count - offset;
            Output.Add(from >= 0 ? Output[(int)from] : _history[(int)(_history.Length + from)]);
        }
    }

    /// <summary>The history a match may reach into: the output so far and the dictionary before it.</summary>
    public long Available => Output.Count + _history.Length;
}
