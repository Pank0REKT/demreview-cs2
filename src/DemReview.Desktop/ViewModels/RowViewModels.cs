using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>Строка списка игроков.</summary>
public sealed class PlayerRowViewModel(PlayerInfo player)
{
    public PlayerInfo Player { get; } = player;

    public ulong SteamId => Player.SteamId;

    public string Name => Player.Name;

    public string SteamIdText => Player.IsBot ? "бот" : Player.SteamId.ToString();

    public string TeamText => $"{Player.TeamName} · {Texts.SideShort(Player.FinalSide)}";

    public Side Side => Player.FinalSide;

    public string KdaText => $"{Player.Kills} / {Player.Deaths} / {Player.Assists}";

    public string SidesByHalfText
    {
        get
        {
            var t = Player.SideByRound.Count(kv => kv.Value == Side.T);
            var ct = Player.SideByRound.Count(kv => kv.Value == Side.CT);
            return $"за T: {t} раунд(ов), за CT: {ct} раунд(ов)";
        }
    }
}

/// <summary>Строка таблицы раундов.</summary>
public sealed class RoundRowViewModel(RoundInfo round)
{
    public int Number => round.Number;

    public string Winner => Texts.SideShort(round.Winner);

    public string Reason => Texts.RoundEndReason(round.EndReason);

    public string ScoreBefore => $"{round.ScoreTBefore} : {round.ScoreCTBefore}";

    public string Duration
    {
        get
        {
            var seconds = Math.Max(0, round.EndTick - round.FreezeEndTick) / 64;
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }

    public string Overtime => round.IsOvertime ? "да" : "";
}

/// <summary>Русские названия значений доменной модели.</summary>
public static class Texts
{
    public static string SideShort(Side side) => side switch
    {
        Side.T => "T",
        Side.CT => "CT",
        _ => "—",
    };

    public static string RoundEndReason(RoundEndReason reason) => reason switch
    {
        Domain.RoundEndReason.TargetBombed => "Бомба взорвана",
        Domain.RoundEndReason.BombDefused => "Бомба обезврежена",
        Domain.RoundEndReason.CTsWin => "CT уничтожили T",
        Domain.RoundEndReason.TerroristsWin => "T уничтожили CT",
        Domain.RoundEndReason.RoundDraw => "Ничья",
        Domain.RoundEndReason.TargetSaved => "Время вышло",
        Domain.RoundEndReason.TerroristsSurrender => "T сдались",
        Domain.RoundEndReason.CTsSurrender => "CT сдались",
        _ => "Не завершён",
    };
}
