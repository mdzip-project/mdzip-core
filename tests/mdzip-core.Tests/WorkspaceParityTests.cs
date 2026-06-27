using MDZip.Core;

namespace MDZip.Core.Tests;

public class WorkspaceParityTests
{
    [Fact]
    public void OpenWorkspace_ReturnsDocumentsAssetsValidationAndOrphanAnalysis()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello\n![Cover](assets/cover.png)",
            ["chapter.md"] = "# Chapter",
            ["assets/cover.png"] = "cover",
            ["assets/orphan.png"] = "orphan",
            ["manifest.json"] = """
            {
              "title": "Workspace",
              "mode": "project",
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var workspace = MdzArchive.OpenWorkspace(
                archivePath,
                new MdzOpenWorkspaceOptions { IncludeOrphanedAssetAnalysis = true });

            Assert.Equal("Workspace", workspace.Title);
            Assert.Equal("project", workspace.Mode);
            Assert.Equal("index.md", workspace.EntryPoint);
            Assert.True(workspace.Validation.IsValid);
            Assert.Equal(2, workspace.Documents.Count);
            Assert.Contains(workspace.Documents, document => document.IsEntryPoint && document.Title == "Workspace");
            Assert.Contains(workspace.Assets, asset => asset.Path == "assets/cover.png" && asset.Kind == "image");
            Assert.Contains("assets/orphan.png", workspace.OrphanedAssets?.OrphanedAssetPaths ?? []);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void CreateWorkspaceAssetFromFile_AndExportWorkspaceAsset_RoundTripBytes()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"mdzip-core-asset-{Guid.NewGuid():N}.png");
        var outputPath = Path.Combine(Path.GetTempPath(), $"mdzip-core-asset-out-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

        try
        {
            var asset = MdzArchive.CreateWorkspaceAssetFromFile(sourcePath, "media/image.png");
            MdzArchive.ExportWorkspaceAsset(asset, outputPath);

            Assert.Equal("media/image.png", asset.Path);
            Assert.Equal("image/png", asset.MimeType);
            Assert.Equal("image", asset.Kind);
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(outputPath));
        }
        finally
        {
            SafeDelete(sourcePath);
            SafeDelete(outputPath);
        }
    }

    [Fact]
    public void BuildWorkspace_WritesDocumentsAssetsAndManifest()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"mdzip-core-workspace-{Guid.NewGuid():N}.mdz");
        var workspace = new MdzWorkspace(
            "Built Workspace",
            "project",
            null,
            "index.md",
            [
                new MdzWorkspaceDocument("index.md", "Index", "# Index", true),
                new MdzWorkspaceDocument("docs/page.md", "Page", "# Page", false),
            ],
            [
                new MdzWorkspaceAsset("assets/cover.png", "cover.png", 4, "image/png", "image", true, [1, 2, 3, 4]),
            ],
            new ValidationResult { IsValid = true },
            null);

        try
        {
            var result = MdzArchive.BuildWorkspace(outputPath, workspace);

            Assert.Equal("index.md", result.ResolvedEntryPoint);
            Assert.Equal("# Page", MdzArchive.ReadText(outputPath, "docs/page.md"));
            Assert.Equal([1, 2, 3, 4], MdzArchive.ReadBytes(outputPath, "assets/cover.png"));
            var manifest = MdzArchive.ReadManifest(outputPath);
            Assert.Equal("Built Workspace", manifest?.Title);
            Assert.Equal("project", manifest?.Mode);
            Assert.Equal("index.md", manifest?.EntryPoint);
        }
        finally
        {
            SafeDelete(outputPath);
        }
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
