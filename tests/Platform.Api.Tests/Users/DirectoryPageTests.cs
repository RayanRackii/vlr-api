using Platform.Api.Common;

namespace Platform.Api.Tests.Users;

public sealed class DirectoryPageTests
{
    [Fact]
    public void Blank_search_is_unfiltered_and_defaults_are_accepted()
    {
        Assert.True(DirectoryPage.TryCreate(null, 1, 20, out var page, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Null(page.Term);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);

        Assert.True(DirectoryPage.TryCreate("  ", 1, DirectoryPage.MaxPageSize, out var blank, out _));
        Assert.Null(blank.Term);
    }

    [Theory]
    [InlineData("a")]
    [InlineData(" x ")]
    public void Search_shorter_than_two_characters_is_rejected(string q)
    {
        Assert.False(DirectoryPage.TryCreate(q, 1, 20, out _, out var error));
        Assert.Contains("2", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_size_above_50_is_rejected()
    {
        Assert.False(DirectoryPage.TryCreate(null, 1, 51, out _, out var error));
        Assert.Contains("50", error, StringComparison.Ordinal);
        Assert.False(DirectoryPage.TryCreate("ana", 0, 20, out _, out _));
    }

    [Fact]
    public void Contains_pattern_escapes_backslash_percent_and_underscore()
    {
        Assert.Equal("%10\\%\\_%", DirectoryPage.ToContainsPattern("10%_"));
        Assert.Equal("%a\\\\b%", DirectoryPage.ToContainsPattern("a\\b"));
    }
}
