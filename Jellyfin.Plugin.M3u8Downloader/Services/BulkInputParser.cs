using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.M3u8Downloader.Model;
using Jellyfin.Plugin.M3u8Downloader.Model.Dto;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Turns the bulk-input textarea into jobs.
/// </summary>
/// <remarks>
/// Pure and side-effect free so it can be unit tested directly. One bad line never invalidates
/// the whole paste: callers get the accepted jobs and the rejections side by side.
/// </remarks>
public static class BulkInputParser
{
    /// <summary>
    /// Parses bulk input into jobs.
    /// </summary>
    /// <param name="text">Raw textarea content, one job per line.</param>
    /// <param name="folder">
    /// Optional destination folder, relative to the output directory, applied to every job in the
    /// batch. Prefixed onto each name and left for
    /// <see cref="OutputPathResolver"/> to sanitise -- it already strips traversal, cleans each
    /// segment and re-checks containment, so this needs no path handling of its own.
    /// </param>
    /// <param name="validateName">
    /// Optional check applied to each resolved name, returning an error message to reject the line
    /// or <c>null</c> to accept it. Injected rather than called directly so the parser stays pure
    /// and the caller decides what "valid" means; the API passes output-path resolution, which
    /// turns a misconfigured output directory into an inline rejection instead of a job that
    /// fails identically on every retry.
    /// </param>
    /// <returns>The accepted jobs and the rejected lines.</returns>
    public static (List<DownloadJob> Accepted, List<RejectedLine> Rejected) Parse(
        string? text,
        string? folder = null,
        Func<string, string?>? validateName = null)
    {
        var accepted = new List<DownloadJob>();
        var rejected = new List<RejectedLine>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return (accepted, rejected);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i].Trim();

            // Blank lines and comments are simply skipped, not reported as errors.
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string urlPart;
            string? namePart = null;

            // A raw '|' cannot appear in a valid URL, so it is an unambiguous delimiter.
            var pipe = line.IndexOf('|', StringComparison.Ordinal);
            if (pipe >= 0)
            {
                urlPart = line[..pipe].Trim();
                namePart = line[(pipe + 1)..].Trim();
            }
            else
            {
                urlPart = line;
            }

            if (urlPart.Length == 0)
            {
                rejected.Add(new RejectedLine(lineNumber, line, "Missing URL before the '|' separator."));
                continue;
            }

            if (!Uri.TryCreate(urlPart, UriKind.Absolute, out var uri))
            {
                rejected.Add(new RejectedLine(lineNumber, line, "Not a valid absolute URL."));
                continue;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add(new RejectedLine(
                    lineNumber,
                    line,
                    string.Create(CultureInfo.InvariantCulture, $"Unsupported scheme '{uri.Scheme}'; only http and https are allowed.")));
                continue;
            }

            if (string.IsNullOrWhiteSpace(namePart))
            {
                namePart = DeriveFileName(uri, accepted.Count + 1);
            }

            var requestedName = Combine(folder, namePart);

            var error = validateName?.Invoke(requestedName);
            if (error is not null)
            {
                rejected.Add(new RejectedLine(lineNumber, line, error));
                continue;
            }

            accepted.Add(new DownloadJob
            {
                Url = uri.ToString(),
                RequestedFileName = requestedName,
            });
        }

        return (accepted, rejected);
    }

    /// <summary>
    /// Places a name inside the batch's destination folder.
    /// </summary>
    /// <param name="folder">The batch folder, or <c>null</c>/blank for none.</param>
    /// <param name="name">The per-job name, which may itself contain subdirectories.</param>
    /// <returns>The name relative to the output directory.</returns>
    private static string Combine(string? folder, string name)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return name;
        }

        // Only the join is done here. Anything hostile in either half is the resolver's problem,
        // and it is already the single place that decides what a name may become on disk.
        return string.Concat(folder.Trim().TrimEnd('/', '\\'), "/", name);
    }

    /// <summary>
    /// Derives an output name from a URL when the user did not supply one.
    /// </summary>
    /// <param name="uri">The source URL.</param>
    /// <param name="ordinal">Position in the batch, used for the fallback name.</param>
    /// <returns>A filename stem (no extension is added here).</returns>
    private static string DeriveFileName(Uri uri, int ordinal)
    {
        // Walk the path from the right, skipping the playlist filename itself ("index.m3u8",
        // "master.m3u8", ...) which carries no information about the content.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = Uri.UnescapeDataString(segments[i]).Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            var stem = segment.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? segment[..^5]
                : segment;

            if (stem.Length > 0 && !IsGenericPlaylistName(stem))
            {
                return stem;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"download-{ordinal}");
    }

    private static bool IsGenericPlaylistName(string stem) =>
        stem.Equals("index", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("master", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("playlist", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("prog_index", StringComparison.OrdinalIgnoreCase);
}
