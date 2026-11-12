using System.Threading;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The <c>Authorization</c> value of a store's bearer token, made once a token: a token of a kilobyte or two made a string
/// of its own at every request, more than half of what a read allocated. A token renewed is a new string, and makes a new
/// value; the credentials hand the same string out until then.
/// </summary>
internal sealed class BearerHeaders
{
    private Entry? _last;

    /// <summary>The value for <paramref name="token"/>: <c>Bearer</c> and the token.</summary>
    internal string Of(string token)
    {
        Entry? last = Volatile.Read(ref _last);
        if (last is null || !ReferenceEquals(last.Token, token))
        {
            last = new Entry(token, "Bearer " + token);
            Volatile.Write(ref _last, last);
        }

        return last.Value;
    }

    private sealed record Entry(string Token, string Value);
}
