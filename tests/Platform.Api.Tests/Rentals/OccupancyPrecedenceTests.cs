using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Tests.Rentals;

public sealed class OccupancyPrecedenceTests
{
    [Theory]
    [InlineData("closed", false, 3)]
    [InlineData("lesson", true, 2)]
    [InlineData("open", false, 1)]
    [InlineData("event", true, 2)]
    [InlineData("buffer", false, 1)]
    public void Rank_matches_known_keys_and_custom_blocks_capacity(string key, bool blocks, int expected)
    {
        Assert.Equal(expected, OccupancyPrecedence.Rank(key, blocks));
    }

    [Fact]
    public void IntervalsOverlap_detects_interior_overlap_and_allows_touching_ends()
    {
        Assert.True(OccupancyPrecedence.IntervalsOverlap(
            new TimeOnly(8, 0), new TimeOnly(22, 0),
            new TimeOnly(18, 0), new TimeOnly(19, 0)));
        Assert.False(OccupancyPrecedence.IntervalsOverlap(
            new TimeOnly(8, 0), new TimeOnly(12, 0),
            new TimeOnly(12, 0), new TimeOnly(18, 0)));
        Assert.True(OccupancyPrecedence.IntervalsOverlap(
            new TimeOnly(8, 0), new TimeOnly(12, 0),
            new TimeOnly(11, 0), new TimeOnly(13, 0)));
    }

    [Fact]
    public void Compare_closed_beats_lesson_and_open()
    {
        Assert.True(OccupancyPrecedence.Compare("closed", true, "lesson", true) > 0);
        Assert.True(OccupancyPrecedence.Compare("lesson", true, "open", false) > 0);
    }

    [Fact]
    public void Compare_equal_rank_uses_higher_key_ordinal()
    {
        Assert.True(OccupancyPrecedence.Compare("zeta", true, "alpha", true) > 0);
        Assert.True(OccupancyPrecedence.Compare("lesson", true, "clinic", true) > 0);
    }
}
