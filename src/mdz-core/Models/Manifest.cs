using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mdz.Models;

/// <summary>
/// Represents the optional manifest.json file at the root of a .mdz archive.
/// </summary>
public sealed class Manifest
{
    /// <summary>
    /// Specification compatibility metadata.
    /// </summary>
    [JsonPropertyName("spec")]
    public ManifestSpec? Spec { get; set; }

    /// <summary>
    /// The human-readable title of the document.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Path to the primary Markdown file, relative to the archive root.
    /// </summary>
    [JsonPropertyName("entryPoint")]
    public string? EntryPoint { get; set; }

    /// <summary>
    /// The natural language of the document as a BCP 47 language tag.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Producer provenance metadata.
    /// </summary>
    [JsonPropertyName("producer")]
    public ManifestProducer? Producer { get; set; }

    /// <summary>
    /// Human/content author attribution metadata.
    /// </summary>
    [JsonPropertyName("author")]
    public ManifestAuthor? Author { get; set; }

    /// <summary>
    /// Legacy pre-1.0.1 field retained for backwards compatibility.
    /// </summary>
    [JsonPropertyName("mdz")]
    public string? LegacyMdz { get; set; }

    /// <summary>
    /// Legacy pre-1.0.1 field retained for backwards compatibility.
    /// </summary>
    [JsonPropertyName("authors")]
    public List<ManifestAuthor>? Authors { get; set; }

    /// <summary>
    /// A short plain-text description of the document.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// The version of the document itself.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// The creation datetime of the document in ISO 8601 format.
    /// Draft 1.0.x accepts either a string timestamp or an object containing "when".
    /// </summary>
    [JsonPropertyName("created")]
    [JsonConverter(typeof(TimestampStringOrObjectConverter))]
    public string? Created { get; set; }

    /// <summary>
    /// The last-modified datetime of the document in ISO 8601 format.
    /// Draft 1.0.x accepts either a string timestamp or an object containing "when".
    /// </summary>
    [JsonPropertyName("modified")]
    [JsonConverter(typeof(TimestampStringOrObjectConverter))]
    public string? Modified { get; set; }

    /// <summary>
    /// An SPDX license identifier or URL pointing to the license text.
    /// </summary>
    [JsonPropertyName("license")]
    public string? License { get; set; }

    /// <summary>
    /// A list of keywords or tags describing the document.
    /// </summary>
    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    /// <summary>
    /// Archive-root-relative path to a cover image asset.
    /// </summary>
    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    /// <summary>
    /// Optional per-file mapping data used to preserve original source names.
    /// </summary>
    [JsonPropertyName("files")]
    public List<ManifestFile>? Files { get; set; }

    /// <summary>
    /// Additional user-defined manifest fields. Consumers must ignore unknown fields.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

/// <summary>
/// Represents the optional spec block in the manifest.
/// </summary>
public sealed class ManifestSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// Represents a producer block in the manifest.
/// </summary>
public sealed class ManifestProducer
{
    [JsonPropertyName("application")]
    public ManifestAgent? Application { get; set; }

    [JsonPropertyName("core")]
    public ManifestAgent? Core { get; set; }
}

/// <summary>
/// Represents an application/core metadata node.
/// </summary>
public sealed class ManifestAgent
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// Represents an author entry in the manifest.
/// </summary>
public sealed class ManifestAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// Represents file mapping metadata for a content file.
/// </summary>
public sealed class ManifestFile
{
    /// <summary>
    /// Archive-relative path used inside the .mdz archive.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// Original source-relative path before sanitization.
    /// </summary>
    [JsonPropertyName("originalPath")]
    public string? OriginalPath { get; set; }

    /// <summary>
    /// Optional display title, primarily for markdown content.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

internal sealed class TimestampStringOrObjectConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.TryGetProperty("when", out var when) && when.ValueKind == JsonValueKind.String)
                return when.GetString();

            return null;
        }

        using var _ = JsonDocument.ParseValue(ref reader);
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
