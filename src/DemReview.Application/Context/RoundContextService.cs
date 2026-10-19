using DemReview.Domain;

namespace DemReview.Application.Context;

public sealed class RoundContextService : IRoundContextService
{
    public IMatchContext Create(MatchData match, EconomyOptions? options = null) =>
        new MatchContext(match, options ?? new EconomyOptions());

    private sealed class MatchContext : IMatchContext
    {
        private readonly EconomyOptions _options;
        private readonly Dictionary<int, RoundEconomy> _economy = [];
        private readonly Dictionary<int, List<(ulong Player, Side Side)>> _startLineup = [];
        private readonly Dictionary<int, List<KillEvent>> _kills;
        private readonly Dictionary<int, List<BombEvent>> _bombs;

        public MatchContext(MatchData match, EconomyOptions options)
        {
            Match = match;
            _options = options;
            _kills = match.Kills.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => g.OrderBy(k => k.Tick).ToList());
            _bombs = match.Bombs.GroupBy(b => b.Round).ToDictionary(g => g.Key, g => g.OrderBy(b => b.Tick).ToList());

            var previousSides = new Dictionary<ulong, Side>();
            foreach (var round in match.Rounds)
            {
                var lineup = match.Players
                    .Select(p => (Player: p.SteamId, Side: p.SideInRound(round.Number)))
                    .Where(x => x.Side != Side.None && match.TrackOf(x.Player).InRound(round.Number).Any())
                    .ToList();
                _startLineup[round.Number] = lineup;

                // Пистолетный раунд — первый раунд матча и первый после смены сторон (кроме овертайма).
                // Смена сторон — большинство игроков, известных в прошлом раунде, перешли на другую сторону.
                var known = lineup.Where(x => previousSides.ContainsKey(x.Player)).ToList();
                var switched = known.Count(x => previousSides[x.Player] != x.Side);
                var sidesSwitched = known.Count > 0 && switched * 2 > known.Count;
                var isPistol = !round.IsOvertime && (round.Number == 1 || sidesSwitched);
                foreach (var (player, side) in lineup)
                    previousSides[player] = side;

                _economy[round.Number] = BuildEconomy(match, round, lineup, isPistol);
            }
        }

        public MatchData Match { get; }

        public RoundEconomy? Economy(int round) => _economy.GetValueOrDefault(round);

        public int AliveCount(int round, Side side, int tick)
        {
            if (!_startLineup.TryGetValue(round, out var lineup))
                return 0;

            var players = lineup.Where(x => x.Side == side).Select(x => x.Player).ToHashSet();
            var dead = _kills.GetValueOrDefault(round, [])
                .Where(k => k.Tick <= tick && players.Contains(k.VictimSteamId))
                .Select(k => k.VictimSteamId)
                .Distinct()
                .Count();
            return players.Count - dead;
        }

        public bool IsAlive(ulong steamId, int round, int tick) =>
            _startLineup.TryGetValue(round, out var lineup) && lineup.Any(x => x.Player == steamId) &&
            !_kills.GetValueOrDefault(round, []).Any(k => k.Tick <= tick && k.VictimSteamId == steamId);

        public (BombState State, BombSite Site) BombAt(int round, int tick)
        {
            var state = BombState.NotPlanted;
            var site = BombSite.Unknown;
            foreach (var e in _bombs.GetValueOrDefault(round, []).TakeWhile(e => e.Tick <= tick))
            {
                site = e.Site != BombSite.Unknown ? e.Site : site;
                state = e.Type switch
                {
                    BombEventType.Planted => BombState.Planted,
                    BombEventType.Defused => BombState.Defused,
                    _ => BombState.Exploded,
                };
            }
            return (state, site);
        }

        public MomentContext At(ulong steamId, int round, int tick)
        {
            var info = Match.FindRound(round);
            var player = Match.FindPlayer(steamId);
            var side = player?.SideInRound(round) ?? Side.None;
            var economy = Economy(round);
            var sample = Match.TrackOf(steamId).InRound(round).LastOrDefault(s => s.Tick <= tick);
            var alive = IsAlive(steamId, round, tick);
            var (bomb, site) = BombAt(round, tick);
            var (scoreUs, scoreThem) = info is null ? (0, 0)
                : side == Side.CT ? (info.ScoreCTBefore, info.ScoreTBefore) : (info.ScoreTBefore, info.ScoreCTBefore);

            return new MomentContext(
                round,
                tick,
                info is null ? 0 : (tick - info.FreezeEndTick) / (float)Match.TickRate,
                side,
                alive,
                sample.Money,
                sample.EquipmentValue,
                economy?.PlayerBuy.GetValueOrDefault(steamId) ?? BuyType.Eco,
                economy?.Of(side).Buy ?? BuyType.Eco,
                economy?.Of(side.Opposite()).Buy ?? BuyType.Eco,
                economy?.Of(side).AverageEquipment ?? 0,
                AliveCount(round, side, tick) - (alive ? 1 : 0),
                AliveCount(round, side.Opposite(), tick),
                bomb,
                site,
                scoreUs,
                scoreThem);
        }

        private RoundEconomy BuildEconomy(MatchData match, RoundInfo round, List<(ulong Player, Side Side)> lineup, bool isPistol)
        {
            var equipment = new Dictionary<ulong, int>();
            var money = new Dictionary<ulong, int>();
            foreach (var (playerId, _) in lineup)
            {
                var samples = match.TrackOf(playerId).InRound(round.Number).ToList();
                var buyWindow = samples.Where(s => s.TimeInRound <= _options.BuyWindowSeconds).ToList();
                equipment[playerId] = (buyWindow.Count > 0 ? buyWindow : samples.Take(1)).Max(s => s.EquipmentValue);
                money[playerId] = samples[0].Money;
            }

            TeamEconomy Team(Side side)
            {
                var players = lineup.Where(x => x.Side == side).Select(x => x.Player).ToList();
                var average = players.Count == 0 ? 0 : (int)players.Average(p => equipment[p]);
                return new TeamEconomy(side, isPistol ? BuyType.Pistol : Classify(average), average,
                    players.Sum(p => money[p]), players.Count);
            }

            var playerBuy = equipment.ToDictionary(kv => kv.Key, kv => isPistol ? BuyType.Pistol : Classify(kv.Value));
            return new RoundEconomy(round.Number, isPistol, Team(Side.T), Team(Side.CT), equipment, money, playerBuy);
        }

        private BuyType Classify(int equipment) =>
            equipment < _options.EcoMaxEquipment ? BuyType.Eco
            : equipment >= _options.FullMinEquipment ? BuyType.FullBuy
            : BuyType.Force;
    }
}
