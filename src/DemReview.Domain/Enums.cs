namespace DemReview.Domain;

/// <summary>Сторона игрока в раунде.</summary>
public enum Side
{
    None = 0,
    T = 2,
    CT = 3,
}

/// <summary>Причина окончания раунда (значения совпадают с CSRoundEndReason игры).</summary>
public enum RoundEndReason
{
    Unknown = -1,
    TargetBombed = 1,
    BombDefused = 7,
    CTsWin = 8,
    TerroristsWin = 9,
    RoundDraw = 10,
    TargetSaved = 12,
    TerroristsSurrender = 17,
    CTsSurrender = 18,
}

public static class SideExtensions
{
    public static Side Opposite(this Side side) => side switch
    {
        Side.T => Side.CT,
        Side.CT => Side.T,
        _ => Side.None,
    };
}
