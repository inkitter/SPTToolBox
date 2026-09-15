using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DbPostPatcher.Patching;

/// <summary>
///     One patches/*.json file - see patches/README.md for the format.
/// </summary>
public sealed class PatchDocument
{
    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("ops")]
    public required List<PatchOp> Ops { get; init; }
}

/// <summary>
///     One RFC 6902 operation ("add" | "replace" | "remove" - move/copy/test are not implemented,
///     see patches/README.md).
/// </summary>
public sealed class PatchOp
{
    [JsonPropertyName("op")]
    public required string Op { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("value")]
    public JsonNode? Value { get; init; }
}

public sealed class PatchException(string message) : Exception(message);
