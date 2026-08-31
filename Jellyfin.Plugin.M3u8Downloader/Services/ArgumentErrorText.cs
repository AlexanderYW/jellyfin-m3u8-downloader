using System;

namespace Jellyfin.Plugin.M3u8Downloader.Services;

/// <summary>
/// Renders an <see cref="ArgumentException"/> as text fit to show an administrator.
/// </summary>
internal static class ArgumentErrorText
{
    /// <summary>The clause <see cref="ArgumentException.Message"/> appends when a parameter is named.</summary>
    private const string ParameterClause = " (Parameter '";

    /// <summary>
    /// Extracts just the sentence from an argument exception.
    /// </summary>
    /// <param name="exception">The exception to describe.</param>
    /// <returns>The message without its parameter-name clause.</returns>
    /// <remarks>
    /// The rejection reasons thrown by <see cref="OutputPathResolver"/> are written to be read by
    /// an admin in the dashboard, but <see cref="ArgumentException.Message"/> appends
    /// <c>(Parameter 'requestedFileName')</c> to whatever it is given. The parameter name is
    /// required by the analyzers and useless to the reader, so it is trimmed at the point of
    /// display rather than omitted at the throw site.
    /// </remarks>
    public static string Describe(ArgumentException exception)
    {
        var message = exception.Message;
        var clause = message.IndexOf(ParameterClause, StringComparison.Ordinal);

        return clause > 0 ? message[..clause].TrimEnd() : message;
    }
}
