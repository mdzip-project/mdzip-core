using System.Text.Json.Nodes;

namespace MDZip.Core.Tests;

/// <summary>Spec test T-018: ZIP-bomb limits (§13.1, resource-limit-cases.json).</summary>
public class ResourceLimitFixtureTests
{
    private static readonly string[] StableCodes = ["ERR_ZIP_INVALID", "ERR_LIMIT_EXCEEDED"];

    private static JsonNode Cases => JsonNode.Parse(File.ReadAllText(TestFixtureHelper.FixturePath("resource-limit-cases.json")))!;

    private static JsonNode Case(string id) =>
        Cases["cases"]!.AsArray().First(c => c!["id"]!.GetValue<string>() == id)!;

    private static string Archive(string id) => TestFixtureHelper.FixturePath(Case(id)["archive"]!.GetValue<string>());

    private static string? CodeOf(Action action)
    {
        var error = Assert.ThrowsAny<InvalidOperationException>(action);
        return MdzSpec.ErrorCode(error);
    }

    [Fact]
    public void Defaults_AreNoLooserThanTheReferenceLimits()
    {
        var reference = Cases["referenceLimits"]!;
        Assert.True(MdzArchive.MaxEntryUncompressedBytes <= reference["maxEntryUncompressedBytes"]!.GetValue<long>());
        Assert.True(MdzArchive.MaxTotalUncompressedBytes <= reference["maxTotalUncompressedBytes"]!.GetValue<long>());
        Assert.True(MdzArchive.MaxCompressionRatio <= reference["maxCompressionRatio"]!.GetValue<double>());
        Assert.True(MdzArchive.MaxEntries <= reference["maxEntries"]!.GetValue<int>());
    }

    [Fact]
    public void ForgedSize_ReadFailsWithAStableCode()
    {
        var archive = Archive("limit-forged-size");
        var entry = Case("limit-forged-size")["entry"]!.GetValue<string>();

        Assert.Contains("index.md", MdzArchive.List(archive));
        Assert.NotEmpty(MdzArchive.ReadText(archive, "index.md")); // ordinary entries stay readable
        Assert.Contains(CodeOf(() => MdzArchive.ReadBytes(archive, entry)), StableCodes);
    }

    [Fact]
    public void ForgedSize_ExtractionLeavesNoCorruptFile()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}");
        try
        {
            Assert.Contains(CodeOf(() => MdzArchive.Extract(Archive("limit-forged-size"), destination)), StableCodes);
            Assert.False(File.Exists(Path.Combine(destination, "data", "blob.bin")));
        }
        finally
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void HighRatio_RejectedBeforeDecompressing()
    {
        Assert.Equal("ERR_LIMIT_EXCEEDED", CodeOf(() => MdzArchive.List(Archive("limit-high-ratio"))));
    }

    [Fact]
    public void OverlappingEntries_RejectedOnTheDeclaredTotal()
    {
        Assert.Equal("ERR_LIMIT_EXCEEDED", CodeOf(() => MdzArchive.List(Archive("limit-overlapping-entries"))));
    }
}
