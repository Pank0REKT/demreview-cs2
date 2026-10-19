using System.Text.Json;
using DemReview.Application.Updates;
using DemReview.Infrastructure.Updates;
using DemReview.Tests.Parsing;

namespace DemReview.Tests.Updates;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V0.10.1-beta.2", "0.10.1")]
    [InlineData("v2.0.0+build.5", "2.0.0")]
    public void Release_tags_are_parsed(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), ReleaseVersion.Parse(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void Invalid_tags_are_rejected(string? tag) => Assert.Null(ReleaseVersion.Parse(tag));

    [Fact]
    public void Newer_release_is_detected_ignoring_revision()
    {
        Assert.True(ReleaseVersion.IsNewer(new Version(0, 2, 0), new Version(0, 1, 0, 0)));
        Assert.True(ReleaseVersion.IsNewer(new Version(0, 10, 0), new Version(0, 9, 9)));
        Assert.False(ReleaseVersion.IsNewer(new Version(0, 1, 0), new Version(0, 1, 0, 0)));
        Assert.False(ReleaseVersion.IsNewer(new Version(0, 1, 0), new Version(0, 2, 0)));
    }

    [Theory]
    [InlineData("https://github.com/someone/demreview", true, "someone", "demreview")]
    [InlineData("https://github.com/some-one/dem.review.git", true, "some-one", "dem.review")]
    [InlineData("https://github.com/OWNER/demreview", false, "OWNER", "demreview")]
    [InlineData("https://gitlab.com/someone/demreview", false, "", "")]
    [InlineData("", false, "", "")]
    public void Repository_address_is_parsed(string url, bool configured, string owner, string name)
    {
        var app = new AppInfo(new Version(0, 1, 0), url);

        Assert.Equal(configured, app.TryGetGitHubRepository(out var o, out var n));
        Assert.Equal(owner, o);
        Assert.Equal(name, n);
    }

    [Fact]
    public void Latest_release_response_is_evaluated()
    {
        const string json = """{"tag_name":"v0.2.0","name":"0.2.0","html_url":"https://github.com/someone/demreview/releases/tag/v0.2.0","draft":false}""";

        var release = GitHubUpdateChecker.ParseRelease(json);

        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal(UpdateStatus.UpdateAvailable, GitHubUpdateChecker.Evaluate(release, new Version(0, 1, 0, 0)).Status);
        Assert.Equal(UpdateStatus.UpToDate, GitHubUpdateChecker.Evaluate(release, new Version(0, 2, 0, 0)).Status);
    }

    [Fact]
    public void Release_with_foreign_link_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            GitHubUpdateChecker.ParseRelease("""{"tag_name":"v9.0.0","html_url":"https://example.com/malware"}"""));
        Assert.ThrowsAny<JsonException>(() => GitHubUpdateChecker.ParseRelease("not json"));
    }

    [Fact]
    public async Task Unconfigured_repository_is_not_requested()
    {
        var checker = new GitHubUpdateChecker(new AppInfo(new Version(0, 1, 0), "https://github.com/OWNER/demreview"),
            new CollectingLogger<GitHubUpdateChecker>());

        Assert.Equal(UpdateStatus.NotConfigured, (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status);
    }
}
