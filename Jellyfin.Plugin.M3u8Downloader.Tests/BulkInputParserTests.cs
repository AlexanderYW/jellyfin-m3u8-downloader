using System.Linq;
using Jellyfin.Plugin.M3u8Downloader.Services;
using Xunit;

namespace Jellyfin.Plugin.M3u8Downloader.Tests;

public class BulkInputParserTests
{
    [Fact]
    public void Parse_PipeDelimited_UsesSuppliedName()
    {
        var (accepted, rejected) = BulkInputParser.Parse("https://example.com/a.m3u8 | My Show S01E01");

        Assert.Empty(rejected);
        var job = Assert.Single(accepted);
        Assert.Equal("https://example.com/a.m3u8", job.Url);
        Assert.Equal("My Show S01E01", job.RequestedFileName);
    }

    [Fact]
    public void Parse_BareUrl_DerivesNameFromLastMeaningfulSegment()
    {
        var (accepted, _) = BulkInputParser.Parse("https://example.com/videos/my-episode.m3u8");

        Assert.Equal("my-episode", Assert.Single(accepted).RequestedFileName);
    }

    [Theory]
    [InlineData("https://example.com/My%20Show/index.m3u8", "My Show")]
    [InlineData("https://example.com/My%20Show/master.m3u8", "My Show")]
    [InlineData("https://example.com/clip/playlist.m3u8", "clip")]
    public void Parse_GenericPlaylistName_FallsBackToParentSegment(string url, string expected)
    {
        var (accepted, _) = BulkInputParser.Parse(url);

        Assert.Equal(expected, Assert.Single(accepted).RequestedFileName);
    }

    [Fact]
    public void Parse_NoUsefulPathSegments_FallsBackToOrdinal()
    {
        var (accepted, _) = BulkInputParser.Parse("https://example.com/index.m3u8");

        Assert.Equal("download-1", Assert.Single(accepted).RequestedFileName);
    }

    [Fact]
    public void Parse_SkipsBlanksAndComments()
    {
        var (accepted, rejected) = BulkInputParser.Parse(
            "\n# a comment\n\nhttps://example.com/a.m3u8 | one\n   \n# another\n");

        Assert.Empty(rejected);
        Assert.Equal("one", Assert.Single(accepted).RequestedFileName);
    }

    [Theory]
    [InlineData("ftp://example.com/a.m3u8")]
    [InlineData("file:///etc/passwd")]
    public void Parse_RejectsNonHttpSchemes(string url)
    {
        var (accepted, rejected) = BulkInputParser.Parse(url);

        Assert.Empty(accepted);
        Assert.Contains("Unsupported scheme", Assert.Single(rejected).Error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMalformedUrl()
    {
        var (accepted, rejected) = BulkInputParser.Parse("not a url | name");

        Assert.Empty(accepted);
        Assert.Single(rejected);
    }

    [Fact]
    public void Parse_RejectsMissingUrlBeforePipe()
    {
        var (_, rejected) = BulkInputParser.Parse("  | just-a-name");

        Assert.Contains("Missing URL", Assert.Single(rejected).Error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MixedBatch_KeepsGoodLinesAndReportsBadOnesWithLineNumbers()
    {
        var (accepted, rejected) = BulkInputParser.Parse(
            "https://example.com/a.m3u8 | one\nnope\nhttps://example.com/b.m3u8 | two");

        Assert.Equal(new[] { "one", "two" }, accepted.Select(j => j.RequestedFileName));
        Assert.Equal(2, Assert.Single(rejected).LineNumber);
    }

    [Fact]
    public void Parse_HandlesCrLfLineEndings()
    {
        var (accepted, _) = BulkInputParser.Parse(
            "https://example.com/a.m3u8 | one\r\nhttps://example.com/b.m3u8 | two");

        Assert.Equal(2, accepted.Count);
    }

    [Fact]
    public void Parse_EmptyNameAfterPipe_FallsBackToDerivedName()
    {
        var (accepted, _) = BulkInputParser.Parse("https://example.com/videos/clip.m3u8 |   ");

        Assert.Equal("clip", Assert.Single(accepted).RequestedFileName);
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsNothing()
    {
        var (accepted, rejected) = BulkInputParser.Parse("   \n  \n");

        Assert.Empty(accepted);
        Assert.Empty(rejected);
    }

    // ---------------------------------------------------------------- destination folder

    [Fact]
    public void Parse_Folder_IsAppliedToEveryLineInTheBatch()
    {
        var (accepted, _) = BulkInputParser.Parse(
            "https://example.com/a.m3u8 | S01E01\nhttps://example.com/b.m3u8 | S01E02",
            folder: "Shows/Breaking Bad/Season 01");

        Assert.Equal(
            new[] { "Shows/Breaking Bad/Season 01/S01E01", "Shows/Breaking Bad/Season 01/S01E02" },
            accepted.Select(j => j.RequestedFileName));
    }

    [Fact]
    public void Parse_Folder_AppliesToDerivedNamesToo()
    {
        var (accepted, _) = BulkInputParser.Parse(
            "https://example.com/videos/my-episode.m3u8",
            folder: "Archive");

        Assert.Equal("Archive/my-episode", Assert.Single(accepted).RequestedFileName);
    }

    [Fact]
    public void Parse_Folder_ComposesWithASubfolderOnTheLine()
    {
        var (accepted, _) = BulkInputParser.Parse(
            "https://example.com/a.m3u8 | Season 01/S01E01",
            folder: "Shows/Breaking Bad");

        Assert.Equal("Shows/Breaking Bad/Season 01/S01E01", Assert.Single(accepted).RequestedFileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoFolder_LeavesNamesUntouched(string? folder)
    {
        var (accepted, _) = BulkInputParser.Parse("https://example.com/a.m3u8 | Episode", folder);

        Assert.Equal("Episode", Assert.Single(accepted).RequestedFileName);
    }

    [Fact]
    public void Parse_Folder_TrailingSeparatorDoesNotDoubleUp()
    {
        var (accepted, _) = BulkInputParser.Parse("https://example.com/a.m3u8 | Episode", folder: "Archive/");

        Assert.Equal("Archive/Episode", Assert.Single(accepted).RequestedFileName);
    }

    // ---------------------------------------------------------------- name validation

    [Fact]
    public void Parse_ValidatorRejection_IsReportedAgainstTheOriginalLine()
    {
        var (accepted, rejected) = BulkInputParser.Parse(
            "https://example.com/a.m3u8 | Good\nhttps://example.com/b.m3u8 | Bad",
            folder: null,
            validateName: name => name == "Bad" ? "No output directory is configured." : null);

        Assert.Equal("Good", Assert.Single(accepted).RequestedFileName);

        var problem = Assert.Single(rejected);
        Assert.Equal(2, problem.LineNumber);
        Assert.Equal("https://example.com/b.m3u8 | Bad", problem.Text);
        Assert.Equal("No output directory is configured.", problem.Error);
    }

    [Fact]
    public void Parse_ValidatorSeesTheNameWithItsFolderApplied()
    {
        string? seen = null;

        BulkInputParser.Parse(
            "https://example.com/a.m3u8 | Episode",
            folder: "Archive",
            validateName: name =>
            {
                seen = name;
                return null;
            });

        Assert.Equal("Archive/Episode", seen);
    }
}
