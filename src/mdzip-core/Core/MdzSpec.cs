using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MDZip.Core;

/// <summary>
/// A spec error code (such as <c>ERR_PATH_TRAVERSAL</c>) with a message.
/// </summary>
public sealed record MdzProblem(string Code, string Message)
{
    /// <summary>The problem as an exception whose message starts with the code, as the library throws them.</summary>
    public InvalidOperationException ToException() => MdzSpec.Error(Code, Message);
}

/// <summary>
/// MDZip spec 1.2.0 rules that don't need a ZIP: path validation and matching (§5.4, §5.4.1),
/// entry point resolution (§5.5) and manifest validation (§6.4). Ported from, and kept in step
/// with, <c>@mdzip/core-js</c> (<c>src/spec.ts</c>) and <c>mdzip-core-python</c>.
/// </summary>
/// <remarks>
/// Errors are <see cref="InvalidOperationException"/>s whose message starts with the spec error
/// code (<c>ERR_ENTRYPOINT_MISSING: ...</c>); <see cref="ErrorCode"/> reads it back.
/// </remarks>
public static class MdzSpec
{
    /// <summary>The MDZip specification version this library implements and writes.</summary>
    public const string SpecVersion = "1.2.0";

    /// <summary>The <c>spec.name</c> this library writes.</summary>
    public const string SpecName = "mdzip-spec";

    /// <summary>The highest <c>spec.version</c> major this library reads (§11.2).</summary>
    public const int SupportedMajorVersion = 1;

    /// <summary>Modes defined by the spec (§5.7).</summary>
    public static readonly IReadOnlyList<string> Modes = ["document", "project"];

    /// <summary>Support file names (§5.5), compared ASCII case-insensitively, with .md or .markdown.</summary>
    public static readonly IReadOnlyList<string> SupportFileNames = ["readme", "agents", "license", "changelog", "contributing"];

    /// <summary>Compares archive paths per §5.4.1: ordinal (case-sensitive) after NFC normalization.</summary>
    public static readonly StringComparer PathComparer = new NfcOrdinalComparer();

