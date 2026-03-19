using System.IO.Compression;
using System.Text.Json;
using Mdz.Core;

namespace mdz_core.Tests;

public class ArchiveMutationTests
{
    [Fact]
    public void AddFile_AddsNewEntry()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello"
        });
        var localFile = CreateTempFile("asset content");

        try
        {
            MdzArchive.AddFile(archivePath, "assets/note.txt", localFile);

            var files = MdzArchive.List(archivePath);
            Assert.Contains("assets/note.txt", files);
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(localFile);
        }
    }

    [Fact]
    public void AddFile_ReplacesExistingEntry()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Old"
        });
        var localFile = CreateTempFile("# New");

        try
        {
            MdzArchive.AddFile(archivePath, "index.md", localFile);

            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry("index.md");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open());
            var content = reader.ReadToEnd();
            Assert.Equal("# New", content);
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(localFile);
        }
    }

    [Fact]
    public void AddFile_ManifestReplacementThatBreaksEntrypoint_Throws()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """{ "entryPoint": "index.md" }"""
        });
        var invalidManifest = CreateTempFile("""{ "entryPoint": "missing.md" }""");

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MdzArchive.AddFile(archivePath, "manifest.json", invalidManifest));
            Assert.Contains("entryPoint", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(invalidManifest);
        }
    }

    [Fact]
    public void AddFile_ManifestReplacementWithInvalidJson_Throws()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """{ "entryPoint": "index.md" }"""
        });
        var invalidManifest = CreateTempFile("{ invalid json");

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MdzArchive.AddFile(archivePath, "manifest.json", invalidManifest));
            Assert.Contains("invalid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Mutation should be atomic; original manifest remains untouched.
            var manifest = MdzArchive.ReadManifest(archivePath);
            Assert.Equal("index.md", manifest?.EntryPoint);
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(invalidManifest);
        }
    }

    [Fact]
    public void AddFile_ManifestReplacementWithoutSpecVersion_PopulatesSpecVersion()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """{ "entryPoint": "index.md" }"""
        });
        var replacementManifest = CreateTempFile("""{ "entryPoint": "index.md", "title": "Updated" }""");

        try
        {
            MdzArchive.AddFile(archivePath, "manifest.json", replacementManifest);

            var manifest = MdzArchive.ReadManifest(archivePath);
            Assert.NotNull(manifest);
            Assert.NotNull(manifest!.Spec);
            Assert.Equal("1.0.1-draft", manifest.Spec!.Version);
            Assert.False(string.IsNullOrWhiteSpace(manifest.Modified));
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(replacementManifest);
        }
    }

    [Fact]
    public void AddFile_ManifestReplacementPreservesCreatedByField()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """{ "entryPoint": "index.md" }"""
        });
        var replacementManifest = CreateTempFile("""
        {
          "entryPoint": "index.md",
          "created": { "when": "2026-01-01T00:00:00Z", "by": { "name": "Author" } }
        }
        """);

        try
        {
            MdzArchive.AddFile(archivePath, "manifest.json", replacementManifest);

            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry("manifest.json");
            Assert.NotNull(entry);
            using var reader = new StreamReader(entry!.Open());
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var created = doc.RootElement.GetProperty("created");
            Assert.Equal(JsonValueKind.Object, created.ValueKind);
            Assert.Equal("2026-01-01T00:00:00Z", created.GetProperty("when").GetString());
            Assert.Equal("Author", created.GetProperty("by").GetProperty("name").GetString());
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(replacementManifest);
        }
    }

    [Fact]
    public void RemoveFile_RemovesEntry()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["assets/one.txt"] = "1"
        });

        try
        {
            MdzArchive.RemoveFile(archivePath, "assets/one.txt");

            var files = MdzArchive.List(archivePath);
            Assert.DoesNotContain("assets/one.txt", files);
            Assert.Contains("index.md", files);
        }
        finally
        {
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public void RemoveFile_MissingEntry_ThrowsFileNotFound()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello"
        });

        try
        {
            Assert.Throws<FileNotFoundException>(() => MdzArchive.RemoveFile(archivePath, "missing.txt"));
        }
        finally
        {
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public void RemoveFile_RejectsWhenEntrypointBecomesUnresolved()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello"
        });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => MdzArchive.RemoveFile(archivePath, "index.md"));
            Assert.Contains("entry point", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public void AddFile_RefreshesManifestModified_ForExistingManifest()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "entryPoint": "index.md",
              "modified": "2000-01-01T00:00:00.0000000Z"
            }
            """
        });
        var localFile = CreateTempFile("new");

        try
        {
            MdzArchive.AddFile(archivePath, "assets/new.txt", localFile);
            var manifest = MdzArchive.ReadManifest(archivePath);
            Assert.NotNull(manifest);
            Assert.NotEqual("2000-01-01T00:00:00.0000000Z", manifest!.Modified);
            Assert.False(string.IsNullOrWhiteSpace(manifest.Modified));
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(localFile);
        }
    }

    [Fact]
    public void AddFile_RefreshesManifestModified_InjectsSpecVersionWhenAbsent()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """{ "entryPoint": "index.md" }"""
        });
        var localFile = CreateTempFile("new content");

        try
        {
            MdzArchive.AddFile(archivePath, "assets/new.txt", localFile);
            var manifest = MdzArchive.ReadManifest(archivePath);
            Assert.NotNull(manifest);
            Assert.False(string.IsNullOrWhiteSpace(manifest!.Spec?.Version));
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(localFile);
        }
    }

    [Fact]
    public void AddFile_InvalidExistingManifest_Throws()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = "{ invalid json"
        });
        var localFile = CreateTempFile("new");

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MdzArchive.AddFile(archivePath, "assets/new.txt", localFile));
            Assert.Contains("Existing manifest.json is invalid", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(archivePath);
            SafeDelete(localFile);
        }
    }

    [Fact]
    public void RemoveFile_RefreshesManifestModified_ForExistingManifest()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["assets/old.txt"] = "old",
            ["manifest.json"] = """
            {
              "entryPoint": "index.md",
              "modified": {
                "when": "2000-01-01T00:00:00.0000000Z",
                "by": { "name": "tester" }
              }
            }
            """
        });

        try
        {
            MdzArchive.RemoveFile(archivePath, "assets/old.txt");

            // Verify modified timestamp was refreshed and object-shape metadata was preserved.
            using var archive = ZipFile.OpenRead(archivePath);
            var manifestEntry = archive.GetEntry("manifest.json");
            Assert.NotNull(manifestEntry);
            using var reader = new StreamReader(manifestEntry!.Open());
            var manifestJson = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(manifestJson);
            var modified = doc.RootElement.GetProperty("modified");
            Assert.Equal(JsonValueKind.Object, modified.ValueKind);
            Assert.NotEqual("2000-01-01T00:00:00.0000000Z", modified.GetProperty("when").GetString());
            Assert.Equal("tester", modified.GetProperty("by").GetProperty("name").GetString());
        }
        finally
        {
            SafeDelete(archivePath);
        }
    }

    [Fact]
    public void RemoveFile_InvalidExistingManifest_Throws()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["assets/old.txt"] = "old",
            ["manifest.json"] = "{ invalid json"
        });

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MdzArchive.RemoveFile(archivePath, "assets/old.txt"));
            Assert.Contains("Existing manifest.json is invalid", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete(archivePath);
        }
    }

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mdz-core-file-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, content);
        return path;
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
