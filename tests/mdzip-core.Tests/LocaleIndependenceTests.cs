using System.Globalization;
using MDZip.Core;

namespace MDZip.Core.Tests;

/// <summary>
/// T-010: support-file detection and entry-point discovery ignore the current culture (§5.4.1).
/// Under Turkish rules a culture-aware lowercase turns <c>LICENSE</c> into <c>lıcense</c> (dotless ı),
/// so a culture-aware call would stop recognizing <c>LICENSE.md</c> as a support file.
/// </summary>
public class LocaleIndependenceTests
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static void UnderTurkishCulture(Action test)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = Turkish;
            CultureInfo.CurrentUICulture = Turkish;
            // .NET ships Turkish casing rules (ICU or Windows NLS); without them this test proves nothing.
            Assert.Equal("lıcense", "LICENSE".ToLower());
            test();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    public void T010_SupportFilesAreDetected_UnderTurkishCulture() => UnderTurkishCulture(() =>
    {
        Assert.Equal("license", MdzSpec.AsciiLower("LICENSE"));
        foreach (var name in new[] { "LICENSE.md", "README.md", "CONTRIBUTING.md", "CHANGELOG.MD", "AGENTS.markdown" })
            Assert.True(MdzSpec.IsSupportFile(name), name);
        Assert.True(MdzSpec.IsMarkdownPath("NOTES.MD"));
    });

    [Fact]
    public void T010_EntryPointSkipsLicense_UnderTurkishCulture() => UnderTurkishCulture(() =>
    {
        var archive = TestFixtureHelper.CreateTempArchive(new Dictionary<string, string>
        {
            ["LICENSE.md"] = "# License\n",
            ["notes.md"] = "# Notes\n",
        });
        try
        {
            Assert.Equal("notes.md", MdzArchive.ResolveEntryPoint(archive));
        }
        finally
        {
            File.Delete(archive);
        }
    });
}
