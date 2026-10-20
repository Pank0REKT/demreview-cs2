using CommunityToolkit.Mvvm.ComponentModel;
using DemReview.Application.Context;
using DemReview.Application.Export;
using DemReview.Application.Localization;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>Строка списка игроков.</summary>
public sealed partial class PlayerRowViewModel(PlayerInfo player) : ObservableObject
{
    public PlayerInfo Player { get; } = player;

    /// <summary>Это основной игрок — сам пользователь.</summary>
    [ObservableProperty]
    public partial bool IsMe { get; set; }

    public ulong SteamId => Player.SteamId;

    public string Name => Player.Name;

    public string SteamIdText => Player.IsBot ? Loc.T("бот", "bot") : Player.SteamId.ToString();

    public string TeamText => $"{ProfileExporter.TeamName(Player.TeamName, Player.FinalSide)} · {Texts.SideShort(Player.FinalSide)}";

    public Side Side => Player.FinalSide;

    public string KdaText => $"{Player.Kills} / {Player.Deaths} / {Player.Assists}";

    public string SidesByHalfText
    {
        get
        {
            var t = Player.SideByRound.Count(kv => kv.Value == Side.T);
            var ct = Player.SideByRound.Count(kv => kv.Value == Side.CT);
            return Loc.T($"за T: {t} раунд(ов), за CT: {ct} раунд(ов)", $"T: {t} round(s), CT: {ct} round(s)");
        }
    }
}

/// <summary>Строка таблицы раундов.</summary>
public sealed class RoundRowViewModel(RoundInfo round, RoundEconomy? economy = null)
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

    public string Overtime => round.IsOvertime ? Loc.T("да", "yes") : "";

    public string BuyT => economy is null ? "" : $"{Labels.Buy(economy.T.Buy)} (${economy.T.AverageEquipment})";

    public string BuyCT => economy is null ? "" : $"{Labels.Buy(economy.CT.Buy)} (${economy.CT.AverageEquipment})";
}

/// <summary>Подписи значений доменной модели на текущем языке.</summary>
public static class Texts
{
    public static string SideShort(Side side) => side switch
    {
        Side.T => "T",
        Side.CT => "CT",
        _ => "—",
    };

    public static string GrenadeName(GrenadeType type) => Labels.Grenade(type);

    public static string ZoneOrUnknown(string zone) => string.IsNullOrEmpty(zone) ? "?" : zone;

    /// <summary>Название оружия из событий демки (ak47, m4a1_silencer, inferno…) в привычном виде.</summary>
    public static string Weapon(string name) => name switch
    {
        "ak47" => "AK-47",
        "m4a1" => "M4A4",
        "m4a1_silencer" or "m4a1_silencer_off" => "M4A1-S",
        "awp" => "AWP",
        "deagle" => "Desert Eagle",
        "glock" => "Glock-18",
        "usp_silencer" or "usp_silencer_off" => "USP-S",
        "hkp2000" => "P2000",
        "p250" => "P250",
        "tec9" => "Tec-9",
        "fiveseven" => "Five-SeveN",
        "cz75a" => "CZ75-Auto",
        "elite" => "Dual Berettas",
        "revolver" => "R8",
        "mac10" => "MAC-10",
        "mp9" => "MP9",
        "mp7" => "MP7",
        "mp5sd" => "MP5-SD",
        "ump45" => "UMP-45",
        "p90" => "P90",
        "bizon" => "PP-Bizon",
        "galilar" => "Galil AR",
        "famas" => "FAMAS",
        "sg556" => "SG 553",
        "aug" => "AUG",
        "ssg08" => "SSG 08",
        "scar20" => "SCAR-20",
        "g3sg1" => "G3SG1",
        "nova" => "Nova",
        "xm1014" => "XM1014",
        "mag7" => "MAG-7",
        "sawedoff" => "Sawed-Off",
        "m249" => "M249",
        "negev" => "Negev",
        "taser" => "Zeus x27",
        "hegrenade" => "HE",
        "flashbang" => Loc.T("флешка", "flashbang"),
        "smokegrenade" => Loc.T("дым", "smoke"),
        "decoy" => Loc.T("ложная", "decoy"),
        "inferno" or "molotov" or "incgrenade" => Loc.T("огонь", "fire"),
        "world" => Loc.T("мир", "world"),
        "planted_c4" or "c4" => Loc.T("бомба", "bomb"),
        _ when name.StartsWith("knife", StringComparison.Ordinal) || name.StartsWith("bayonet", StringComparison.Ordinal) => Loc.T("нож", "knife"),
        _ => name,
    };

    /// <summary>Время от конца фризтайма в формате м:сс.</summary>
    public static string Time(float seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    public static string RoundEndReason(RoundEndReason reason) => reason switch
    {
        Domain.RoundEndReason.TargetBombed => Loc.T("Бомба взорвана", "Bomb exploded"),
        Domain.RoundEndReason.BombDefused => Loc.T("Бомба обезврежена", "Bomb defused"),
        Domain.RoundEndReason.CTsWin => Loc.T("CT уничтожили T", "CTs eliminated Ts"),
        Domain.RoundEndReason.TerroristsWin => Loc.T("T уничтожили CT", "Ts eliminated CTs"),
        Domain.RoundEndReason.RoundDraw => Loc.T("Ничья", "Draw"),
        Domain.RoundEndReason.TargetSaved => Loc.T("Время вышло", "Time ran out"),
        Domain.RoundEndReason.TerroristsSurrender => Loc.T("T сдались", "Ts surrendered"),
        Domain.RoundEndReason.CTsSurrender => Loc.T("CT сдались", "CTs surrendered"),
        _ => Loc.T("Не завершён", "Not finished"),
    };
}
