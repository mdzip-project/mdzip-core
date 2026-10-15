# MDZip.Core
Core .NET library for working with `.mdz` archives.

> Formerly published as `mdzip-core`. The package was renamed to
> `MDZip.Core` starting with version 1.4.0 to match the `MDZip.*`
> NuGet ID scheme; `mdzip-core` is deprecated but remains listed for
> back-compat.

`MDZip.Core` provides APIs to:
- create `.mdz` archives from folders or explicit file mappings
- extract archives safely
- list archive contents
- read `manifest.json`
- resolve the archive entry point
- validate archives against path and manifest rules

## Package Info
- Package ID: `MDZip.Core`
- Target frameworks: `.NET 8`, `.NET 10`
- Output assembly: `MDZip.Core.dll`
- Main namespace: `MDZip.Core`

## Installation (NuGet.org)
Install the package from NuGet.org:

```bash
dotnet add package MDZip.Core
```

## Quick Start
```csharp
using MDZip.Core;
using MDZip.Core.Models;

// Create from a directory
MdzArchive.Create(
    outputPath: "book.mdz",
    sourceDirectory: "content",
    manifest: new Manifest
    {
        Title = "My Document",
        EntryPoint = "index.md"
    }); // spec.version, mode and timestamps are filled in

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

## Spec 1.2.0 behaviour

Version 2.0.0 implements MDZip spec [1.2.0](https://github.com/mdzip-project/mdzip-spec/releases/tag/v1.2.0) and runs its shared fixtures (`tests/mdzip-core.Tests/Fixtures`, synced with `mdzip-spec/tools/sync-fixtures.mjs --ref v1.2.0`). Things to know when upgrading from 1.x:

- **Paths are case-sensitive** after Unicode NFC normalization (§5.4.1): `Index.md` no longer finds `index.md`. `MdzArchive.FindCaseOnlyMatch` suggests the near match after a failed lookup.
- **Every read checks the container first**: encrypted entries (`ERR_ZIP_ENCRYPTED`), compression methods other than stored/DEFLATE (`ERR_ZIP_INVALID`), two entries equal after NFC (`ERR_DUPLICATE_PATH`) and a file that is also a directory (`ERR_PATH_CONFLICT`) throw.
- **Symbolic link entries** are skipped and never followed: they're not listed, can't be read or extracted, are listed by `ListSpecialFiles` and reported by `Validate`. Mutations keep them unless `dropSpecialFiles: true`.
- **Entry point** (§5.5): `manifest.entryPoint` (invalid → `ERR_ENTRYPOINT_INVALID`, missing file → `ERR_ENTRYPOINT_MISSING`, never a fallback); else root `index.md`; else the single root Markdown file once support files (README, AGENTS, LICENSE, CHANGELOG, CONTRIBUTING) are set aside. `ResolveEntryPoint` returns null when there is none; `ResolveEntryPointStrict` says why.
- **Manifests** (§6.4): an invalid critical field (`spec`, `spec.version`, `mode`, `entryPoint`) is an error; an invalid descriptive field is ignored — left out of `ReadManifest` and reported as a `Validate` warning. `null` counts as a wrong type.
- **Producers always write `manifest.json`** with `spec.version` `1.2.0`, `entryPoint` and `mode`. Edits (`AddFile`, `RemoveFile`, `UpdateFiles`, `UpdateManifest`) keep every existing manifest field, nested unknown ones included, add a manifest to an archive without one, and throw `ERR_ENTRYPOINT_CHANGED` if a change would move the entry point.
- **Line endings**: only Markdown files and `manifest.json` are normalized to LF when written (§12.2).
- **Errors** are `InvalidOperationException`s whose message starts with the spec code; `MdzSpec.ErrorCode(ex)` reads it.
- The rules are public too: `MdzSpec` (paths, entry point, manifest checks) and `MdzLinks.ResolveLink` (links inside, out of and into archives, §9.3–9.6).

## Development
Build locally:

```bash
dotnet restore src/mdzip-core/mdzip-core.csproj
dotnet build src/mdzip-core/mdzip-core.csproj --configuration Release
```

Pack locally:

```bash
dotnet pack src/mdzip-core/mdzip-core.csproj --configuration Release -o out
```
