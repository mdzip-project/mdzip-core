using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MDZip.Core.Models;

namespace MDZip.Core;

/// <summary>
/// Validation result for a .mdz archive.
/// </summary>
public sealed class ValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Core logic for reading, writing, and validating .mdz archives.
/// </summary>
public static class MdzArchive
{
    private const string ManifestFileName = "manifest.json";
    private const string SpecName = "markdownzip-spec";
    private const string ProducedSpecVersion = "1.0.1-draft";
    private const int SupportedMajorVersion = 1;
    private static readonly Regex SemVerRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static readonly JsonDocumentOptions StrictJsonDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    // -------------------------------------------------------------------------
    // Create
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a .mdz archive from a source directory.
    /// </summary>
    public static void Create(
        string outputPath,
        string sourceDirectory,
        Manifest? manifest = null)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new ArgumentException($"Source directory '{sourceDirectory}' does not exist.", nameof(sourceDirectory));

        var allFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToList();
        var archivePaths = new List<string>();

        foreach (var filePath in allFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (manifest is not null && relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            archivePaths.Add(relativePath);
        }

        EnsureCreatableEntryPoint(archivePaths, manifest);

        CreateAtomic(outputPath, archive =>
        {
            foreach (var filePath in allFiles)
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                // Skip existing manifest.json if we are writing our own
                if (manifest is not null && relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var error = PathValidator.Validate(relativePath);
                if (error is not null)
                    throw new InvalidOperationException($"Invalid path in source: {error}");

                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
                using var entryStream = entry.Open();

                // Normalise text file line endings to LF
                if (IsTextFile(relativePath))
                {
                    var content = File.ReadAllText(filePath, Encoding.UTF8);
                    content = NormaliseLf(content);
                    var bytes = Encoding.UTF8.GetBytes(content);
                    entryStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    using var fileStream = File.OpenRead(filePath);
                    fileStream.CopyTo(entryStream);
                }
            }

            // Write manifest last (if provided)
            if (manifest is not null)
            {
                manifest.Spec ??= new ManifestSpec();
                manifest.Spec.Name ??= SpecName;
                manifest.Spec.Version ??= ProducedSpecVersion;
                manifest.Created ??= DateTime.UtcNow.ToString("o");
                manifest.Modified = DateTime.UtcNow.ToString("o");

                var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
                using var ms = manifestEntry.Open();
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
                json = NormaliseLf(json);
                var bytes = Encoding.UTF8.GetBytes(json);
                ms.Write(bytes, 0, bytes.Length);
            }
        });
    }

