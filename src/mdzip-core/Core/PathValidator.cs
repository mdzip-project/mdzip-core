namespace MDZip.Core;

/// <summary>
/// Validates file paths inside a .mdz archive per Section 5.4 of the spec.
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Validates an archive entry path according to Section 5.4 path constraints: not empty, no leading
    /// slash, no control or OS-reserved characters, no <c>..</c>, empty or <c>.</c> segments. A trailing
    /// <c>/</c> marks a directory entry. Returns null if valid, or a message describing the violation.
    /// </summary>
    public static string? Validate(string path) => GetProblem(path)?.Message;

    /// <summary>
    /// Why <paramref name="path"/> isn't a valid archive entry path (§5.4), with its spec error code
    /// (<c>ERR_PATH_EMPTY</c>, <c>ERR_PATH_LEADING_SLASH</c>, <c>ERR_PATH_CONTROL_CHAR</c>,
    /// <c>ERR_PATH_RESERVED_CHAR</c>, <c>ERR_PATH_TRAVERSAL</c> or <c>ERR_PATH_INVALID</c>), or null.
    /// </summary>
    public static MdzProblem? GetProblem(string path) => MdzSpec.PathProblem(path);

    /// <summary>
    /// Returns true if the path contains a <c>..</c> segment that could escape the archive root.
    /// </summary>
    public static bool ContainsPathTraversal(string path) =>
        path.Replace('\\', '/').Split('/').Contains("..");

    /// <summary>
    /// Returns true if the path is valid according to Section 5.4 constraints.
    /// </summary>
    public static bool IsValid(string path) => Validate(path) is null;
}
