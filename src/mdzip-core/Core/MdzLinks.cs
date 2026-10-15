using System.Text;
using System.Text.RegularExpressions;

namespace MDZip.Core;

/// <summary>Where a link is written: a document inside an archive, or an ordinary host file.</summary>
public abstract record MdzLinkSource;

/// <summary>A link written in document <paramref name="Path"/> of the archive at host path <paramref name="Archive"/>.</summary>
public sealed record MdzArchiveLinkSource(string Archive, string Path) : MdzLinkSource;

/// <summary>A link written in an ordinary file at host path <paramref name="Host"/>.</summary>
public sealed record MdzHostLinkSource(string Host) : MdzLinkSource;

/// <summary>
/// Where a link points. <see cref="Kind"/> is <c>internal</c> (<see cref="Entry"/> in the source
/// archive), <c>host</c> (host file <see cref="Path"/>), <c>archive</c> (<see cref="Entry"/> in archive
/// <see cref="Archive"/>, addressed through the host), <c>url</c> (not a path) or <c>unresolved</c>
/// (with a <see cref="Reason"/>: <c>not-found</c>, <c>invalid</c>, <c>unsupported</c>,
/// <c>no-host-location</c>, <c>nested-archive</c> or <c>denied</c>).
/// </summary>
public sealed record MdzLinkTarget(
    string Kind,
    string? Entry = null,
    string? Path = null,
    string? Archive = null,
    string? Fragment = null,
    string? Reason = null);

/// <summary>
/// What link resolution needs to know about the files around an archive. Host paths are
/// <c>/</c>-separated.
/// </summary>
public interface IMdzLinkHost
{
    /// <summary>What exists at a host path: <c>file</c>, <c>directory</c>, <c>archive</c>, or null.</summary>
    string? Kind(string path);

    /// <summary>The stored entry path in <paramref name="archive"/> matching <paramref name="entry"/> (NFC, case-sensitive), or null.</summary>
    string? FindEntry(string archive, string entry);

    /// <summary>The entry point of <paramref name="archive"/> (§5.5), or null.</summary>
    string? EntryPoint(string archive);

    /// <summary>False when the archive was opened from bytes and has no location (§9.4).</summary>
    bool HostLocation => true;

    /// <summary>Whether links may leave the archive for files next to it (§9.4).</summary>
    bool OutsideLinks => true;

    /// <summary>Whether links may enter archives (§9.5).</summary>
    bool IntoArchiveLinks => true;

    /// <summary>Trust boundary (§9.6): <c>""</c> (default) trusts the whole host; null trusts nothing.</summary>
    string? TrustRoot => string.Empty;

    /// <summary>Host paths outside the boundary that the user explicitly allowed.</summary>
    IReadOnlyCollection<string> AllowedPaths => [];

    /// <summary>Whether embeds may load resources from outside an archive (inside the boundary only).</summary>
    bool LoadOutsideResources => false;
}

