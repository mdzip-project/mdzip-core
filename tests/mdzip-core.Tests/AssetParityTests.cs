using MDZip.Core;

namespace MDZip.Core.Tests;

public class AssetParityTests
{
    [Fact]
    public void InferMimeType_ClassifyAssetKind_AndPreviewabilityMatchKnownTypes()
    {
        Assert.Equal("image/png", MdzArchive.InferMimeType("assets/cover.png"));
        Assert.Equal("audio/mpeg", MdzArchive.InferMimeType("audio/theme.mp3"));
        Assert.Equal("font/woff2", MdzArchive.InferMimeType("fonts/display.woff2"));
        Assert.Equal("application/custom", MdzArchive.InferMimeType("data.unknown", "application/custom"));

        Assert.Equal("image", MdzArchive.ClassifyAssetKind("assets/cover.png"));
        Assert.Equal("audio", MdzArchive.ClassifyAssetKind("audio/theme.mp3"));
        Assert.Equal("video", MdzArchive.ClassifyAssetKind("video/clip.mp4"));
        Assert.Equal("font", MdzArchive.ClassifyAssetKind("fonts/display.woff2"));
        Assert.Equal("data", MdzArchive.ClassifyAssetKind("data/config.json"));
        Assert.Equal("other", MdzArchive.ClassifyAssetKind("archive.bin"));

        Assert.True(MdzArchive.IsPreviewableAsset("assets/cover.png"));
        Assert.True(MdzArchive.IsPreviewableAsset("data/config.json"));
        Assert.False(MdzArchive.IsPreviewableAsset("archive.bin"));
    }

    [Fact]
    public void FindOrphanedAssets_UsesEntrypointMarkdownAndManifestCover()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["docs/index.md"] = """
            # Hello
            ![Cover](../assets/cover.png)
            ![Remote](https://example.test/image.png)
            ![Missing](../assets/missing.png)
            """,
            ["docs/extra.md"] = "![Other](../assets/other.png)",
            ["assets/cover.png"] = "cover",
            ["assets/other.png"] = "other",
            ["assets/manifest-cover.png"] = "manifest cover",
            ["manifest.json"] = """
            {
              "entryPoint": "docs/index.md",
              "cover": "assets/manifest-cover.png"
            }
            """
        });

        try
        {
            var result = MdzArchive.FindOrphanedAssets(archivePath);

            Assert.Equal(["docs/index.md"], result.ScannedMarkdownPaths);
            Assert.Contains("assets/cover.png", result.ReferencedAssetPaths);
            Assert.Contains("assets/manifest-cover.png", result.ReferencedAssetPaths);
            Assert.Contains("assets/other.png", result.OrphanedAssetPaths);
            Assert.Contains(result.UnresolvedReferences, issue => issue.Reason == "unsupported-scheme");
            Assert.Contains(result.UnresolvedReferences, issue => issue.Reason == "not-found");
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void FindOrphanedAssets_RecognizesRawHtmlImgTagReferences()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = """
            <img src="assets/sized.png" width="200">
            <img src='assets/single-quoted.png' />
            ![md](assets/markdown.png)
            """,
            ["assets/sized.png"] = "sized",
            ["assets/single-quoted.png"] = "single-quoted",
            ["assets/markdown.png"] = "markdown",
            ["assets/orphan.png"] = "orphan",
            ["manifest.json"] = """
            {
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var result = MdzArchive.FindOrphanedAssets(archivePath);

            Assert.Contains("assets/sized.png", result.ReferencedAssetPaths);
            Assert.Contains("assets/single-quoted.png", result.ReferencedAssetPaths);
            Assert.Contains("assets/markdown.png", result.ReferencedAssetPaths);
            Assert.Equal(["assets/orphan.png"], result.OrphanedAssetPaths);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void FindOrphanedAssets_AllMarkdownModeScansEveryMarkdownFile()
    {
        var archivePath = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Index",
            ["extra.md"] = "![Other](assets/other.png)",
            ["assets/other.png"] = "other"
        });

        try
        {
            var result = MdzArchive.FindOrphanedAssets(
                archivePath,
                new MdzOrphanedAssetsOptions { ScanMode = "all-markdown" });

            Assert.Contains("index.md", result.ScannedMarkdownPaths);
            Assert.Contains("extra.md", result.ScannedMarkdownPaths);
            Assert.Equal(["assets/other.png"], result.ReferencedAssetPaths);
            Assert.Empty(result.OrphanedAssetPaths);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }
}
