using Mdz.Core;

namespace mdz_core.Tests;

public class SpecConformanceTests
{
    [Fact]
    public void Validate_AllowsMissingManifestCoverWithWarning()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "mdz": "1.0.0",
              "title": "Doc",
              "entryPoint": "index.md",
              "cover": "assets/images/cover.png"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.True(result.IsValid);
            Assert.Contains(result.Warnings, w => w.Contains("cover", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_AllowsLowerManifestMajorVersionWithWarning()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "mdz": "0.9.0",
              "title": "Doc",
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.True(result.IsValid);
            Assert.Contains(result.Warnings, w => w.Contains("mdz", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_RejectsHigherManifestMajorVersion()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "mdz": "2.0.0",
              "title": "Doc",
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ERR_VERSION_UNSUPPORTED", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_AllowsManifestWithoutTitle()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "mdz": "1.0.0",
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.True(result.IsValid);
        }
        finally
        {
            File.Delete(archive);
        }
    }
}
