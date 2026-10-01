namespace Vorticity.Scanning;

/// <summary>
/// Rows a key-ordered scan, a key cursor or a count leaves out whatever its filter says: the rows a
/// dataset deleted from a file without rewriting it, which only the dataset knows. They form sorted,
/// disjoint runs of file rows.
/// </summary>
internal interface IRowExclusion
{
    /// <summary>How many rows are left out.</summary>
    long ExcludedCount { get; }

    /// <summary>How many runs they form.</summary>
    int Runs { get; }

    /// <summary>Whether <paramref name="row"/>, a row of the file, is left out.</summary>
    bool Excludes(long row);

    /// <summary>How many rows below <paramref name="row"/> are left out.</summary>
    long ExcludedBefore(long row);

    /// <summary>The row of the <paramref name="kept"/>-th row kept, counting from 0.</summary>
    long KeptRow(long kept);

    /// <summary>The first run that ends past <paramref name="row"/>; <see cref="Runs"/> when none does.</summary>
    int FirstEndingAfter(long row);

    /// <summary>The row the <paramref name="run"/>-th run starts at.</summary>
    long StartOf(int run);

    /// <summary>The row past the <paramref name="run"/>-th run's last.</summary>
    long EndOf(int run);
}