    private static readonly Regex SemVerRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DateTimeRegex = new(
        @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonDocumentOptions StrictJson = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    // --- Errors ---------------------------------------------------------------

    /// <summary>An exception whose message is <c>CODE: message</c>.</summary>
    public static InvalidOperationException Error(string code, string message) => new($"{code}: {message}");

    /// <summary>The spec error code at the start of an exception message, or null.</summary>
    public static string? ErrorCode(Exception? error)
    {
        var match = Regex.Match(error?.Message ?? string.Empty, @"^(ERR_[A-Z0-9_]+)\b", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    // --- Paths (§5.4, §5.4.1) --------------------------------------------------

    /// <summary>Unicode Normalization Form C.</summary>
    public static string Nfc(string path) => path.IsNormalized(NormalizationForm.FormC) ? path : path.Normalize(NormalizationForm.FormC);

    /// <summary>Lowercases A–Z only, never culture-aware (§5.4.1): <c>LICENSE</c> → <c>license</c> even under Turkish rules.</summary>
    public static string AsciiLower(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        return builder.ToString();
    }

    /// <summary>Path equality per §5.4.1: case-sensitive, after NFC normalization.</summary>
    public static bool SamePath(string a, string b) => string.Equals(Nfc(a), Nfc(b), StringComparison.Ordinal);

    /// <summary>The stored path in <paramref name="paths"/> that matches <paramref name="wanted"/> (§5.4.1), or null.</summary>
    public static string? FindPath(IEnumerable<string> paths, string wanted)
    {
        var target = Nfc(wanted);
        return paths.FirstOrDefault(p => string.Equals(Nfc(p), target, StringComparison.Ordinal));
    }

    /// <summary>True for <c>.md</c> / <c>.markdown</c>, extension compared ASCII case-insensitively.</summary>
    public static bool IsMarkdownPath(string path)
    {
        var lower = AsciiLower(path);
        return lower.EndsWith(".md", StringComparison.Ordinal) || lower.EndsWith(".markdown", StringComparison.Ordinal);
    }

    /// <summary>True for a root-level support file (§5.5): README, AGENTS, LICENSE, CHANGELOG or CONTRIBUTING.</summary>
    public static bool IsSupportFile(string path)
    {
        if (path.Contains('/') || !IsMarkdownPath(path))
            return false;
        var lower = AsciiLower(path);
        var stem = lower.EndsWith(".markdown", StringComparison.Ordinal) ? lower[..^9] : lower[..^3];
        return SupportFileNames.Contains(stem);
    }

    /// <summary>Why <paramref name="path"/> isn't a valid archive entry path (§5.4), or null. A trailing <c>/</c> marks a directory entry.</summary>
    public static MdzProblem? PathProblem(string? path)
    {
        var raw = path ?? string.Empty;
        if (raw.Length == 0)
            return new("ERR_PATH_EMPTY", "path is empty");
        if (raw.StartsWith('/'))
            return new("ERR_PATH_LEADING_SLASH", $"'{raw}' starts with '/'");
        if (raw.Any(c => c <= '\u001F' || c == '\u007F'))
            return new("ERR_PATH_CONTROL_CHAR", $"'{raw}' contains a control character");
        if (raw.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return new("ERR_PATH_RESERVED_CHAR", $"'{raw}' contains a reserved character (\\ : * ? \" < > |)");
        var segments = (raw.EndsWith('/') ? raw[..^1] : raw).Split('/');
        if (segments.Contains(".."))
            return new("ERR_PATH_TRAVERSAL", $"'{raw}' contains a '..' segment");
        if (segments.Any(s => s.Length == 0 || s == "."))
            return new("ERR_PATH_INVALID", $"'{raw}' contains an empty or '.' segment");
        return null;
    }

    /// <summary>
    /// Why a set of entry names can't form one archive (§5.4), or null. Names ending in <c>/</c> are
    /// directory entries. Two names equal after NFC are duplicates (<c>ERR_DUPLICATE_PATH</c>); a file
    /// whose path is also a directory of another entry is a conflict (<c>ERR_PATH_CONFLICT</c>).
    /// </summary>
    public static MdzProblem? EntrySetProblem(IEnumerable<string> names)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var isDirectory = name.EndsWith('/');
            var key = Nfc(isDirectory ? name[..^1] : name);
            if (!isDirectory)
            {
                if (files.TryGetValue(key, out var other))
                    return new("ERR_DUPLICATE_PATH", $"'{name}' and '{other}' are the same path (§5.4)");
                files[key] = name;
            }
            else
            {
                directories.TryAdd(key, name);
            }

            var parts = key.Split('/');
            for (var i = 1; i < parts.Length; i++)
                directories.TryAdd(string.Join('/', parts[..i]), name);
        }

        foreach (var (key, name) in files)
        {
            if (directories.TryGetValue(key, out var directory))
                return new("ERR_PATH_CONFLICT", $"'{name}' is a file and also a directory of '{directory}' (§5.4)");
        }

        return null;
    }

    /// <summary>
    /// Why a manifest <c>entryPoint</c> value isn't a valid entry point path (§5.5), or null: a non-empty
    /// string, a relative archive path (§5.4) naming a file, with a .md/.markdown extension. A single
    /// leading <c>./</c> is allowed.
    /// </summary>
    public static string? EntryPointProblem(JsonNode? value)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length == 0)
            return "entryPoint must be a non-empty string";
        var path = text.StartsWith("./", StringComparison.Ordinal) ? text[2..] : text;
        if (path.EndsWith('/'))
            return $"entryPoint '{text}' names a directory";
        var problem = PathProblem(path);
        if (problem is not null)
            return $"entryPoint '{text}' is not a valid archive path ({problem.Message})";
        return IsMarkdownPath(path) ? null : $"entryPoint '{text}' is not a .md or .markdown file";
    }

