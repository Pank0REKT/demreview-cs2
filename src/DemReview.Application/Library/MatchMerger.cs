using DemReview.Domain;

namespace DemReview.Application.Library;

/// <summary>Демка для склейки: разобранный матч и время, когда он сыгран (для порядка и подписи).</summary>
public sealed record MatchSource(MatchData Match, DateTime PlayedUtc);

/// <summary>
/// Склеивает несколько демок одной карты в один сводный матч: раунды нумеруются подряд, тики сдвигаются,
/// чтобы раунды разных демок не пересекались. Весь анализ (паттерны, роли, радар, тепловые карты) работает
/// со сводным матчем как с обычным — и считает по всем раундам сразу.
/// </summary>
public static class MatchMerger
{
    /// <summary>Промежуток тиков между демками — раунды соседних демок не соприкасаются.</summary>
    private const int TickGap = 100_000;

    public static MatchData Merge(IReadOnlyList<MatchSource> sources)
    {
        if (sources.Count == 0)
            throw new ArgumentException("Нужна хотя бы одна демка.", nameof(sources));
        var maps = sources.Select(s => s.Match.MapName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (maps.Count > 1)
            throw new ArgumentException($"Склеиваются только демки одной карты: {string.Join(", ", maps)}.", nameof(sources));

        var ordered = sources.OrderBy(s => s.PlayedUtc).ToList();
        var first = ordered[0].Match;

        var rounds = new List<RoundInfo>();
        var kills = new List<KillEvent>();
        var grenades = new List<GrenadeThrow>();
        var bombs = new List<BombEvent>();
        var damages = new List<DamageEvent>();
        var samples = new Dictionary<ulong, List<PlayerSample>>();
        var players = new Dictionary<ulong, PlayerAccumulator>();
        var parts = new List<MatchPart>();

        var roundOffset = 0;
        var tickOffset = 0;
        var grenadeOffset = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var (match, played) = (ordered[index].Match, ordered[index].PlayedUtc);
            // Боты опознаются по нику: «Bot Ivan» из разных демок — разные игроки.
            var bots = match.Players.Where(p => p.IsBot).Select(p => p.SteamId).ToHashSet();
            ulong Id(ulong steamId) => index > 0 && bots.Contains(steamId) ? BotId(steamId, index) : steamId;
            ulong? OptionalId(ulong? steamId) => steamId is { } id ? Id(id) : null;
            int Round(int round) => round + roundOffset;
            int Tick(int tick) => tick + tickOffset;

            foreach (var r in match.Rounds)
            {
                rounds.Add(r with
                {
                    Number = Round(r.Number),
                    StartTick = Tick(r.StartTick),
                    FreezeEndTick = Tick(r.FreezeEndTick),
                    EndTick = Tick(r.EndTick),
                    // Тики сдвинуты — тик демки для demo_gototick должен остаться прежним.
                    DemoTickOffset = r.DemoTickOffset + tickOffset,
                });
            }

            kills.AddRange(match.Kills.Select(k => k with
            {
                Tick = Tick(k.Tick),
                Round = Round(k.Round),
                AttackerSteamId = OptionalId(k.AttackerSteamId),
                VictimSteamId = Id(k.VictimSteamId),
                AssisterSteamId = OptionalId(k.AssisterSteamId),
            }));
            grenades.AddRange(match.Grenades.Select(g => g with
            {
                Id = g.Id + grenadeOffset,
                ThrowerSteamId = Id(g.ThrowerSteamId),
                Round = Round(g.Round),
                ThrowTick = Tick(g.ThrowTick),
                DetonateTick = g.DetonateTick is { } t ? Tick(t) : null,
                FlashVictims = g.FlashVictims.Select(v => v with { SteamId = Id(v.SteamId) }).ToList(),
            }));
            bombs.AddRange(match.Bombs.Select(b => b with
            {
                Tick = Tick(b.Tick),
                Round = Round(b.Round),
                PlayerSteamId = OptionalId(b.PlayerSteamId),
            }));
            damages.AddRange(match.Damages.Select(d => d with
            {
                Tick = Tick(d.Tick),
                Round = Round(d.Round),
                AttackerSteamId = OptionalId(d.AttackerSteamId),
                VictimSteamId = Id(d.VictimSteamId),
            }));

            foreach (var (steamId, track) in match.Tracks)
            {
                var id = Id(steamId);
                if (!samples.TryGetValue(id, out var list))
                    samples[id] = list = [];
                list.AddRange(track.Samples.Select(s => s with { Tick = Tick(s.Tick), Round = Round(s.Round) }));
            }

            foreach (var player in match.Players)
            {
                var id = Id(player.SteamId);
                if (!players.TryGetValue(id, out var acc))
                    players[id] = acc = new PlayerAccumulator(id);
                acc.Add(player, roundOffset);
            }

            if (match.Rounds.Count > 0)
            {
                parts.Add(new MatchPart(match.SourcePath, match.FinalScore, Round(match.Rounds.Min(r => r.Number)),
                    Round(match.Rounds.Max(r => r.Number)), roundOffset, played));
            }

            roundOffset += match.Rounds.Count == 0 ? 0 : match.Rounds.Max(r => r.Number);
            tickOffset += MaxTick(match) + TickGap;
            grenadeOffset += match.Grenades.Count == 0 ? 0 : match.Grenades.Max(g => g.Id) + 1;
        }

        var tracks = samples.ToDictionary(kv => kv.Key, kv => new PlayerTrack(kv.Key, kv.Value));
        return new MatchData(
            first.SourcePath,
            first.MapName,
            first.TickRate,
            new MatchScore("", 0, "", 0),
            rounds,
            players.Values.Select(p => p.Build()).ToList(),
            kills,
            tracks,
            first.SampleTickStep,
            grenades,
            bombs,
            damages) { Parts = parts };
    }

    private static int MaxTick(MatchData match)
    {
        var max = match.Rounds.Count == 0 ? 0 : match.Rounds.Max(r => r.EndTick);
        foreach (var track in match.Tracks.Values)
        {
            if (track.Samples.Count > 0)
                max = Math.Max(max, track.Samples[^1].Tick);
        }
        return max;
    }

    /// <summary>Ключ бота из следующей демки: старшие биты — номер демки (ключи ботов — хеш ника, не SteamID).</summary>
    private static ulong BotId(ulong id, int index) => id ^ ((ulong)index << 48);

    private sealed class PlayerAccumulator(ulong steamId)
    {
        private readonly Dictionary<int, Side> _sides = [];
        private PlayerInfo? _last;
        private int _kills, _deaths, _assists;

        public void Add(PlayerInfo player, int roundOffset)
        {
            _last = player;
            _kills += player.Kills;
            _deaths += player.Deaths;
            _assists += player.Assists;
            foreach (var (round, side) in player.SideByRound)
                _sides[round + roundOffset] = side;
        }

        /// <summary>Ник, команда и сторона — по последней демке.</summary>
        public PlayerInfo Build() => _last! with
        {
            SteamId = steamId,
            Kills = _kills,
            Deaths = _deaths,
            Assists = _assists,
            SideByRound = _sides,
        };
    }
}
