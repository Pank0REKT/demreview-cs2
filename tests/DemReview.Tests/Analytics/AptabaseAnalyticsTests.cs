using System.Text.Json;
using DemReview.Infrastructure.Analytics;

namespace DemReview.Tests.Analytics;

public class AptabaseAnalyticsTests
{
    [Theory]
    [InlineData("A-EU-0000000000", "https://eu.aptabase.com/api/v0/events")]
    [InlineData("A-US-0000000000", "https://us.aptabase.com/api/v0/events")]
    [InlineData("A-SH-0000000000", null)]
    [InlineData("garbage", null)]
    public void Endpoint_FromKeyRegion(string key, string? expected) =>
        Assert.Equal(expected, AptabaseAnalytics.GetEndpoint(key)?.ToString());

    [Fact]
    public void Payload_IsArrayWithOneEvent()
    {
        var system = new AptabaseAnalytics.SystemProps(false, "Windows", "10.0.19045", "ru-RU", "0.2.0");
        var props = new Dictionary<string, object> { ["map"] = "de_mirage", ["parse_seconds"] = 12.5 };

        var json = AptabaseAnalytics.BuildPayload("demo_loaded", props, new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), "123", system);

        using var document = JsonDocument.Parse(json);
        var e = Assert.Single(document.RootElement.EnumerateArray().ToList());
        Assert.Equal("demo_loaded", e.GetProperty("eventName").GetString());
        Assert.Equal("123", e.GetProperty("sessionId").GetString());
        Assert.StartsWith("2026-10-20T12:00:00", e.GetProperty("timestamp").GetString());
        Assert.Equal("Windows", e.GetProperty("systemProps").GetProperty("osName").GetString());
        Assert.False(e.GetProperty("systemProps").GetProperty("isDebug").GetBoolean());
        Assert.Equal("de_mirage", e.GetProperty("props").GetProperty("map").GetString());
        Assert.Equal(12.5, e.GetProperty("props").GetProperty("parse_seconds").GetDouble());
    }

    [Fact]
    public void Payload_WithoutProps_OmitsProps()
    {
        var system = new AptabaseAnalytics.SystemProps(true, "Linux", "6.8", "en-US", "0.2.0");
        var json = AptabaseAnalytics.BuildPayload("app_started", null, DateTime.UtcNow, "1", system);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement[0].TryGetProperty("props", out _));
    }
}
