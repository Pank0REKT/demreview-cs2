using System.Numerics;
using DemReview.Domain;

namespace DemReview.Application.Review;

/// <summary>Дистанция дуэли.</summary>
public enum DuelRange
{
    /// <summary>До 500 units (~10 м).</summary>
    Close,

    /// <summary>500–1200 units (~10–23 м).</summary>
    Medium,

    /// <summary>Дальше 1200 units (~23 м).</summary>
    Long,
}

/// <summary>Класс оружия игрока в дуэли.</summary>
public enum WeaponClass
{
    Pistol,
    Smg,
    Rifle,
    Sniper,
    Heavy,
    Other,
}

/// <summary>Сторона угла: игрок выходил на противника или держал позицию.</summary>
public enum DuelStance
{
    /// <summary>Игрок двигался перед дуэлью — выходил на угол (пик).</summary>
    Peeking,

    /// <summary>Игрок стоял — держал угол.</summary>
    Holding,
}

/// <summary>Дуэль игрока: убийство противника или смерть от противника.</summary>
/// <param name="Distance">Расстояние между игроками, units.</param>
/// <param name="Weapon">Оружие игрока (без префикса weapon_).</param>
/// <param name="OpponentWeapon">Оружие противника.</param>
public sealed record Duel(
    int Round,
    int Tick,
    bool Won,
    ulong OpponentSteamId,
    float Distance,
    string Weapon,
    string OpponentWeapon,
    DuelStance Stance,
    bool Headshot)
{
    public DuelRange Range => DuelAnalysis.RangeOf(Distance);

    public WeaponClass WeaponClass => DuelAnalysis.ClassOf(Weapon);
}

/// <summary>Победы и поражения в группе дуэлей (по дистанции, оружию или стороне угла).</summary>
public sealed record DuelRecord<TKey>(TKey Key, int Wins, int Losses)
{
    public int Total => Wins + Losses;

    public double WinRate => Total == 0 ? 0 : Wins / (double)Total;
}

/// <summary>
/// Дуэли игрока: с какой дистанции, с каким оружием и с какой стороны угла (выходил или держал) он их выигрывает
/// и проигрывает. Дуэль — убийство из оружия (не гранатой и не бомбой) между игроком и противником.
/// </summary>
public static class DuelAnalysis
{
    public const float CloseRange = 500;
    public const float LongRange = 1200;

    /// <summary>Игрок сместился больше чем на столько за последнюю секунду перед дуэлью — он выходил на угол, units.</summary>
    private const float PeekDisplacement = 64;

    private const float PeekWindowSeconds = 1;

    public static IReadOnlyList<Duel> Analyze(MatchData match, ulong steamId)
    {
        var result = new List<Duel>();
        foreach (var kill in match.Kills.OrderBy(k => k.Tick))
        {
            if (kill.AttackerSteamId is not { } attacker || attacker == kill.VictimSteamId || !IsGunfight(kill.Weapon))
                continue;

            var won = attacker == steamId;
            if (!won && kill.VictimSteamId != steamId)
                continue;

            var opponent = won ? kill.VictimSteamId : attacker;
            var side = match.FindPlayer(steamId)?.SideInRound(kill.Round) ?? Side.None;
            if (side == Side.None || match.FindPlayer(opponent)?.SideInRound(kill.Round) != side.Opposite())
                continue;

            var attackerPosition = kill.AttackerPosition ?? LastSample(match, attacker, kill).Position;
            var playerSample = LastSample(match, steamId, kill);
            var opponentSample = LastSample(match, opponent, kill);
            result.Add(new Duel(
                kill.Round,
                kill.Tick,
                won,
                opponent,
                Vector3.Distance(attackerPosition, MistakeAnalysis.DeathPosition(match, kill)),
                won ? kill.Weapon : playerSample.Weapon ?? "",
                won ? opponentSample.Weapon ?? "" : kill.Weapon,
                StanceOf(match, steamId, kill),
                kill.Headshot));
        }
        return result;
    }

