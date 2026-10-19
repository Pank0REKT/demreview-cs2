using System.Numerics;
using DemReview.Application.Context;
using DemReview.Application.Filters;
using DemReview.Application.Heatmaps;
using DemReview.Domain;
using DemReview.Tests.Context;

namespace DemReview.Tests.Heatmaps;

public class HeatmapTests
{
    private readonly HeatmapService _service = new();

    [Fact]
    public void Hottest_cell_is_where_points_concentrate()
    {
        var points = Enumerable.Repeat(new Vector2(512, 256), 50).Append(new Vector2(100, 900));
        var map = _service.Build(points, new HeatmapOptions(Size: 64, BlurRadius: 2));

        Assert.Equal(1f, map[32, 16], 3);
        Assert.True(map[6, 56] < 0.2f);
        Assert.Equal(51, map.PointCount);
    }

    [Fact]
    public void Values_are_normalized_to_unit_range()
    {
        var points = Enumerable.Range(0, 200).Select(i => new Vector2(i * 5 % 1024, i * 7 % 1024));
        var map = _service.Build(points, new HeatmapOptions(Size: 128));

        Assert.All(map.Values, v => Assert.InRange(v, 0f, 1f));
        Assert.Equal(1f, map.Values.Max(), 3);
    }

    [Fact]
    public void Points_outside_radar_are_ignored()
    {
        var map = _service.Build([new Vector2(-10, 5), new Vector2(2000, 10)], new HeatmapOptions(Size: 32));

        Assert.Equal(0, map.PointCount);
        Assert.All(map.Values, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Blur_preserves_total_mass_away_from_edges()
    {
        var size = 32;
        var grid = new float[size * size];
        grid[16 * size + 16] = 10;

        var blurred = HeatmapService.Blur(grid, size, 3);

        Assert.Equal(10f, blurred.Sum(), 2);
        Assert.True(blurred[16 * size + 16] < 10);
        Assert.True(blurred[16 * size + 18] > 0);
    }
}

public class AnalysisFilterTests
{
    private static IMatchContext Context() =>
        new RoundContextService().Create(new SyntheticMatch().FiveVsFive(halfAfter: 2).Rounds(4)
            .Equipment(2, _ => 800).Equipment(3, _ => 900).Equipment(4, _ => 5000).Build());

    [Fact]
    public void Empty_filter_includes_everything()
    {
        var context = Context();
        Assert.True(AnalysisFilter.None.IsEmpty);
        Assert.All(Enumerable.Range(1, 4), r => Assert.True(AnalysisFilter.None.IncludesRound(context, 1, r)));
    }

    [Fact]
    public void Side_filter_uses_player_side_in_round()
    {
        var context = Context();
        var filter = new AnalysisFilter(Side: Side.CT);

        // Игрок 1: T в раундах 1–2, CT в 3–4.
        Assert.Equal([3, 4], Enumerable.Range(1, 4).Where(r => filter.IncludesRound(context, 1, r)));
    }

    [Fact]
    public void Buy_filter_uses_team_buy_of_player()
    {
        var context = Context();
        var filter = new AnalysisFilter(TeamBuy: BuyType.Eco);

        // Раунд 1 и 3 — пистолетки (3-й — первый после смены сторон), 2 — эко, 4 — фулл-бай.
        Assert.Equal([2], Enumerable.Range(1, 4).Where(r => filter.IncludesRound(context, 1, r)));
    }

    [Fact]
    public void Round_filter_selects_single_round()
    {
        var filter = new AnalysisFilter(Round: 3);
        Assert.Equal([3], Enumerable.Range(1, 4).Where(r => filter.IncludesRound(Context(), 1, r)));
    }

    [Theory]
    [InlineData(GrenadeType.Molotov, true)]
    [InlineData(GrenadeType.Incendiary, true)]
    [InlineData(GrenadeType.Smoke, false)]
    public void Fire_group_includes_molotov_and_incendiary(GrenadeType type, bool expected)
    {
        var grenade = new GrenadeThrow(1, type, 1, Side.T, 1, 0, 0, Vector3.Zero, 0, 0, "", Vector3.Zero, null, null, []);
        Assert.Equal(expected, new AnalysisFilter(Grenade: GrenadeFilterGroup.Fire).IncludesGrenade(grenade));
    }
}
