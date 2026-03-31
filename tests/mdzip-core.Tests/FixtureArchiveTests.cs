using MDZip.Core;

namespace MDZip.Core.Tests;

public class FixtureArchiveTests
{
    [Theory]
    [InlineData("minimal.mdz", true)]
    [InlineData("with-manifest.mdz", true)]
    [InlineData("line-endings-crlf.mdz", true)]
    [InlineData("invalid-missing-index.mdz", false)]
    [InlineData("invalid-bad-entrypoint.mdz", false)]
    public void Validate_MatchesExpectedFixtureValidity(string fileName, bool expectedValid)
    {
        var result = MdzArchive.Validate(TestFixtureHelper.FixturePath(fileName));
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void InvalidMissingIndex_HasMissingEntryPointError()
    {
        var result = MdzArchive.Validate(TestFixtureHelper.FixturePath("invalid-missing-index.mdz"));
        Assert.Contains(result.Errors, e => e.Contains("ERR_ENTRYPOINT_MISSING", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidBadEntryPoint_HasMissingEntryPointError()
    {
        var result = MdzArchive.Validate(TestFixtureHelper.FixturePath("invalid-bad-entrypoint.mdz"));
        Assert.Contains(result.Errors, e => e.Contains("ERR_ENTRYPOINT_MISSING", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("minimal.mdz", "index.md")]
    [InlineData("with-manifest.mdz", "index.md")]
    [InlineData("line-endings-crlf.mdz", "index.md")]
    public void ResolveEntryPoint_UsesExpectedFixtureEntryPoint(string fileName, string expectedEntryPoint)
    {
        var entryPoint = MdzArchive.ResolveEntryPoint(TestFixtureHelper.FixturePath(fileName));
        Assert.Equal(expectedEntryPoint, entryPoint);
    }
}
