using Mdz.Core;

namespace mdz_core.Tests;

public class PathValidationFixtureTests
{
    [Fact]
    public void PathValidation_MatchesFixtureCases()
    {
        var cases = TestFixtureHelper.ReadJsonFixture<List<PathCase>>("path-validation-cases.json");

        foreach (var @case in cases)
        {
            var isValid = PathValidator.IsValid(@case.Path);
            Assert.True(
                isValid == @case.Valid,
                $"Fixture '{@case.Name}' expected valid={@case.Valid} but got {isValid} for path '{@case.Path}'.");
        }
    }

    public sealed record PathCase(string Name, string Path, bool Valid, string? ErrorCode);
}
