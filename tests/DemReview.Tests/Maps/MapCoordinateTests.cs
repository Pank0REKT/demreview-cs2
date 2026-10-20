using System.Numerics;
using DemReview.Application.Maps;
using DemReview.Domain;
using DemReview.Infrastructure.Maps;

namespace DemReview.Tests.Maps;

public class MapCoordinateTests
{
    private readonly MapCoordinateConverter _converter = new();
    private static readonly MapConfig Mirage = new("de_mirage", "Mirage", -3230, 1713, 5.0f);

    [Fact]
    public void Origin_of_overview_maps_to_top_left_corner()
    {
        var radar = _converter.ToRadar(Mirage, new Vector3(-3230, 1713, 0));
        Assert.Equal(Vector2.Zero, radar);
    }

    [Fact]
    public void X_grows_right_and_Y_grows_down()
    {
        var radar = _converter.ToRadar(Mirage, new Vector3(-3230 + 500, 1713 - 1000, 0));
        Assert.Equal(new Vector2(100, 200), radar);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-1500.5f, 320.25f)]
    [InlineData(1800f, -2400f)]
    public void Conversion_round_trips(float x, float y)
    {
        var world = _converter.ToWorld(Mirage, _converter.ToRadar(Mirage, new Vector3(x, y, 0)));
        Assert.Equal(x, world.X, 0.01f);
        Assert.Equal(y, world.Y, 0.01f);
    }

    [Fact]
    public void Lower_level_is_detected_by_height()
    {
        var nuke = new MapConfig("de_nuke", "Nuke", -3453, 2887, 7, LowerLevelMaxZ: -495);
        Assert.True(_converter.IsLowerLevel(nuke, new Vector3(0, 0, -600)));
        Assert.False(_converter.IsLowerLevel(nuke, new Vector3(0, 0, -400)));
        Assert.False(_converter.IsLowerLevel(Mirage, new Vector3(0, 0, -600)));
    }

    [Fact]
    public void Fit_to_positions_places_all_points_inside_radar()
    {
        Vector3[] points = [new(-5000, 300, 0), new(2500, -4100, 0), new(100, 100, 0)];
        var map = _converter.FitToPositions("de_unknown", points);

        Assert.All(points, p =>
        {
            var r = _converter.ToRadar(map, p);
            Assert.InRange(r.X, 0, MapConfig.RadarSize);
            Assert.InRange(r.Y, 0, MapConfig.RadarSize);
        });
    }

    [Fact]
    public void Embedded_config_contains_all_required_maps()
    {
        var provider = new EmbeddedMapConfigProvider();
        string[] required = ["de_mirage", "de_inferno", "de_dust2", "de_nuke", "de_ancient", "de_anubis", "de_train", "de_overpass", "de_vertigo", "de_cache"];

        Assert.All(required, name =>
        {
            var map = provider.Find(name);
            Assert.NotNull(map);
            Assert.True(map.Scale > 0);
            Assert.Equal(name, map.Name);
        });
    }

    [Theory]
    [InlineData("de_mirage")]
    [InlineData("DE_MIRAGE")]
    [InlineData("workshop/123456/de_mirage")]
    public void Map_name_lookup_is_case_insensitive_and_strips_workshop_path(string name)
    {
        Assert.Equal("de_mirage", new EmbeddedMapConfigProvider().Find(name)?.Name);
    }
}
