using DemReview.Application.Players;

namespace DemReview.Tests.Players;

public class SteamIdsTests
{
    private const ulong Expected = 76561197960287930;

    [Theory]
    [InlineData("76561197960287930")]
    [InlineData("  76561197960287930  ")]
    [InlineData("https://steamcommunity.com/profiles/76561197960287930/")]
    [InlineData("steamcommunity.com/profiles/76561197960287930")]
    [InlineData("[U:1:22202]")]
    [InlineData("U:1:22202")]
    [InlineData("STEAM_0:0:11101")]
    [InlineData("STEAM_1:0:11101")]
    public void Parses_supported_formats(string input)
    {
        Assert.True(SteamIds.TryParse(input, out var id));
        Assert.Equal(Expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("76561197960265728")]
    [InlineData("https://steamcommunity.com/id/gabelogannewell")]
    [InlineData("[U:1:0]")]
    public void Rejects_invalid_input(string? input)
    {
        Assert.False(SteamIds.TryParse(input, out var id));
        Assert.Equal(0ul, id);
    }

    [Fact]
    public void Detects_vanity_url()
    {
        Assert.True(SteamIds.IsVanityUrl("https://steamcommunity.com/id/gabelogannewell/"));
        Assert.False(SteamIds.IsVanityUrl("https://steamcommunity.com/profiles/76561197960287930"));
    }
}