/// <summary>
/// Link resolution (§9.3–9.6): links inside an archive, links that leave it for files next to the
/// <c>.mdz</c>, and links into (other) archives. A port of <c>resolveLink</c> in
/// <c>@mdzip/core-js</c> and <c>mdzip-core-python</c>.
/// </summary>
public static class MdzLinks
{
    private static readonly Regex SchemeRegex = new(@"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BadPercentRegex = new(@"%(?![0-9A-Fa-f]{2})", RegexOptions.CultureInvariant);

    /// <summary>True for a name with an <c>.mdz</c> extension, compared ASCII case-insensitively.</summary>
    public static bool IsMdzName(string name) => MdzSpec.AsciiLower(name).EndsWith(".mdz", StringComparison.Ordinal);

    /// <summary>
    /// Resolves a Markdown link destination written in <paramref name="source"/> (§9.3–9.6).
    /// <paramref name="use"/> is <c>navigate</c> for a link the user follows or <c>embed</c> for a
    /// resource the document loads automatically. Host targets outside the trust boundary are
    /// <c>denied</c> whether or not they exist; embeds that leave an archive are <c>denied</c> unless the
    /// host allows outside resources. A <c>..</c> is never clamped at the archive root.
    /// </summary>
    public static MdzLinkTarget ResolveLink(MdzLinkSource source, string link, IMdzLinkHost host, string use = "navigate")
    {
        if (SchemeRegex.IsMatch(link))
            return new MdzLinkTarget("url");

        // Split at the first "#" before removing the query: a "?" in the fragment stays there.
        var hash = link.IndexOf('#');
        var dest = hash >= 0 ? link[..hash] : link;
        var fragment = hash >= 0 ? link[(hash + 1)..] : string.Empty;
        var query = dest.IndexOf('?');
        if (query >= 0)
            dest = dest[..query];
        // A trailing "/" names a directory: never an entry or a host file (only an archive itself).
        var directory = dest.EndsWith('/');
        MdzLinkTarget WithFragment(MdzLinkTarget target) => fragment.Length > 0 ? target with { Fragment = fragment } : target;

        if (dest.Length == 0)
        {
            return WithFragment(source is MdzArchiveLinkSource a
                ? new MdzLinkTarget("internal", Entry: a.Path)
                : new MdzLinkTarget("host", Path: ((MdzHostLinkSource)source).Host));
        }

        var segments = DecodeSegments(dest);
        if (segments is null)
            return Unresolved("invalid");

        var i = 0;
        List<string> stack;
        if (source is MdzArchiveLinkSource archiveSource)
        {
            stack = DirSegments(archiveSource.Path);
            var left = false;
            while (i < segments.Count)
            {
                var s = segments[i++];
                if (s is "" or ".")
                    continue;
                if (s == "..")
                {
                    if (stack.Count == 0)
                    {
                        left = true;
                        break;
                    }
                    stack.RemoveAt(stack.Count - 1);
                }
                else
                {
                    stack.Add(s);
                }
            }

            if (!left)
            {
                if (directory)
                    return Unresolved("not-found");
                var entry = host.FindEntry(archiveSource.Archive, string.Join('/', stack));
                return entry is null ? Unresolved("not-found") : WithFragment(new MdzLinkTarget("internal", Entry: entry));
            }

            if (!host.OutsideLinks)
                return Unresolved("unsupported");
            if (!host.HostLocation)
                return Unresolved("no-host-location");
            if (use == "embed" && !host.LoadOutsideResources)
                return Unresolved("denied");
            stack = DirSegments(archiveSource.Archive);
        }
        else
        {
            stack = DirSegments(((MdzHostLinkSource)source).Host);
        }

        while (i < segments.Count)
        {
            var s = segments[i++];
            if (s is "" or ".")
                continue;
            if (s == "..")
            {
                if (stack.Count == 0)
                    return Unresolved("not-found");
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(s);
            var candidate = string.Join('/', stack);
            if (IsMdzName(s) && Permitted(host, candidate) && host.Kind(candidate) == "archive")
            {
                if (!host.IntoArchiveLinks)
                    return Unresolved("unsupported");
                if (source is MdzArchiveLinkSource a && candidate != a.Archive && use == "embed" && !host.LoadOutsideResources)
                    return Unresolved("denied");
                return InsideArchive(candidate, segments.Skip(i).ToList(), host, WithFragment, directory);
            }
        }

        var hostPath = string.Join('/', stack);
        // Checked before existence, so a link can't probe for files outside the boundary.
        if (!Permitted(host, hostPath))
            return Unresolved("denied");
        if (directory)
            return Unresolved("not-found");
        return host.Kind(hostPath) == "file" ? WithFragment(new MdzLinkTarget("host", Path: hostPath)) : Unresolved("not-found");
    }

    private static MdzLinkTarget InsideArchive(
        string archive,
        List<string> segments,
        IMdzLinkHost host,
        Func<MdzLinkTarget, MdzLinkTarget> withFragment,
        bool directory)
    {
        var inner = new List<string>();
        for (var j = 0; j < segments.Count; j++)
        {
            var s = segments[j];
            if (s is "" or ".")
                continue;
            if (s == "..")
            {
                if (inner.Count == 0)
                    return Unresolved("invalid");
                inner.RemoveAt(inner.Count - 1);
                continue;
            }

            inner.Add(s);
            if (IsMdzName(s) && j < segments.Count - 1)
                return Unresolved("nested-archive");
        }

        if (inner.Count == 0)
        {
            var entryPoint = host.EntryPoint(archive);
            return entryPoint is null ? Unresolved("not-found") : withFragment(new MdzLinkTarget("archive", Entry: entryPoint, Archive: archive));
        }

        if (directory)
            return Unresolved("not-found");
        var entry = host.FindEntry(archive, string.Join('/', inner));
        return entry is null ? Unresolved("not-found") : withFragment(new MdzLinkTarget("archive", Entry: entry, Archive: archive));
    }

    private static MdzLinkTarget Unresolved(string reason) => new("unresolved", Reason: reason);

    private static List<string> DirSegments(string path) =>
        path.Split('/')[..^1].Where(s => s.Length > 0).ToList();

    /// <summary>Destination → decoded segments (§9.3 steps 3–4), or null if the link is invalid.</summary>
    private static List<string>? DecodeSegments(string dest)
    {
        if (dest.StartsWith('/') || dest.Contains('\\'))
            return null;

        var segments = new List<string>();
        foreach (var raw in dest.Split('/'))
        {
            var segment = raw;
            if (raw.Contains('%'))
            {
                // Decode exactly once; invalid escapes and non-UTF-8 bytes make the link unresolved.
                if (BadPercentRegex.IsMatch(raw))
                    return null;
                segment = DecodePercent(raw);
                if (segment is null)
                    return null;
            }

            if (segment.Any(c => c is '/' or '\\' || c <= '\u001F' || c == '\u007F'))
                return null;
            segments.Add(segment);
        }

        return segments;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string? DecodePercent(string raw)
    {
        var bytes = new List<byte>(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '%')
            {
                bytes.Add(Convert.ToByte(raw.Substring(i + 1, 2), 16));
                i += 3;
                continue;
            }

            // Copy the literal run up to the next escape as UTF-8 (keeps surrogate pairs together).
            var next = raw.IndexOf('%', i);
            if (next < 0)
                next = raw.Length;
            bytes.AddRange(Encoding.UTF8.GetBytes(raw[i..next]));
            i = next;
        }

        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool Permitted(IMdzLinkHost host, string path)
    {
        if (host.AllowedPaths.Contains(path))
            return true;
        var root = host.TrustRoot;
        if (root is null)
            return false;
        return root.Length == 0 || path == root || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
    }
}
