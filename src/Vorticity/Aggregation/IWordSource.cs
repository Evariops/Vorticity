using System;

namespace Vorticity.Aggregating;

/// <summary>
/// The words a sketch wrote, read back one at a time: a spill's section (<see cref="SpillReader"/>), or a test's list.
/// The sketches read themselves through one, so that what a test reads back is what a spill does.
/// </summary>
internal interface IWordSource
{
    /// <summary>The next word.</summary>
    long NextWord();
}

/// <summary>The words a function hands out one at a time.</summary>
internal readonly struct WordsOf(Func<long> word) : IWordSource
{
    public long NextWord() => word();
}
