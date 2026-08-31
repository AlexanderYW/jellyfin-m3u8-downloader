using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Maps a user-supplied filename onto a safe absolute path under the output root.
/// </summary>
/// <remarks>
/// This is the security boundary for the plugin: the filename comes straight from a textarea, so
/// it is treated as hostile. Relative subdirectories are allowed (they are genuinely useful for
/// Jellyfin's <c>Show/Season 01/Episode.mkv</c> layout), but the resolved path is verified to stay
/// inside the root before anything is written.
/// </remarks>
public static class OutputPathResolver
{
    /// <summary>The container extension every download is written as.</summary>
    public const string Extension = ".mkv";

    /// <summary>Maximum length of any single path segment, to stay clear of filesystem limits.</summary>
    private const int MaxSegmentLength = 180;

    /// <summary>Both separators are accepted so a name pasted from Windows still splits.</summary>
    private static readonly char[] _separators = { '/', '\\' };

    /// <summary>
    /// Resolves a requested filename to an absolute path under <paramref name="outputRoot"/>.
    /// </summary>
    /// <param name="outputRoot">The configured output directory.</param>
    /// <param name="requestedFileName">The user-supplied, possibly hostile, name.</param>
    /// <param name="fileExists">
    /// Predicate used to test for collisions. Injected so the resolver stays testable without
    /// touching the disk; production callers pass <see cref="File.Exists(string)"/>.
    /// </param>
    /// <returns>An absolute path that is guaranteed to sit inside the output root.</returns>
    /// <exception cref="ArgumentException">
    /// The root is not configured, or the name escapes the root / sanitises down to nothing.
    /// </exception>
    public static string Resolve(string outputRoot, string requestedFileName, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("No output directory is configured for the M3U8 Downloader plugin.", nameof(outputRoot));
        }

        if (string.IsNullOrWhiteSpace(requestedFileName))
        {
            throw new ArgumentException("The output filename is empty.", nameof(requestedFileName));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));

        // Reject anything rooted before we even try to combine, so an absolute path can never
        // silently replace the root.
        if (Path.IsPathRooted(requestedFileName))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{requestedFileName}' is an absolute path; only names relative to the output directory are allowed."),
                nameof(requestedFileName));
        }

        var segments = SanitizeSegments(requestedFileName);
        if (segments.Count == 0)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{requestedFileName}' does not contain any usable filename characters."),
                nameof(requestedFileName));
        }

        // Force the container extension on the last segment.
        segments[^1] = ForceExtension(segments[^1]);

        var combined = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));

        // Belt and braces: even though '..' segments were stripped above, confirm the fully
        // resolved path is still under the root (this also catches symlink-free edge cases such
        // as a root of "/media" versus a sibling "/media-other").
        if (!IsUnder(root, combined))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{requestedFileName}' resolves outside the configured output directory."),
                nameof(requestedFileName));
        }

        return Deduplicate(combined, fileExists);
    }

    /// <summary>
    /// Splits a relative name into sanitised path segments, dropping traversal and empty parts.
    /// </summary>
    /// <param name="requestedFileName">The user-supplied name.</param>
    /// <returns>The cleaned segments, in order.</returns>
    private static List<string> SanitizeSegments(string requestedFileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new List<string>();

        var rawSegments = requestedFileName.Split(
            _separators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var raw in rawSegments)
        {
            // Drop traversal outright rather than trying to resolve it; there is no legitimate
            // reason for a download name to contain it.
            if (raw is "." or "..")
            {
                continue;
            }

            var builder = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
            }

            // Windows refuses names ending in a dot or space, and they are confusing everywhere.
            var cleaned = builder.ToString().Trim().TrimEnd('.', ' ').Trim();

            if (cleaned.Length > MaxSegmentLength)
            {
                cleaned = cleaned[..MaxSegmentLength].TrimEnd('.', ' ').Trim();
            }

            if (cleaned.Length > 0)
            {
                result.Add(cleaned);
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces any existing extension with the MKV container extension.
    /// </summary>
    /// <param name="fileName">The final path segment.</param>
    /// <returns>The segment with an <c>.mkv</c> extension.</returns>
    private static string ForceExtension(string fileName)
    {
        if (fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        var existing = Path.GetExtension(fileName);

        // Only strip something that actually looks like an extension. This keeps names such as
        // "Movie (2019). Directors Cut" from losing their tail.
        if (existing.Length is > 1 and <= 5 && existing.AsSpan(1).ToString().All(char.IsLetterOrDigit))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }

        return fileName + Extension;
    }

    /// <summary>
    /// Determines whether a resolved path lies inside a root directory.
    /// </summary>
    /// <param name="root">The absolute, normalised root.</param>
    /// <param name="candidate">The absolute, normalised candidate path.</param>
    /// <returns><c>true</c> when the candidate is inside the root.</returns>
    private static bool IsUnder(string root, string candidate)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        // Case-insensitive on Windows and macOS, case-sensitive elsewhere. Comparing
        // case-insensitively everywhere is the conservative choice: it can only reject more.
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Appends a " (n)" suffix until the path is free, so an existing file is never overwritten.
    /// </summary>
    /// <param name="path">The desired absolute path.</param>
    /// <param name="fileExists">Existence predicate.</param>
    /// <returns>A path that does not currently exist.</returns>
    private static string Deduplicate(string path, Func<string, bool> fileExists)
    {
        if (!fileExists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(
                directory,
                string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){Extension}"));

            if (!fileExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            string.Create(CultureInfo.InvariantCulture, $"Could not find a free filename for '{path}'."));
    }
}
