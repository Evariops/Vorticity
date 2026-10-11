namespace Vorticity.IO;

/// <summary>
/// A store whose service takes no condition on the parts of an upload, so that it holds a put
/// conditioned on the key's absence whole, in memory and in one request, whatever its size. A caller
/// whose key nobody else can take, a dataset's data object named by a fresh uid, puts a large object
/// without the condition, in parts.
/// </summary>
internal interface IWholeConditionalPuts
{
    /// <summary>Whether a put conditioned on the key's absence is held whole, whatever its size.</summary>
    bool HoldsConditionalPutsWhole { get; }
}
