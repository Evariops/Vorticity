using System;

namespace Vorticity.Dataset;

/// <summary>
/// A question's answer over one part of a window, as <see cref="VortexDataset.AskNewestFirstAsync{TAnswer}"/>
/// delivers the parts: newest first, each final for its range of time.
/// </summary>
/// <typeparam name="TAnswer">What the question answers.</typeparam>
/// <param name="From">The part's first instant.</param>
/// <param name="To">The instant past its last.</param>
/// <param name="Answer">The question's answer over the part.</param>
public sealed record WindowPart<TAnswer>(DateTimeOffset From, DateTimeOffset To, TAnswer Answer);
