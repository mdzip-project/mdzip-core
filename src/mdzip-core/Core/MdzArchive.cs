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
    private const string ProducedSpecVersion = "1.1.0-draft";
    private const int SupportedMajorVersion = 1;
    private static readonly IReadOnlyDictionary<string, string> ImageMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["png"] = "image/png",
            ["jpg"] = "image/jpeg",
            ["jpeg"] = "image/jpeg",
            ["gif"] = "image/gif",
            ["webp"] = "image/webp",
            ["svg"] = "image/svg+xml",
            ["avif"] = "image/avif",
            ["ico"] = "image/x-icon",
        };

    private static readonly IReadOnlyDictionary<string, string> KnownMimeTypes =
        new Dictionary<string, string>(ImageMimeTypes, StringComparer.OrdinalIgnoreCase)
        {
            ["mp3"] = "audio/mpeg",
            ["wav"] = "audio/wav",
            ["ogg"] = "audio/ogg",
            ["m4a"] = "audio/mp4",
            ["mp4"] = "video/mp4",
            ["webm"] = "video/webm",
            ["mov"] = "video/quicktime",
            ["woff"] = "font/woff",
            ["woff2"] = "font/woff2",
            ["ttf"] = "font/ttf",
            ["otf"] = "font/otf",
            ["json"] = "application/json",
            ["csv"] = "text/csv",
            ["txt"] = "text/plain",
            ["css"] = "text/css",
            ["html"] = "text/html",
            ["htm"] = "text/html",
            ["xml"] = "application/xml",
            ["pdf"] = "application/pdf",
        };

    private static readonly Regex SemVerRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
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

    /// <summary>
    /// Builds a .mdz archive from local files with packaging options and warnings.
    /// </summary>
    public static MdzPackBuildResult BuildArchive(
        string outputPath,
        IEnumerable<MdzPackInputFile> files,
        string rootName,
        MdzPackOptions options)
    {
        var cleanInput = files
            .Select(file => new MdzPackInputFile(NormaliseArchivePath(file.ArchivePath), file.LocalPath))
            .Where(file => !string.IsNullOrWhiteSpace(file.ArchivePath) && !file.ArchivePath.EndsWith("/", StringComparison.Ordinal))
            .ToList();

        if (cleanInput.Count == 0)
            throw new InvalidOperationException("ERR_PACK_NO_INPUT: No files found to package.");

        var manifest = BuildManifestFromOptions(rootName, options);
        var skipMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<MdzSelectedFile>();
        var manifestFiles = new List<ManifestFile>();
        var invalidPathCount = 0;
        var sanitizedPathCount = 0;

        void AddSkip(string reason) =>
            skipMap[reason] = skipMap.TryGetValue(reason, out var count) ? count + 1 : 1;

        foreach (var item in cleanInput)
        {
            var originalPath = item.ArchivePath;
            var archivePath = originalPath;

            if (!MatchesAnyFilter(originalPath, options.Filters))
            {
                AddSkip("excluded by filter");
                continue;
            }

            if (manifest is not null && archivePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                AddSkip("manifest.json replaced by generated manifest");
                continue;
            }

            var pathError = PathValidator.Validate(archivePath);
            if (pathError is not null)
            {
                invalidPathCount++;
                if (!options.MapFiles)
                {
                    AddSkip("invalid path for MDZ rules");
                    continue;
                }

                archivePath = MakeUniqueArchivePath(SanitiseArchivePath(archivePath), usedPaths);
                sanitizedPathCount++;
            }
            else
            {
                archivePath = MakeUniqueArchivePath(archivePath, usedPaths);
            }

            selected.Add(new MdzSelectedFile(archivePath, originalPath, item.LocalPath));

            if (options.MapFiles && manifest is not null && IsMarkdownPath(archivePath))
            {
                var title = Path.GetFileNameWithoutExtension(originalPath)
                    .Replace('_', ' ')
                    .Replace('-', ' ')
                    .Trim();
                manifestFiles.Add(new ManifestFile
                {
                    Path = archivePath,
                    OriginalPath = originalPath,
                    Title = string.IsNullOrWhiteSpace(title) ? originalPath : title,
                });
            }
        }

        if (manifest is null)
            manifest = ReadProvidedManifest(selected);

        var archivePaths = selected.Select(file => file.ArchivePath).ToList();
        var resolvedEntryPoint = ResolveEntryPoint(archivePaths, manifest);
        var warningMessages = new List<string>();
        var markdownPaths = archivePaths.Where(IsMarkdownPath).ToList();

        if (manifest?.Mode is null && markdownPaths.Count > 1)
        {
            warningMessages.Add(
                "Archive contains multiple Markdown files and no explicit manifest.mode; consumers will default to document mode. If these files are intended as separate documents, set mode: \"project\".");
        }

        if (options.CreateIndex && resolvedEntryPoint is null)
        {
            if (!string.IsNullOrWhiteSpace(manifest?.EntryPoint))
                throw new InvalidOperationException($"ERR_PACK_ENTRYPOINT_MISSING: Manifest entry-point \"{manifest.EntryPoint}\" does not exist.");

            var generatedIndex = BuildGeneratedIndex(markdownPaths, options.Title ?? rootName);
            var generatedPath = MakeUniqueArchivePath("index.md", usedPaths);
            var generatedLocalPath = Path.Combine(Path.GetTempPath(), $"mdzip-core-index-{Guid.NewGuid():N}.md");
            File.WriteAllText(generatedLocalPath, generatedIndex, Encoding.UTF8);
            selected.Add(new MdzSelectedFile(generatedPath, "[generated]", generatedLocalPath));
            archivePaths = selected.Select(file => file.ArchivePath).ToList();
            resolvedEntryPoint = generatedPath;
            if (manifest is not null)
                manifest.EntryPoint = generatedPath;
        }

        if (!string.IsNullOrWhiteSpace(manifest?.EntryPoint)
            && !archivePaths.Any(path => path.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"ERR_PACK_ENTRYPOINT_MISSING: Manifest entry-point \"{manifest.EntryPoint}\" does not exist in archive.");
        }

        if (options.MapFiles && manifest is not null)
            manifest.Files = manifestFiles;

        try
        {
            CreateAtomic(outputPath, archive =>
            {
                foreach (var item in selected)
                    WriteFileEntry(archive, item.ArchivePath, item.LocalPath);

                if (manifest is not null)
                    WriteManifestEntry(archive, SerializeManifest(manifest));
            });
        }
        finally
        {
            foreach (var generated in selected.Where(file => file.OriginalPath == "[generated]"))
            {
                if (File.Exists(generated.LocalPath))
                    File.Delete(generated.LocalPath);
            }
        }

        return new MdzPackBuildResult(
            manifest,
            resolvedEntryPoint,
            archivePaths,
            selected,
            new MdzPackWarnings(
                invalidPathCount,
                sanitizedPathCount,
                skipMap,
                warningMessages,
                resolvedEntryPoint is null));
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

    /// <summary>
    /// Removes multiple files from an existing .mdz archive in one atomic rewrite.
    /// </summary>
    public static ArchiveMutationResult RemoveFiles(
        string archivePath,
        IEnumerable<string> archiveEntryPaths) =>
        UpdateFiles(archivePath, writes: [], removals: archiveEntryPaths);

    /// <summary>
    /// Applies multiple archive entry writes and removals in one atomic rewrite.
    /// </summary>
    public static ArchiveMutationResult UpdateFiles(
        string archivePath,
        IEnumerable<ArchiveWriteSpec> writes,
        IEnumerable<string>? removals = null,
        Manifest? manifest = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        var normalizedWrites = new Dictionary<string, ArchiveWriteSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var write in writes)
        {
            var normalisedPath = NormaliseArchivePath(write.ArchivePath);
            var pathError = PathValidator.Validate(normalisedPath);
            if (pathError is not null)
                throw new InvalidOperationException($"Invalid path '{normalisedPath}': {pathError}");

            if (!File.Exists(write.LocalPath))
                throw new FileNotFoundException($"Source file '{write.LocalPath}' does not exist.", write.LocalPath);

            normalizedWrites[normalisedPath] = new ArchiveWriteSpec(normalisedPath, write.LocalPath);
        }

        var normalizedRemovals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var removal in removals ?? [])
        {
            var normalisedPath = NormaliseArchivePath(removal);
            var pathError = PathValidator.Validate(normalisedPath);
            if (pathError is not null)
                throw new InvalidOperationException($"Invalid path '{normalisedPath}': {pathError}");

            normalizedRemovals[normalisedPath] = normalisedPath;
            normalizedWrites.Remove(normalisedPath);
        }

        ArchiveMutationResult? result = null;
        CreateAtomic(archivePath, destinationArchive =>
        {
            using var sourceArchive = ZipFile.OpenRead(archivePath);
            var sourceEntries = sourceArchive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .ToList();
            var existingPaths = sourceEntries
                .Select(e => e.FullName.Replace('\\', '/'))
                .ToList();

            foreach (var removal in normalizedRemovals.Values)
            {
                if (!existingPaths.Any(path => path.Equals(removal, StringComparison.OrdinalIgnoreCase)))
                    throw new FileNotFoundException($"Entry '{removal}' was not found in archive.", removal);
            }

            var manifestJson = ResolveMutationManifestJson(
                sourceArchive,
                normalizedWrites,
                normalizedRemovals,
                manifest);

            var manifestForValidation = ResolveMutationManifestForValidation(
                sourceArchive,
                manifestJson,
                normalizedRemovals);
            var nextPaths = existingPaths
                .Where(path => !normalizedRemovals.ContainsKey(path)
                    && !normalizedWrites.ContainsKey(path)
                    && !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Concat(normalizedWrites.Values
                    .Select(write => write.ArchivePath)
                    .Where(path => !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            EnsureCreatableEntryPoint(nextPaths, manifestForValidation);

            foreach (var sourceEntry in sourceEntries)
            {
                var sourcePath = sourceEntry.FullName.Replace('\\', '/');
                if (normalizedRemovals.ContainsKey(sourcePath)
                    || normalizedWrites.ContainsKey(sourcePath)
                    || (manifestJson is not null && sourcePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                CopyEntry(sourceEntry, destinationArchive);
            }

            foreach (var write in normalizedWrites.Values)
            {
                if (write.ArchivePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                WriteFileEntry(destinationArchive, write.ArchivePath, write.LocalPath);
            }

            if (manifestJson is not null)
                WriteManifestEntry(destinationArchive, manifestJson);

            var archivePaths = nextPaths
                .Concat(manifestJson is null ? [] : [ManifestFileName])
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result = new ArchiveMutationResult(
                ReadManifestJson(manifestJson),
                ResolveEntryPoint(archivePaths, manifestForValidation),
                archivePaths);
        });

        return result!;
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
    /// Returns true when a file or directory entry exists in the archive.
    /// </summary>
    public static bool HasEntry(string archivePath, string archiveEntryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return FindEntryWithDirectoryFallback(archive, archiveEntryPath) is not null;
    }

    /// <summary>
    /// Finds a file or directory entry by archive path using case-insensitive lookup.
    /// </summary>
    public static ArchiveEntry? FindEntry(string archivePath, string archiveEntryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = FindEntryWithDirectoryFallback(archive, archiveEntryPath);
        return entry is null ? null : ToArchiveEntry(entry);
    }

    /// <summary>
    /// Returns detailed entry information for all files in the archive.
    /// </summary>
    public static IReadOnlyList<ArchiveEntry> ListDetailed(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(ToArchiveEntry)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Builds an inferred folder tree from all file paths in the archive.
    /// </summary>
    public static IReadOnlyList<PathTreeNode> BuildPathTree(string archivePath) =>
        BuildPathTree(List(archivePath));

    /// <summary>
    /// Builds an inferred folder tree from archive-relative paths.
    /// </summary>
    public static IReadOnlyList<PathTreeNode> BuildPathTree(IEnumerable<string> paths)
    {
        var roots = new List<PathTreeNode>();

        foreach (var archivePath in SortArchivePaths(paths))
        {
            var parts = archivePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var siblings = roots;
            var currentPath = string.Empty;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}/{part}";
                var isDirectory = i < parts.Length - 1;
                var node = EnsureTreeNode(siblings, part, currentPath, isDirectory);
                siblings = node.Children;
            }
        }

        SortTreeNodes(roots);
        return roots;
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
    /// Creates a canonical manifest from editable metadata.
    /// </summary>
    public static Manifest CreateManifest(ManifestEditableMetadata? metadata = null)
    {
        var manifest = new Manifest();
        ApplyManifestMetadata(manifest, metadata);
        EnsureCanonicalManifest(manifest);
        return manifest;
    }

    /// <summary>
    /// Updates a manifest while preserving spec-managed fields unless metadata explicitly changes them.
    /// </summary>
    public static Manifest UpdateManifest(
        Manifest? manifest,
        ManifestEditableMetadata? metadata = null,
        ManifestUpdateOptions? options = null)
    {
        var next = manifest is null ? new Manifest() : CloneManifest(manifest);
        EnsureCanonicalManifest(next, options);

        if (metadata is not null)
            ApplyManifestMetadata(next, metadata);

        EnsureCanonicalManifest(next, options);
        return next;
    }

    /// <summary>
    /// Updates manifest.json in an archive atomically and returns the manifest that was written.
    /// </summary>
    public static Manifest UpdateManifest(
        string archivePath,
        ManifestEditableMetadata? metadata,
        ManifestUpdateOptions? options = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        Manifest? updatedManifest = null;
        CreateAtomic(archivePath, destinationArchive =>
        {
            using var sourceArchive = ZipFile.OpenRead(archivePath);
            var sourceEntries = sourceArchive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .ToList();
            var currentManifest = ReadManifestFromArchive(
                sourceArchive,
                replacedOrRemovedPath: string.Empty,
                localManifestPath: null,
                requireValidReplacementManifest: false,
                requireValidExistingManifest: true);

            updatedManifest = UpdateManifest(currentManifest, metadata, options);
            var archivePaths = sourceEntries
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .Where(path => !path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            EnsureCreatableEntryPoint(archivePaths, updatedManifest);

            foreach (var sourceEntry in sourceEntries)
            {
                var sourcePath = sourceEntry.FullName.Replace('\\', '/');
                if (sourcePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CopyEntry(sourceEntry, destinationArchive);
            }

            WriteManifestEntry(destinationArchive, SerializeManifest(updatedManifest));
        });

        return updatedManifest!;
    }

    /// <summary>
    /// Splits a manifest into spec-managed fields and editable metadata.
    /// </summary>
    public static ManifestMetadataSplit SplitManifestMetadata(Manifest manifest) =>
        new(
            new ManifestReservedFields
            {
                Spec = manifest.Spec,
                Producer = manifest.Producer,
                Created = manifest.Created,
                Modified = manifest.Modified,
                EntryPoint = manifest.EntryPoint,
                Mode = manifest.Mode,
                Files = manifest.Files,
            },
            new ManifestEditableMetadata
            {
                Title = manifest.Title,
                Author = manifest.Author,
                Description = manifest.Description,
                Keywords = manifest.Keywords,
                Language = manifest.Language,
                License = manifest.License,
                Version = manifest.Version,
                Cover = manifest.Cover,
            });

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

    /// <summary>
    /// Resolves the archive interpretation mode from the manifest, defaulting to document mode.
    /// </summary>
    public static string ResolveMode(string archivePath)
    {
        var manifest = ReadManifest(archivePath);
        return manifest?.Mode ?? "document";
    }

    /// <summary>
    /// Reads UTF-8 text content from an archive entry.
    /// </summary>
    public static string ReadText(string archivePath, string archiveEntryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = GetFileEntryOrThrow(archive, archiveEntryPath);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads raw bytes from an archive entry.
    /// </summary>
    public static byte[] ReadBytes(string archivePath, string archiveEntryPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = GetFileEntryOrThrow(archive, archiveEntryPath);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Reads entry content as raw base64 with no data URI prefix.
    /// </summary>
    public static string ReadBase64(string archivePath, string archiveEntryPath) =>
        Convert.ToBase64String(ReadBytes(archivePath, archiveEntryPath));

    /// <summary>
    /// Reads entry content and returns a data URI string.
    /// </summary>
    public static string ReadDataUri(
        string archivePath,
        string archiveEntryPath,
        string? fallbackMime = null)
    {
        var normalisedPath = NormaliseArchivePath(archiveEntryPath);
        var extension = Path.GetExtension(normalisedPath).TrimStart('.').ToLowerInvariant();
        var mime = ImageMimeTypes.TryGetValue(extension, out var imageMime)
            ? imageMime
            : string.IsNullOrWhiteSpace(fallbackMime)
                ? "application/octet-stream"
                : fallbackMime.Trim();
        return $"data:{mime};base64,{ReadBase64(archivePath, normalisedPath)}";
    }

    // -------------------------------------------------------------------------
    // Assets
    // -------------------------------------------------------------------------

    /// <summary>
    /// Infers a MIME type from an archive path.
    /// </summary>
    public static string InferMimeType(string path, string fallbackMime = "application/octet-stream")
    {
        var extension = Path.GetExtension(NormaliseArchivePath(path)).TrimStart('.').ToLowerInvariant();
        return KnownMimeTypes.TryGetValue(extension, out var mimeType) ? mimeType : fallbackMime;
    }

    /// <summary>
    /// Classifies an archive asset as image, audio, video, font, data, or other.
    /// </summary>
    public static string ClassifyAssetKind(string path, string? mimeType = null)
    {
        mimeType ??= InferMimeType(path);
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        if (mimeType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)) return "font";
        return IsDataMimeType(mimeType) ? "data" : "other";
    }

    /// <summary>
    /// Returns true when common browser surfaces can preview this asset.
    /// </summary>
    public static bool IsPreviewableAsset(string path, string? mimeType = null)
    {
        mimeType ??= InferMimeType(path);
        return mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || IsDataMimeType(mimeType);
    }

    /// <summary>
    /// Finds image-like archive assets not referenced by scanned Markdown or manifest cover metadata.
    /// </summary>
    public static MdzOrphanedAssetsResult FindOrphanedAssets(
        string archivePath,
        MdzOrphanedAssetsOptions? options = null)
    {
        options ??= new MdzOrphanedAssetsOptions();
        var allEntries = ListDetailed(archivePath);
        var assetPaths = allEntries
            .Where(entry => !entry.IsDirectory && entry.IsImage)
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var assetPathSet = assetPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scannedMarkdownPaths = options.ScanMode == "all-markdown"
            ? allEntries.Where(entry => entry.IsMarkdown).Select(entry => entry.Path).ToList()
            : [NormaliseArchivePath(options.EntryPoint ?? ResolveEntryPoint(archivePath) ?? string.Empty)];
        scannedMarkdownPaths = scannedMarkdownPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

        var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvedReferences = new List<MdzOrphanedAssetReferenceIssue>();

        foreach (var markdownPath in scannedMarkdownPaths)
        {
            var markdown = ReadText(archivePath, markdownPath);
            foreach (var reference in ExtractMarkdownImageReferences(markdown))
            {
                if (HasUriScheme(reference))
                {
                    unresolvedReferences.Add(new MdzOrphanedAssetReferenceIssue(markdownPath, reference, "unsupported-scheme"));
                    continue;
                }

                string resolved;
                try
                {
                    resolved = ResolveArchiveRelativePath(markdownPath, reference);
                }
                catch (InvalidOperationException)
                {
                    unresolvedReferences.Add(new MdzOrphanedAssetReferenceIssue(markdownPath, reference, "invalid-path"));
                    continue;
                }

                var entry = FindEntry(archivePath, resolved);
                if (entry is null || entry.IsDirectory)
                {
                    unresolvedReferences.Add(new MdzOrphanedAssetReferenceIssue(markdownPath, reference, "not-found"));
                    continue;
                }

                if (!IsImagePath(resolved) || !assetPathSet.Contains(resolved))
                {
                    unresolvedReferences.Add(new MdzOrphanedAssetReferenceIssue(markdownPath, reference, "not-asset"));
                    continue;
                }

                referencedAssets.Add(ResolvePathCase(assetPaths, resolved));
            }
        }

        var manifest = ReadManifest(archivePath);
        if (!string.IsNullOrWhiteSpace(manifest?.Cover))
        {
            var cover = NormaliseArchivePath(manifest.Cover);
            if (assetPathSet.Contains(cover))
                referencedAssets.Add(ResolvePathCase(assetPaths, cover));
        }

        var referencedAssetPaths = referencedAssets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var orphanedAssetPaths = assetPaths
            .Where(path => !referencedAssets.Contains(path))
            .ToList();

        return new MdzOrphanedAssetsResult(
            scannedMarkdownPaths,
            assetPaths,
            referencedAssetPaths,
            orphanedAssetPaths,
            unresolvedReferences);
    }

    // -------------------------------------------------------------------------
    // Workspace
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens an archive as a normalized workspace model.
    /// </summary>
    public static MdzWorkspace OpenWorkspace(string archivePath, MdzOpenWorkspaceOptions? options = null)
    {
        options ??= new MdzOpenWorkspaceOptions();
        var validation = Validate(archivePath);
        var manifest = ReadManifest(archivePath);
        var mode = ResolveMode(archivePath);
        var entryPoint = ResolveEntryPoint(archivePath);
        var entries = ListDetailed(archivePath);

        var documents = entries
            .Where(entry => entry.IsMarkdown)
            .Select(entry => new MdzWorkspaceDocument(
                entry.Path,
                GetDocumentTitle(entry.Path, manifest),
                ReadText(archivePath, entry.Path),
                entryPoint is not null && entry.Path.Equals(entryPoint, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var assets = entries
            .Where(entry => !entry.IsMarkdown
                && !entry.IsDirectory
                && !entry.Path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                var mimeType = InferMimeType(entry.Path);
                return new MdzWorkspaceAsset(
                    entry.Path,
                    Path.GetFileName(entry.Path),
                    entry.Size,
                    mimeType,
                    ClassifyAssetKind(entry.Path, mimeType),
                    IsPreviewableAsset(entry.Path, mimeType),
                    ReadBytes(archivePath, entry.Path));
            })
            .ToList();

        var orphanedAssets = options.IncludeOrphanedAssetAnalysis
            ? FindOrphanedAssets(archivePath, new MdzOrphanedAssetsOptions { ScanMode = options.OrphanedAssetScanMode })
            : null;

        return new MdzWorkspace(
            manifest?.Title,
            mode,
            manifest,
            entryPoint,
            documents,
            assets,
            validation,
            orphanedAssets);
    }

    /// <summary>
    /// Creates a workspace asset from a local file.
    /// </summary>
    public static MdzWorkspaceAsset CreateWorkspaceAssetFromFile(string localFilePath, string? targetPath = null)
    {
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException($"Source file '{localFilePath}' does not exist.", localFilePath);

        var path = NormaliseArchivePath(targetPath ?? Path.GetFileName(localFilePath));
        var pathError = PathValidator.Validate(path);
        if (pathError is not null)
            throw new InvalidOperationException($"ERR_PATH_INVALID: {pathError}");

        var bytes = File.ReadAllBytes(localFilePath);
        var mimeType = InferMimeType(path);
        return new MdzWorkspaceAsset(
            path,
            Path.GetFileName(path),
            bytes.LongLength,
            mimeType,
            ClassifyAssetKind(path, mimeType),
            IsPreviewableAsset(path, mimeType),
            bytes);
    }

    /// <summary>
    /// Exports a workspace asset to a local file path.
    /// </summary>
    public static void ExportWorkspaceAsset(MdzWorkspaceAsset asset, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        File.WriteAllBytes(outputPath, asset.Bytes);
    }

    /// <summary>
    /// Builds an archive from a normalized workspace model.
    /// </summary>
    public static MdzPackBuildResult BuildWorkspace(
        string outputPath,
        MdzWorkspace workspace,
        MdzBuildWorkspaceOptions? options = null)
    {
        options ??= new MdzBuildWorkspaceOptions();
        var entryPoint = options.EntryPoint
            ?? workspace.EntryPoint
            ?? workspace.Documents.FirstOrDefault(document => document.IsEntryPoint)?.Path
            ?? workspace.Documents.FirstOrDefault()?.Path;
        var mode = options.Mode ?? workspace.Mode;
        var title = options.Title ?? workspace.Title ?? workspace.Manifest?.Title;
        var metadata = options.Metadata ?? new ManifestEditableMetadata();
        metadata.Title ??= title;
        metadata.Mode ??= mode;
        metadata.EntryPoint ??= entryPoint;
        var manifest = UpdateManifest(workspace.Manifest, metadata);

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"mdzip-core-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var files = new List<MdzPackInputFile>();
            foreach (var document in workspace.Documents)
            {
                var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.md");
                File.WriteAllText(tempPath, document.Text, Encoding.UTF8);
                files.Add(new MdzPackInputFile(document.Path, tempPath));
            }

            foreach (var asset in workspace.Assets)
            {
                var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.bin");
                File.WriteAllBytes(tempPath, asset.Bytes);
                files.Add(new MdzPackInputFile(asset.Path, tempPath));
            }

            var manifestPath = Path.Combine(tempDirectory, "manifest.json");
            File.WriteAllText(manifestPath, SerializeManifest(manifest), Encoding.UTF8);
            files.Add(new MdzPackInputFile(ManifestFileName, manifestPath));

            return BuildArchive(
                outputPath,
                files,
                options.RootName ?? title ?? "MDZip Workspace",
                new MdzPackOptions
                {
                    CreateIndex = false,
                    MapFiles = false,
                    Filters = ["**/*", "*"],
                });
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Validate
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts validation details into a compact status string: valid, warning, or error.
    /// </summary>
    public static string GetValidationStatus(ValidationResult result)
    {
        if (!result.IsValid || result.Errors.Count > 0)
            return "error";

        return result.Warnings.Count > 0 ? "warning" : "valid";
    }

    /// <summary>
    /// Validates raw manifest.json text without requiring an archive.
    /// </summary>
    public static ValidationResult ValidateManifest(string manifestJson)
    {
        try
        {
            using var manifestDocument = JsonDocument.Parse(manifestJson, StrictJsonDocumentOptions);
            var root = manifestDocument.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
                return ValidateManifest(manifest: null, root);

            var manifest = JsonSerializer.Deserialize<Manifest>(manifestJson, JsonOptions);
            return ValidateManifest(manifest, root);
        }
        catch (JsonException ex)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = [$"ERR_MANIFEST_INVALID: manifest.json could not be parsed: {ex.Message}"],
                Warnings = [],
            };
        }
    }

    /// <summary>
    /// Validates a manifest object without requiring an archive.
    /// </summary>
    public static ValidationResult ValidateManifest(Manifest? manifest) =>
        ValidateManifest(manifest, manifestRoot: null);

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
                    errors.AddRange(ValidateManifest(manifest, manifestRoot).Errors);
                }
                else
                {
                    var manifestValidation = ValidateManifest(manifest, manifestRoot);
                    errors.AddRange(manifestValidation.Errors);
                    warnings.AddRange(manifestValidation.Warnings);

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

    private static string NormaliseArchivePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static IReadOnlyList<string> SortArchivePaths(IEnumerable<string> paths) =>
        paths
            .Select(NormaliseArchivePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ArchiveEntry ToArchiveEntry(ZipArchiveEntry entry)
    {
        var isDirectory = IsDirectoryEntry(entry);
        return new ArchiveEntry(
            entry.FullName.Replace('\\', '/'),
            isDirectory ? 0 : entry.Length,
            isDirectory ? 0 : entry.CompressedLength,
            entry.LastWriteTime.UtcDateTime,
            isDirectory,
            !isDirectory && IsMarkdownPath(entry.FullName),
            !isDirectory && IsImagePath(entry.FullName));
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalised = NormaliseArchivePath(path);
        return archive.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(normalised, StringComparison.OrdinalIgnoreCase));
    }

    private static ZipArchiveEntry? FindEntryWithDirectoryFallback(ZipArchive archive, string path)
    {
        var normalised = NormaliseArchivePath(path);
        var direct = FindEntry(archive, normalised);
        if (direct is not null)
            return direct;

        return normalised.EndsWith("/", StringComparison.Ordinal)
            ? FindEntry(archive, normalised.TrimEnd('/'))
            : FindEntry(archive, $"{normalised}/");
    }

    private static ZipArchiveEntry GetFileEntryOrThrow(ZipArchive archive, string path)
    {
        var normalised = NormaliseArchivePath(path);
        var entry = FindEntryWithDirectoryFallback(archive, normalised);
        if (entry is null)
            throw new FileNotFoundException($"Entry '{normalised}' was not found in archive.", normalised);

        if (IsDirectoryEntry(entry))
            throw new InvalidOperationException($"Entry '{normalised}' is a directory.");

        return entry;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith("/", StringComparison.Ordinal);

    private static PathTreeNode EnsureTreeNode(
        List<PathTreeNode> siblings,
        string name,
        string path,
        bool isDirectory)
    {
        var existing = siblings.FirstOrDefault(node =>
            node.IsDirectory == isDirectory
            && node.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var node = new PathTreeNode(name, path, isDirectory, []);
        siblings.Add(node);
        return node;
    }

    private static void SortTreeNodes(List<PathTreeNode> nodes)
    {
        nodes.Sort((left, right) =>
        {
            if (left.IsDirectory != right.IsDirectory)
                return left.IsDirectory ? -1 : 1;

            return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });

        foreach (var node in nodes)
            SortTreeNodes(node.Children);
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

    private static bool IsDataMimeType(string mimeType) =>
        mimeType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("text/csv", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("text/css", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mimeType.Equals("text/xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsMarkdownPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static bool IsImagePath(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "ico"
            or "webm" or "mp4" or "mp3" or "wav" or "ogg";
    }

    private static bool IsSupportedMode(string mode) =>
        mode is "document" or "project";

    private static IReadOnlyList<string> ExtractMarkdownImageReferences(string markdown)
    {
        var references = new List<string>();
        foreach (Match match in Regex.Matches(
            markdown,
            @"!\[[^\]]*\]\(\s*(?<target><[^>]+>|[^)\s]+)(?:\s+""[^""]*"")?\s*\)",
            RegexOptions.CultureInvariant))
        {
            var target = match.Groups["target"].Value.Trim();
            if (target.StartsWith("<", StringComparison.Ordinal) && target.EndsWith(">", StringComparison.Ordinal))
                target = target[1..^1].Trim();
            references.Add(target);
        }

        return references;
    }

    private static bool HasUriScheme(string reference) =>
        Regex.IsMatch(reference, @"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant);

    private static string ResolveArchiveRelativePath(string basePath, string relative)
    {
        var target = relative.Trim();
        var queryIndex = target.IndexOf('?');
        if (queryIndex >= 0)
            target = target[..queryIndex];
        var hashIndex = target.IndexOf('#');
        if (hashIndex >= 0)
            target = target[..hashIndex];

        target = Uri.UnescapeDataString(target).Replace('\\', '/');
        if (target.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("Path must be relative.");

        var baseDirectory = string.Empty;
        var slash = NormaliseArchivePath(basePath).LastIndexOf('/');
        if (slash >= 0)
            baseDirectory = NormaliseArchivePath(basePath)[..(slash + 1)];

        var parts = (baseDirectory + target).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var output = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (output.Count == 0)
                    throw new InvalidOperationException("Path escapes archive root.");
                output.RemoveAt(output.Count - 1);
                continue;
            }

            output.Add(part);
        }

        return string.Join('/', output);
    }

    private static string ResolvePathCase(IEnumerable<string> paths, string candidate) =>
        paths.First(path => path.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static string GetDocumentTitle(string path, Manifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.EntryPoint)
            && path.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(manifest.Title))
        {
            return manifest.Title;
        }

        return Path.GetFileNameWithoutExtension(path)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
    }

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

    private static string? ResolveMutationManifestJson(
        ZipArchive sourceArchive,
        IReadOnlyDictionary<string, ArchiveWriteSpec> normalizedWrites,
        IReadOnlyDictionary<string, string> normalizedRemovals,
        Manifest? manifest)
    {
        if (normalizedWrites.TryGetValue(ManifestFileName, out var manifestWrite))
        {
            var rawJson = File.ReadAllText(manifestWrite.LocalPath, Encoding.UTF8);
            return PrepareReplacementManifestJson(rawJson);
        }

        if (manifest is not null)
            return SerializeManifest(UpdateManifest(manifest));

        if ((normalizedWrites.Count > 0 || normalizedRemovals.Count > 0)
            && !normalizedRemovals.ContainsKey(ManifestFileName))
        {
            return TryRefreshManifestModifiedJson(sourceArchive);
        }

        return null;
    }

    private static Manifest? ResolveMutationManifestForValidation(
        ZipArchive sourceArchive,
        string? manifestJson,
        IReadOnlyDictionary<string, string> normalizedRemovals)
    {
        if (manifestJson is not null)
            return JsonSerializer.Deserialize<Manifest>(manifestJson, JsonOptions);

        if (normalizedRemovals.ContainsKey(ManifestFileName))
            return null;

        return ReadManifestFromArchive(
            sourceArchive,
            replacedOrRemovedPath: string.Empty,
            localManifestPath: null,
            requireValidReplacementManifest: false,
            requireValidExistingManifest: true);
    }

    private static Manifest? ReadManifestJson(string? manifestJson)
    {
        if (manifestJson is null)
            return null;

        return JsonSerializer.Deserialize<Manifest>(manifestJson, JsonOptions);
    }

    private static Manifest CloneManifest(Manifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, WriteJsonOptions);
        return JsonSerializer.Deserialize<Manifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Manifest clone failed.");
    }

    private static string SerializeManifest(Manifest manifest) =>
        JsonSerializer.Serialize(manifest, WriteJsonOptions);

    private static void EnsureCanonicalManifest(Manifest manifest, ManifestUpdateOptions? options = null)
    {
        options ??= new ManifestUpdateOptions();

        manifest.Spec ??= new ManifestSpec();
        manifest.Spec.Name ??= SpecName;
        manifest.Spec.Version ??= ProducedSpecVersion;

        if (options.SetCreatedIfMissing && string.IsNullOrWhiteSpace(manifest.Created))
            manifest.Created = DateTime.UtcNow.ToString("o");

        if (options.RefreshModified)
            manifest.Modified = DateTime.UtcNow.ToString("o");
    }

    private static void ApplyManifestMetadata(Manifest manifest, ManifestEditableMetadata? metadata)
    {
        if (metadata is null)
            return;

        if (metadata.Title is not null) manifest.Title = metadata.Title;
        if (metadata.Author is not null)
        {
            manifest.Author = metadata.Author;
            manifest.Authors = [metadata.Author];
        }
        if (metadata.Description is not null) manifest.Description = metadata.Description;
        if (metadata.Keywords is not null) manifest.Keywords = metadata.Keywords;
        if (metadata.Language is not null) manifest.Language = metadata.Language;
        if (metadata.License is not null) manifest.License = metadata.License;
        if (metadata.Version is not null) manifest.Version = metadata.Version;
        if (metadata.Cover is not null) manifest.Cover = metadata.Cover;
        if (metadata.Mode is not null) manifest.Mode = metadata.Mode;
        if (metadata.EntryPoint is not null) manifest.EntryPoint = metadata.EntryPoint;
    }

    private static Manifest? BuildManifestFromOptions(string rootName, MdzPackOptions options)
    {
        var hasManifestOption = options.MapFiles
            || !string.IsNullOrWhiteSpace(options.Title)
            || !string.IsNullOrWhiteSpace(options.Mode)
            || !string.IsNullOrWhiteSpace(options.EntryPoint)
            || !string.IsNullOrWhiteSpace(options.Language)
            || !string.IsNullOrWhiteSpace(options.Author)
            || !string.IsNullOrWhiteSpace(options.Description)
            || !string.IsNullOrWhiteSpace(options.DocVersion);

        if (!hasManifestOption)
            return null;

        return CreateManifest(new ManifestEditableMetadata
        {
            Title = string.IsNullOrWhiteSpace(options.Title) ? rootName : options.Title,
            Mode = options.Mode,
            EntryPoint = options.EntryPoint,
            Language = string.IsNullOrWhiteSpace(options.Language) ? "en" : options.Language,
            Author = string.IsNullOrWhiteSpace(options.Author) ? null : new ManifestAuthor { Name = options.Author },
            Description = options.Description,
            Version = options.DocVersion,
        });
    }

    private static Manifest? ReadProvidedManifest(IEnumerable<MdzSelectedFile> selected)
    {
        var manifestFile = selected.FirstOrDefault(file =>
            file.ArchivePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (manifestFile is null)
            return null;

        var rawJson = File.ReadAllText(manifestFile.LocalPath, Encoding.UTF8);
        var validation = ValidateManifest(rawJson);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));

        return JsonSerializer.Deserialize<Manifest>(rawJson, JsonOptions);
    }

    private static string BuildGeneratedIndex(IEnumerable<string> markdownPaths, string? title)
    {
        var pageTitle = string.IsNullOrWhiteSpace(title) ? "Index" : title.Trim();
        var lines = new List<string> { $"# {pageTitle}", string.Empty };
        var sorted = markdownPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

        if (sorted.Count == 0)
        {
            lines.Add("No Markdown files were found.");
            return string.Join('\n', lines);
        }

        foreach (var path in sorted)
        {
            var fileName = Path.GetFileName(path);
            var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            lines.Add($"- [{fileName}](<{encoded}>)");
        }

        lines.AddRange([string.Empty, "---", string.Empty, "Generated by `mdz-core`", string.Empty, "More info: [markdownzip.org](https://markdownzip.org)"]);
        return string.Join('\n', lines);
    }

    private static bool MatchesAnyFilter(string path, IReadOnlyList<string> filters)
    {
        var effectiveFilters = filters.Count == 0 ? ["**/*", "*"] : filters;
        return effectiveFilters.Any(filter => GlobMatch(path, filter));
    }

    private static bool GlobMatch(string path, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]")
            + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string SanitiseArchivePath(string path)
    {
        var parts = NormaliseArchivePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitisePathSegment)
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var sanitised = string.Join('/', parts);
        return string.IsNullOrWhiteSpace(sanitised) ? "_" : sanitised;
    }

    private static string SanitisePathSegment(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (c is '\0' || (c >= '\u0001' && c <= '\u001F') || c == '\u007F'
                || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(c);
            }
        }

        var output = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(output))
            return "_";
        return output is "." or ".." ? output.Replace('.', '_') : output;
    }

    private static string MakeUniqueArchivePath(string candidate, ISet<string> usedPaths)
    {
        if (usedPaths.Add(candidate))
            return candidate;

        var slash = candidate.LastIndexOf('/');
        var dir = slash >= 0 ? candidate[..(slash + 1)] : string.Empty;
        var name = slash >= 0 ? candidate[(slash + 1)..] : candidate;
        var dot = name.LastIndexOf('.');
        var baseName = dot >= 0 ? name[..dot] : name;
        var extension = dot >= 0 ? name[dot..] : string.Empty;

        var index = 2;
        while (true)
        {
            var next = $"{dir}{baseName}-{index}{extension}";
            if (usedPaths.Add(next))
                return next;

            index++;
        }
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

    private static ValidationResult ValidateManifest(Manifest? manifest, JsonElement? manifestRoot)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (manifestRoot is { ValueKind: not JsonValueKind.Object })
        {
            errors.Add("ERR_MANIFEST_INVALID: manifest.json must be a JSON object.");
            return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
        }

        if (manifest is null)
        {
            errors.Add("ERR_MANIFEST_INVALID: manifest.json deserialised to null.");
            return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
        }

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
            if (!TryParseSemVerMajor(manifest.Spec.Version, out var major))
                errors.Add($"ERR_MANIFEST_INVALID: 'spec.version' field '{manifest.Spec.Version}' is not a valid semver string.");
            else if (major > SupportedMajorVersion)
                errors.Add($"ERR_VERSION_UNSUPPORTED: manifest 'spec.version' major version {major} is not supported (supported: {SupportedMajorVersion}).");
            else if (major < SupportedMajorVersion)
                warnings.Add($"manifest 'spec.version' major version {major} is older than supported major {SupportedMajorVersion}.");
        }

        if (manifest.Mode is not null && !IsSupportedMode(manifest.Mode))
            errors.Add($"ERR_MODE_UNSUPPORTED: manifest 'mode' value '{manifest.Mode}' is not supported.");

        if (manifest.Title is not null && string.IsNullOrWhiteSpace(manifest.Title))
            errors.Add("ERR_MANIFEST_INVALID: manifest field 'title' must not be empty when present.");

        if (manifest.EntryPoint is not null)
        {
            var pathError = PathValidator.Validate(manifest.EntryPoint);
            if (pathError is not null)
                errors.Add($"ERR_MANIFEST_INVALID: manifest 'entryPoint' must be a valid archive-relative path: {pathError}");
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
        };
    }
}

/// <summary>
/// Detailed information about a single archive entry.
/// </summary>
public record ArchiveEntry(
    string Path,
    long Size,
    long CompressedSize,
    DateTime LastModified,
    bool IsDirectory = false,
    bool IsMarkdown = false,
    bool IsImage = false);

/// <summary>
/// Inferred archive path tree node for navigation and inspection.
/// </summary>
public sealed record PathTreeNode(
    string Name,
    string Path,
    bool IsDirectory,
    List<PathTreeNode> Children);

/// <summary>
/// One local file write to apply to an archive path.
/// </summary>
public sealed record ArchiveWriteSpec(string ArchivePath, string LocalPath);

/// <summary>
/// Summary returned after an archive mutation.
/// </summary>
public sealed record ArchiveMutationResult(
    Manifest? Manifest,
    string? ResolvedEntryPoint,
    IReadOnlyList<string> ArchivePaths);

/// <summary>
/// Local file candidate for rich archive packaging.
/// </summary>
public sealed record MdzPackInputFile(string ArchivePath, string LocalPath);

/// <summary>
/// File selected for archive output after filtering, mapping, and de-duplication.
/// </summary>
public sealed record MdzSelectedFile(
    string ArchivePath,
    string OriginalPath,
    string LocalPath);

/// <summary>
/// Packaging controls for rich archive builds.
/// </summary>
public sealed class MdzPackOptions
{
    public bool CreateIndex { get; set; }
    public bool MapFiles { get; set; }
    public List<string> Filters { get; set; } = ["**/*", "*"];
    public string? Title { get; set; }
    public string? Mode { get; set; }
    public string? EntryPoint { get; set; }
    public string? Language { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? DocVersion { get; set; }
}

/// <summary>
/// Packaging warnings and counters.
/// </summary>
public sealed record MdzPackWarnings(
    int InvalidPathCount,
    int SanitizedPathCount,
    IReadOnlyDictionary<string, int> SkippedByReason,
    IReadOnlyList<string> Messages,
    bool UnresolvedEntry);

/// <summary>
/// Result returned after building a rich archive.
/// </summary>
public sealed record MdzPackBuildResult(
    Manifest? Manifest,
    string? ResolvedEntryPoint,
    IReadOnlyList<string> ArchivePaths,
    IReadOnlyList<MdzSelectedFile> Selected,
    MdzPackWarnings Warnings);

/// <summary>
/// Controls orphaned asset analysis.
/// </summary>
public sealed class MdzOrphanedAssetsOptions
{
    public string ScanMode { get; set; } = "entrypoint";
    public string? EntryPoint { get; set; }
}

/// <summary>
/// One image reference that could not be counted as a valid asset reference.
/// </summary>
public sealed record MdzOrphanedAssetReferenceIssue(
    string SourcePath,
    string Reference,
    string Reason);

/// <summary>
/// Result returned by orphaned asset analysis.
/// </summary>
public sealed record MdzOrphanedAssetsResult(
    IReadOnlyList<string> ScannedMarkdownPaths,
    IReadOnlyList<string> AssetPaths,
    IReadOnlyList<string> ReferencedAssetPaths,
    IReadOnlyList<string> OrphanedAssetPaths,
    IReadOnlyList<MdzOrphanedAssetReferenceIssue> UnresolvedReferences);

/// <summary>
/// Options for opening an archive as a workspace.
/// </summary>
public sealed class MdzOpenWorkspaceOptions
{
    public bool IncludeOrphanedAssetAnalysis { get; set; }
    public string OrphanedAssetScanMode { get; set; } = "entrypoint";
}

/// <summary>
/// One Markdown document in a workspace.
/// </summary>
public sealed record MdzWorkspaceDocument(
    string Path,
    string Title,
    string Text,
    bool IsEntryPoint);

/// <summary>
/// One non-Markdown asset in a workspace.
/// </summary>
public sealed record MdzWorkspaceAsset(
    string Path,
    string FileName,
    long ByteSize,
    string MimeType,
    string Kind,
    bool IsPreviewable,
    byte[] Bytes);

/// <summary>
/// Normalized archive workspace for app/editor hosts.
/// </summary>
public sealed record MdzWorkspace(
    string? Title,
    string Mode,
    Manifest? Manifest,
    string? EntryPoint,
    IReadOnlyList<MdzWorkspaceDocument> Documents,
    IReadOnlyList<MdzWorkspaceAsset> Assets,
    ValidationResult Validation,
    MdzOrphanedAssetsResult? OrphanedAssets);

/// <summary>
/// Options for building an archive from a workspace.
/// </summary>
public sealed class MdzBuildWorkspaceOptions
{
    public string? RootName { get; set; }
    public ManifestEditableMetadata? Metadata { get; set; }
    public string? Title { get; set; }
    public string? Mode { get; set; }
    public string? EntryPoint { get; set; }
}
