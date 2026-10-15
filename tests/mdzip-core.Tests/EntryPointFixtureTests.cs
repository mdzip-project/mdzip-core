using System.Text.Json;
using MDZip.Core;

namespace MDZip.Core.Tests;

public class EntryPointFixtureTests
{
    // entrypoint-cases.json is the spec's shared fixture (synced, never edited
    // here); local-entrypoint-cases.json holds cases specific to this library.
    [Theory]
    [InlineData("entrypoint-cases.json")]
    [InlineData("local-entrypoint-cases.json")]
    public void ResolveEntryPoint_MatchesFixtureCases(string fixtureFile)
    {
        var cases = TestFixtureHelper.ReadJsonFixture<List<EntryPointCase>>(fixtureFile);

        foreach (var @case in cases)
        {
            var tempArchive = CreateArchiveForCase(@case);
            try
            {
                var resolved = MdzArchive.ResolveEntryPoint(tempArchive);
                Assert.True(
                    string.Equals(resolved, @case.ExpectedEntryPoint, StringComparison.Ordinal),
                    $"Fixture '{@case.Name}' expected '{@case.ExpectedEntryPoint ?? "<null>"}' but got '{resolved ?? "<null>"}'.");

                if (@case.ExpectedError is not null)
                {
                    var ex = Assert.ThrowsAny<InvalidOperationException>(() => MdzArchive.ResolveEntryPointStrict(tempArchive));
                    Assert.True(
                        MdzSpec.ErrorCode(ex) == @case.ExpectedError,
                        $"Fixture '{@case.Name}' expected {@case.ExpectedError} but got: {ex.Message}");
                }
            }
            finally
            {
                File.Delete(tempArchive);
            }
        }
    }

    private static string CreateArchiveForCase(EntryPointCase @case)
    {
        // Ordinal: the fixtures include names that differ only in case or normalization.
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in @case.Files)
        {
            entries[file] = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
                ? "# test"
                : "fixture";
        }

        // A malformed manifest is given as raw text in `manifestJson` (with `manifest: null`).
        if (@case.ManifestJson is not null)
        {
            entries["manifest.json"] = @case.ManifestJson;
        }
        else if (@case.Manifest is { ValueKind: not JsonValueKind.Null } manifest)
        {
            entries["manifest.json"] = JsonSerializer.Serialize(manifest);
        }

        return TestFixtureHelper.CreateTempArchive(entries);
    }

    public sealed record EntryPointCase(
        string Name,
        List<string> Files,
        JsonElement? Manifest,
        string? ManifestJson,
        string? ExpectedEntryPoint,
        string? ExpectedError);
}
