using MDZip.Core;

namespace MDZip.Core.Tests;

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
              "spec": { "version": "1.0.1" },
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
    public void Validate_AllowsLowerSpecMajorVersionWithWarning()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "spec": { "version": "0.9.0" },
              "title": "Doc",
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.True(result.IsValid);
            Assert.Contains(result.Warnings, w => w.Contains("spec.version", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_RejectsHigherSpecMajorVersion()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "spec": { "version": "2.0.0" },
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
              "spec": { "version": "1.0.1" },
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

    [Fact]
    public void Validate_AllowsManifestWithoutSpecVersionAndWarns()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "entryPoint": "index.md"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.True(result.IsValid);
            Assert.Contains(result.Warnings, w => w.Contains("spec.version", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_AcceptsCreatedAndModifiedObjectFormWithWhen()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "spec": { "version": "1.0.1" },
              "entryPoint": "index.md",
              "created": { "when": "2026-03-16T00:00:00Z", "by": { "name": "A" } },
              "modified": { "when": "2026-03-16T01:00:00Z" }
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

    [Fact]
    public void Validate_RejectsCreatedObjectWithoutWhen()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "spec": { "version": "1.0.1" },
              "entryPoint": "index.md",
              "created": { "by": { "name": "A" } }
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("created", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_RejectsNonMarkdownEntryPoint()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["assets/cover.png"] = "png",
            ["manifest.json"] = """
            {
              "spec": { "version": "1.0.1" },
              "entryPoint": "assets/cover.png"
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ERR_ENTRYPOINT_INVALID", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public void Validate_RejectsManifestWithTrailingComma()
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["index.md"] = "# Hello",
            ["manifest.json"] = """
            {
              "entryPoint": "index.md",
            }
            """
        });

        try
        {
            var result = MdzArchive.Validate(archive);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ERR_MANIFEST_INVALID", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(archive);
        }
    }
}
