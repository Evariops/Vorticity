using System.Text.Json.Serialization;

namespace Vorticity.ObjectStorage.Credentials;

/// <summary>The JSON the credential chains write, generated at build time so that a trimmed or AOT application keeps it.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class StoreJson : JsonSerializerContext;
