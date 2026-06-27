using MDZip.Core;

namespace MDZip.Core.Tests;

public class PackagerParityTests
{
    [Fact]
    public void BuildArchive_CreatesGeneratedIndexWhenNoEntryPointExists()
    {
        var sourceDirectory = CreateTempDirectory();
        var outputPath = TempArchivePath();
        var chapterOne = CreateFile(sourceDirectory, "chapters-one.md", "# One");
        var chapterTwo = CreateFile(sourceDirectory, "chapters-two.md", "# Two");

        try
        {
            var result = MdzArchive.BuildArchive(
                outputPath,
                [
                    new MdzPackInputFile("chapters/one.md", chapterOne),
                    new MdzPackInputFile("chapters/two.md", chapterTwo),
                ],
                "Book",
                new MdzPackOptions
                {
                    CreateIndex = true,
                    Title = "Book",
                });

            Assert.Equal("index.md", result.ResolvedEntryPoint);
            Assert.Contains("index.md", result.ArchivePaths);
            Assert.Equal("index.md", MdzArchive.ReadManifest(outputPath)?.EntryPoint);
            Assert.Contains("[one.md]", MdzArchive.ReadText(outputPath, "index.md"));
        }
        finally
        {
            SafeDelete(outputPath);
            SafeDeleteDirectory(sourceDirectory);
        }
    }

    [Fact]
    public void BuildArchive_MapsInvalidPathsAndWritesManifestFileMap()
    {
        var sourceDirectory = CreateTempDirectory();
        var outputPath = TempArchivePath();
        var invalid = CreateFile(sourceDirectory, "bad-name.md", "# Bad");

        try
        {
            var result = MdzArchive.BuildArchive(
                outputPath,
                [new MdzPackInputFile("bad:name.md", invalid)],
                "Mapped",
                new MdzPackOptions
                {
                    MapFiles = true,
                    Title = "Mapped",
                });

            Assert.Equal(1, result.Warnings.InvalidPathCount);
            Assert.Equal(1, result.Warnings.SanitizedPathCount);
            Assert.Contains("bad_name.md", result.ArchivePaths);

            var manifest = MdzArchive.ReadManifest(outputPath);
            Assert.Equal("bad_name.md", manifest?.Files?.Single().Path);
            Assert.Equal("bad:name.md", manifest?.Files?.Single().OriginalPath);
        }
        finally
        {
            SafeDelete(outputPath);
            SafeDeleteDirectory(sourceDirectory);
        }
    }

    [Fact]
    public void BuildArchive_SkipsFilesExcludedByFilters()
    {
        var sourceDirectory = CreateTempDirectory();
        var outputPath = TempArchivePath();
        var index = CreateFile(sourceDirectory, "index.md", "# Hello");
        var note = CreateFile(sourceDirectory, "note.txt", "note");

        try
        {
            var result = MdzArchive.BuildArchive(
                outputPath,
                [
                    new MdzPackInputFile("index.md", index),
                    new MdzPackInputFile("note.txt", note),
                ],
                "Filtered",
                new MdzPackOptions
                {
                    Filters = ["*.md"],
                });

            Assert.Contains("index.md", result.ArchivePaths);
            Assert.DoesNotContain("note.txt", result.ArchivePaths);
            Assert.Equal(1, result.Warnings.SkippedByReason["excluded by filter"]);
        }
        finally
        {
            SafeDelete(outputPath);
            SafeDeleteDirectory(sourceDirectory);
        }
    }

    private static string TempArchivePath() =>
        Path.Combine(Path.GetTempPath(), $"mdzip-core-pack-{Guid.NewGuid():N}.mdz");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mdzip-core-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
