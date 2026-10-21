using System.Numerics;

namespace DemReview.Domain;

/// <summary>Результат разбора одной демки.</summary>
public sealed record MatchData(
    string SourcePath,
    string MapName,
    int TickRate,
    MatchScore FinalScore,
    IReadOnlyList<RoundInfo> Rounds,
    IReadOnlyList<PlayerInfo> Players,
    IReadOnlyList<KillEvent> Kills,
    IReadOnlyDictionary<ulong, PlayerTrack> Tracks,
    int SampleTickStep,
    IReadOnlyList<GrenadeThrow> Grenades,
    IReadOnlyList<BombEvent> Bombs,
    IReadOnlyList<DamageEvent> Damages)
{
    /// <summary>Демки, из которых склеен сводный матч (раунды перенумерованы подряд); пусто — обычная одиночная демка.</summary>
    public IReadOnlyList<MatchPart> Parts { get; init; } = [];

    public bool IsCombined => Parts.Count > 0;

    /// <summary>Демка сводного матча, к которой относится раунд.</summary>
    public MatchPart? PartOf(int round) => Parts.FirstOrDefault(p => round >= p.FirstRound && round <= p.LastRound);

    public IEnumerable<GrenadeThrow> GrenadesOf(ulong steamId) => Grenades.Where(g => g.ThrowerSteamId == steamId);

    public PlayerInfo? FindPlayer(ulong steamId) => Players.FirstOrDefault(p => p.SteamId == steamId);

    public PlayerTrack TrackOf(ulong steamId) =>
        Tracks.TryGetValue(steamId, out var track) ? track : new PlayerTrack(steamId, []);

    public RoundInfo? FindRound(int number) => Rounds.FirstOrDefault(r => r.Number == number);
}

/// <summary>Одна демка в сводном матче: её раунды — <paramref name="FirstRound"/>…<paramref name="LastRound"/> в общей нумерации.</summary>
/// <param name="RoundOffset">Сколько прибавлено к номерам раундов демки.</param>
public sealed record MatchPart(string SourcePath, MatchScore Score, int FirstRound, int LastRound, int RoundOffset, DateTime PlayedUtc)
{
    public string FileName => Path.GetFileName(SourcePath);
}

/// <summary>Итоговый счёт. Команды названы по составу на конец матча.</summary>
public sealed record MatchScore(string TeamTName, int TeamTScore, string TeamCTName, int TeamCTScore);

/// <summary>Раунд матча. Тики — игровые (GameTick).</summary>
public sealed record RoundInfo(
    int Number,
    int StartTick,
    int FreezeEndTick,
    int EndTick,
    Side Winner,
    RoundEndReason EndReason,
    int ScoreTBefore,
    int ScoreCTBefore,
    bool IsOvertime)
{
    public bool ContainsTick(int tick) => tick >= StartTick && tick <= EndTick;
}

/// <summary>Игрок матча. Идентифицируется по SteamID (боты — по синтетическому ID).</summary>
public sealed record PlayerInfo(
    ulong SteamId,
    string Name,
    bool IsBot,
    Side FinalSide,
    string TeamName,
    int Kills,
    int Deaths,
    int Assists,
    IReadOnlyDictionary<int, Side> SideByRound)
{
    public Side SideInRound(int round) => SideByRound.TryGetValue(round, out var side) ? side : Side.None;
}

/// <summary>Убийство (игрок → игрок).</summary>
public sealed record KillEvent(
    int Tick,
    int Round,
    ulong? AttackerSteamId,
    ulong VictimSteamId,
    ulong? AssisterSteamId,
    string Weapon,
    bool Headshot,
    Vector3? AttackerPosition,
    Vector3 VictimPosition);
