using System.Text.Json;
using Mdz.Core;

namespace mdz_core.Tests;

public class EntryPointFixtureTests
{
    [Fact]
    public void ResolveEntryPoint_MatchesFixtureCases()
    {
        var cases = TestFixtureHelper.ReadJsonFixture<List<EntryPointCase>>("entrypoint-cases.json");

        foreach (var @case in cases)
        {
            var tempArchive = CreateArchiveForCase(@case);
            try
            {
                var resolved = MdzArchive.ResolveEntryPoint(tempArchive);
                Assert.True(
                    string.Equals(resolved, @case.ExpectedEntryPoint, StringComparison.Ordinal),
                    $"Fixture '{@case.Name}' expected '{@case.ExpectedEntryPoint ?? "<null>"}' but got '{resolved ?? "<null>"}'.");
            }
            finally
            {
                File.Delete(tempArchive);
            }
        }
    }

    private static string CreateArchiveForCase(EntryPointCase @case)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in @case.Files)
        {
            entries[file] = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
                ? "# test"
                : "fixture";
        }

        if (@case.Manifest is not null)
        {
            entries["manifest.json"] = JsonSerializer.Serialize(@case.Manifest);
        }

        return TestFixtureHelper.CreateTempArchive(entries);
    }

    public sealed record EntryPointCase(
        string Name,
        List<string> Files,
        JsonElement? Manifest,
        string? ExpectedEntryPoint);
}
