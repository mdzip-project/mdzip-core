using System.Security.Cryptography;
using System.Text.Json;

namespace MDZip.Core.Tests;

/// <summary>
/// Fixtures/ holds a copy of the spec's shared fixtures at a released tag,
/// made with <c>node ../mdzip-spec/tools/sync-fixtures.mjs tests/mdzip-core.Tests/Fixtures --ref &lt;tag&gt;</c>.
/// This guards against hand edits and partial updates of that copy.
/// </summary>
public class SpecFixturesMarkerTests
{
    [Fact]
    public void SpecFixtures_MatchMarker()
    {
        using var marker = JsonDocument.Parse(File.ReadAllText(TestFixtureHelper.FixturePath("SPEC-FIXTURES.json")));
        var root = marker.RootElement;
        var specRef = root.GetProperty("ref").GetString();

        foreach (var file in root.GetProperty("files").EnumerateObject())
        {
            var path = TestFixtureHelper.FixturePath(file.Name);
            Assert.True(File.Exists(path), $"Spec fixture '{file.Name}' is missing; re-run sync-fixtures.mjs.");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            Assert.True(
                actual == file.Value.GetString(),
                $"Spec fixture '{file.Name}' differs from spec {specRef}; re-run sync-fixtures.mjs instead of editing it.");
        }
    }
}
