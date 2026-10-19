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
    private readonly Dictionary<ulong, List<PlayerSample>> _samples = [];
    private readonly Dictionary<ulong, PlayerState> _players = [];
    private readonly List<GrenadeState> _grenades = [];
    private int _nextGrenadeId = 1;
    private OpenRound? _current;
    private int _pendingStartTick = -1;

    public int? CurrentRoundNumber => _current?.Number ?? _rounds.LastOrDefault()?.Number;

    /// <summary>Идущий раунд (после конца фризтайма и до конца раунда) — номер и тик конца фризтайма.</summary>
    public (int Number, int FreezeEndTick)? LiveRound => _current is { } r ? (r.Number, r.FreezeEndTick) : null;

    /// <summary>Начало нового матча (begin_new_match): всё предыдущее — предматчевые раунды.</summary>
    public void BeginNewMatch()
    {
        _rounds.Clear();
        _kills.Clear();
        _samples.Clear();
        _grenades.Clear();
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
        foreach (var list in _samples.Values)
            list.RemoveAll(s => s.Round >= number);
        _grenades.RemoveAll(g => g.Round >= number);
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

    /// <summary>Бросок гранаты. Возвращает идентификатор для последующей детонации или null, если раунд не идёт.</summary>
    public int? ThrowGrenade(GrenadeType type, ulong thrower, int tick, Vector3 throwerPosition, float pitch, float yaw,
        string place, Vector3 projectileStart)
    {
        if (CurrentRoundNumber is not { } round)
            return null;

        var freezeEnd = _current?.FreezeEndTick ?? _rounds.LastOrDefault()?.FreezeEndTick ?? tick;
        var side = _players.TryGetValue(thrower, out var p) && p.Sides.TryGetValue(round, out var s) ? s : Side.None;
        var state = new GrenadeState(_nextGrenadeId++, type, thrower, side, round, tick,
            (tick - freezeEnd) / 64f, throwerPosition, pitch, yaw, place, projectileStart);
        _grenades.Add(state);
        return state.Id;
    }

    public void DetonateGrenade(int id, int tick, Vector3 position)
    {
        if (_grenades.Find(g => g.Id == id) is { DetonateTick: null } grenade)
        {
            grenade.DetonateTick = tick;
            grenade.DetonatePosition = position;
        }
    }

    public void AddFlashVictim(int id, ulong victim, float duration)
    {
        if (_grenades.Find(g => g.Id == id) is not { } grenade || duration <= 0)
            return;

        var victimSide = _players.TryGetValue(victim, out var p) && p.Sides.TryGetValue(grenade.Round, out var s) ? s : Side.None;
        var isSelf = victim == grenade.Thrower;
        grenade.Victims.Add(new FlashVictim(victim, duration, isSelf || victimSide == grenade.Side, isSelf));
    }

    private List<GrenadeThrow> BuildGrenades() =>
        _grenades
            .Select(g => new GrenadeThrow(g.Id, g.Type, g.Thrower, g.Side, g.Round, g.ThrowTick, g.ThrowTime,
                g.ThrowerPosition, g.Pitch, g.Yaw, g.Place, g.ProjectileStart, g.DetonateTick, g.DetonatePosition,
                g.Victims.ToList()))
            .ToList();

    private sealed record GrenadeState(int Id, GrenadeType Type, ulong Thrower, Side Side, int Round, int ThrowTick,
        float ThrowTime, Vector3 ThrowerPosition, float Pitch, float Yaw, string Place, Vector3 ProjectileStart)
    {
        public int? DetonateTick { get; set; }
        public Vector3? DetonatePosition { get; set; }
        public List<FlashVictim> Victims { get; } = [];
    }

    /// <summary>Сэмпл позиции игрока; принимается только во время идущего раунда.</summary>
    public void AddSample(ulong player, PlayerSample sample)
    {
        if (_current is null || sample.Round != _current.Number)
            return;

        if (!_samples.TryGetValue(player, out var list))
            _samples[player] = list = [];
        list.Add(sample);
    }

    public MatchData Build(string sourcePath, string mapName, int tickRate, int lastTick,
        string teamTName, string teamCTName, int finalScoreT, int finalScoreCT, int sampleTickStep = 16)
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

        var tracks = players.ToDictionary(p => p.SteamId,
            p => new PlayerTrack(p.SteamId, _samples.TryGetValue(p.SteamId, out var list) ? list.ToArray() : []));

        return new MatchData(sourcePath, mapName, tickRate,
            new MatchScore(teamTName, finalScoreT, teamCTName, finalScoreCT),
            rounds, players, _kills.ToList(), tracks, sampleTickStep, BuildGrenades());
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
