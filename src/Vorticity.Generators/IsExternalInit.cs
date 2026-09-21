namespace System.Runtime.CompilerServices;

/// <summary>Lets records and <c>init</c> accessors compile on netstandard2.0, which does not ship the marker.</summary>
internal static class IsExternalInit
{
}
