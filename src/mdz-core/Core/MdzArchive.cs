using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mdz.Models;

namespace Mdz.Core;

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
    private const int SupportedMajorVersion = 1;
    private static readonly Regex SemVerRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
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
                try
                {
                    using var stream = manifestEntry.Open();
                    manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonOptions);
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
                    // Required fields
                    if (string.IsNullOrWhiteSpace(manifest.Mdz))
                        errors.Add("ERR_MANIFEST_INVALID: manifest.json is missing required field 'mdz'.");
                    else
                    {
                        // Validate SemVer 2.0.0 and enforce major-version compatibility:
                        // - reject higher unsupported major versions
                        // - allow lower major versions with a warning
                        if (!TryParseSemVerMajor(manifest.Mdz, out var major))
                            errors.Add($"ERR_MANIFEST_INVALID: 'mdz' field '{manifest.Mdz}' is not a valid semver string.");
                        else if (major > SupportedMajorVersion)
                            errors.Add($"ERR_VERSION_UNSUPPORTED: manifest 'mdz' major version {major} is not supported (supported: {SupportedMajorVersion}).");
                        else if (major < SupportedMajorVersion)
                            warnings.Add($"manifest 'mdz' major version {major} is older than supported major {SupportedMajorVersion}.");
                    }

                    if (manifest.Title is not null && string.IsNullOrWhiteSpace(manifest.Title))
                        errors.Add("ERR_MANIFEST_INVALID: manifest field 'title' must not be empty when present.");

                    // Validate entryPoint reference
                    if (!string.IsNullOrWhiteSpace(manifest.EntryPoint))
                    {
                        if (FindEntry(archive, manifest.EntryPoint) is null)
                            errors.Add($"ERR_ENTRYPOINT_MISSING: manifest 'entryPoint' references '{manifest.EntryPoint}' which does not exist in the archive.");
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
            if (FindEntry(archive, ep) is not null)
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
