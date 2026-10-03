using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Unit;

/// <summary>
/// Sequences built by hand (<see cref="FrameBuilder.RleSequences"/>) against RFC 8878's text
/// (<see cref="SequenceModel"/>): repeated offsets, overlapping copies, matches into a dictionary,
/// and every way a sequence can be invalid.
/// </summary>
public sealed class SequenceTests
{
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < 300; i++)
        {
            data.Add(i);
        }

        return data;
    }

    /// <summary>
    /// Random valid sequences, one block each, half of them repeat codes: every rule of the repeated
    /// offsets, with and without literals before the match, and offsets from 1 up.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_sequences_follow_the_rfc(int seed)
    {
        var random = new Random(seed);
        byte[]? dictionary = seed % 3 == 0 ? RandomBytes(random, 1 + random.Next(3000)) : null;
        var model = new SequenceModel(dictionary);
        var frame = new FrameBuilder();
        int blocks = 1 + random.Next(40);
        for (int b = 0; b < blocks; b++)
        {
            int literalLength = random.Next(3) == 0 ? 0 : random.Next(random.Next(2) == 0 ? 20 : 300);
            byte[] literals = RandomBytes(random, literalLength + random.Next(5)); // some left after the sequence
            Seq? sequence = PickSequence(random, model, literalLength);
            if (sequence is null)
            {
                frame.Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.NoSequences()]);
                model.Output.AddRange(literals);
                continue;
            }

            frame.Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.RleSequences(sequence.Value)]);
            model.Execute(literals, sequence.Value);
            model.Output.AddRange(literals.AsSpan(literalLength).ToArray());
        }

        frame.Raw([], last: true);
        frame.ContentSize = (ulong)model.Output.Count;
        FrameAssert.DecodesTo(frame.Build(), [.. model.Output], dictionary);
    }

    /// <summary>A sequence whose offset is valid for the history so far, or null when there is none yet.</summary>
    private static Seq? PickSequence(Random random, SequenceModel model, int literalLength)
    {
        long available = model.Available + literalLength;
        if (available == 0)
        {
            return null;
        }

        int matchLength = 3 + (random.Next(4) == 0 ? random.Next(2000) : random.Next(40));
        for (int attempt = 0; attempt < 10; attempt++)
        {
            uint offsetValue = (uint)(random.Next(2) == 0 ? 1 + random.Next(3) : 3 + 1 + random.Next((int)Math.Min(available, random.Next(2) == 0 ? 64 : int.MaxValue - 4)));
            uint[] saved = (uint[])model.Reps.Clone();
            long offset = model.Resolve(offsetValue, literalLength);
            saved.CopyTo(model.Reps, 0);
            if (offset >= 1 && offset <= available)
            {
                return new Seq(literalLength, matchLength, offsetValue);
            }
        }

        return null;
    }

    [Fact]
    public void Overlapping_matches_copy_their_own_output()
    {
        foreach (int offset in Enumerable.Range(1, 40))
        {
            foreach (int matchLength in new[] { 3, 4, 7, 8, 9, 15, 16, 17, 31, 32, 33, 64, 100, 1000 })
            {
                byte[] literals = Enumerable.Range(0, offset).Select(i => (byte)('a' + i)).ToArray();
                var seq = new Seq(offset, matchLength, (uint)offset + 3);
                var model = new SequenceModel();
                model.Execute(literals, seq);
                byte[] frame = new FrameBuilder { ContentSize = (ulong)model.Output.Count }
                    .Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.RleSequences(seq)], last: true)
                    .Build();
                FrameAssert.DecodesTo(frame, [.. model.Output]);
            }
        }
    }

    [Fact]
    public void Matches_reach_into_and_across_a_dictionary()
    {
        byte[] dictionary = RandomBytes(new Random(5), 1000);
        foreach ((int literalLength, int offset, int matchLength) in new[]
        {
            (0, 1000, 10),    // the dictionary's first bytes
            (0, 1, 50),       // its last byte, repeated
            (5, 20, 15),      // within the dictionary
            (5, 20, 40),      // across into the frame's own output
            (10, 1010, 2000), // from the first byte of the dictionary to well past its end
        })
        {
            byte[] literals = RandomBytes(new Random(literalLength), literalLength);
            var seq = new Seq(literalLength, matchLength, (uint)offset + 3);
            var model = new SequenceModel(dictionary);
            model.Execute(literals, seq);
            byte[] frame = new FrameBuilder { ContentSize = (ulong)model.Output.Count }
                .Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.RleSequences(seq)], last: true)
                .Build();
            FrameAssert.DecodesTo(frame, [.. model.Output], dictionary);
        }
    }

    [Fact]
    public void An_offset_before_the_history_is_refused()
    {
        byte[] dictionary = RandomBytes(new Random(6), 100);
        byte[] literals = [1, 2, 3, 4];
        var seq = new Seq(4, 3, 4 + 100 + 1 + 3); // one byte before the dictionary
        byte[] frame = new FrameBuilder { ContentSize = 7 }
            .Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.RleSequences(seq)], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.OffsetTooLarge, dictionary);
        FrameAssert.Refused(frame, ZstdError.OffsetTooLarge);
    }

    [Fact]
    public void A_repeat_offset_of_zero_is_refused()
    {
        // A first match at offset 1 makes Repeated_Offset1 1; with no literal, code 3 then means 1 - 1.
        byte[] frame = new FrameBuilder { ContentSize = 13 }
            .Compressed([.. FrameBuilder.RawLiterals([9, 8, 7, 6, 5]), .. FrameBuilder.RleSequences(new Seq(5, 3, 4))])
            .Compressed([.. FrameBuilder.RawLiterals([]), .. FrameBuilder.RleSequences(new Seq(0, 5, 3))], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.OffsetTooLarge);
    }

    [Fact]
    public void More_literals_than_the_section_holds_are_refused()
    {
        byte[] frame = new FrameBuilder { ContentSize = 10 }
            .Compressed([.. FrameBuilder.RawLiterals([1, 2, 3]), .. FrameBuilder.RleSequences(new Seq(4, 3, 4))], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.LiteralsOverrun);
    }

    [Fact]
    public void Output_past_the_destination_is_refused()
    {
        byte[] block = [.. FrameBuilder.RawLiterals([1, 2, 3, 4]), .. FrameBuilder.RleSequences(new Seq(4, 100, 4))];
        byte[] frame = new FrameBuilder().Compressed(block, last: true).Build(); // no content size
        FrameAssert.Refused(frame, ZstdError.DestinationTooSmall, capacity: 103);
    }

    [Fact]
    public void Extra_bits_after_the_last_sequence_are_refused()
    {
        byte[] sequences = FrameBuilder.RleSequences(new Seq(4, 3, 4));

        // Offset value 4 is code 2 with two extra bits (0b00), then the end marker: 0b100. A third
        // bit before the marker, 0b1000, is never read.
        Assert.Equal(0x04, sequences[^1]);
        sequences[^1] = 0x08;
        byte[] frame = new FrameBuilder { ContentSize = 7 }
            .Compressed([.. FrameBuilder.RawLiterals([1, 2, 3, 4]), .. sequences], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.SequenceBitstream);
    }

    [Fact]
    public void Reserved_mode_bits_are_refused()
    {
        byte[] sequences = FrameBuilder.RleSequences(new Seq(4, 3, 4));
        sequences[1] |= 1;
        byte[] frame = new FrameBuilder { ContentSize = 7 }
            .Compressed([.. FrameBuilder.RawLiterals([1, 2, 3, 4]), .. sequences], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.SequencesHeader);
    }

    [Fact]
    public void Repeat_mode_needs_a_previous_table()
    {
        // One sequence; LL in repeat mode (0b11), OF and ML in RLE mode, in the frame's first block
        // with sequences: there is no table to repeat.
        byte[] frame = new FrameBuilder { ContentSize = 7 }
            .Compressed([.. FrameBuilder.RawLiterals([1, 2, 3, 4]), 1, 0xD4, /* OF code */ 2, /* ML code */ 0, 0x04], last: true)
            .Build();
        FrameAssert.Refused(frame, ZstdError.RepeatWithoutTable);
    }

    [Fact]
    public void Repeat_mode_reuses_the_previous_blocks_tables()
    {
        // Both sequences share their codes: literal length 8 (code 8), match length 5 (code 2) and
        // offset values 7 and 6 (code 2, extra bits 3 and 2). The second block repeats all three tables.
        byte[] first = [10, 20, 30, 40, 50, 60, 70, 80];
        byte[] second = [11, 21, 31, 41, 51, 61, 71, 81];
        var model = new SequenceModel();
        model.Execute(first, new Seq(8, 5, 7));
        model.Execute(second, new Seq(8, 5, 6));
        byte[] frame = new FrameBuilder { ContentSize = (ulong)model.Output.Count }
            .Compressed([.. FrameBuilder.RawLiterals(first), .. FrameBuilder.RleSequences(new Seq(8, 5, 7))])
            .Compressed([.. FrameBuilder.RawLiterals(second), .. RepeatAllSequences(new Seq(8, 5, 6))], last: true)
            .Build();
        FrameAssert.DecodesTo(frame, [.. model.Output]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(0x7EFF)]
    [InlineData(0x7F00)]
    [InlineData(40000)]
    public void Sequence_counts_in_every_encoding(int count)
    {
        // Count sequences of no literal and three bytes at repeated offset 2, which alternates
        // between the two first repeated offsets.
        byte[] literals = [1, 2, 3, 4, 5, 6, 7, 8];
        var model = new SequenceModel();
        model.Output.AddRange(literals);
        var seq = new Seq(0, 3, 1);
        var sequences = Enumerable.Repeat(seq, count).ToArray();
        foreach (Seq s in sequences)
        {
            model.Execute([], s);
        }

        byte[] frame = new FrameBuilder { ContentSize = (ulong)model.Output.Count }
            .Raw(literals)
            .Compressed([.. FrameBuilder.RawLiterals([]), .. FrameBuilder.RleSequences(sequences)], last: true)
            .Build();
        FrameAssert.DecodesTo(frame, [.. model.Output]);
    }

    [Fact]
    public void Long_literal_and_match_lengths()
    {
        byte[] literals = RandomBytes(new Random(9), 70000);
        var seq = new Seq(70000, 50000, 1 + 3); // literal length code 35, match length code 51
        var model = new SequenceModel();
        model.Execute(literals, seq);
        byte[] frame = new FrameBuilder { ContentSize = (ulong)model.Output.Count }
            .Compressed([.. FrameBuilder.RawLiterals(literals), .. FrameBuilder.RleSequences(seq)], last: true)
            .Build();
        FrameAssert.DecodesTo(frame, [.. model.Output]);
    }

    /// <summary>A sequences section with all three tables in repeat mode.</summary>
    private static byte[] RepeatAllSequences(Seq seq)
    {
        byte[] rle = FrameBuilder.RleSequences(seq);

        // [count, modes, LL symbol, OF symbol, ML symbol, bitstream...]: repeat mode drops the symbols.
        return [rle[0], 0xFC, .. rle.AsSpan(5).ToArray()];
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        byte[] bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}