    /// <summary>Why a manifest <c>cover</c> value isn't a valid path (§6.2), or null: like <c>entryPoint</c>, any file type.</summary>
    public static string? CoverProblem(JsonNode? value)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length == 0)
            return "cover must be a non-empty string";
        var path = text.StartsWith("./", StringComparison.Ordinal) ? text[2..] : text;
        if (path.EndsWith('/'))
            return $"cover '{text}' names a directory";
        var problem = PathProblem(path);
        return problem is null ? null : $"cover '{text}' is not a valid archive path ({problem.Message})";
    }

    // --- Entry point (§5.5) ----------------------------------------------------

    /// <summary>
    /// Resolves the entry point (§5.5), throwing a coded error when there is none:
    /// <list type="number">
    /// <item>A manifest <c>entryPoint</c>, matched case-sensitively after NFC. An invalid value is
    /// <c>ERR_ENTRYPOINT_INVALID</c> (<c>null</c> included); a valid one matching no entry is
    /// <c>ERR_ENTRYPOINT_MISSING</c>. Neither falls back to the rules below.</item>
    /// <item><c>index.md</c> at the root.</item>
    /// <item>The single root Markdown file once support files are set aside; or, if every root
    /// Markdown file is a support file and there is exactly one, that file.</item>
    /// <item>Otherwise <c>ERR_ENTRYPOINT_UNRESOLVED</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="archivePaths">File paths in the archive; directory entries (ending in <c>/</c>) are ignored.</param>
    /// <param name="manifest">The parsed manifest object, or null.</param>
    /// <returns>The matching path as stored.</returns>
    public static string ResolveEntryPoint(IEnumerable<string> archivePaths, JsonObject? manifest)
    {
        var files = archivePaths.Where(p => !p.EndsWith('/')).ToList();

        if (manifest is not null && manifest.TryGetPropertyValue("entryPoint", out var entryPoint))
        {
            var problem = EntryPointProblem(entryPoint);
            if (problem is not null)
                throw Error("ERR_ENTRYPOINT_INVALID", problem);
            var value = entryPoint!.GetValue<string>();
            return FindPath(files, value.StartsWith("./", StringComparison.Ordinal) ? value[2..] : value)
                ?? throw Error("ERR_ENTRYPOINT_MISSING", $"manifest.json entryPoint \"{value}\" is not in the archive");
        }

        var index = FindPath(files, "index.md");
        if (index is not null)
            return index;

        var rootMarkdown = files.Where(p => !p.Contains('/') && IsMarkdownPath(p)).ToList();
        var content = rootMarkdown.Where(p => !IsSupportFile(p)).ToList();
        if (content.Count == 1)
            return content[0];
        if (content.Count == 0 && rootMarkdown.Count == 1)
            return rootMarkdown[0];

        throw rootMarkdown.Count == 0
            ? Error("ERR_ENTRYPOINT_UNRESOLVED", "No entry point: there is no Markdown file at the archive root.")
            : Error("ERR_ENTRYPOINT_UNRESOLVED", "No entry point: multiple Markdown files at the archive root and no manifest.json entryPoint. Add an index.md or a manifest.json entryPoint.");
    }

    /// <summary><see cref="ResolveEntryPoint(IEnumerable{string}, JsonObject?)"/>, returning null instead of throwing.</summary>
    public static string? TryResolveEntryPoint(IEnumerable<string> archivePaths, JsonObject? manifest)
    {
        try
        {
            return ResolveEntryPoint(archivePaths, manifest);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // --- Manifest (§6.4) -------------------------------------------------------

    /// <summary>True for a Semantic Versioning 2.0.0 string.</summary>
    public static bool IsSemVer(string? value) => value is not null && SemVerRegex.IsMatch(value);

    /// <summary>True for an RFC 3339 date-time string (§6.1).</summary>
    public static bool IsDateTime(string? value) => value is not null && DateTimeRegex.IsMatch(value);

    /// <summary>
    /// Parses <c>manifest.json</c> text (a UTF-8 BOM is ignored). Throws <c>ERR_MANIFEST_INVALID</c> unless it
    /// is strict JSON whose top-level value is an object. For duplicate members the last one wins; their
    /// names are returned so callers can report them (§6.4).
    /// </summary>
    public static (JsonObject Manifest, IReadOnlyList<string> DuplicateMembers) ParseManifest(string text)
    {
        var source = text.TrimStart('﻿');
        var duplicates = new List<string>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source, StrictJson);
        }
        catch (JsonException ex)
        {
            throw Error("ERR_MANIFEST_INVALID", $"manifest.json is invalid JSON ({ex.Message})");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Error("ERR_MANIFEST_INVALID", "manifest.json must be a JSON object");
            return ((JsonObject)ToNode(document.RootElement, duplicates)!, duplicates);
        }
    }

    // JsonNode.Parse throws on duplicate members; build the tree by hand so the last one wins.
    private static JsonNode? ToNode(JsonElement element, List<string> duplicates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (obj.ContainsKey(property.Name))
                    {
                        duplicates.Add(property.Name);
                        obj.Remove(property.Name);
                    }
                    obj[property.Name] = ToNode(property.Value, duplicates);
                }
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    array.Add(ToNode(item, duplicates));
                return array;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    private static bool IsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static bool IsTimestamp(JsonNode? node) =>
        (IsString(node) && IsDateTime(node!.GetValue<string>()))
        || (node is JsonObject o && IsString(o["when"]) && IsDateTime(o["when"]!.GetValue<string>()));

    private static readonly (string Name, Func<JsonNode?, bool> Ok)[] DescriptiveFields =
    [
        ("title", n => IsString(n) && n!.GetValue<string>().Length > 0),
        ("language", IsString),
        ("description", IsString),
        ("version", IsString),
        ("license", IsString),
        ("keywords", n => n is JsonArray a && a.All(IsString)),
        ("author", n => n is JsonObject),
        ("producer", n => n is JsonObject),
        ("created", IsTimestamp),
        ("modified", IsTimestamp),
        ("cover", n => CoverProblem(n) is null),
    ];

    /// <summary>
    /// Applies the §6.4 rules to a parsed manifest and returns the descriptive fields a consumer ignores
    /// because their type or value is wrong. Critical fields throw: <c>spec</c>/<c>spec.version</c>
    /// (<c>ERR_MANIFEST_INVALID</c>, or <c>ERR_VERSION_UNSUPPORTED</c> for a newer major), <c>mode</c>
    /// (<c>ERR_MANIFEST_INVALID</c> or <c>ERR_MODE_UNSUPPORTED</c>) and, unless
    /// <paramref name="checkEntryPoint"/> is false, <c>entryPoint</c> (<c>ERR_ENTRYPOINT_INVALID</c>).
    /// <c>null</c> is a wrong type. Unrecognized fields are never checked.
    /// </summary>
    public static IReadOnlyList<string> CheckManifest(JsonObject manifest, bool checkEntryPoint = true)
    {
        if (manifest.TryGetPropertyValue("spec", out var spec))
        {
            if (spec is not JsonObject specObject)
                throw Error("ERR_MANIFEST_INVALID", $"spec must be an object, not {Describe(spec)}");
            if (specObject.TryGetPropertyValue("version", out var version))
            {
                if (!IsString(version))
                    throw Error("ERR_MANIFEST_INVALID", $"spec.version must be a string, not {Describe(version)}");
                var text = version!.GetValue<string>();
                var match = SemVerRegex.Match(text);
                if (!match.Success)
                    throw Error("ERR_MANIFEST_INVALID", $"spec.version '{text}' is not a semantic version");
                if (int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture) > SupportedMajorVersion)
                    throw Error("ERR_VERSION_UNSUPPORTED", $"archive targets spec {text}; this library supports {SupportedMajorVersion}.x");
            }
        }

        if (manifest.TryGetPropertyValue("mode", out var mode))
        {
            if (!IsString(mode))
                throw Error("ERR_MANIFEST_INVALID", $"mode must be a string, not {Describe(mode)}");
            var text = mode!.GetValue<string>();
            if (!Modes.Contains(text))
                throw Error("ERR_MODE_UNSUPPORTED", $"unrecognized mode '{text}'; supported: {string.Join(", ", Modes)}");
        }

        if (checkEntryPoint && manifest.TryGetPropertyValue("entryPoint", out var entryPoint))
        {
            var problem = EntryPointProblem(entryPoint);
            if (problem is not null)
                throw Error("ERR_ENTRYPOINT_INVALID", problem);
        }

        return DescriptiveFields
            .Where(field => manifest.TryGetPropertyValue(field.Name, out var value) && !field.Ok(value))
            .Select(field => field.Name)
            .ToList();
    }

    private static string Describe(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private sealed class NfcOrdinalComparer : StringComparer
    {
        public override int Compare(string? x, string? y) =>
            string.CompareOrdinal(x is null ? null : Nfc(x), y is null ? null : Nfc(y));

        public override bool Equals(string? x, string? y) =>
            string.Equals(x is null ? null : Nfc(x), y is null ? null : Nfc(y), StringComparison.Ordinal);

        public override int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Nfc(obj));
    }
}
