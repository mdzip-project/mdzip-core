using MDZip.Core;
using MDZip.Core.Models;

namespace MDZip.Core.Tests;

public class ManifestEditingParityTests
{
    [Fact]
    public void CreateManifest_BuildsCanonicalManifestFromMetadata()
    {
        var manifest = MdzArchive.CreateManifest(new ManifestEditableMetadata
        {
            Title = "Created",
            EntryPoint = "index.md",
            Mode = "document",
            Author = new ManifestAuthor { Name = "Author" },
            Keywords = ["one", "two"],
        });

        Assert.Equal("Created", manifest.Title);
        Assert.Equal("index.md", manifest.EntryPoint);
        Assert.Equal("document", manifest.Mode);
        Assert.Equal("Author", manifest.Author?.Name);
        Assert.Single(manifest.Authors!);
        Assert.Equal("markdownzip-spec", manifest.Spec?.Name);
        Assert.Equal("1.1.0-draft", manifest.Spec?.Version);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Created));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Modified));
    }

    [Fact]
    public void UpdateManifest_PreservesReservedFieldsAndAppliesEditableMetadata()
    {
        var original = new Manifest
        {
            Spec = new ManifestSpec { Name = "custom-spec", Version = "1.0.1" },
            Title = "Old",
            EntryPoint = "docs/start.md",
            Mode = "project",
            Created = "2026-01-01T00:00:00.0000000Z",
            Modified = "2026-01-01T00:00:00.0000000Z",
        };

        var updated = MdzArchive.UpdateManifest(
            original,
            new ManifestEditableMetadata
            {
                Title = "New",
                Language = "en",
            },
            new ManifestUpdateOptions { RefreshModified = false });

        Assert.Equal("New", updated.Title);
        Assert.Equal("en", updated.Language);
        Assert.Equal("custom-spec", updated.Spec?.Name);
        Assert.Equal("1.0.1", updated.Spec?.Version);
        Assert.Equal("docs/start.md", updated.EntryPoint);
        Assert.Equal("project", updated.Mode);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", updated.Created);
        Assert.Equal("2026-01-01T00:00:00.0000000Z", updated.Modified);
        Assert.Equal("Old", original.Title);
    }

    [Fact]
    public void SplitManifestMetadata_SeparatesReservedAndEditableFields()
    {
        var manifest = new Manifest
        {
            Spec = new ManifestSpec { Version = "1.0.1" },
            Producer = new ManifestProducer { Core = new ManifestAgent { Name = "core" } },
            Title = "Doc",
            EntryPoint = "index.md",
            Mode = "project",
            Created = "2026-01-01T00:00:00Z",
            Modified = "2026-01-02T00:00:00Z",
            Cover = "assets/cover.png",
        };

        var split = MdzArchive.SplitManifestMetadata(manifest);

        Assert.Equal("1.0.1", split.Reserved.Spec?.Version);
        Assert.Equal("core", split.Reserved.Producer?.Core?.Name);
        Assert.Equal("index.md", split.Reserved.EntryPoint);
        Assert.Equal("project", split.Reserved.Mode);
        Assert.Equal("Doc", split.Editable.Title);
        Assert.Equal("assets/cover.png", split.Editable.Cover);
    }

    [Fact]
    public void UpdateManifest_ArchiveRewritesManifestAtomically()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["docs/start.md"] = "# Start",
            ["manifest.json"] = """
            {
              "spec": { "version": "1.0.1" },
              "title": "Old",
              "mode": "project",
              "entryPoint": "docs/start.md",
              "created": "2026-01-01T00:00:00.0000000Z",
              "modified": "2026-01-01T00:00:00.0000000Z"
            }
            """
        });

        try
        {
            var written = MdzArchive.UpdateManifest(
                archive,
                new ManifestEditableMetadata { Title = "Renamed" },
                new ManifestUpdateOptions { RefreshModified = false });
            var readBack = MdzArchive.ReadManifest(archive);

            Assert.Equal("Renamed", written.Title);
            Assert.Equal("Renamed", readBack?.Title);
            Assert.Equal("docs/start.md", readBack?.EntryPoint);
            Assert.Equal("project", readBack?.Mode);
            Assert.Equal("2026-01-01T00:00:00.0000000Z", readBack?.Modified);
            Assert.True(MdzArchive.HasEntry(archive, "docs/start.md"));
        }
        finally
        {
            File.Delete(archive);
        }
    }
}
