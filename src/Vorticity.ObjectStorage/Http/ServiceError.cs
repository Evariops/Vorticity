using System;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>What a service said when it refused a request: its status, its code and its request id, never a secret.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The service's code, <c>NoSuchKey</c> or <c>SlowDown</c>, when its body named one.</param>
/// <param name="Message">The service's message, when its body had one.</param>
/// <param name="RequestId">The id the service gave the request, which its support asks for.</param>
/// <param name="RetryAfter">How long the service asked to wait, by <c>Retry-After</c>, if it did.</param>
internal readonly record struct ServiceError(int Status, string? Code, string? Message, string? RequestId, TimeSpan? RetryAfter = null)
{
    /// <summary>Whether the service asked to slow down.</summary>
    internal bool IsThrottle => Status == 429 || (Status == 503 && Code is null or "SlowDown" or "ServerBusy" or "Throttling");

    /// <summary>The exception that says this refusal, with its status, code and request id as properties a caller tests.</summary>
    internal ObjectStoreException ToException(string message) =>
        new ObjectStoreException(message)
        {
            StatusCode = Status,
            ErrorCode = Code,
            RequestId = RequestId,
        };

    /// <summary>The error as a message names it.</summary>
    public override string ToString()
    {
        string code = Code is null ? string.Empty : $" {Code}";
        string id = RequestId is null ? string.Empty : $" (request id {RequestId})";
        string message = string.IsNullOrEmpty(Message) ? string.Empty : $": {Message}";
        return $"HTTP {Status}{code}{id}{message}";
    }

    /// <summary>Whether the status is one a retry may cure: the service's failures, its throttling, a request it timed out.</summary>
    internal static bool IsRetryable(int status) => status is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>A body cut to what a message carries.</summary>
    internal static string Clip(string text) => text.Length <= 512 ? text : string.Concat(text.AsSpan(0, 512), "...");
}
