using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Claims an output name before a download starts and moves the finished file onto it.
/// </summary>
/// <remarks>
/// Split out of <see cref="FfmpegDownloader"/> because this is a distinct problem from running
/// ffmpeg: it is entirely about the filesystem, and about the two windows in which a name can be
/// taken out from under a download -- between resolving the name and starting, and across the
/// hours the download itself runs.
///
/// Registered as a singleton, so the reservation lock covers every download in the server.
/// </remarks>
public sealed class OutputFilePublisher
{
    /// <summary>How many times publishing may re-resolve around a name that appeared mid-download.</summary>
    private const int PublishAttempts = 5;

    /// <summary>Suffix of the in-progress file, before it is published under its real name.</summary>
    /// <remarks>
    /// Public because the worker sweeps abandoned ones at startup; see
    /// <see cref="ReserveOutputPath"/> for why the file is the reservation itself.
    /// </remarks>
    public const string PartExtension = ".part";

    /// <summary>
    /// Serialises output-path selection across concurrent downloads.
    /// </summary>
    /// <remarks>
    /// Resolving a name and reserving it must be one step. Two downloads of the same name would
    /// otherwise both find the path free -- deduplication only consults the filesystem -- and race
    /// to publish it at the end. Held only for the resolve and the reservation, never across the
    /// download itself.
    ///
    /// An instance field rather than a static one: the publisher is registered as a singleton, so
    /// every download in the server still serialises against the same lock, but nothing is shared
    /// implicitly through class state -- which in particular keeps one test's reservations from
    /// blocking another's.
    /// </remarks>
    private readonly object _reservationLock = new();

    private readonly ILogger<OutputFilePublisher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutputFilePublisher"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public OutputFilePublisher(ILogger<OutputFilePublisher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Picks the output path and claims it before anything is downloaded.
    /// </summary>
    /// <param name="outputRoot">The configured output directory.</param>
    /// <param name="requestedFileName">The name the job asked for.</param>
    /// <returns>The final path and the temporary path now reserved for it.</returns>
    /// <remarks>
    /// The empty <c>.part</c> file is the reservation itself, and it is created immediately rather
    /// than left to ffmpeg: the probe runs first and may take a minute, which is ample time for a
    /// second download of the same name to pick the same path. Passing a predicate that also sees
    /// <c>.part</c> files is what makes an in-flight download visible to the next one.
    /// </remarks>
    public (string FinalPath, string TempPath) ReserveOutputPath(string outputRoot, string requestedFileName)
    {
        lock (_reservationLock)
        {
            var finalPath = OutputPathResolver.Resolve(
                outputRoot,
                requestedFileName,
                path => File.Exists(path) || File.Exists(path + PartExtension));

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            var tempPath = finalPath + PartExtension;

            // ffmpeg is invoked with -y, so the empty placeholder is simply overwritten.
            File.Create(tempPath).Dispose();

            return (finalPath, tempPath);
        }
    }

    /// <summary>
    /// Moves a finished download onto its final name, working around a name taken since it started.
    /// </summary>
    /// <param name="outputRoot">The configured output directory.</param>
    /// <param name="requestedFileName">The name the job asked for.</param>
    /// <param name="tempPath">The completed <c>.part</c> file.</param>
    /// <param name="finalPath">The name reserved when the download began.</param>
    /// <returns>Where the file actually landed.</returns>
    public string PublishFinishedFile(string outputRoot, string requestedFileName, string tempPath, string finalPath)
    {
        string destination;

        // Held across the move as well as the resolve: picking a replacement name and taking it are
        // one step here, exactly as they are when the name is first reserved.
        try
        {
            lock (_reservationLock)
            {
                destination = Publish(
                    outputRoot,
                    requestedFileName,
                    tempPath,
                    finalPath,
                    File.Exists,
                    (from, to) => File.Move(from, to, overwrite: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The download itself succeeded, so say plainly where the finished bytes are: the job
            // will retry and re-download, but this file can simply be renamed instead.
            _logger.LogError(
                ex,
                "The download finished but could not be published to {Destination}. The complete file is at {TempPath}; rename it by hand to keep it, or it will be swept on the next server restart",
                finalPath,
                tempPath);
            throw;
        }

        if (!string.Equals(destination, finalPath, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "{Reserved} was taken while the download was running; published to {Destination} instead",
                finalPath,
                destination);
        }

        return destination;
    }

    /// <summary>
    /// Moves a finished download onto its reserved name, resolving a fresh one if it was taken.
    /// </summary>
    /// <param name="outputRoot">The configured output directory.</param>
    /// <param name="requestedFileName">The name the job asked for.</param>
    /// <param name="tempPath">The completed <c>.part</c> file to move.</param>
    /// <param name="finalPath">The name reserved when the download began.</param>
    /// <param name="fileExists">Existence predicate; production passes <see cref="File.Exists(string)"/>.</param>
    /// <param name="move">Performs the move, failing if the destination exists.</param>
    /// <returns>Where the file actually landed.</returns>
    /// <remarks>
    /// The <c>.part</c> reservation makes a collision with another job impossible, but nothing stops
    /// a person or another program from creating the file during what may be a multi-hour download.
    /// Failing there would delete the finished download and start it over, so a fresh name is
    /// resolved and the move retried -- exactly what deduplication would have done had the file
    /// existed when the job started.
    ///
    /// The filesystem is injected for the same reason it is in <see cref="OutputPathResolver"/>: the
    /// interesting behaviour is the retry, and it should be assertable without a real disk.
    ///
    /// An <see cref="IOException"/> that is not a collision -- and one that outlives
    /// <see cref="PublishAttempts"/> tries -- propagates. The caller leaves the finished
    /// <c>.part</c> in place rather than discarding it.
    /// </remarks>
    public static string Publish(
        string outputRoot,
        string requestedFileName,
        string tempPath,
        string finalPath,
        Func<string, bool> fileExists,
        Action<string, string> move)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(move);

        var destination = finalPath;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move(tempPath, destination);
                return destination;
            }
            catch (IOException) when (attempt < PublishAttempts && fileExists(destination))
            {
                destination = OutputPathResolver.Resolve(
                    outputRoot,
                    requestedFileName,
                    // Our own .part is not a collision: it is the file being published.
                    path => (!string.Equals(path + PartExtension, tempPath, StringComparison.Ordinal)
                            && fileExists(path + PartExtension))
                        || fileExists(path));
            }
        }
    }

    /// <summary>
    /// Deletes a partial file, ignoring failures.
    /// </summary>
    /// <param name="path">The file to remove.</param>
    public void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the partial download at {Path}", path);
        }
    }
}
