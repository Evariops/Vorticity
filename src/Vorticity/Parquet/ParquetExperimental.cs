namespace Vorticity.Parquet;

/// <summary>
/// The diagnostic every public Parquet type carries until the surface settles, as the dataset's and
/// the row encoding's assemblies carry theirs. It is set type by type, the rest of the core being
/// stable.
/// </summary>
internal static class ParquetExperimental
{
    /// <summary>The diagnostic a caller suppresses to use the Parquet API.</summary>
    public const string Diagnostic = "VX0003";

    /// <summary>What the diagnostic says.</summary>
    public const string Message = "Vorticity's Parquet API may change between releases.";
}
