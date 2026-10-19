using System.Numerics;
using DemReview.Domain;

namespace DemReview.Infrastructure.Parsing;

/// <summary>
/// Собирает <see cref="MatchData"/> из потока событий демки. Не зависит от DemoFile.Net,
/// поэтому покрывается unit-тестами на синтетических данных.
/// </summary>
/// <remarks>
/// Особенности реальных демок, которые здесь учитываются:
/// <list type="bullet">
/// <item>до начала матча есть предматчевый раунд и разминка — всё до последнего begin_new_match отбрасывается;</item>
/// <item>раунды разминки отбрасываются;</item>
/// <item>при откате раунда (бэкап) раунды с номером ≥ нового перезаписываются;</item>
/// <item>игроки идентифицируются по ключу (SteamID), что учитывает реконнекты; сторона фиксируется на каждый раунд.</item>
/// </list>
/// </remarks>
internal sealed class MatchBuilder
{
    private readonly List<RoundInfo> _rounds = [];
    private readonly List<KillEvent> _kills = [];
    private readonly Dictionary<ulong, PlayerState> _players = [];
    private OpenRound? _current;
    private int _pendingStartTick = -1;

    public int? CurrentRoundNumber => _current?.Number ?? _rounds.LastOrDefault()?.Number;

    /// <summary>Начало нового матча (begin_new_match): всё предыдущее — предматчевые раунды.</summary>
    public void BeginNewMatch()
    {
        _rounds.Clear();
        _kills.Clear();
        _current = null;
        foreach (var player in _players.Values)
            player.Sides.Clear();
    }

    public void RoundStart(int tick) => _pendingStartTick = tick;

    /// <summary>Конец фризтайма — с этого момента раунд считается начавшимся.</summary>
    public void FreezeEnd(int tick, int number, int scoreT, int scoreCT, bool isOvertime, bool isWarmup,
        IEnumerable<PlayerSnapshot> players)
    {
        if (isWarmup)
            return;

        // Откат раунда (восстановление бэкапа): переписываем раунды начиная с этого номера.
        _rounds.RemoveAll(r => r.Number >= number);
        _kills.RemoveAll(k => k.Round >= number);
        foreach (var player in _players.Values)
            player.Sides.Remove(number);

        var startTick = _pendingStartTick >= 0 && _pendingStartTick <= tick ? _pendingStartTick : tick;
        _current = new OpenRound(number, startTick, tick, scoreT, scoreCT, isOvertime);
        _pendingStartTick = -1;

        foreach (var snapshot in players)
            ObservePlayer(snapshot, number);
    }

    public void RoundEnd(int tick, Side winner, RoundEndReason reason)
    {
        if (_current is not { } round)
            return;

        _rounds.Add(round.Close(tick, winner, reason));
        _current = null;
    }

    /// <summary>Обновляет сведения об игроке (ник, сторона) вне начала раунда.</summary>
    public void ObservePlayer(PlayerSnapshot snapshot, int? round = null)
    {
        if (snapshot.Side == Side.None)
            return;

        if (!_players.TryGetValue(snapshot.Key, out var state))
            _players[snapshot.Key] = state = new PlayerState(snapshot.Key);

        state.Name = snapshot.Name;
        state.IsBot = snapshot.IsBot;
        state.LastSide = snapshot.Side;
        if (round is { } number)
            state.Sides[number] = snapshot.Side;
    }

    public void Kill(int tick, ulong? attacker, ulong victim, ulong? assister, string weapon, bool headshot,
        Vector3? attackerPosition, Vector3 victimPosition)
    {
        // Убийства после конца раунда (добивания) относим к последнему раунду.
        if (CurrentRoundNumber is not { } round)
            return;

        _kills.Add(new KillEvent(tick, round, attacker, victim, assister, weapon, headshot, attackerPosition, victimPosition));
    }

    public MatchData Build(string sourcePath, string mapName, int tickRate, int lastTick,
        string teamTName, string teamCTName, int finalScoreT, int finalScoreCT)
    {
        var rounds = _rounds.ToList();
        // Демка оборвалась до конца раунда.
        if (_current is { } open)
            rounds.Add(open.Close(Math.Max(lastTick, open.FreezeEndTick), Side.None, RoundEndReason.Unknown));

        var players = _players.Values
            .Where(p => p.Sides.Count > 0)
            .Select(p => BuildPlayer(p, rounds, teamTName, teamCTName))
            .OrderBy(p => p.FinalSide)
            .ThenByDescending(p => p.Kills)
            .ToList();

        return new MatchData(sourcePath, mapName, tickRate,
            new MatchScore(teamTName, finalScoreT, teamCTName, finalScoreCT),
            rounds, players, _kills.ToList());
    }

    private PlayerInfo BuildPlayer(PlayerState state, IReadOnlyList<RoundInfo> rounds, string teamTName, string teamCTName)
    {
        var finalSide = rounds.Count > 0 && state.Sides.TryGetValue(rounds[^1].Number, out var lastRoundSide)
            ? lastRoundSide
            : state.LastSide;

        Side SideOf(ulong key, int round) =>
            _players.TryGetValue(key, out var p) && p.Sides.TryGetValue(round, out var s) ? s : Side.None;

        var kills = 0;
        var deaths = 0;
        var assists = 0;
        foreach (var kill in _kills)
        {
            if (kill.VictimSteamId == state.Key)
                deaths++;

            if (kill.AttackerSteamId == state.Key && kill.VictimSteamId != state.Key)
            {
                // Убийство тиммейта вычитается, как на табло игры.
                var teamKill = SideOf(kill.VictimSteamId, kill.Round) == SideOf(state.Key, kill.Round);
                kills += teamKill ? -1 : 1;
            }

            if (kill.AssisterSteamId == state.Key)
                assists++;
        }

        return new PlayerInfo(state.Key, state.Name, state.IsBot, finalSide,
            finalSide == Side.T ? teamTName : finalSide == Side.CT ? teamCTName : "",
            kills, deaths, assists, new Dictionary<int, Side>(state.Sides));
    }

    private sealed record OpenRound(int Number, int StartTick, int FreezeEndTick, int ScoreT, int ScoreCT, bool IsOvertime)
    {
        public RoundInfo Close(int endTick, Side winner, RoundEndReason reason) =>
            new(Number, StartTick, FreezeEndTick, endTick, winner, reason, ScoreT, ScoreCT, IsOvertime);
    }

    private sealed class PlayerState(ulong key)
    {
        public ulong Key { get; } = key;
        public string Name { get; set; } = "";
        public bool IsBot { get; set; }
        public Side LastSide { get; set; }
        public Dictionary<int, Side> Sides { get; } = [];
    }
}

/// <summary>Состояние игрока в момент наблюдения.</summary>
internal readonly record struct PlayerSnapshot(ulong Key, string Name, bool IsBot, Side Side);