    /// <summary>
    /// Creates a .mdz archive from an explicit list of (archivePath, localPath) pairs.
    /// </summary>
    public static void CreateFromFiles(
        string outputPath,
        IEnumerable<(string ArchivePath, string LocalPath)> files,
        Manifest? manifest = null)
    {
        var fileList = files.ToList();
        var archivePaths = fileList
            .Select(f => f.ArchivePath.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !(manifest is not null && path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        EnsureCreatableEntryPoint(archivePaths, manifest);

        CreateAtomic(outputPath, archive =>
        {
            foreach (var (archivePath, localPath) in fileList)
            {
                var normalised = archivePath.Replace(Path.DirectorySeparatorChar, '/');

                if (manifest is not null && normalised.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var error = PathValidator.Validate(normalised);
                if (error is not null)
                    throw new InvalidOperationException($"Invalid path '{normalised}': {error}");

                var entry = archive.CreateEntry(normalised, CompressionLevel.Optimal);
                using var entryStream = entry.Open();

                if (IsTextFile(normalised))
                {
                    var content = File.ReadAllText(localPath, Encoding.UTF8);
                    content = NormaliseLf(content);
                    var bytes = Encoding.UTF8.GetBytes(content);
                    entryStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    using var fileStream = File.OpenRead(localPath);
                    fileStream.CopyTo(entryStream);
                }
            }

            if (manifest is not null)
            {
                manifest.Spec ??= new ManifestSpec();
                manifest.Spec.Name ??= SpecName;
                manifest.Spec.Version ??= ProducedSpecVersion;
                manifest.Created ??= DateTime.UtcNow.ToString("o");
                manifest.Modified = DateTime.UtcNow.ToString("o");

                var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
                using var ms = manifestEntry.Open();
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
                json = NormaliseLf(json);
                var bytes = Encoding.UTF8.GetBytes(json);
                ms.Write(bytes, 0, bytes.Length);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Update (in-place)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds a file to an existing .mdz archive or replaces an existing entry at the same archive path.
    /// </summary>
    public static void AddFile(string archivePath, string archiveEntryPath, string localFilePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        if (!File.Exists(localFilePath))
            throw new FileNotFoundException($"Source file '{localFilePath}' does not exist.", localFilePath);

        var normalisedPath = archiveEntryPath.Replace(Path.DirectorySeparatorChar, '/');
        var pathError = PathValidator.Validate(normalisedPath);
        if (pathError is not null)
            throw new InvalidOperationException($"Invalid path '{normalisedPath}': {pathError}");

        CreateAtomic(archivePath, destinationArchive =>
        {
            using var sourceArchive = ZipFile.OpenRead(archivePath);
            var existingPaths = sourceArchive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(p => !p.Equals(normalisedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            existingPaths.Add(normalisedPath);

            var manifest = ReadManifestFromArchive(
                sourceArchive,
                normalisedPath,
                localFilePath,
                requireValidReplacementManifest: true,
                requireValidExistingManifest: true);
            EnsureCreatableEntryPoint(existingPaths, manifest);
            var refreshedManifestJson = normalisedPath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
                ? null
                : TryRefreshManifestModifiedJson(sourceArchive);

            foreach (var sourceEntry in sourceArchive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
            {
                var sourcePath = sourceEntry.FullName.Replace('\\', '/');
                if (sourcePath.Equals(normalisedPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (refreshedManifestJson is not null
                    && sourcePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CopyEntry(sourceEntry, destinationArchive);
            }

            if (normalisedPath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (manifest is null)
                    throw new InvalidOperationException("Replacement manifest.json is invalid: expected a JSON object.");

                // Use raw JSON so that created.by / modified.by subobjects are preserved.
                var rawJson = File.ReadAllText(localFilePath, Encoding.UTF8);
                WriteManifestEntry(destinationArchive, PrepareReplacementManifestJson(rawJson));
            }
            else
            {
                WriteFileEntry(destinationArchive, normalisedPath, localFilePath);
                if (refreshedManifestJson is not null)
                    WriteManifestEntry(destinationArchive, refreshedManifestJson);
            }
        });
    }

    /// <summary>
    /// Removes a file from an existing .mdz archive.
    /// </summary>
    public static void RemoveFile(string archivePath, string archiveEntryPath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        var normalisedPath = archiveEntryPath.Replace(Path.DirectorySeparatorChar, '/');
        var pathError = PathValidator.Validate(normalisedPath);
        if (pathError is not null)
            throw new InvalidOperationException($"Invalid path '{normalisedPath}': {pathError}");

        CreateAtomic(archivePath, destinationArchive =>
        {
            using var sourceArchive = ZipFile.OpenRead(archivePath);

            var sourceEntries = sourceArchive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .ToList();

            var entryToRemove = sourceEntries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(normalisedPath, StringComparison.OrdinalIgnoreCase));
            if (entryToRemove is null)
                throw new FileNotFoundException($"Entry '{normalisedPath}' was not found in archive.", normalisedPath);

            var remainingPaths = sourceEntries
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(p => !p.Equals(normalisedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var manifest = ReadManifestFromArchive(
                sourceArchive,
                normalisedPath,
                localManifestPath: null,
                requireValidReplacementManifest: false,
                requireValidExistingManifest: true);
            EnsureCreatableEntryPoint(remainingPaths, manifest);
            var refreshedManifestJson = normalisedPath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
                ? null
                : TryRefreshManifestModifiedJson(sourceArchive);

            foreach (var sourceEntry in sourceEntries)
            {
                var sourcePath = sourceEntry.FullName.Replace('\\', '/');
                if (sourcePath.Equals(normalisedPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (refreshedManifestJson is not null
                    && sourcePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CopyEntry(sourceEntry, destinationArchive);
            }

            if (refreshedManifestJson is not null)
                WriteManifestEntry(destinationArchive, refreshedManifestJson);
        });
    }

    // -------------------------------------------------------------------------
    // Extract
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts a .mdz archive to a destination directory.
    /// </summary>
    public static void Extract(string archivePath, string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        Directory.CreateDirectory(destinationDirectory);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry

            var entryPath = entry.FullName.Replace('\\', '/');

            var error = PathValidator.Validate(entryPath);
            if (error is not null)
                throw new InvalidOperationException($"Refusing to extract entry with invalid path: {error}");

            var destPath = Path.Combine(destinationDirectory, entryPath.Replace('/', Path.DirectorySeparatorChar));

            // Ensure the path does not escape destination directory
            var fullDest = Path.GetFullPath(destPath);
            var fullDestDir = Path.GetFullPath(destinationDirectory);
            if (!fullDest.StartsWith(fullDestDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !fullDest.Equals(fullDestDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Path traversal attempt detected for entry '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(destPath);
            entryStream.CopyTo(fileStream);
        }
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a list of all entry paths within the archive.
    /// </summary>
    public static IReadOnlyList<string> List(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => e.FullName.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns detailed entry information for all files in the archive.
    /// </summary>
    public static IReadOnlyList<ArchiveEntry> ListDetailed(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => new ArchiveEntry(
                e.FullName.Replace('\\', '/'),
                e.Length,
                e.CompressedLength,
                e.LastWriteTime.UtcDateTime))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Inspect / Read manifest
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the manifest.json from the archive, or returns null if not present.
    /// </summary>
    public static Manifest? ReadManifest(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = FindEntry(archive, ManifestFileName);
        if (manifestEntry is null)
            return null;

        using var stream = manifestEntry.Open();
        return JsonSerializer.Deserialize<Manifest>(stream, JsonOptions);
    }

    /// <summary>
    /// Resolves the entry point Markdown file for the archive per Section 5.5.
    /// Returns null if no unambiguous entry point can be determined.
    /// </summary>
    public static string? ResolveEntryPoint(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        // Read manifest so its entryPoint override is honoured
        Manifest? manifest = null;
        var manifestEntry = FindEntry(archive, ManifestFileName);
        if (manifestEntry is not null)
        {
            try
            {
                using var stream = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonOptions);
            }
            catch (JsonException)
            {
                // Ignore parse errors here; validation will catch them
            }
        }

        return ResolveEntryPoint(archive, manifest);
    }

    // -------------------------------------------------------------------------
    // Validate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates a .mdz archive against the specification.
    /// </summary>
    public static ValidationResult Validate(string archivePath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            if (IsArchiveEncrypted(archivePath))
            {
                errors.Add("ERR_ZIP_ENCRYPTED: Encrypted ZIP entries are not supported.");
                return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            errors.Add("ERR_ZIP_INVALID: The file is not a valid ZIP archive.");
            return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
        }

        ZipArchive? archive = null;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException)
        {
            errors.Add("ERR_ZIP_INVALID: The file is not a valid ZIP archive.");
            return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
        }

        using (archive)
        {
            // Check for encrypted entries
            foreach (var entry in archive.Entries)
            {
                // Encryption is validated up front by reading central directory flags.
                _ = entry;
            }

            // Validate all entry paths
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var path = entry.FullName.Replace('\\', '/');
                var pathError = PathValidator.Validate(path);
                if (pathError is not null)
                    errors.Add($"ERR_PATH_INVALID: {pathError}");
            }

            // Parse manifest if present
            Manifest? manifest = null;
            var manifestEntry = FindEntry(archive, ManifestFileName);
            if (manifestEntry is not null)
            {
                JsonElement? manifestRoot = null;
                try
                {
                    using var stream = manifestEntry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var manifestText = reader.ReadToEnd();
                    using var manifestJson = JsonDocument.Parse(manifestText, StrictJsonDocumentOptions);
                    manifestRoot = manifestJson.RootElement.Clone();
                    manifest = JsonSerializer.Deserialize<Manifest>(manifestText, JsonOptions);
                }
                catch (JsonException ex)
                {
                    errors.Add($"ERR_MANIFEST_INVALID: manifest.json could not be parsed: {ex.Message}");
                    return new ValidationResult { IsValid = errors.Count == 0, Errors = errors, Warnings = warnings };
                }

                if (manifest is null)
                {
                    errors.Add("ERR_MANIFEST_INVALID: manifest.json deserialised to null.");
                }
                else
                {
                    if (manifestRoot is not null)
                    {
                        ValidateDraftTimestampField(manifestRoot.Value, "created", errors);
                        ValidateDraftTimestampField(manifestRoot.Value, "modified", errors);
                    }

                    if (string.IsNullOrWhiteSpace(manifest.Spec?.Version))
                    {
                        warnings.Add("manifest 'spec.version' is missing; version metadata is unavailable.");
                    }
                    else
                    {
                        // Validate SemVer 2.0.0 and enforce major-version compatibility:
                        // - reject higher unsupported major versions
                        // - allow lower major versions with a warning
                        if (!TryParseSemVerMajor(manifest.Spec.Version, out var major))
                            errors.Add($"ERR_MANIFEST_INVALID: 'spec.version' field '{manifest.Spec.Version}' is not a valid semver string.");
                        else if (major > SupportedMajorVersion)
                            errors.Add($"ERR_VERSION_UNSUPPORTED: manifest 'spec.version' major version {major} is not supported (supported: {SupportedMajorVersion}).");
                        else if (major < SupportedMajorVersion)
                            warnings.Add($"manifest 'spec.version' major version {major} is older than supported major {SupportedMajorVersion}.");
                    }

                    if (manifest.Mode is not null && !IsSupportedMode(manifest.Mode))
                    {
                        errors.Add($"ERR_MODE_UNSUPPORTED: manifest 'mode' value '{manifest.Mode}' is not supported.");
                    }

                    if (manifest.Title is not null && string.IsNullOrWhiteSpace(manifest.Title))
                        errors.Add("ERR_MANIFEST_INVALID: manifest field 'title' must not be empty when present.");

                    // Validate entryPoint reference
                    if (!string.IsNullOrWhiteSpace(manifest.EntryPoint))
                    {
                        if (!IsMarkdownPath(manifest.EntryPoint))
                        {
                            errors.Add("ERR_ENTRYPOINT_INVALID: manifest 'entryPoint' must reference a Markdown file.");
                        }
                        else if (FindEntry(archive, manifest.EntryPoint) is null)
                        {
                            errors.Add($"ERR_ENTRYPOINT_MISSING: manifest 'entryPoint' references '{manifest.EntryPoint}' which does not exist in the archive.");
                        }
                    }

                    // Validate cover reference
                    if (!string.IsNullOrWhiteSpace(manifest.Cover))
                    {
                        if (FindEntry(archive, manifest.Cover) is null)
                            warnings.Add($"manifest 'cover' references '{manifest.Cover}' which does not exist in the archive.");
                    }
                }
            }
            else
            {
                warnings.Add("No manifest.json present. Version metadata is unavailable.");
            }

            // Validate entry point resolution
            var entryPoint = ResolveEntryPoint(archive, manifest);
            if (entryPoint is null)
            {
                errors.Add("ERR_ENTRYPOINT_UNRESOLVED: No unambiguous primary Markdown file could be determined.");
            }
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalised = path.Replace('\\', '/');
        return archive.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(normalised, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveEntryPoint(ZipArchive archive, Manifest? manifest = null)
    {
        // 1. manifest.json entryPoint
        if (manifest?.EntryPoint is { Length: > 0 } ep)
        {
            if (IsMarkdownPath(ep) && FindEntry(archive, ep) is not null)
                return ep;
        }

        // 2. index.md at archive root
        if (FindEntry(archive, "index.md") is not null)
            return "index.md";

        // 3. Exactly one .md/.markdown file at archive root
        var rootMarkdown = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.Contains('/'))
            .Where(e => e.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                     || e.Name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rootMarkdown.Count == 1)
            return rootMarkdown[0].FullName.Replace('\\', '/');

        return null;
    }

    private static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".markdown" or ".json" or ".txt" or ".css" or ".html" or ".htm"
            or ".xml" or ".svg" or ".yaml" or ".yml" or ".toml";
    }

    private static void EnsureCreatableEntryPoint(IReadOnlyList<string> archivePaths, Manifest? manifest)
    {
        if (manifest?.Mode is not null && !IsSupportedMode(manifest.Mode))
        {
            throw new InvalidOperationException(
                $"Manifest mode '{manifest.Mode}' is not supported. Allowed values are 'document' and 'project'.");
        }

        if (manifest?.EntryPoint is { Length: > 0 } entryPointFormat
            && !IsMarkdownPath(entryPointFormat))
        {
            throw new InvalidOperationException(
                $"Manifest entryPoint '{entryPointFormat}' must reference a Markdown file.");
        }

        if (manifest?.EntryPoint is { Length: > 0 } entryPoint
            && !archivePaths.Any(path => path.Equals(entryPoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Manifest entryPoint '{entryPoint}' does not exist in the source content.");
        }

        if (ResolveEntryPoint(archivePaths, manifest) is null)
        {
            throw new InvalidOperationException(
                "No unambiguous entry point could be determined. Add index.md at the archive root, keep exactly one root Markdown file, or set --entry-point.");
        }
    }

    private static string? ResolveEntryPoint(IReadOnlyList<string> archivePaths, Manifest? manifest = null)
    {
        if (manifest?.EntryPoint is { Length: > 0 } ep
            && archivePaths.Any(path => path.Equals(ep, StringComparison.OrdinalIgnoreCase)))
        {
            if (IsMarkdownPath(ep))
                return ep;
        }

        if (archivePaths.Any(path => path.Equals("index.md", StringComparison.OrdinalIgnoreCase)))
            return "index.md";

        var rootMarkdown = archivePaths
            .Where(path => !path.Contains('/'))
            .Where(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rootMarkdown.Count == 1)
            return rootMarkdown[0];

        return null;
    }

    private static bool TryParseSemVerMajor(string version, out int major)
    {
        major = default;
        var match = SemVerRegex.Match(version);
        if (!match.Success)
            return false;

        return int.TryParse(match.Groups["major"].Value, out major);
    }

    private static string NormaliseLf(string content) =>
        content.Replace("\r\n", "\n").Replace("\r", "\n");

    private static bool IsMarkdownPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedMode(string mode) =>
        mode is "document" or "project";

    private static Manifest? ReadManifestFromArchive(
        ZipArchive archive,
        string replacedOrRemovedPath,
        string? localManifestPath,
        bool requireValidReplacementManifest,
        bool requireValidExistingManifest)
    {
        if (replacedOrRemovedPath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            if (localManifestPath is null)
                return null;

            try
            {
                var manifestText = File.ReadAllText(localManifestPath, Encoding.UTF8);
                using var _ = JsonDocument.Parse(manifestText, StrictJsonDocumentOptions); // Must be strict JSON if manifest.json is present.
                var manifest = JsonSerializer.Deserialize<Manifest>(manifestText, JsonOptions);
                if (manifest is null)
                {
                    if (requireValidReplacementManifest)
                        throw new InvalidOperationException("Replacement manifest.json is invalid: expected a JSON object.");

                    return null;
                }

                return manifest;
            }
            catch (JsonException)
            {
                if (requireValidReplacementManifest)
                    throw new InvalidOperationException("Replacement manifest.json is invalid JSON.");

                return null;
            }
        }

        var manifestEntry = FindEntry(archive, ManifestFileName);
        if (manifestEntry is null)
            return null;

        try
        {
            using var stream = manifestEntry.Open();
            var manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonOptions);
            if (manifest is null && requireValidExistingManifest)
                throw new InvalidOperationException("Existing manifest.json is invalid: expected a JSON object.");

            return manifest;
        }
        catch (JsonException)
        {
            if (requireValidExistingManifest)
                throw new InvalidOperationException("Existing manifest.json is invalid JSON.");

            return null;
        }
    }

    private static void CopyEntry(ZipArchiveEntry sourceEntry, ZipArchive destinationArchive)
    {
        var path = sourceEntry.FullName.Replace('\\', '/');
        var destinationEntry = destinationArchive.CreateEntry(path, CompressionLevel.Optimal);
        using var source = sourceEntry.Open();
        using var destination = destinationEntry.Open();
        source.CopyTo(destination);
    }

    private static void WriteFileEntry(ZipArchive archive, string archivePath, string localFilePath)
    {
        var entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
        using var entryStream = entry.Open();

        if (IsTextFile(archivePath))
        {
            var content = File.ReadAllText(localFilePath, Encoding.UTF8);
            content = NormaliseLf(content);
            var bytes = Encoding.UTF8.GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
            return;
        }

        using var fileStream = File.OpenRead(localFilePath);
        fileStream.CopyTo(entryStream);
    }

    /// <summary>
    /// Prepares raw manifest JSON for writing: injects spec metadata and refreshes timestamps
    /// using raw JSON manipulation to preserve fields like created.by / modified.by.
    /// </summary>
    private static string PrepareReplacementManifestJson(string rawJson)
    {
        var node = JsonNode.Parse(rawJson, nodeOptions: null, documentOptions: StrictJsonDocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("Replacement manifest.json is invalid: expected a JSON object.");

        if (node["spec"] is not JsonObject specNode)
        {
            specNode = [];
            node["spec"] = specNode;
        }
        if (specNode["name"] is null) specNode["name"] = SpecName;
        if (specNode["version"] is null) specNode["version"] = ProducedSpecVersion;

        if (node["created"] is null)
            node["created"] = DateTime.UtcNow.ToString("o");

        var now = DateTime.UtcNow.ToString("o");
        if (node["modified"] is JsonObject modifiedObj)
            modifiedObj["when"] = now;
        else
            node["modified"] = now;

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteManifestEntry(ZipArchive archive, string manifestJson)
    {
        var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
        using var stream = manifestEntry.Open();
        var normalised = NormaliseLf(manifestJson);
        var bytes = Encoding.UTF8.GetBytes(normalised);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string? TryRefreshManifestModifiedJson(ZipArchive archive)
    {
        var manifestEntry = FindEntry(archive, ManifestFileName);
        if (manifestEntry is null)
            return null;

        try
        {
            using var stream = manifestEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd();
            var node = JsonNode.Parse(json, nodeOptions: null, documentOptions: StrictJsonDocumentOptions) as JsonObject;
            if (node is null)
                return null;

            var now = DateTime.UtcNow.ToString("o");
            if (node["modified"] is JsonObject modifiedObject)
            {
                modifiedObject["when"] = now;
            }
            else
            {
                node["modified"] = now;
            }

            // Ensure spec.version is present (required when a conforming producer emits a manifest).
            if (node["spec"] is not JsonObject specNode)
            {
                specNode = [];
                node["spec"] = specNode;
            }
            if (specNode["name"] is null) specNode["name"] = SpecName;
            if (specNode["version"] is null) specNode["version"] = ProducedSpecVersion;

            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateDraftTimestampField(JsonElement manifestRoot, string fieldName, ICollection<string> errors)
    {
        if (!manifestRoot.TryGetProperty(fieldName, out var value))
            return;

        if (value.ValueKind == JsonValueKind.String)
            return;

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("when", out var when)
                || when.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(when.GetString()))
            {
                errors.Add($"ERR_MANIFEST_INVALID: manifest field '{fieldName}' object form must include string property 'when'.");
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            errors.Add($"ERR_MANIFEST_INVALID: manifest field '{fieldName}' must be a string or an object with 'when', not null.");
            return;
        }

        errors.Add($"ERR_MANIFEST_INVALID: manifest field '{fieldName}' must be a string or an object with 'when'.");
    }

    private static void CreateAtomic(string outputPath, Action<ZipArchive> writeArchive)
    {
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var tempPath = Path.Combine(
            outputDir ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                writeArchive(archive);
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            File.Move(tempPath, outputPath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static bool IsArchiveEncrypted(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        const uint EndOfCentralDirectorySignature = 0x06054B50;
        const uint CentralDirectoryHeaderSignature = 0x02014B50;
        const int EocdMinSize = 22;
        const int MaxCommentLength = 65535;

        var scanSize = (int)Math.Min(stream.Length, EocdMinSize + MaxCommentLength);
        if (scanSize < EocdMinSize)
            return false;

        var tail = new byte[scanSize];
        stream.Seek(-scanSize, SeekOrigin.End);
        _ = stream.Read(tail, 0, tail.Length);

        var eocdOffsetInTail = -1;
        for (var i = tail.Length - EocdMinSize; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(tail, i) == EndOfCentralDirectorySignature)
            {
                eocdOffsetInTail = i;
                break;
            }
        }

        if (eocdOffsetInTail < 0)
            return false;

        var centralDirectoryOffset = BitConverter.ToUInt32(tail, eocdOffsetInTail + 16);
        var totalEntries = BitConverter.ToUInt16(tail, eocdOffsetInTail + 10);

        stream.Seek(centralDirectoryOffset, SeekOrigin.Begin);

        for (var i = 0; i < totalEntries; i++)
        {
            if (reader.ReadUInt32() != CentralDirectoryHeaderSignature)
                return false;

            _ = reader.ReadUInt16(); // version made by
            _ = reader.ReadUInt16(); // version needed
            var generalPurposeBitFlag = reader.ReadUInt16();

            if ((generalPurposeBitFlag & 0x0001) != 0)
                return true;

            _ = reader.ReadUInt16(); // compression method
            _ = reader.ReadUInt16(); // mod time
            _ = reader.ReadUInt16(); // mod date
            _ = reader.ReadUInt32(); // crc32
            _ = reader.ReadUInt32(); // compressed size
            _ = reader.ReadUInt32(); // uncompressed size

            var fileNameLength = reader.ReadUInt16();
            var extraLength = reader.ReadUInt16();
            var commentLength = reader.ReadUInt16();

            _ = reader.ReadUInt16(); // disk number start
            _ = reader.ReadUInt16(); // internal attrs
            _ = reader.ReadUInt32(); // external attrs
            _ = reader.ReadUInt32(); // local header offset

            stream.Seek(fileNameLength + extraLength + commentLength, SeekOrigin.Current);
        }

        return false;
    }
}

/// <summary>
/// Detailed information about a single archive entry.
/// </summary>
public record ArchiveEntry(string Path, long Size, long CompressedSize, DateTime LastModified);
