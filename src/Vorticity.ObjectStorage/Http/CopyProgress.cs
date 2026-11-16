namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// What a copy of an object has written so far, and the token of the object its first answer came from: an attempt after a
/// body cut short asks for the bytes after those, of that object alone, so that two objects under one key are never
/// spliced into one copy.
/// </summary>
internal sealed class CopyProgress
{
    /// <summary>The bytes written to the destination.</summary>
    internal long Written;

    /// <summary>The token of the object copied, once an answer said it; null before.</summary>
    internal string? Token;
}
