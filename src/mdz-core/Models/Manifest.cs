using System.Text.Json.Serialization;

namespace Mdz.Models;

/// <summary>
/// Represents the optional manifest.json file at the root of a .mdz archive.
/// </summary>
public sealed class Manifest
{
    /// <summary>
    /// The version of the MDZ specification the file conforms to. REQUIRED when manifest is present.
    /// Must be a Semantic Versioning 2.0.0 string.
    /// </summary>
    [JsonPropertyName("mdz")]
    public string? Mdz { get; set; }

    /// <summary>
    /// The human-readable title of the document. REQUIRED when manifest is present.
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
    /// An array of author objects.
    /// </summary>
    [JsonPropertyName("authors")]
    public List<Author>? Authors { get; set; }

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
    /// </summary>
    [JsonPropertyName("created")]
    public string? Created { get; set; }

    /// <summary>
    /// The last-modified datetime of the document in ISO 8601 format.
    /// </summary>
    [JsonPropertyName("modified")]
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
}

/// <summary>
/// Represents an author entry in the manifest.
/// </summary>
public sealed class Author
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
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
