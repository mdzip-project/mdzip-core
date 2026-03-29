# mdzip-core
Core .NET library for working with `.mdz` archives.

`mdzip-core` provides APIs to:
- create `.mdz` archives from folders or explicit file mappings
- extract archives safely
- list archive contents
- read `manifest.json`
- resolve the archive entry point
- validate archives against path and manifest rules

## Package Info
- Package ID: `mdzip-core`
- Target framework: `.NET 8`
- Output assembly: `mdz.core.dll`
- Main namespace: `Mdz.Core`

## Installation (GitHub Packages)
Add the GitHub Packages source and install the package:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/mdzip-project/index.json" \
  --name github \
  --username <github-username> \
  --password <github-token> \
  --store-password-in-clear-text

dotnet add package mdzip-core --source github
```

## Quick Start
```csharp
using Mdz.Core;
using Mdz.Models;

// Create from a directory
MdzArchive.Create(
    outputPath: "book.mdz",
    sourceDirectory: "content",
    manifest: new Manifest
    {
        Spec = new ManifestSpec { Name = "markdownzip-spec", Version = "1.0.1-draft" },
        Title = "My Document",
        EntryPoint = "index.md"
    });

// Validate
var result = MdzArchive.Validate("book.mdz");
Console.WriteLine(result.IsValid);

// List entries
var files = MdzArchive.List("book.mdz");

// Add or replace a file in-place
MdzArchive.AddFile("book.mdz", "assets/notes.txt", "local-notes.txt");

// Remove a file in-place
MdzArchive.RemoveFile("book.mdz", "assets/old-image.png");

// Extract
MdzArchive.Extract("book.mdz", "out");
```

## Public API
`MdzArchive`:
- `Create(string outputPath, string sourceDirectory, Manifest? manifest = null)`
- `CreateFromFiles(string outputPath, IEnumerable<(string ArchivePath, string LocalPath)> files, Manifest? manifest = null)`
- `AddFile(string archivePath, string archiveEntryPath, string localFilePath)`
- `RemoveFile(string archivePath, string archiveEntryPath)`
- `Extract(string archivePath, string destinationDirectory)`
- `List(string archivePath)`
- `ListDetailed(string archivePath)`
- `ReadManifest(string archivePath)`
- `ResolveEntryPoint(string archivePath)`
- `Validate(string archivePath)`

Additional types:
- `ValidationResult`
- `ArchiveEntry`
- `PathValidator`
- `Manifest`, `ManifestSpec`, `ManifestAuthor`, `ManifestProducer`, `ManifestAgent`, `ManifestFile`

## Notes on Validation Behavior
- Entry paths are validated for traversal attempts and OS-reserved characters.
- `manifest.json` is optional, but if present it is parsed and validated.
- Supported manifest `spec.version` major version is `1` (when present).
- Entry point resolution order:
1. `manifest.entryPoint`
2. root `index.md`
3. exactly one root markdown file (`.md` or `.markdown`)

## Development
Build locally:

```bash
dotnet restore src/mdz-core/mdz-core.csproj
dotnet build src/mdz-core/mdz-core.csproj --configuration Release
```

Pack locally:

```bash
dotnet pack src/mdz-core/mdz-core.csproj --configuration Release -o out
```