    public static IReadOnlyList<DuelRecord<DuelRange>> ByRange(IEnumerable<Duel> duels) =>
        Group(duels, d => d.Range, Enum.GetValues<DuelRange>());

    public static IReadOnlyList<DuelRecord<WeaponClass>> ByWeaponClass(IEnumerable<Duel> duels) =>
        Group(duels, d => d.WeaponClass, Enum.GetValues<WeaponClass>());

    public static IReadOnlyList<DuelRecord<DuelStance>> ByStance(IEnumerable<Duel> duels) =>
        Group(duels, d => d.Stance, Enum.GetValues<DuelStance>());

    /// <summary>Оружие игрока: сначала самое частое.</summary>
    public static IReadOnlyList<DuelRecord<string>> ByWeapon(IEnumerable<Duel> duels) =>
        duels.GroupBy(d => d.Weapon)
            .Select(g => new DuelRecord<string>(g.Key, g.Count(d => d.Won), g.Count(d => !d.Won)))
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

    public static DuelRange RangeOf(float distance) =>
        distance < CloseRange ? DuelRange.Close : distance <= LongRange ? DuelRange.Medium : DuelRange.Long;

    public static WeaponClass ClassOf(string weapon) => weapon switch
    {
        "glock" or "usp_silencer" or "usp_silencer_off" or "hkp2000" or "p250" or "tec9" or "fiveseven" or "cz75a" or "elite"
            or "deagle" or "revolver" => WeaponClass.Pistol,
        "mac10" or "mp9" or "mp7" or "mp5sd" or "ump45" or "p90" or "bizon" => WeaponClass.Smg,
        "ak47" or "m4a1" or "m4a1_silencer" or "m4a1_silencer_off" or "galilar" or "famas" or "sg556" or "aug" => WeaponClass.Rifle,
        "awp" or "ssg08" or "scar20" or "g3sg1" => WeaponClass.Sniper,
        "nova" or "xm1014" or "mag7" or "sawedoff" or "m249" or "negev" => WeaponClass.Heavy,
        _ => WeaponClass.Other,
    };

    /// <summary>Убийство из оружия, а не гранатой, огнём, бомбой или падением.</summary>
    private static bool IsGunfight(string weapon) =>
        weapon is not ("hegrenade" or "flashbang" or "smokegrenade" or "decoy" or "inferno" or "molotov" or "incgrenade"
            or "world" or "planted_c4" or "c4" or "");

    /// <summary>Пик или удержание: насколько игрок сместился за последнюю секунду перед дуэлью.</summary>
    private static DuelStance StanceOf(MatchData match, ulong steamId, KillEvent kill)
    {
        var from = kill.Tick - PeekWindowSeconds * match.TickRate;
        var samples = match.TrackOf(steamId).InRound(kill.Round).Where(s => s.Tick >= from && s.Tick <= kill.Tick).ToList();
        if (samples.Count < 2)
            return DuelStance.Holding;

        var moved = Vector2.Distance(new Vector2(samples[0].Position.X, samples[0].Position.Y),
            new Vector2(samples[^1].Position.X, samples[^1].Position.Y));
        return moved > PeekDisplacement ? DuelStance.Peeking : DuelStance.Holding;
    }

    private static PlayerSample LastSample(MatchData match, ulong steamId, KillEvent kill) =>
        match.TrackOf(steamId).InRound(kill.Round).LastOrDefault(s => s.Tick <= kill.Tick);

    private static List<DuelRecord<TKey>> Group<TKey>(IEnumerable<Duel> duels, Func<Duel, TKey> key, IEnumerable<TKey> order)
        where TKey : notnull
    {
        var groups = duels.GroupBy(key).ToDictionary(g => g.Key, g => (Wins: g.Count(d => d.Won), Losses: g.Count(d => !d.Won)));
        return order.Where(groups.ContainsKey).Select(k => new DuelRecord<TKey>(k, groups[k].Wins, groups[k].Losses)).ToList();
    }
}
