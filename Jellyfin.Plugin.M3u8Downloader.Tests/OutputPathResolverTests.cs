using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class OutputPathResolverTests
{
    private static readonly string _root = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "m3u8-downloader-tests"));

    private static string Resolve(string name, params string[] existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return OutputPathResolver.Resolve(_root, name, set.Contains);
    }

    [Fact]
    public void Resolve_AddsMkvExtension()
    {
        Assert.Equal(Path.Combine(_root, "episode.mkv"), Resolve("episode"));
    }

    [Fact]
    public void Resolve_ReplacesOtherExtension()
    {
        Assert.Equal(Path.Combine(_root, "episode.mkv"), Resolve("episode.mp4"));
    }

    [Fact]
    public void Resolve_KeepsExistingMkvExtension()
    {
        Assert.Equal(Path.Combine(_root, "episode.mkv"), Resolve("episode.mkv"));
    }

    [Fact]
    public void Resolve_DoesNotMistakeATrailingWordForAnExtension()
    {
        // "Cut" is not an extension; stripping it would silently mangle the name.
        Assert.Equal(
            Path.Combine(_root, "Movie (2019). Directors Cut.mkv"),
            Resolve("Movie (2019). Directors Cut"));
    }

    [Fact]
    public void Resolve_AllowsSubdirectories()
    {
        Assert.Equal(
            Path.Combine(_root, "My Show", "Season 01", "S01E01.mkv"),
            Resolve("My Show/Season 01/S01E01"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape")]
    [InlineData("./../escape")]
    public void Resolve_StripsTraversalAndStaysUnderRoot(string name)
    {
        var resolved = Resolve(name);

        Assert.StartsWith(_root + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsAbsoluteUnixPath()
    {
        var ex = Assert.Throws<ArgumentException>(() => Resolve("/etc/passwd"));

        Assert.Contains("absolute path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReplacesControlCharacters()
    {
        var name = "bad" + ((char)1) + "name";

        Assert.Equal(Path.Combine(_root, "bad_name.mkv"), Resolve(name));
    }

    [Fact]
    public void Resolve_ReplacesPlatformInvalidCharacters()
    {
        // Exercises whatever Path.GetInvalidFileNameChars reports on the host, so the test stays
        // meaningful on Windows (where '?' and '*' are invalid) without failing on Unix, where
        // the only invalid characters are NUL and the separator.
        var offender = Array.FindLast(
            Path.GetInvalidFileNameChars(),
            c => !char.IsControl(c) && c != '/' && c != '\\');

        if (offender == default)
        {
            return;
        }

        Assert.Equal(Path.Combine(_root, "bad_name.mkv"), Resolve("bad" + offender + "name"));
    }

    [Fact]
    public void Resolve_TrimsTrailingDotsAndSpaces()
    {
        Assert.Equal(Path.Combine(_root, "name.mkv"), Resolve("  name . . "));
    }

    [Fact]
    public void Resolve_TruncatesOverlongSegments()
    {
        var resolved = Resolve(new string('a', 400));

        Assert.True(Path.GetFileName(resolved).Length <= 200);
        Assert.EndsWith(".mkv", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DeduplicatesAgainstExistingFiles()
    {
        var first = Path.Combine(_root, "episode.mkv");
        var second = Path.Combine(_root, "episode (2).mkv");

        Assert.Equal(second, Resolve("episode", first));
        Assert.Equal(Path.Combine(_root, "episode (3).mkv"), Resolve("episode", first, second));
    }

    [Fact]
    public void Resolve_RejectsEmptyOutputRoot()
    {
        Assert.Throws<ArgumentException>(
            () => OutputPathResolver.Resolve(string.Empty, "episode", _ => false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_RejectsEmptyName(string name)
    {
        Assert.Throws<ArgumentException>(() => Resolve(name));
    }

    [Fact]
    public void Resolve_RejectsNameThatSanitizesToNothing()
    {
        Assert.Throws<ArgumentException>(() => Resolve("../.."));
    }

    [Fact]
    public void Resolve_StaysInsideTheRoot()
    {
        // A root of "<root>" must never be treated as containing a sibling "<root>-other".
        var resolved = Resolve("episode");

        Assert.StartsWith(_root + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_TreatsAnInFlightDownloadAsTaken()
    {
        // How concurrent downloads avoid colliding: the running job's ".part" reservation is fed
        // to the predicate, so the next job with the same name resolves somewhere else instead of
        // both picking the free path and racing to publish it.
        var reserved = Path.Combine(_root, "episode.mkv.part");
        var existing = new HashSet<string>(new[] { reserved }, StringComparer.OrdinalIgnoreCase);

        var resolved = OutputPathResolver.Resolve(
            _root,
            "episode",
            path => existing.Contains(path) || existing.Contains(path + ".part"));

        Assert.Equal(Path.Combine(_root, "episode (2).mkv"), resolved);
    }
}
