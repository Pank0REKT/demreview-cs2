using DemReview.Application.Context;
using DemReview.Domain;

namespace DemReview.Application.Filters;

/// <summary>Группа гранат для фильтра (молотов и зажигательная — «огонь»).</summary>
public enum GrenadeFilterGroup
{
    HighExplosive,
    Flash,
    Smoke,
    Fire,
    Decoy,
}

/// <summary>
/// Фильтр анализа: раунд, сторона игрока, тип закупки команды игрока, тип гранаты.
/// null — без ограничения.
/// </summary>
public sealed record AnalysisFilter(int? Round = null, Side? Side = null, BuyType? TeamBuy = null, GrenadeFilterGroup? Grenade = null)
{
    public static AnalysisFilter None { get; } = new();

    public bool IsEmpty => Round is null && Side is null && TeamBuy is null && Grenade is null;

    /// <summary>Подходит ли раунд для игрока под фильтр (раунд, сторона, закупка).</summary>
    public bool IncludesRound(IMatchContext context, ulong steamId, int round)
    {
        if (Round is { } r && r != round)
            return false;

        var side = context.Match.FindPlayer(steamId)?.SideInRound(round) ?? Domain.Side.None;
        if (Side is { } s && s != side)
            return false;

        if (TeamBuy is { } buy && context.Economy(round)?.Of(side).Buy != buy)
            return false;

        return true;
    }

    /// <summary>Подходит ли граната под фильтр типа (раунд проверяется отдельно).</summary>
    public bool IncludesGrenade(GrenadeThrow grenade) => Grenade is not { } group || GroupOf(grenade.Type) == group;

    public static GrenadeFilterGroup GroupOf(GrenadeType type) => type switch
    {
        GrenadeType.HighExplosive => GrenadeFilterGroup.HighExplosive,
        GrenadeType.Flashbang => GrenadeFilterGroup.Flash,
        GrenadeType.Smoke => GrenadeFilterGroup.Smoke,
        GrenadeType.Decoy => GrenadeFilterGroup.Decoy,
        _ => GrenadeFilterGroup.Fire,
    };
}
