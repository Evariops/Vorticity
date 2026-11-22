namespace Vorticity.Execution;

/// <summary>
/// What a question's work spreads over: the session's core, whose lanes it computes on, and the question's degree, the
/// lanes it takes of them at most, its caller's included.
/// </summary>
/// <param name="Core">The session's core.</param>
/// <param name="Degree">The lanes at most, the caller's included.</param>
internal readonly record struct Spread(FlowCore Core, int Degree)
{
    /// <summary>One lane, the caller's, on the process's core: for work that spreads nothing.</summary>
    internal static Spread Alone => new Spread(FlowCore.Process, 1);
}
