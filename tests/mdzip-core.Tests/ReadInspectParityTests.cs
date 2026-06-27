using System.IO.Compression;
using System.Text;
using MDZip.Core;

namespace MDZip.Core.Tests;

public class ReadInspectParityTests
{
    [Fact]
    public void HasEntry_AndFindEntry_UseCaseInsensitiveLookupAndDirectoryFallback()
    {
        var archive = CreateArchive(
            textEntries: new Dictionary<string, string>
            {
                ["Index.md"] = "# Hello",
                ["docs/Chapter.md"] = "# Chapter",
            },
            directoryEntries: ["assets/"]);

        try
        {
            Assert.True(MdzArchive.HasEntry(archive, "/docs/chapter.md"));
            Assert.True(MdzArchive.HasEntry(archive, "assets"));

            var entry = MdzArchive.FindEntry(archive, "DOCS/CHAPTER.MD");
            Assert.NotNull(entry);
            Assert.Equal("docs/Chapter.md", entry.Path);
            Assert.True(entry.IsMarkdown);
            Assert.False(entry.IsDirectory);

            var directory = MdzArchive.FindEntry(archive, "assets");
            Assert.NotNull(directory);
            Assert.True(directory.IsDirectory);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void TypedReads_ReturnTextBytesBase64AndDataUri()
    {
        var archive = CreateArchive(
            textEntries: new Dictionary<string, string>
            {
                ["index.md"] = "# Hello\r\n",
            },
            binaryEntries: new Dictionary<string, byte[]>
            {
                ["assets/cover.png"] = [0x89, 0x50, 0x4e, 0x47],
                ["assets/raw.bin"] = [0x01, 0x02, 0x03],
            });

        try
        {
            Assert.Equal("# Hello\r\n", MdzArchive.ReadText(archive, "INDEX.md"));
            Assert.Equal([0x89, 0x50, 0x4e, 0x47], MdzArchive.ReadBytes(archive, "/assets/cover.png"));
            Assert.Equal("AQID", MdzArchive.ReadBase64(archive, "assets/raw.bin"));
            Assert.Equal("data:image/png;base64,iVBORw==", MdzArchive.ReadDataUri(archive, "assets/cover.png"));
            Assert.Equal("data:application/custom;base64,AQID", MdzArchive.ReadDataUri(archive, "assets/raw.bin", " application/custom "));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void ReadText_RejectsDirectoriesAndMissingEntries()
    {
        var archive = CreateArchive(
            textEntries: new Dictionary<string, string>
            {
                ["index.md"] = "# Hello",
            },
            directoryEntries: ["assets/"]);

        try
        {
            Assert.Throws<InvalidOperationException>(() => MdzArchive.ReadText(archive, "assets"));
            Assert.Throws<FileNotFoundException>(() => MdzArchive.ReadText(archive, "missing.md"));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void ResolveMode_ReturnsManifestModeOrDocumentDefault()
    {
        var projectArchive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "mode": "project",
              "entryPoint": "index.md"
            }
            """
        });
        var defaultArchive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
        });

        try
        {
            Assert.Equal("project", MdzArchive.ResolveMode(projectArchive));
            Assert.Equal("document", MdzArchive.ResolveMode(defaultArchive));
        }
        finally
        {
            File.Delete(projectArchive);
            File.Delete(defaultArchive);
        }
    }

    [Fact]
    public void BuildPathTree_SortsDirectoriesBeforeFilesAndInfersFolders()
    {
        var tree = MdzArchive.BuildPathTree([
            "zeta.md",
            "docs/tutorial/intro.md",
            "assets/cover.png",
            "docs/readme.md",
            "alpha.md",
        ]);

        Assert.Collection(
            tree,
            node => Assert.Equal("assets", node.Name),
            node => Assert.Equal("docs", node.Name),
            node => Assert.Equal("alpha.md", node.Name),
            node => Assert.Equal("zeta.md", node.Name));

        var docs = tree[1];
        Assert.True(docs.IsDirectory);
        Assert.Collection(
            docs.Children,
            node => Assert.Equal("tutorial", node.Name),
            node => Assert.Equal("readme.md", node.Name));
        Assert.Equal("docs/tutorial/intro.md", docs.Children[0].Children[0].Path);
    }

    private static string CreateArchive(
        IDictionary<string, string>? textEntries = null,
        IDictionary<string, byte[]>? binaryEntries = null,
        IEnumerable<string>? directoryEntries = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}.mdz");

        using var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create);

        foreach (var directoryEntry in directoryEntries ?? [])
            archive.CreateEntry(directoryEntry);

        foreach (var (entryPath, content) in textEntries ?? new Dictionary<string, string>())
        {
            var entry = archive.CreateEntry(entryPath);
            using var stream = new StreamWriter(entry.Open(), Encoding.UTF8);
            stream.Write(content);
        }

        foreach (var (entryPath, content) in binaryEntries ?? new Dictionary<string, byte[]>())
        {
            var entry = archive.CreateEntry(entryPath);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return tempPath;
    }
}
