using System.Numerics;
using DemReview.Domain;

namespace DemReview.Tests.Context;

/// <summary>Построитель синтетического матча для тестов анализа: 2×N игроков, раунды по 60 с.</summary>
internal sealed class SyntheticMatch
{
    public const int FreezeTicks = 640;
    public const int RoundTicks = 64 * 60;

    private readonly List<(ulong Id, string Name, Func<int, Side> Side)> _players = [];
    private readonly List<RoundInfo> _rounds = [];
    private readonly List<KillEvent> _kills = [];
    private readonly List<DamageEvent> _damages = [];
    private readonly List<BombEvent> _bombs = [];
    private readonly List<GrenadeThrow> _grenades = [];
    private readonly Dictionary<(ulong, int), int> _equipment = [];
    private readonly Dictionary<(ulong, int), string[]> _routes = [];
    private readonly Dictionary<string, Vector3> _zonePositions = [];

    public SyntheticMatch Player(ulong id, Func<int, Side> side)
    {
        _players.Add((id, $"p{id}", side));
        return this;
    }

    /// <summary>Пять T (1–5) и пять CT (6–10); после раунда <paramref name="halfAfter"/> стороны меняются.</summary>
    public SyntheticMatch FiveVsFive(int halfAfter = 12)
    {
        for (ulong i = 1; i <= 10; i++)
        {
            var firstHalf = i <= 5 ? Side.T : Side.CT;
            Player(i, r => r <= halfAfter ? firstHalf : firstHalf.Opposite());
        }
        return this;
    }

    public SyntheticMatch Rounds(int count, Func<int, Side>? winner = null)
    {
        int t = 0, ct = 0;
        for (var r = 1; r <= count; r++)
        {
            var start = RoundStart(r);
            var w = winner?.Invoke(r) ?? Side.T;
            _rounds.Add(new RoundInfo(r, start, start + FreezeTicks, start + FreezeTicks + RoundTicks, w,
                w == Side.T ? RoundEndReason.TerroristsWin : RoundEndReason.CTsWin, t, ct, false));
            if (w == Side.T) t++; else ct++;
        }
        return this;
    }

    public static int RoundStart(int round) => (round - 1) * (FreezeTicks + RoundTicks + 100);

    /// <summary>Тик через <paramref name="seconds"/> после конца фризтайма раунда.</summary>
    public static int At(int round, float seconds) => RoundStart(round) + FreezeTicks + (int)(seconds * 64);

    public SyntheticMatch Equipment(int round, Func<ulong, int> value)
    {
        foreach (var p in _players)
            _equipment[(p.Id, round)] = value(p.Id);
        return this;
    }

    public SyntheticMatch Route(ulong player, int round, params string[] zones)
    {
        _routes[(player, round)] = zones;
        return this;
    }

    /// <summary>Координаты зоны: сэмплы в этой зоне получают эту позицию.</summary>
    public SyntheticMatch ZonePosition(string zone, float x, float y)
    {
        _zonePositions[zone] = new Vector3(x, y, 0);
        return this;
    }

    public SyntheticMatch Kill(int round, float seconds, ulong? attacker, ulong victim)
    {
        _kills.Add(new KillEvent(At(round, seconds), round, attacker, victim, null, "ak47", false, null, Vector3.Zero));
        return this;
    }

    public SyntheticMatch Damage(int round, float seconds, ulong attacker, ulong victim)
    {
        _damages.Add(new DamageEvent(At(round, seconds), round, attacker, victim, 27, "ak47"));
        return this;
    }

    public SyntheticMatch Bomb(int round, float seconds, BombEventType type, BombSite site = BombSite.A)
    {
        _bombs.Add(new BombEvent(At(round, seconds), round, type, site, Vector3.Zero, null));
        return this;
    }

    public SyntheticMatch Grenade(ulong thrower, int round, GrenadeType type)
    {
        _grenades.Add(new GrenadeThrow(_grenades.Count + 1, type, thrower, Side.T, round, At(round, 5), 5, Vector3.Zero, 0, 0,
            "", Vector3.Zero, At(round, 7), Vector3.One, []));
        return this;
    }

    public MatchData Build()
    {
        var tracks = new Dictionary<ulong, PlayerTrack>();
        foreach (var (id, _, side) in _players)
        {
            var samples = new List<PlayerSample>();
            foreach (var round in _rounds)
            {
                var zones = _routes.GetValueOrDefault((id, round.Number), ["Spawn"]);
                var death = _kills.FirstOrDefault(k => k.Round == round.Number && k.VictimSteamId == id);
                var equipment = _equipment.GetValueOrDefault((id, round.Number), 4500);
                for (var s = 0f; s <= 60; s += 0.25f)
                {
                    var tick = At(round.Number, s);
                    if (death is not null && tick > death.Tick)
                        break;
                    var zone = zones[Math.Min(zones.Length - 1, (int)(s / 5))];
                    samples.Add(new PlayerSample(tick, round.Number, s, _zonePositions.GetValueOrDefault(zone), 0, 0, 0, MovementFlags.None, "ak47",
                        100, 100, 800, equipment, zone));
                }
            }
            tracks[id] = new PlayerTrack(id, samples);
        }

        var players = _players
            .Select(p => new PlayerInfo(p.Id, p.Name, false, p.Side(_rounds.Count), "", 0, 0, 0,
                _rounds.ToDictionary(r => r.Number, r => p.Side(r.Number))))
            .ToList();

        return new MatchData("x.dem", "de_mirage", 64, new MatchScore("A", 0, "B", 0), _rounds, players, _kills, tracks, 16,
            _grenades, _bombs, _damages);
    }
}
