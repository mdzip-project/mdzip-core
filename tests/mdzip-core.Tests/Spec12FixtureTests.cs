using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MDZip.Core;
using MDZip.Core.Models;

namespace MDZip.Core.Tests;

/// <summary>
/// The spec 1.2.0 shared fixtures not covered elsewhere: manifest validation (T-012), entry sets
/// (T-013), path matching (T-009), link resolution (T-011), round trips (P-004, P-005) and the
/// generated archives (T-014, T-015, T-017, P-008).
/// </summary>
public class Spec12FixtureTests
{
    private static JsonNode LoadNode(string name) =>
        JsonNode.Parse(File.ReadAllText(TestFixtureHelper.FixturePath(name)))!;

    private static string TempCopy(string fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}.mdz");
        File.Copy(TestFixtureHelper.FixturePath(fixture), path);
        return path;
    }

    private static string TempFile(string text) => TempFile(Encoding.UTF8.GetBytes(text));

    private static string TempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void T012_ManifestCases()
    {
        foreach (var c in LoadNode("manifest-cases.json")["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var text = c["manifestJson"]!.GetValue<string>();
            var expected = c["expected"]!;
            if (!expected["valid"]!.GetValue<bool>())
            {
                var ex = Assert.ThrowsAny<InvalidOperationException>(() => MdzSpec.CheckManifest(MdzSpec.ParseManifest(text).Manifest));
                Assert.True(MdzSpec.ErrorCode(ex) == expected["errorCode"]!.GetValue<string>(), $"{name}: {ex.Message}");
                continue;
            }

            var (manifest, duplicates) = MdzSpec.ParseManifest(text);
            var ignored = MdzSpec.CheckManifest(manifest).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var expectedIgnored = (expected["ignoredFields"]?.AsArray().Select(f => f!.GetValue<string>()) ?? [])
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.True(expectedIgnored.SequenceEqual(ignored), $"{name}: ignored [{string.Join(", ", ignored)}]");
            Assert.True((duplicates.Count > 0) == (expected["duplicateMembers"]?.GetValue<bool>() ?? false), name);
            foreach (var (pointer, value) in expected["values"]?.AsObject() ?? [])
            {
                JsonNode? node = manifest;
                foreach (var key in pointer.Split('/').Skip(1))
                    node = node?[key];
                Assert.True(JsonNode.DeepEquals(node, value), $"{name}: {pointer}");
            }
        }
    }

    [Fact]
    public void T012_ReadManifest_DropsIgnoredFields_AndValidateReportsThem()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# hi\n",
            ["manifest.json"] = """{"title": 5, "language": "en", "title": "", "x-tool": {"keep": true}}""",
        });
        try
        {
            var manifest = MdzArchive.ReadManifest(archive)!;
            Assert.Null(manifest.Title);
            Assert.Equal("en", manifest.Language);
            Assert.True(manifest.AdditionalFields!.ContainsKey("x-tool"));
            var result = MdzArchive.Validate(archive);
            Assert.True(result.IsValid);
            Assert.Contains(result.Warnings, w => w.Contains("'title'", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, w => w.Contains("repeats", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void T013_EntrySetCases()
    {
        foreach (var c in LoadNode("entry-set-cases.json")["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var entries = c["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
            var problem = MdzSpec.EntrySetProblem(entries);
            Assert.True((problem is null) == c["valid"]!.GetValue<bool>(), name);
            if (problem is not null)
                Assert.Equal(c["errorCode"]!.GetValue<string>(), problem.Code);

            // The reader applies the same checks to real ZIP files.
            var path = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}.mdz");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    var created = zip.CreateEntry(entry);
                    if (!entry.EndsWith('/'))
                    {
                        using var writer = new StreamWriter(created.Open());
                        writer.Write("# x\n");
                    }
                }
                using var index = new StreamWriter(zip.CreateEntry(entries.Contains("index.md") ? "other.md" : "index.md").Open());
                index.Write("# i\n");
            }

            try
            {
                if (problem is null)
                {
                    MdzArchive.List(path);
                }
                else
                {
                    var ex = Assert.ThrowsAny<InvalidOperationException>(() => MdzArchive.List(path));
                    Assert.Equal(problem.Code, MdzSpec.ErrorCode(ex));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void T009_PathMatchingCases()
    {
        foreach (var c in LoadNode("path-matching-cases.json").AsArray())
        {
            Assert.True(
                MdzSpec.SamePath(c!["entry"]!.GetValue<string>(), c["lookup"]!.GetValue<string>()) == c["matches"]!.GetValue<bool>(),
                c["name"]!.GetValue<string>());
        }
    }

    private sealed class FixtureHost(JsonNode model, JsonNode? context) : IMdzLinkHost
    {
        private readonly HashSet<string> files = model["files"]!.AsArray().Select(f => f!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> directories = model["directories"]!.AsArray().Select(f => f!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        private readonly JsonObject archives = model["archives"]!.AsObject();

        public string? Kind(string path) =>
            archives.ContainsKey(path) ? "archive" : files.Contains(path) ? "file" : directories.Contains(path) ? "directory" : null;

        public string? FindEntry(string archive, string entry) =>
            archives[archive] is { } a ? MdzSpec.FindPath(a["entries"]!.AsArray().Select(e => e!.GetValue<string>()), entry) : null;

        public string? EntryPoint(string archive) => archives[archive]?["entryPoint"]?.GetValue<string>();

        public bool HostLocation => context?["hostLocation"]?.GetValue<bool>() ?? true;
        public bool OutsideLinks => context?["outsideLinks"]?.GetValue<bool>() ?? true;
        public bool IntoArchiveLinks => context?["intoArchiveLinks"]?.GetValue<bool>() ?? true;

        public string? TrustRoot => context is JsonObject o && o.ContainsKey("trustRoot")
            ? o["trustRoot"]?.GetValue<string>()
            : model["trustRoot"]?.GetValue<string>() ?? string.Empty;

        public IReadOnlyCollection<string> AllowedPaths =>
            context?["allowedPaths"]?.AsArray().Select(p => p!.GetValue<string>()).ToList() ?? [];

        public bool LoadOutsideResources => context?["loadOutsideResources"]?.GetValue<bool>() ?? false;
    }

    [Fact]
    public void T011_LinkResolutionCases()
    {
        var fixture = LoadNode("link-resolution-cases.json");
        foreach (var c in fixture["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var from = c["from"]!;
            MdzLinkSource source = from["archive"] is not null
                ? new MdzArchiveLinkSource(from["archive"]!.GetValue<string>(), from["path"]!.GetValue<string>())
                : new MdzHostLinkSource(from["host"]!.GetValue<string>());
            var target = MdzLinks.ResolveLink(
                source,
                c["link"]!.GetValue<string>(),
                new FixtureHost(fixture["host"]!, c["context"]),
                c["use"]?.GetValue<string>() ?? "navigate");

            var expected = c["expected"]!;
            Assert.True(expected["kind"]!.GetValue<string>() == target.Kind, $"{name}: kind {target.Kind}");
            foreach (var (field, actual) in new[]
            {
                ("entry", target.Entry), ("path", target.Path), ("archive", target.Archive),
                ("fragment", target.Fragment), ("reason", target.Reason),
            })
            {
                Assert.True(expected[field]?.GetValue<string>() == actual, $"{name}: {field} was '{actual}'");
            }
        }
    }

    [Fact]
    public void T011_ResolveLink_InsideAnArchiveWithoutAHost()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# hi\n",
            ["docs/a b.md"] = "# a\n",
        });
        try
        {
            Assert.Equal(new MdzLinkTarget("internal", Entry: "docs/a b.md", Fragment: "top"), MdzArchive.ResolveLink(archive, "index.md", "docs/a%20b.md#top"));
            Assert.Equal(new MdzLinkTarget("unresolved", Reason: "no-host-location"), MdzArchive.ResolveLink(archive, "docs/a b.md", "../../Notes.md"));
            Assert.Equal(new MdzLinkTarget("url"), MdzArchive.ResolveLink(archive, "index.md", "https://example.com"));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void P004_P005_RoundTripFlows_SavedTwice()
    {
        var expectations = LoadNode("round-trip-expectations.json");
        foreach (var flow in expectations["flows"]!.AsArray())
        {
            var flowName = flow!["name"]!.GetValue<string>();
            var archive = TempCopy("round-trip.mdz");
            try
            {
                for (var round = 1; round <= 2; round++)
                {
                    RunFlow(flowName, archive, round);
                    CheckRoundTrip(expectations, flow, archive, $"{flowName} (save {round})");
                }
            }
            finally
            {
                File.Delete(archive);
            }
        }
    }

    private static void RunFlow(string flow, string archive, int round)
    {
        switch (flow)
        {
            case "open and save without an edit":
                MdzArchive.UpdateFiles(archive, []);
                break;
            case "change a recognized manifest field":
                MdzArchive.UpdateManifest(archive, new ManifestEditableMetadata { Title = "Edited title" });
                break;
            case "edit document content":
                MdzArchive.AddFile(archive, "index.md", TempFile(MdzArchive.ReadText(archive, "index.md") + "\nEdited.\n"));
                break;
            case "add a resource":
                MdzArchive.AddFile(archive, $"images/added-{round}.png", TempFile([0x89, 0x50, 0x4e, 0x47]));
                break;
            case "remove a recognized resource":
                if (round == 2)
                {
                    MdzArchive.UpdateManifest(archive, new ManifestEditableMetadata { Title = "Second save" });
                    break;
                }
                // Remove the file and its link, as a tool would (the flow lists index.md in changes).
                var index = MdzArchive.ReadText(archive, "index.md").Replace("See [the guide](docs/guide.md).", string.Empty);
                MdzArchive.UpdateFiles(archive, [new ArchiveWriteSpec("index.md", TempFile(index))], ["docs/guide.md"]);
                break;
            default:
                throw new InvalidOperationException($"unknown flow {flow}");
        }
    }

    private static void CheckRoundTrip(JsonNode expectations, JsonNode flow, string archive, string label)
    {
        var manifest = JsonNode.Parse(MdzArchive.ReadText(archive, "manifest.json"))!;
        foreach (var item in expectations["preservedManifestValues"]!.AsArray())
        {
            JsonNode? node = manifest;
            foreach (var key in item!["pointer"]!.GetValue<string>().Split('/').Skip(1))
                node = node?[key.Replace("~1", "/").Replace("~0", "~")];
            Assert.True(JsonNode.DeepEquals(node, item["value"]), $"{label}: {item["pointer"]}");
        }

        var changes = flow["changes"]?.AsArray().Select(c => c!.GetValue<string>()).ToHashSet() ?? [];
        var removes = flow["removes"]?.AsArray().Select(c => c!.GetValue<string>()).ToHashSet() ?? [];
        foreach (var removed in removes)
            Assert.False(MdzArchive.HasEntry(archive, removed), $"{label}: {removed} should be removed");

        foreach (var entry in expectations["preservedEntries"]!.AsArray())
        {
            var path = entry!["path"]!.GetValue<string>();
            if (changes.Contains(path) || removes.Contains(path))
                continue;
            var bytes = MdzArchive.ReadBytes(archive, path);
            Assert.True(bytes.Length == entry["bytes"]!.GetValue<int>(), $"{label}: {path} length");
            Assert.True(
                Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry["sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase),
                $"{label}: {path} sha256");
        }
    }

    [Fact]
    public void ArchiveCases_T017_SymbolicLinks_AreSkippedNeverFollowedAndReported()
    {
        var c = LoadNode("archive-cases.json")["cases"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == "symlink-entries")!;
        var archive = TestFixtureHelper.FixturePath(c["archive"]!.GetValue<string>());
        var symlinks = c["symlinks"]!.AsObject().Select(p => p.Key).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(c["expectedEntryPoint"]!.GetValue<string>(), MdzArchive.ResolveEntryPoint(archive));
        Assert.Equal(symlinks, MdzArchive.ListSpecialFiles(archive).OrderBy(p => p, StringComparer.Ordinal));
        Assert.DoesNotContain(MdzArchive.List(archive), p => symlinks.Contains(p));
        foreach (var link in c["links"]!.AsArray())
        {
            var href = link!["href"]!.GetValue<string>();
            var target = MdzArchive.ResolveLink(archive, "index.md", href);
            if (link["expected"]!.GetValue<string>() == "resolved")
                Assert.Equal(new MdzLinkTarget("internal", Entry: link["target"]!.GetValue<string>()), target);
            else
                Assert.True(target.Kind == "unresolved", href);
        }

        Assert.Throws<FileNotFoundException>(() => MdzArchive.ReadText(archive, "shortcut.md"));
        var warnings = MdzArchive.Validate(archive).Warnings;
        foreach (var name in symlinks)
            Assert.Contains(warnings, w => w.Contains($"'{name}'", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchiveCases_P008_SavingKeepsSymbolicLinksUnlessDropped()
    {
        var kept = TempCopy("symlink-entries.mdz");
        var dropped = TempCopy("symlink-entries.mdz");
        try
        {
            MdzArchive.AddFile(kept, "notes.md", TempFile("# Notes\n\nEdited.\n"));
            Assert.Equal(3, MdzArchive.ListSpecialFiles(kept).Count);
            Assert.NotEmpty(MdzArchive.Validate(kept).Warnings);

            MdzArchive.UpdateFiles(dropped, [new ArchiveWriteSpec("notes.md", TempFile("# Notes\n"))], dropSpecialFiles: true);
            Assert.Empty(MdzArchive.ListSpecialFiles(dropped));
        }
        finally
        {
            File.Delete(kept);
            File.Delete(dropped);
        }
    }

    [Fact]
    public void ArchiveCases_T014_UnsupportedCompression_IsZipInvalid()
    {
        var archive = TestFixtureHelper.FixturePath("unsupported-compression.mdz");
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => MdzArchive.ReadText(archive, "index.md"));
        Assert.Equal("ERR_ZIP_INVALID", MdzSpec.ErrorCode(ex));
        var result = MdzArchive.Validate(archive);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith("ERR_ZIP_INVALID", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchiveCases_T015_ProjectMode()
    {
        var c = LoadNode("archive-cases.json")["cases"]!.AsArray().First(x => x!["id"]!.GetValue<string>() == "project-mode")!;
        var archive = TestFixtureHelper.FixturePath(c["archive"]!.GetValue<string>());
        Assert.Equal("project", MdzArchive.ResolveMode(archive));
        Assert.Equal(c["expectedEntryPoint"]!.GetValue<string>(), MdzArchive.ResolveEntryPoint(archive));
        var documents = MdzArchive.OpenWorkspace(archive).Documents.Select(d => d.Path).OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal(c["documents"]!.AsArray().Select(d => d!.GetValue<string>()).OrderBy(p => p, StringComparer.Ordinal), documents);
    }

    [Fact]
    public void Producers_AlwaysWriteACompleteManifest_AndKeepTheEntryPoint()
    {
        var output = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}.mdz");
        var plain = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string> { ["notes.md"] = "# Notes\n" });
        var two = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string> { ["index.md"] = "# i\n", ["other.md"] = "# o\n" });
        try
        {
            MdzArchive.CreateFromFiles(output, [("notes.md", TempFile("# Notes\n")), ("AGENTS.md", TempFile("# Agents\n"))]);
            var manifest = JsonNode.Parse(MdzArchive.ReadText(output, "manifest.json"))!.AsObject();
            Assert.Equal("1.2.0", manifest["spec"]!["version"]!.GetValue<string>());
            Assert.Equal("notes.md", manifest["entryPoint"]!.GetValue<string>());
            Assert.Equal("document", manifest["mode"]!.GetValue<string>());
            Assert.DoesNotContain(manifest, p => p.Value is null);
            Assert.False(manifest.ContainsKey("language"));

            // Modifying a manifest-less archive adds a manifest that keeps the same entry point.
            MdzArchive.AddFile(plain, "AGENTS.md", TempFile("# Agents\n"));
            Assert.Equal("notes.md", MdzArchive.ReadManifest(plain)!.EntryPoint);

            // ...and a change that would silently move the entry point is refused.
            var ex = Assert.ThrowsAny<InvalidOperationException>(() => MdzArchive.RemoveFile(two, "index.md"));
            Assert.Equal("ERR_ENTRYPOINT_CHANGED", MdzSpec.ErrorCode(ex));
        }
        finally
        {
            File.Delete(output);
            File.Delete(plain);
            File.Delete(two);
        }
    }
}
