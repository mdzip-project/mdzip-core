using System.IO.Compression;
using System.Text.Json;

namespace MDZip.Core.Tests;

internal static class TestFixtureHelper
{
    public static string FixturesDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string FixturePath(string fileName) => Path.Combine(FixturesDirectory, fileName);

    public static T ReadJsonFixture<T>(string fileName)
    {
        var json = File.ReadAllText(FixturePath(fileName));
        var model = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return model ?? throw new InvalidOperationException($"Failed to deserialize fixture '{fileName}'.");
    }

    public static string CreateTempArchive(IDictionary<string, string> textEntries)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mdzip-core-tests-{Guid.NewGuid():N}.mdz");

        using var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in textEntries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var stream = new StreamWriter(entry.Open());
            stream.Write(content);
        }

        return tempPath;
    }
}
