# Changelog

## [2.0.0] - 2026-10-14

Implements MDZip spec **1.2.0** (issue #3) and runs all of its shared fixtures,
synced to the `v1.2.0` tag. A major version because several changes alter
behaviour callers may rely on; see the README's "Spec 1.2.0 behaviour" section.

### Migrating from 1.x
- Path lookups (`HasEntry`, `FindEntry`, `Read*`, `RemoveFile(s)`) are
  case-sensitive. Use the exact stored path; `FindCaseOnlyMatch` can suggest it.
- Every read can throw `ERR_ZIP_ENCRYPTED`, `ERR_ZIP_INVALID`,
  `ERR_DUPLICATE_PATH` or `ERR_PATH_CONFLICT`.
- Archives written by `Create`, `CreateFromFiles`, `BuildArchive`,
  `BuildWorkspace` and the mutation methods always contain `manifest.json`.
- Mutations can throw `ERR_ENTRYPOINT_CHANGED`; set `entryPoint` in the manifest
  to move the entry point on purpose.
- `ReadManifest` leaves out descriptive fields with invalid values instead of
  failing; an invalid manifest throws `ERR_MANIFEST_INVALID` (an
  `InvalidOperationException`), not `JsonException`.
- A manifest `entryPoint` that isn't a Markdown file, or names a missing file,
  is an error: no fallback to `index.md`.
- `spec.name` is written as `mdzip-spec` and `spec.version` as `1.2.0`
  (was `markdownzip-spec` / `1.1.0-draft`); no default `language`.
- Non-Markdown text files keep their line endings when written.

### Added
- `MdzSpec`: `PathProblem`, `EntrySetProblem`, `SamePath`, `FindPath`,
  `IsSupportFile`, `ResolveEntryPoint`, `ParseManifest` (reports duplicate
  members), `CheckManifest`, `ErrorCode`, `PathComparer`.
- `MdzLinks.ResolveLink` (§9.3–9.6: links inside, out of and into archives,
  trust boundary, embeds) and `MdzArchive.ResolveLink`.
- `MdzArchive.ResolveEntryPointStrict`, `ListSpecialFiles`, `FindCaseOnlyMatch`;
  `PathValidator.GetProblem`; `UpdateFiles(..., dropSpecialFiles)`.
- Tests for the shared fixtures: manifest cases (T-012), entry sets (T-013),
  path matching (T-009), link resolution (T-011), round trips (P-004, P-005)
  and the archive cases (T-014, T-015, T-017, P-008); a marker test that the
  fixtures match `SPEC-FIXTURES.json`.

### Changed
- Entry point resolution follows §5.5 (support files set aside; invalid vs.
  missing `entryPoint`; no silent fallback).
- Path validation rejects empty and `.` segments (`ERR_PATH_INVALID`).
- Manifest validation follows §6.4 (critical vs. descriptive fields, RFC 3339
  timestamps, semver.org `spec.version`, `null` is a wrong type).
- Mutations edit the manifest as JSON, so unknown and nested fields survive
  (§6.3); copied entries keep their timestamps and attributes (§14.1).
- Paths are written in NFC.
