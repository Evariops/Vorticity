namespace Vorticity;

/// <summary>Rows being assembled column by column before a writer encodes them.</summary>
public class ColumnsBuilder
{
    private protected ColumnsBuilder()
    {
    }
}

/// <summary>Rows being assembled as the columns of <typeparamref name="TRecord"/>.</summary>
/// <typeparam name="TRecord">The record the writer is typed by.</typeparam>
public sealed class ColumnsBuilder<TRecord> : ColumnsBuilder
    where TRecord : IVortexRecord<TRecord>
{
    internal ColumnsBuilder()
    {
    }
}
