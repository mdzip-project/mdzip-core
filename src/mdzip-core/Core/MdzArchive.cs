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

    // -------------------------------------------------------------------------
    // Create
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a .mdz archive from a source directory. A conforming producer always writes
    /// manifest.json (§14.1): <paramref name="manifest"/>, else the directory's own manifest.json,
    /// else a minimal one titled after the directory.
    /// </summary>
    public static void Create(
        string outputPath,
        string sourceDirectory,
        Manifest? manifest = null)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new ArgumentException($"Source directory '{sourceDirectory}' does not exist.", nameof(sourceDirectory));

        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Select(filePath => (
                ArchivePath: Path.GetRelativePath(sourceDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/'),
                LocalPath: filePath))
            .ToList();

        var title = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)));
        CreateArchive(outputPath, files, manifest, title);
    }

    /// <summary>
    /// Creates a .mdz archive from an explicit list of (archivePath, localPath) pairs. A conforming
    /// producer always writes manifest.json (§14.1): <paramref name="manifest"/>, else a
    /// manifest.json in <paramref name="files"/>, else a minimal one.
    /// </summary>
    public static void CreateFromFiles(
        string outputPath,
        IEnumerable<(string ArchivePath, string LocalPath)> files,
        Manifest? manifest = null) =>
        CreateArchive(outputPath, files, manifest, title: null);

    private static void CreateArchive(
        string outputPath,
        IEnumerable<(string ArchivePath, string LocalPath)> files,
        Manifest? manifest,
        string? title)
    {
        // Producers write NFC paths (§5.4).
        var fileList = files
            .Select(f => (ArchivePath: MdzSpec.Nfc(f.ArchivePath.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/')), f.LocalPath))
            .ToList();

        JsonObject manifestNode;
        var provided = fileList.FindIndex(f => f.ArchivePath == ManifestFileName);
        if (manifest is not null)
        {
            manifestNode = ManifestToNode(manifest);
        }
        else if (provided >= 0)
        {
            manifestNode = MdzSpec.ParseManifest(File.ReadAllText(fileList[provided].LocalPath, Encoding.UTF8)).Manifest;
        }
        else
        {
            manifestNode = ManifestToNode(CreateManifest(new ManifestEditableMetadata { Title = string.IsNullOrWhiteSpace(title) ? null : title }));
        }
        fileList.RemoveAll(f => f.ArchivePath == ManifestFileName);

        foreach (var (archivePath, _) in fileList)
        {
            var error = PathValidator.Validate(archivePath);
            if (error is not null)
                throw new InvalidOperationException($"Invalid path '{archivePath}': {error}");
        }

        var duplicate = MdzSpec.EntrySetProblem(fileList.Select(f => f.ArchivePath));
        if (duplicate is not null)
            throw duplicate.ToException();

        CompleteProducerManifest(manifestNode, fileList.Select(f => f.ArchivePath).ToList());

        CreateAtomic(outputPath, archive =>
        {
            foreach (var (archivePath, localPath) in fileList)
                WriteFileEntry(archive, archivePath, localPath);

            WriteManifestEntry(archive, SerializeManifestNode(manifestNode));
        });

        if (manifest is not null)
            CopyManifestValues(manifestNode, manifest);
    }

    /// <summary>
    /// Builds a .mdz archive from local files with packaging options and warnings. The archive always
    /// gets manifest.json (§14.1): generated from the options, the caller's own manifest.json, or a
    /// minimal one; <c>entryPoint</c> and <c>mode</c> are filled in when missing.
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

        var generated = BuildManifestFromOptions(rootName, options);
        var skipMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Case-folded so two names that differ only in case (§5.4 SHOULD NOT) get distinct paths.
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<MdzSelectedFile>();
        var manifestFiles = new List<ManifestFile>();
        var invalidPathCount = 0;
        var sanitizedPathCount = 0;
        MdzSelectedFile? providedManifest = null;

        void AddSkip(string reason) =>
            skipMap[reason] = skipMap.TryGetValue(reason, out var count) ? count + 1 : 1;

        foreach (var item in cleanInput)
        {
            var originalPath = item.ArchivePath;
            // Producers write NFC paths (§5.4).
            var archivePath = MdzSpec.Nfc(originalPath);

            if (!MatchesAnyFilter(originalPath, options.Filters))
            {
                AddSkip("excluded by filter");
                continue;
            }

            if (archivePath == ManifestFileName)
            {
                if (generated is not null)
                    AddSkip("manifest.json replaced by generated manifest");
                else
                    providedManifest = new MdzSelectedFile(archivePath, originalPath, item.LocalPath);
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

            if (options.MapFiles && generated is not null && IsMarkdownPath(archivePath))
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

        JsonObject manifestNode;
        if (generated is not null)
        {
            manifestNode = ManifestToNode(generated);
        }
        else if (providedManifest is not null)
        {
            manifestNode = ReadProvidedManifest(providedManifest.LocalPath);
            selected.Add(providedManifest);
        }
        else
        {
            manifestNode = ManifestToNode(CreateManifest(new ManifestEditableMetadata
            {
                Title = string.IsNullOrWhiteSpace(options.Title) ? rootName : options.Title,
            }));
        }

        var archivePaths = selected.Where(f => f.ArchivePath != ManifestFileName).Select(file => file.ArchivePath).ToList();
        var resolvedEntryPoint = MdzSpec.TryResolveEntryPoint(archivePaths, manifestNode);
        var warningMessages = new List<string>();
        var markdownPaths = archivePaths.Where(IsMarkdownPath).ToList();

        if (!manifestNode.ContainsKey("mode") && markdownPaths.Count > 1)
        {
            warningMessages.Add(
                "Archive contains multiple Markdown files and no explicit manifest.mode; consumers will default to document mode. If these files are intended as separate documents, set mode: \"project\".");
        }

        var declaredEntryPoint = manifestNode["entryPoint"] is JsonValue declared && declared.TryGetValue<string>(out var declaredText)
            ? declaredText
            : null;

        if (options.CreateIndex && resolvedEntryPoint is null)
        {
            if (!string.IsNullOrWhiteSpace(declaredEntryPoint))
                throw new InvalidOperationException($"ERR_PACK_ENTRYPOINT_MISSING: Manifest entry-point \"{declaredEntryPoint}\" does not exist.");

            var generatedIndex = BuildGeneratedIndex(markdownPaths, options.Title ?? rootName);
            var generatedPath = MakeUniqueArchivePath("index.md", usedPaths);
            var generatedLocalPath = Path.Combine(Path.GetTempPath(), $"mdzip-core-index-{Guid.NewGuid():N}.md");
            File.WriteAllText(generatedLocalPath, generatedIndex, Encoding.UTF8);
            selected.Add(new MdzSelectedFile(generatedPath, "[generated]", generatedLocalPath));
            archivePaths.Add(generatedPath);
            resolvedEntryPoint = generatedPath;
            manifestNode["entryPoint"] = generatedPath;
        }
        else if (!string.IsNullOrWhiteSpace(declaredEntryPoint)
            && MdzSpec.FindPath(archivePaths, declaredEntryPoint.StartsWith("./", StringComparison.Ordinal) ? declaredEntryPoint[2..] : declaredEntryPoint) is null)
        {
            throw new InvalidOperationException($"ERR_PACK_ENTRYPOINT_MISSING: Manifest entry-point \"{declaredEntryPoint}\" does not exist in archive.");
        }

        // entryPoint is required in a producer's manifest (§6.2, §14.1): record the one that resolved.
        if (!manifestNode.ContainsKey("entryPoint") && resolvedEntryPoint is not null)
            manifestNode["entryPoint"] = resolvedEntryPoint;
        if (!manifestNode.ContainsKey("mode"))
            manifestNode["mode"] = "document";
        if (manifestNode["spec"] is not JsonObject spec)
            manifestNode["spec"] = spec = new JsonObject();
        spec["name"] ??= MdzSpec.SpecName;
        spec["version"] ??= MdzSpec.SpecVersion;

        if (options.MapFiles && generated is not null)
            manifestNode["files"] = JsonSerializer.SerializeToNode(manifestFiles, WriteJsonOptions);

        try
        {
            CreateAtomic(outputPath, archive =>
            {
                foreach (var item in selected.Where(f => f.ArchivePath != ManifestFileName))
                    WriteFileEntry(archive, item.ArchivePath, item.LocalPath);

                WriteManifestEntry(archive, SerializeManifestNode(manifestNode));
            });
        }
        finally
        {
            foreach (var generatedFile in selected.Where(file => file.OriginalPath == "[generated]"))
            {
                if (File.Exists(generatedFile.LocalPath))
                    File.Delete(generatedFile.LocalPath);
            }
        }

        return new MdzPackBuildResult(
            NodeToManifest(manifestNode),
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
    /// Adds a file to an existing .mdz archive or replaces an existing entry at the same archive path
    /// (matched case-sensitively after NFC, §5.4.1). See <see cref="UpdateFiles"/>.
    /// </summary>
    public static void AddFile(string archivePath, string archiveEntryPath, string localFilePath)
    {
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException($"Source file '{localFilePath}' does not exist.", localFilePath);

        UpdateFiles(archivePath, [new ArchiveWriteSpec(archiveEntryPath, localFilePath)]);
    }

    /// <summary>
    /// Removes a file from an existing .mdz archive (matched case-sensitively after NFC, §5.4.1).
    /// See <see cref="UpdateFiles"/>.
    /// </summary>
    public static void RemoveFile(string archivePath, string archiveEntryPath) =>
        UpdateFiles(archivePath, writes: [], removals: [archiveEntryPath]);

    /// <summary>
    /// Removes multiple files from an existing .mdz archive in one atomic rewrite.
    /// </summary>
    public static ArchiveMutationResult RemoveFiles(
        string archivePath,
        IEnumerable<string> archiveEntryPaths) =>
        UpdateFiles(archivePath, writes: [], removals: archiveEntryPaths);

    /// <summary>
    /// Applies multiple archive entry writes and removals in one atomic rewrite (§14.1).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Paths match case-sensitively after NFC (§5.4.1); written paths are stored in NFC.</item>
    /// <item>Untouched entries keep their bytes, timestamps and attributes. Symbolic link entries are
    /// kept unless <paramref name="dropSpecialFiles"/> is true or they are named in
    /// <paramref name="removals"/> (§4, §14.1).</item>
    /// <item>The existing manifest is kept as read — unknown fields and descriptive values this library
    /// would ignore survive (§6.3) — and its <c>modified</c> refreshed. An archive without a manifest
    /// gets one recording its entry point.</item>
    /// <item>Throws <c>ERR_ENTRYPOINT_CHANGED</c> if the change would move the entry point, unless
    /// <paramref name="manifest"/> or a written manifest.json sets it explicitly.</item>
    /// </list>
    /// </remarks>
    public static ArchiveMutationResult UpdateFiles(
        string archivePath,
        IEnumerable<ArchiveWriteSpec> writes,
        IEnumerable<string>? removals = null,
        Manifest? manifest = null,
        bool dropSpecialFiles = false)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        var normalizedWrites = new Dictionary<string, ArchiveWriteSpec>(StringComparer.Ordinal);
        foreach (var write in writes)
        {
            // Producers write NFC paths (§5.4).
            var normalisedPath = MdzSpec.Nfc(NormaliseArchivePath(write.ArchivePath));
            var pathError = PathValidator.Validate(normalisedPath);
            if (pathError is not null)
                throw new InvalidOperationException($"ERR_PATH_INVALID: Invalid path '{normalisedPath}': {pathError}");

            if (!File.Exists(write.LocalPath))
                throw new FileNotFoundException($"Source file '{write.LocalPath}' does not exist.", write.LocalPath);

            normalizedWrites[normalisedPath] = new ArchiveWriteSpec(normalisedPath, write.LocalPath);
        }

        var removalRequests = new List<string>();
        foreach (var removal in removals ?? [])
        {
            var normalisedPath = NormaliseArchivePath(removal);
            var pathError = PathValidator.Validate(normalisedPath);
            if (pathError is not null)
                throw new InvalidOperationException($"ERR_PATH_INVALID: Invalid path '{normalisedPath}': {pathError}");
            removalRequests.Add(normalisedPath);
        }

        ArchiveMutationResult? result = null;
        CreateAtomic(archivePath, destinationArchive =>
        {
            using var sourceArchive = OpenChecked(archivePath);
            var fileEntries = FileEntries(sourceArchive).ToList();
            var specialEntries = sourceArchive.Entries.Where(IsSymlink).ToList();
            var existingPaths = fileEntries.Select(EntryPath).Where(p => p != ManifestFileName).ToList();

            // Removals: NFC key → stored name, which must exist (symbolic links included).
            var normalizedRemovals = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var removal in removalRequests)
            {
                var stored = MdzSpec.FindPath(fileEntries.Concat(specialEntries).Select(EntryPath), removal)
                    ?? throw new FileNotFoundException($"ERR_NOT_FOUND: Entry '{removal}' was not found in archive.", removal);
                normalizedRemovals[MdzSpec.Nfc(stored)] = stored;
            }
            if (dropSpecialFiles)
            {
                foreach (var special in specialEntries)
                    normalizedRemovals[MdzSpec.Nfc(EntryPath(special))] = EntryPath(special);
            }
            foreach (var key in normalizedRemovals.Keys)
                normalizedWrites.Remove(key);

            var changing = normalizedWrites.Count > 0 || normalizedRemovals.Count > 0;
            // An unreadable existing manifest blocks a save that would keep it, not one that replaces or removes it.
            var keepsManifest = !normalizedRemovals.ContainsKey(ManifestFileName) && !normalizedWrites.ContainsKey(ManifestFileName);
            JsonObject? existingManifest;
            try
            {
                existingManifest = ReadManifestNode(sourceArchive, requireValid: keepsManifest && (changing || manifest is not null));
            }
            catch (InvalidOperationException ex)
            {
                throw MdzSpec.Error("ERR_MANIFEST_INVALID", $"Existing manifest.json is invalid: {ex.Message}");
            }
            var entryPointBefore = MdzSpec.TryResolveEntryPoint(existingPaths, existingManifest);

            // The manifest to write: an explicit manifest.json write > the manifest argument > the
            // existing one refreshed > a new one for an archive that has none (§14.1).
            JsonObject? manifestNode = null;
            var explicitManifest = false;
            if (normalizedWrites.TryGetValue(ManifestFileName, out var manifestWrite))
            {
                explicitManifest = true;
                manifestNode = MdzSpec.ParseManifest(File.ReadAllText(manifestWrite.LocalPath, Encoding.UTF8)).Manifest;
                StampManifestNode(manifestNode, setCreatedIfMissing: true);
                EnsureWritableManifest(manifestNode);
            }
            else if (manifest is not null)
            {
                explicitManifest = true;
                manifestNode = ManifestToNode(UpdateManifest(manifest));
                EnsureWritableManifest(manifestNode);
            }
            else if (changing && !normalizedRemovals.ContainsKey(ManifestFileName))
            {
                if (existingManifest is not null)
                {
                    // Kept as read, so unknown fields — and descriptive values this library would
                    // ignore — survive the save (§6.3); only critical fields must be valid.
                    manifestNode = existingManifest;
                    StampManifestNode(manifestNode, setCreatedIfMissing: false);
                    MdzSpec.CheckManifest(manifestNode);
                }
                else
                {
                    manifestNode = ManifestToNode(CreateManifest());
                    if (entryPointBefore is not null && !normalizedRemovals.ContainsKey(MdzSpec.Nfc(entryPointBefore)))
                        manifestNode["entryPoint"] = entryPointBefore;
                }
            }

            var nextPaths = existingPaths
                .Where(path => !normalizedRemovals.ContainsKey(MdzSpec.Nfc(path)) && !normalizedWrites.ContainsKey(MdzSpec.Nfc(path)))
                .Concat(normalizedWrites.Keys.Where(path => path != ManifestFileName))
                .ToList();
            var nextManifest = manifestNode ?? (normalizedRemovals.ContainsKey(ManifestFileName) ? null : existingManifest);
            var entryPointAfter = MdzSpec.ResolveEntryPoint(nextPaths, nextManifest);
            if (entryPointBefore is not null && !explicitManifest && !MdzSpec.SamePath(entryPointBefore, entryPointAfter))
            {
                throw MdzSpec.Error(
                    "ERR_ENTRYPOINT_CHANGED",
                    $"this change would move the entry point from \"{entryPointBefore}\" to \"{entryPointAfter}\"; set manifest entryPoint to change it.");
            }

            foreach (var sourceEntry in fileEntries.Concat(specialEntries))
            {
                var key = MdzSpec.Nfc(EntryPath(sourceEntry));
                if (normalizedRemovals.ContainsKey(key)
                    || normalizedWrites.ContainsKey(key)
                    || (manifestNode is not null && key == ManifestFileName))
                {
                    continue;
                }

                CopyEntry(sourceEntry, destinationArchive);
            }

            foreach (var write in normalizedWrites.Values.Where(write => write.ArchivePath != ManifestFileName))
                WriteFileEntry(destinationArchive, write.ArchivePath, write.LocalPath);

            if (manifestNode is not null)
                WriteManifestEntry(destinationArchive, SerializeManifestNode(manifestNode));

            var archivePaths = nextPaths
                .Concat(nextManifest is null ? [] : [ManifestFileName])
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result = new ArchiveMutationResult(
                manifestNode is null ? null : NodeToManifest(manifestNode),
                entryPointAfter,
                archivePaths);
        });

        return result!;
    }

    // -------------------------------------------------------------------------
    // Extract
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts a .mdz archive to a destination directory. Symbolic link entries are skipped, never
    /// created as links (§13.1); see <see cref="ListSpecialFiles"/>.
    /// </summary>
    public static void Extract(string archivePath, string destinationDirectory)
    {
        using var archive = OpenChecked(archivePath);

        Directory.CreateDirectory(destinationDirectory);

        foreach (var entry in FileEntries(archive))
        {
            var entryPath = EntryPath(entry);

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
    /// Returns a list of all file entry paths within the archive. Symbolic link entries are not
    /// listed (§13.1); see <see cref="ListSpecialFiles"/>.
    /// </summary>
    public static IReadOnlyList<string> List(string archivePath)
    {
        using var archive = OpenChecked(archivePath);
        return FileEntries(archive)
            .Select(EntryPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Symbolic link entries (and other special files) in the archive. They are not listed, can't be
    /// read and are never followed (§4, §13.1); <see cref="Validate"/> reports them. Mutations keep them
    /// unless <c>dropSpecialFiles</c> is set.
    /// </summary>
    public static IReadOnlyList<string> ListSpecialFiles(string archivePath)
    {
        using var archive = OpenChecked(archivePath);
        return archive.Entries.Where(IsSymlink).Select(EntryPath).ToList();
    }

    /// <summary>
    /// Returns true when a file or directory entry exists in the archive (case-sensitive after NFC, §5.4.1).
    /// </summary>
    public static bool HasEntry(string archivePath, string archiveEntryPath)
    {
        using var archive = OpenChecked(archivePath);
        return FindEntryWithDirectoryFallback(archive, archiveEntryPath) is not null;
    }

    /// <summary>
    /// Finds a file or directory entry by archive path: case-sensitive after NFC normalization (§5.4.1).
    /// </summary>
    public static ArchiveEntry? FindEntry(string archivePath, string archiveEntryPath)
    {
        using var archive = OpenChecked(archivePath);
        var entry = FindEntryWithDirectoryFallback(archive, archiveEntryPath);
        return entry is null ? null : ToArchiveEntry(entry);
    }

    /// <summary>
    /// The stored path of an entry whose name differs from <paramref name="archiveEntryPath"/> only in
    /// case, for reporting a failed lookup ("did you mean ...?", §5.4.1). Never use it to open the entry.
    /// </summary>
    public static string? FindCaseOnlyMatch(string archivePath, string archiveEntryPath)
    {
        var wanted = MdzSpec.Nfc(NormaliseArchivePath(archiveEntryPath));
        return List(archivePath).FirstOrDefault(path =>
            MdzSpec.Nfc(path).Equals(wanted, StringComparison.OrdinalIgnoreCase)
            && !MdzSpec.SamePath(path, wanted));
    }

    /// <summary>
    /// Returns detailed entry information for all file entries in the archive.
    /// </summary>
    public static IReadOnlyList<ArchiveEntry> ListDetailed(string archivePath)
    {
        using var archive = OpenChecked(archivePath);
        return FileEntries(archive)
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
    /// Reads manifest.json from the archive, or returns null if not present (§6.4).
    /// </summary>
    /// <remarks>
    /// Throws <c>ERR_MANIFEST_INVALID</c> for an unreadable manifest and the matching error for an
    /// invalid critical field (<c>spec</c>, <c>spec.version</c>, <c>mode</c>, <c>entryPoint</c>).
    /// Descriptive fields with a wrong type or value are ignored — left out of the result and reported
    /// by <see cref="Validate"/>. Unknown top-level fields are kept in
    /// <see cref="Manifest.AdditionalFields"/>.
    /// </remarks>
    public static Manifest? ReadManifest(string archivePath)
    {
        using var archive = OpenChecked(archivePath);
        var node = ReadManifestNode(archive, requireValid: true);
        if (node is null)
            return null;

        foreach (var field in MdzSpec.CheckManifest(node))
            node.Remove(field);
        return NodeToManifest(node);
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
    /// The existing manifest is edited as JSON, so every field it has — nested unknown fields
    /// included — survives (§6.3). An archive without a manifest gets one.
    /// </summary>
    public static Manifest UpdateManifest(
        string archivePath,
        ManifestEditableMetadata? metadata,
        ManifestUpdateOptions? options = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        options ??= new ManifestUpdateOptions();
        Manifest? updatedManifest = null;
        CreateAtomic(archivePath, destinationArchive =>
        {
            using var sourceArchive = OpenChecked(archivePath);
            var archivePaths = FileEntries(sourceArchive).Select(EntryPath).Where(p => p != ManifestFileName).ToList();
            var existing = ReadManifestNode(sourceArchive, requireValid: true);
            var entryPointBefore = MdzSpec.TryResolveEntryPoint(archivePaths, existing);

            var node = existing ?? ManifestToNode(CreateManifest());
            if (existing is null && entryPointBefore is not null)
                node["entryPoint"] = entryPointBefore;
            ApplyManifestMetadata(node, metadata);
            StampManifestNode(node, options.SetCreatedIfMissing, options.RefreshModified);
            MdzSpec.CheckManifest(node);
            MdzSpec.ResolveEntryPoint(archivePaths, node);

            foreach (var sourceEntry in FileEntries(sourceArchive).Concat(sourceArchive.Entries.Where(IsSymlink)))
            {
                if (EntryPath(sourceEntry) != ManifestFileName)
                    CopyEntry(sourceEntry, destinationArchive);
            }

            WriteManifestEntry(destinationArchive, SerializeManifestNode(node));
            updatedManifest = NodeToManifest(node);
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
    /// Resolves the entry point Markdown file for the archive per Section 5.5. Returns null if there
    /// is none, including when manifest.json is unreadable or its <c>entryPoint</c> is invalid or
    /// missing (which never falls back to another file). <see cref="ResolveEntryPointStrict"/> says why.
    /// </summary>
    public static string? ResolveEntryPoint(string archivePath)
    {
        try
        {
            return ResolveEntryPointStrict(archivePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the entry point per §5.5, throwing <c>ERR_ENTRYPOINT_INVALID</c>,
    /// <c>ERR_ENTRYPOINT_MISSING</c>, <c>ERR_ENTRYPOINT_UNRESOLVED</c> or a manifest error when there is
    /// none. See <see cref="MdzSpec.ResolveEntryPoint"/>.
    /// </summary>
    public static string ResolveEntryPointStrict(string archivePath)
    {
        using var archive = OpenChecked(archivePath);
        var manifest = ReadManifestNode(archive, requireValid: true);
        if (manifest is not null)
            MdzSpec.CheckManifest(manifest);
        return MdzSpec.ResolveEntryPoint(FileEntries(archive).Select(EntryPath), manifest);
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
    /// Resolves a Markdown link written in <paramref name="document"/> (§9.3–9.6). Without a
    /// <paramref name="host"/>, only links inside this archive resolve; links that leave it are
    /// <c>unresolved</c> with reason <c>no-host-location</c>.
    /// </summary>
    /// <param name="archivePath">The archive file.</param>
    /// <param name="document">Archive path of the document containing the link.</param>
    /// <param name="link">The link destination as written.</param>
    /// <param name="host">Optional host model; this archive is then addressed as host path <paramref name="archiveHostPath"/>.</param>
    /// <param name="archiveHostPath">This archive's host path (default <c>archive.mdz</c>).</param>
    /// <param name="use"><c>navigate</c> or <c>embed</c>.</param>
    public static MdzLinkTarget ResolveLink(
        string archivePath,
        string document,
        string link,
        IMdzLinkHost? host = null,
        string archiveHostPath = "archive.mdz",
        string use = "navigate") =>
        MdzLinks.ResolveLink(
            new MdzArchiveLinkSource(archiveHostPath, document),
            link,
            host ?? new SingleArchiveLinkHost(List(archivePath)),
            use);

    private sealed class SingleArchiveLinkHost(IReadOnlyList<string> paths) : IMdzLinkHost
    {
        public string? Kind(string path) => null;
        public string? FindEntry(string archive, string entry) => MdzSpec.FindPath(paths, entry);
        public string? EntryPoint(string archive) => null;
        public bool HostLocation => false;
    }

    /// <summary>
    /// Reads UTF-8 text content from an archive entry.
    /// </summary>
    public static string ReadText(string archivePath, string archiveEntryPath)
    {
        using var archive = OpenChecked(archivePath);
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
        using var archive = OpenChecked(archivePath);
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
        var assetPathSet = assetPaths.ToHashSet(MdzSpec.PathComparer);

        var scannedMarkdownPaths = options.ScanMode == "all-markdown"
            ? allEntries.Where(entry => entry.IsMarkdown).Select(entry => entry.Path).ToList()
            : [NormaliseArchivePath(options.EntryPoint ?? ResolveEntryPoint(archivePath) ?? string.Empty)];
        scannedMarkdownPaths = scannedMarkdownPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

        var referencedAssets = new HashSet<string>(MdzSpec.PathComparer);
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
            var cover = NormaliseArchivePath(manifest.Cover.StartsWith("./", StringComparison.Ordinal) ? manifest.Cover[2..] : manifest.Cover);
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
                entryPoint is not null && MdzSpec.SamePath(entry.Path, entryPoint)))
            .ToList();

        var assets = entries
            .Where(entry => !entry.IsMarkdown
                && !entry.IsDirectory
                && entry.Path != ManifestFileName)
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
    /// Validates raw manifest.json text without requiring an archive (§6.4). Errors: unreadable JSON,
    /// a non-object, and invalid critical fields. Warnings: ignored descriptive fields, duplicate
    /// members, and a missing or older-major <c>spec.version</c>.
    /// </summary>
    public static ValidationResult ValidateManifest(string manifestJson)
    {
        JsonObject manifest;
        IReadOnlyList<string> duplicates;
        try
        {
            (manifest, duplicates) = MdzSpec.ParseManifest(manifestJson);
        }
        catch (InvalidOperationException ex)
        {
            return new ValidationResult { IsValid = false, Errors = [ex.Message], Warnings = [] };
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        CheckManifestInto(manifest, duplicates, errors, warnings);
        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors, Warnings = warnings };
    }

    /// <summary>
    /// Validates a manifest object without requiring an archive (see <see cref="ValidateManifest(string)"/>).
    /// </summary>
    public static ValidationResult ValidateManifest(Manifest? manifest)
    {
        if (manifest is null)
        {
            return new ValidationResult
            {
                IsValid = false,
                Errors = ["ERR_MANIFEST_INVALID: manifest is null."],
                Warnings = [],
            };
        }

        return ValidateManifest(SerializeManifest(manifest));
    }

    /// <summary>
    /// Validates a .mdz archive against the specification.
    /// </summary>
    /// <remarks>
    /// Errors: an unreadable or encrypted ZIP, unsupported compression, duplicate or conflicting entry
    /// names (§4, §5.4), invalid entry paths, an unreadable manifest or invalid critical field (§6.4), and
    /// an entry point that can't be resolved (§5.5). Warnings: symbolic link entries, which are skipped
    /// (§13.1); ignored descriptive manifest fields and duplicate members; a missing <c>cover</c>; and
    /// missing version metadata.
    /// </remarks>
    public static ValidationResult Validate(string archivePath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        ZipArchive archive;
        try
        {
            archive = OpenChecked(archivePath);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
            return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
        }

        using (archive)
        {
            foreach (var entry in FileEntries(archive))
            {
                var problem = MdzSpec.PathProblem(EntryPath(entry));
                if (problem is not null)
                    errors.Add($"ERR_PATH_INVALID: {problem.Message}");
            }

            foreach (var entry in archive.Entries.Where(IsSymlink))
                warnings.Add($"ERR_PATH_INVALID: symbolic link '{EntryPath(entry)}' is ignored and never followed (§4, §13.1).");

            JsonObject? manifest = null;
            var manifestEntry = FindEntry(archive, ManifestFileName);
            if (manifestEntry is not null)
            {
                IReadOnlyList<string> duplicates;
                try
                {
                    using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
                    (manifest, duplicates) = MdzSpec.ParseManifest(reader.ReadToEnd());
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(ex.Message);
                    return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
                }

                if (!CheckManifestInto(manifest, duplicates, errors, warnings))
                    return new ValidationResult { IsValid = false, Errors = errors, Warnings = warnings };

                if (manifest["cover"] is JsonValue cover && cover.TryGetValue<string>(out var coverPath)
                    && MdzSpec.CoverProblem(cover) is null)
                {
                    var found = FindEntry(archive, coverPath.StartsWith("./", StringComparison.Ordinal) ? coverPath[2..] : coverPath);
                    if (found is null || IsDirectoryEntry(found))
                        warnings.Add($"manifest 'cover' references '{coverPath}' which does not exist in the archive.");
                }
            }
            else
            {
                warnings.Add("No manifest.json present. Version metadata is unavailable.");
            }

            try
            {
                MdzSpec.ResolveEntryPoint(FileEntries(archive).Select(EntryPath), manifest);
            }
            catch (InvalidOperationException ex)
            {
                var message = ex.Message;
                // A lookup that fails only because of case gets a suggestion, never a silent match (§5.4.1).
                if (MdzSpec.ErrorCode(ex) == "ERR_ENTRYPOINT_MISSING"
                    && manifest?["entryPoint"] is JsonValue declared
                    && declared.TryGetValue<string>(out var wanted))
                {
                    wanted = wanted.StartsWith("./", StringComparison.Ordinal) ? wanted[2..] : wanted;
                    var near = FileEntries(archive).Select(EntryPath).FirstOrDefault(p =>
                        MdzSpec.Nfc(p).Equals(MdzSpec.Nfc(wanted), StringComparison.OrdinalIgnoreCase));
                    if (near is not null)
                        message += $" (did you mean \"{near}\"? Paths are case-sensitive.)";
                }
                errors.Add(message);
            }
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Adds the §6.4 findings for a parsed manifest. Returns false when a critical field is invalid.
    /// </summary>
    private static bool CheckManifestInto(
        JsonObject manifest,
        IReadOnlyList<string> duplicates,
        List<string> errors,
        List<string> warnings)
    {
        IReadOnlyList<string> ignored;
        try
        {
            ignored = MdzSpec.CheckManifest(manifest);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
            return false;
        }

        foreach (var field in ignored)
            warnings.Add($"manifest '{field}' has an invalid type or value and is ignored (§6.4).");
        if (duplicates.Count > 0)
            warnings.Add($"manifest.json repeats {string.Join(", ", duplicates.Select(d => $"'{d}'"))}; the last of each is used (§6.4).");

        var version = manifest["spec"]?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version))
        {
            warnings.Add("manifest 'spec.version' is missing; version metadata is unavailable.");
        }
        else if (int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture) < MdzSpec.SupportedMajorVersion)
        {
            warnings.Add($"manifest 'spec.version' major version {version.Split('.')[0]} is older than supported major {MdzSpec.SupportedMajorVersion}.");
        }

        return true;
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

    private static string EntryPath(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    /// <summary>True for a symbolic link entry: Unix file type <c>S_IFLNK</c> in the external attributes (§4).</summary>
    private static bool IsSymlink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    /// <summary>File entries: no directory entries and no symbolic links (§13.1).</summary>
    private static IEnumerable<ZipArchiveEntry> FileEntries(ZipArchive archive) =>
        archive.Entries.Where(e => !IsDirectoryEntry(e) && !IsSymlink(e));

    /// <summary>
    /// Opens an archive for reading after the container checks every reader applies: encrypted entries
    /// (<c>ERR_ZIP_ENCRYPTED</c>), compression methods other than 0 and 8 or an unreadable ZIP
    /// (<c>ERR_ZIP_INVALID</c>, §4), and NFC-duplicate or conflicting entry names
    /// (<c>ERR_DUPLICATE_PATH</c>, <c>ERR_PATH_CONFLICT</c>, §5.4, §13.1).
    /// </summary>
    private static ZipArchive OpenChecked(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"Archive '{archivePath}' does not exist.", archivePath);

        try
        {
            ScanCentralDirectory(archivePath);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            throw MdzSpec.Error("ERR_ZIP_INVALID", "The file is not a valid ZIP archive.");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException ex)
        {
            throw MdzSpec.Error("ERR_ZIP_INVALID", $"The file is not a valid ZIP archive ({ex.Message}).");
        }

        var problem = MdzSpec.EntrySetProblem(archive.Entries.Select(EntryPath));
        if (problem is not null)
        {
            archive.Dispose();
            throw problem.ToException();
        }

        return archive;
    }

    private static ArchiveEntry ToArchiveEntry(ZipArchiveEntry entry)
    {
        var isDirectory = IsDirectoryEntry(entry);
        return new ArchiveEntry(
            EntryPath(entry),
            isDirectory ? 0 : entry.Length,
            isDirectory ? 0 : entry.CompressedLength,
            entry.LastWriteTime.UtcDateTime,
            isDirectory,
            !isDirectory && IsMarkdownPath(entry.FullName),
            !isDirectory && IsImagePath(entry.FullName));
    }

    /// <summary>Finds an entry case-sensitively after NFC (§5.4.1). Symbolic links are never returned.</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalised = MdzSpec.Nfc(NormaliseArchivePath(path));
        return archive.Entries.FirstOrDefault(e =>
            !IsSymlink(e) && string.Equals(MdzSpec.Nfc(EntryPath(e)), normalised, StringComparison.Ordinal));
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
            && MdzSpec.SamePath(node.Name, name));
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

    /// <summary>Files whose line endings a producer normalizes to LF when it writes them (§12.2).</summary>
    private static bool IsTextFile(string path) => IsMarkdownPath(path) || path == ManifestFileName;

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

    private static bool IsMarkdownPath(string path) => MdzSpec.IsMarkdownPath(path);

    private static bool IsImagePath(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "png" or "jpg" or "jpeg" or "gif" or "webp" or "bmp" or "ico"
            or "webm" or "mp4" or "mp3" or "wav" or "ogg";
    }

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

        // Raw HTML <img src> tags aren't part of the markdown image syntax above,
        // but authors use them for sizing/alignment control markdown can't express.
        foreach (Match match in Regex.Matches(
            markdown,
            @"<img\b[^>]*\ssrc\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<uq>[^\s""'>]+))[^>]*>",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            var target = match.Groups["dq"].Success ? match.Groups["dq"].Value
                : match.Groups["sq"].Success ? match.Groups["sq"].Value
                : match.Groups["uq"].Value;
            target = target.Trim();
            if (target.Length > 0)
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
        MdzSpec.FindPath(paths, candidate) ?? candidate;

    private static string GetDocumentTitle(string path, Manifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.EntryPoint)
            && MdzSpec.SamePath(path, manifest.EntryPoint.StartsWith("./", StringComparison.Ordinal) ? manifest.EntryPoint[2..] : manifest.EntryPoint)
            && !string.IsNullOrWhiteSpace(manifest.Title))
        {
            return manifest.Title;
        }

        return Path.GetFileNameWithoutExtension(path)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
    }

    /// <summary>
    /// Reads manifest.json as a JSON object (duplicate members: last wins), or null if absent. An
    /// unreadable manifest throws <c>ERR_MANIFEST_INVALID</c> when <paramref name="requireValid"/>, else
    /// returns null.
    /// </summary>
    private static JsonObject? ReadManifestNode(ZipArchive archive, bool requireValid)
    {
        var manifestEntry = FindEntry(archive, ManifestFileName);
        if (manifestEntry is null || IsDirectoryEntry(manifestEntry))
            return null;

        try
        {
            using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
            return MdzSpec.ParseManifest(reader.ReadToEnd()).Manifest;
        }
        catch (InvalidOperationException) when (!requireValid)
        {
            return null;
        }
    }

    /// <summary>A caller's manifest.json, which must be fully valid (§6.4): a producer doesn't write values a consumer would ignore.</summary>
    private static JsonObject ReadProvidedManifest(string localPath)
    {
        var node = MdzSpec.ParseManifest(File.ReadAllText(localPath, Encoding.UTF8)).Manifest;
        EnsureWritableManifest(node);
        return node;
    }

    /// <summary>
    /// Checks a manifest a producer is about to write (§6.4, §14.1): critical fields must be valid,
    /// and so must every descriptive field present. Unknown fields are never checked.
    /// </summary>
    private static void EnsureWritableManifest(JsonObject manifest)
    {
        var ignored = MdzSpec.CheckManifest(manifest);
        if (ignored.Count > 0)
        {
            throw MdzSpec.Error(
                "ERR_MANIFEST_INVALID",
                $"manifest.json has invalid values for {string.Join(", ", ignored.Select(f => $"'{f}'"))} (§6.4)");
        }
    }

    /// <summary>
    /// Fills in what a producer's manifest must or should have (§6.2, §14.1): <c>spec.version</c>,
    /// timestamps, the resolved <c>entryPoint</c> and <c>mode</c>. Throws when the entry point can't be
    /// resolved or the manifest is invalid.
    /// </summary>
    private static void CompleteProducerManifest(JsonObject manifest, IReadOnlyList<string> archivePaths)
    {
        StampManifestNode(manifest, setCreatedIfMissing: true);
        EnsureWritableManifest(manifest);
        var entryPoint = MdzSpec.ResolveEntryPoint(archivePaths, manifest);
        if (!manifest.ContainsKey("entryPoint"))
            manifest["entryPoint"] = entryPoint;
        if (!manifest.ContainsKey("mode"))
            manifest["mode"] = "document";
    }

    /// <summary>Adds missing <c>spec</c> metadata and refreshes timestamps, keeping each timestamp's form (string or object with <c>when</c>).</summary>
    private static void StampManifestNode(JsonObject manifest, bool setCreatedIfMissing, bool refreshModified = true)
    {
        if (manifest["spec"] is not JsonObject spec)
            manifest["spec"] = spec = new JsonObject();
        spec["name"] ??= MdzSpec.SpecName;
        spec["version"] ??= MdzSpec.SpecVersion;

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        if (setCreatedIfMissing && !manifest.ContainsKey("created"))
            manifest["created"] = now;
        if (refreshModified)
        {
            if (manifest["modified"] is JsonObject modified)
                modified["when"] = now;
            else
                manifest["modified"] = now;
        }
    }

    private static JsonObject ManifestToNode(Manifest manifest) =>
        JsonSerializer.SerializeToNode(manifest, WriteJsonOptions) as JsonObject ?? [];

    private static Manifest NodeToManifest(JsonObject manifest) =>
        manifest.Deserialize<Manifest>(JsonOptions) ?? new Manifest();

    private static string SerializeManifestNode(JsonObject manifest) =>
        manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Reflects the values a create wrote back onto the caller's manifest object.</summary>
    private static void CopyManifestValues(JsonObject written, Manifest target)
    {
        var result = NodeToManifest(written);
        target.Spec = result.Spec;
        target.Created = result.Created;
        target.Modified = result.Modified;
        target.EntryPoint = result.EntryPoint;
        target.Mode = result.Mode;
    }

    /// <summary>
    /// Copies an entry unchanged: content, timestamp and external attributes (so a symbolic link stays
    /// what it was, §14.1).
    /// </summary>
    private static void CopyEntry(ZipArchiveEntry sourceEntry, ZipArchive destinationArchive)
    {
        var destinationEntry = destinationArchive.CreateEntry(EntryPath(sourceEntry), CompressionLevel.Optimal);
        destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
        destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;
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
        manifest.Spec.Name ??= MdzSpec.SpecName;
        manifest.Spec.Version ??= MdzSpec.SpecVersion;

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        if (options.SetCreatedIfMissing && string.IsNullOrWhiteSpace(manifest.Created))
            manifest.Created = now;

        if (options.RefreshModified)
            manifest.Modified = now;
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

    /// <summary>The JSON-level counterpart of <see cref="ApplyManifestMetadata(Manifest, ManifestEditableMetadata?)"/>: other fields are untouched.</summary>
    private static void ApplyManifestMetadata(JsonObject manifest, ManifestEditableMetadata? metadata)
    {
        if (metadata is null)
            return;

        if (metadata.Title is not null) manifest["title"] = metadata.Title;
        if (metadata.Author is not null)
        {
            manifest["author"] = JsonSerializer.SerializeToNode(metadata.Author, WriteJsonOptions);
            manifest["authors"] = new JsonArray(JsonSerializer.SerializeToNode(metadata.Author, WriteJsonOptions));
        }
        if (metadata.Description is not null) manifest["description"] = metadata.Description;
        if (metadata.Keywords is not null) manifest["keywords"] = JsonSerializer.SerializeToNode(metadata.Keywords, WriteJsonOptions);
        if (metadata.Language is not null) manifest["language"] = metadata.Language;
        if (metadata.License is not null) manifest["license"] = metadata.License;
        if (metadata.Version is not null) manifest["version"] = metadata.Version;
        if (metadata.Cover is not null) manifest["cover"] = metadata.Cover;
        if (metadata.Mode is not null) manifest["mode"] = metadata.Mode;
        if (metadata.EntryPoint is not null) manifest["entryPoint"] = metadata.EntryPoint;
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

        // No default language: an omitted language is unspecified (§6.2).
        return CreateManifest(new ManifestEditableMetadata
        {
            Title = string.IsNullOrWhiteSpace(options.Title) ? rootName : options.Title,
            Mode = string.IsNullOrWhiteSpace(options.Mode) ? null : options.Mode,
            EntryPoint = string.IsNullOrWhiteSpace(options.EntryPoint) ? null : options.EntryPoint,
            Language = string.IsNullOrWhiteSpace(options.Language) ? null : options.Language,
            Author = string.IsNullOrWhiteSpace(options.Author) ? null : new ManifestAuthor { Name = options.Author },
            Description = string.IsNullOrWhiteSpace(options.Description) ? null : options.Description,
            Version = string.IsNullOrWhiteSpace(options.DocVersion) ? null : options.DocVersion,
        });
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

        lines.AddRange([string.Empty, "---", string.Empty, "Generated by `mdz-core`", string.Empty, "More info: [mdzip.org](https://mdzip.org)"]);
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

    private static void WriteManifestEntry(ZipArchive archive, string manifestJson)
    {
        var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
        using var stream = manifestEntry.Open();
        var normalised = NormaliseLf(manifestJson);
        var bytes = Encoding.UTF8.GetBytes(normalised);
        stream.Write(bytes, 0, bytes.Length);
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

    /// <summary>
    /// Reads the central directory directly, for what <see cref="ZipArchive"/> doesn't expose: encrypted
    /// entries (<c>ERR_ZIP_ENCRYPTED</c>) and compression methods other than 0 and 8 (<c>ERR_ZIP_INVALID</c>, §4).
    /// </summary>
    private static void ScanCentralDirectory(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        const uint EndOfCentralDirectorySignature = 0x06054B50;
        const uint CentralDirectoryHeaderSignature = 0x02014B50;
        const int EocdMinSize = 22;
        const int MaxCommentLength = 65535;

        var scanSize = (int)Math.Min(stream.Length, EocdMinSize + MaxCommentLength);
        if (scanSize < EocdMinSize)
            return;

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
            return;

        var centralDirectoryOffset = BitConverter.ToUInt32(tail, eocdOffsetInTail + 16);
        var totalEntries = BitConverter.ToUInt16(tail, eocdOffsetInTail + 10);
        if (centralDirectoryOffset == 0xFFFFFFFF || totalEntries == 0xFFFF)
            return; // ZIP64: ZipArchive reads it; nothing more to check here.

        stream.Seek(centralDirectoryOffset, SeekOrigin.Begin);

        for (var i = 0; i < totalEntries; i++)
        {
            if (reader.ReadUInt32() != CentralDirectoryHeaderSignature)
                return;

            _ = reader.ReadUInt16(); // version made by
            _ = reader.ReadUInt16(); // version needed
            var generalPurposeBitFlag = reader.ReadUInt16();
            var compressionMethod = reader.ReadUInt16();
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

            var name = Encoding.UTF8.GetString(reader.ReadBytes(fileNameLength));
            stream.Seek(extraLength + commentLength, SeekOrigin.Current);

            if ((generalPurposeBitFlag & 0x0001) != 0)
                throw MdzSpec.Error("ERR_ZIP_ENCRYPTED", "Encrypted ZIP entries are not supported.");
            if (compressionMethod is not 0 and not 8)
            {
                throw MdzSpec.Error(
                    "ERR_ZIP_INVALID",
                    $"entry '{name}' uses compression method {compressionMethod}; MDZip allows 0 (stored) and 8 (DEFLATE) (§4).");
            }
        }
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
