using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dongled.Core.Configuration;

/// <summary>Serialization settings shared by both stores.</summary>
internal static class JsonSetup
{
    /// <summary>
    /// Cached in a static: constructing these is expensive, and a fresh instance per call
    /// defeats the serializer's internal metadata cache.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // Both files are meant to be readable and hand-editable, so tolerate what a human
        // leaves behind.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };
}
