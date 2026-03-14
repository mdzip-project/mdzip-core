namespace Mdz.Core;

/// <summary>
/// Validates file paths inside a .mdz archive per Section 5.4 of the spec.
/// </summary>
public static class PathValidator
{
    // Characters reserved by common operating systems per Section 5.4
    private static readonly char[] ReservedChars = ['\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Validates an archive entry path according to Section 5.4 path constraints.
    /// Returns null if valid, or an error message describing the violation.
    /// </summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "Path must not be empty.";

        // MUST NOT begin with a leading slash
        if (path.StartsWith('/'))
            return $"Path '{path}' must not begin with a leading slash.";

        // MUST NOT contain path traversal sequences
        if (ContainsPathTraversal(path))
            return $"Path '{path}' must not contain path traversal sequences (e.g., '../').";

        // MUST NOT contain null bytes or ASCII control characters (U+0000-U+001F, U+007F)
        foreach (char c in path)
        {
            if (c is '\0' || (c >= '\u0001' && c <= '\u001F') || c == '\u007F')
                return $"Path '{path}' must not contain null bytes or ASCII control characters.";
        }

        // MUST NOT contain characters reserved by common operating systems
        foreach (char reserved in ReservedChars)
        {
            if (path.Contains(reserved))
                return $"Path '{path}' must not contain OS-reserved character '{reserved}'.";
        }

        return null;
    }

    /// <summary>
    /// Returns true if the path contains traversal sequences that could escape the archive root.
    /// </summary>
    public static bool ContainsPathTraversal(string path)
    {
        // Normalise to forward slashes first
        var normalised = path.Replace('\\', '/');

        // Split on forward slashes and inspect each segment
        var segments = normalised.Split('/');
        foreach (var segment in segments)
        {
            if (segment == "..")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the path is valid according to Section 5.4 constraints.
    /// </summary>
    public static bool IsValid(string path) => Validate(path) is null;
}
