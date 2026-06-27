using MDZip.Core;
using MDZip.Core.Models;

namespace MDZip.Core.Tests;

public class ValidationParityTests
{
    [Fact]
    public void GetValidationStatus_ReturnsValidWarningOrError()
    {
        Assert.Equal("valid", MdzArchive.GetValidationStatus(new ValidationResult
        {
            IsValid = true,
            Errors = [],
            Warnings = [],
        }));
        Assert.Equal("warning", MdzArchive.GetValidationStatus(new ValidationResult
        {
            IsValid = true,
            Errors = [],
            Warnings = ["warn"],
        }));
        Assert.Equal("error", MdzArchive.GetValidationStatus(new ValidationResult
        {
            IsValid = false,
            Errors = ["err"],
            Warnings = [],
        }));
        Assert.Equal("error", MdzArchive.GetValidationStatus(new ValidationResult
        {
            IsValid = false,
            Errors = [],
            Warnings = ["warn"],
        }));
    }

    [Fact]
    public void ValidateManifest_ReturnsWarningsForMissingSpecVersion()
    {
        var result = MdzArchive.ValidateManifest(new Manifest
        {
            EntryPoint = "index.md",
        });

        Assert.True(result.IsValid);
        Assert.Equal("warning", MdzArchive.GetValidationStatus(result));
        Assert.Contains(result.Warnings, warning => warning.Contains("spec.version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateManifest_RejectsUnsupportedSpecMajor()
    {
        var result = MdzArchive.ValidateManifest(new Manifest
        {
            Spec = new ManifestSpec { Version = "2.0.0" },
            EntryPoint = "index.md",
        });

        Assert.False(result.IsValid);
        Assert.Equal("error", MdzArchive.GetValidationStatus(result));
        Assert.Contains(result.Errors, error => error.Contains("ERR_VERSION_UNSUPPORTED", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateManifest_RejectsInvalidEntryPointPath()
    {
        var result = MdzArchive.ValidateManifest(new Manifest
        {
            Spec = new ManifestSpec { Version = "1.0.1" },
            EntryPoint = "../index.md",
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("entryPoint", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateManifest_RawJsonRejectsNonObjectAndTimestampObjectWithoutWhen()
    {
        var arrayResult = MdzArchive.ValidateManifest("[]");
        Assert.False(arrayResult.IsValid);
        Assert.Contains(arrayResult.Errors, error => error.Contains("JSON object", StringComparison.Ordinal));

        var timestampResult = MdzArchive.ValidateManifest("""
        {
          "spec": { "version": "1.0.1" },
          "entryPoint": "index.md",
          "created": { "by": { "name": "A" } }
        }
        """);
        Assert.False(timestampResult.IsValid);
        Assert.Contains(timestampResult.Errors, error => error.Contains("created", StringComparison.OrdinalIgnoreCase));
    }
}
