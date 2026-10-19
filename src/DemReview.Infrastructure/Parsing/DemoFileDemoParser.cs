using System.Numerics;
using DemoFile;
using DemoFile.Game.Cs;
using DemoFile.GameStatic;
using DemReview.Application.Localization;
using DemReview.Application.Parsing;
using DemReview.Domain;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Parsing;

/// <summary>Разбор демок CS2 через DemoFile.Net (DemoFile.Game.Cs).</summary>
public sealed class DemoFileDemoParser(ILogger<DemoFileDemoParser> logger) : IDemoParser
{
    private static readonly byte[] Cs2Magic = "PBDEMS2\0"u8.ToArray();
    private static readonly byte[] Source1Magic = "HL2DEMO\0"u8.ToArray();

    public async Task<MatchData> ParseAsync(string path, ParseOptions options, IProgress<ParseProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenDemo(path);
        CheckMagic(stream);

        var demo = new CsDemoParser();
        var builder = new MatchBuilder();
        Subscribe(demo, builder);
        SubscribeSampling(demo, builder, Math.Clamp(options.SampleTickStep, 1, 128));
        SubscribeGrenades(demo, builder);
        SubscribeBombAndDamage(demo, builder);

        var reader = DemoFileReader.Create(demo, stream);
        var lastReported = -1;
        reader.OnProgress = e =>
        {
            // OnProgress вызывается на каждую команду демки — сообщаем только при смене процента.
            var percent = (int)(e.ProgressRatio * 100);
            if (percent == lastReported)
                return;
            lastReported = percent;
            progress?.Report(new ParseProgress(e.ProgressRatio, Loc.T($"Разбор демки: {percent}%", $"Parsing demo: {percent}%")));
        };

        try
        {
            await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDemoException ex)
        {
            logger.LogWarning(ex, "Неподдерживаемая демка {Path}", path);
            throw new DemoParseException(Loc.T("Файл не является демкой CS2 или повреждён.", "The file is not a CS2 demo or is corrupted."), ex);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException
                                       or IndexOutOfRangeException or ArgumentException or InvalidOperationException
                                       or NullReferenceException or FormatException)
        {
            logger.LogError(ex, "Ошибка разбора демки {Path}", path);
            throw new DemoParseException(Loc.T("Демка повреждена или записана в неподдерживаемом формате — разбор прерван.", "The demo is corrupted or uses an unsupported format — parsing stopped."), ex);
        }

        if (reader.IsIncompleteFile)
            logger.LogWarning("Демка {Path} записана не полностью (обрыв записи) — данные до места обрыва", path);

        var mapName = (demo.ServerInfo?.MapName ?? demo.FileHeader?.MapName ?? "").Trim().ToLowerInvariant();
        var teamT = demo.TeamTerrorist;
        var teamCT = demo.TeamCounterTerrorist;
        var match = builder.Build(path, mapName, CsDemoParser.TickRate, (int)demo.CurrentGameTick.Value,
            TeamName(teamT.ClanTeamname), TeamName(teamCT.ClanTeamname),
            teamT.Score, teamCT.Score, options.SampleTickStep);

        if (match.Rounds.Count == 0)
            throw new DemoParseException(Loc.T("В демке не найдено ни одного сыгранного раунда.", "No played rounds were found in the demo."));

        LogScoreboardMismatch(demo, match);
        progress?.Report(new ParseProgress(1, Loc.T("Готово", "Done")));
        return match;
    }

    private static FileStream OpenDemo(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DemoParseException(Loc.T($"Не удалось открыть файл: {ex.Message}", $"Could not open the file: {ex.Message}"), ex);
        }
    }

    private static void CheckMagic(FileStream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        var read = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        stream.Position = 0;

        if (read == magic.Length && magic.SequenceEqual(Cs2Magic))
            return;

        if (read == magic.Length && magic.SequenceEqual(Source1Magic))
            throw new DemoParseException(Loc.T("Это демка CS:GO (Source 1). Поддерживаются только демки CS2.", "This is a CS:GO (Source 1) demo. Only CS2 demos are supported."));

        throw new DemoParseException(Loc.T("Файл не является демкой CS2 (неверная сигнатура .dem).", "The file is not a CS2 demo (invalid .dem signature)."));
    }

    private static void Subscribe(CsDemoParser demo, MatchBuilder builder)
    {
        int Tick() => (int)demo.CurrentGameTick.Value;

        demo.Source1GameEvents.BeginNewMatch += _ => builder.BeginNewMatch();
        demo.Source1GameEvents.RoundStart += _ => builder.RoundStart(Tick());

        demo.Source1GameEvents.RoundFreezeEnd += _ =>
        {
            var rules = demo.GameRules;
            builder.FreezeEnd(
                Tick(),
                rules.TotalRoundsPlayed + 1,
                demo.TeamTerrorist.Score,
                demo.TeamCounterTerrorist.Score,
                rules.OvertimePlaying > 0,
                rules.WarmupPeriod,
                demo.PlayersIncludingDisconnected.Select(Snapshot).ToList());
        };

        demo.Source1GameEvents.RoundEnd += e =>
        {
            var reason = Enum.IsDefined(typeof(RoundEndReason), e.Reason) ? (RoundEndReason)e.Reason : RoundEndReason.Unknown;
            builder.RoundEnd(Tick(), ToSide(e.Winner), reason);
        };

        // Ник мог смениться, игрок — переподключиться: обновляем сведения при смене команды.
        demo.Source1GameEvents.PlayerTeam += e =>
        {
            if (e.Player is { } controller)
                builder.ObservePlayer(Snapshot(controller) with { Side = ToSide(e.Team) });
        };

        demo.Source1GameEvents.PlayerDeath += e =>
        {
            if (e.Player is not { } victim)
                return;

            builder.Kill(
                Tick(),
                e.Attacker is { } attacker ? KeyOf(attacker) : null,
                KeyOf(victim),
                e.Assister is { } assister ? KeyOf(assister) : null,
                e.Weapon,
                e.Headshot,
                e.AttackerPawn is { } attackerPawn ? ToVector(attackerPawn.Origin) : null,
                e.PlayerPawn is { } victimPawn ? ToVector(victimPawn.Origin) : Vector3.Zero);
        };
    }

    /// <summary>
    /// Гранаты: бросок — создание снаряда (CBaseCSGrenadeProjectile.Create срабатывает и для наследников),
    /// детонация HE/flash/smoke/decoy — по entity id снаряда в событии, ослепления — по entity id флешки.
    /// У inferno_startburn entity id — это CInferno, а не снаряд, поэтому молотов связывается с огнём
    /// по бросившему (OwnerEntity у CInferno) и удалению снаряда в тот же тик рядом с точкой возгорания.
    /// </summary>
    private static void SubscribeGrenades(CsDemoParser demo, MatchBuilder builder)
    {
        int Tick() => (int)demo.CurrentGameTick.Value;
        var byEntity = new Dictionary<int, int>();
        var molotovEnds = new List<(int Tick, Vector3 Position, ulong Thrower, int GrenadeId)>();
        var infernoStarts = new List<(int Tick, Vector3 Position, ulong Thrower)>();

        demo.EntityEvents.CBaseCSGrenadeProjectile.Create += projectile =>
        {
            if (GrenadeTypeOf(projectile) is not { } type ||
                demo.GetEntityByHandle(projectile.ThrowerHandle) is not CCSPlayerPawn { Controller: { } controller } pawn)
                return;

            var eye = pawn.EyeAngles;
            var id = builder.ThrowGrenade(type, KeyOf(controller), Tick(), ToVector(pawn.Origin), eye.Pitch, eye.Yaw,
                pawn.LastPlaceName ?? "", ToVector(projectile.InitialPosition));
            var entity = (int)projectile.EntityIndex.Value;
            if (id is { } grenadeId)
                byEntity[entity] = grenadeId;
            else
                byEntity.Remove(entity);
        };

        void Detonate(int entityId, float x, float y, float z)
        {
            if (byEntity.TryGetValue(entityId, out var id))
                builder.DetonateGrenade(id, Tick(), new Vector3(x, y, z));
        }

        demo.Source1GameEvents.HegrenadeDetonate += e => Detonate(e.Entityid, e.X, e.Y, e.Z);
        demo.Source1GameEvents.FlashbangDetonate += e => Detonate(e.Entityid, e.X, e.Y, e.Z);
        demo.Source1GameEvents.SmokegrenadeDetonate += e => Detonate(e.Entityid, e.X, e.Y, e.Z);
        demo.Source1GameEvents.DecoyStarted += e => Detonate(e.Entityid, e.X, e.Y, e.Z);

        demo.Source1GameEvents.PlayerBlind += e =>
        {
            if (e.Player is { } victim && byEntity.TryGetValue(e.Entityid, out var id))
                builder.AddFlashVictim(id, KeyOf(victim), e.BlindDuration);
        };

        // Молотов: снаряд удаляется и в тот же тик появляется CInferno. Порядок событий внутри тика не гарантирован —
        // сопоставляем в обе стороны.
        void TryLinkMolotov()
        {
            var tick = Tick();
            molotovEnds.RemoveAll(m => tick - m.Tick > 4);
            infernoStarts.RemoveAll(i => tick - i.Tick > 4);

            foreach (var fire in infernoStarts.ToList())
            {
                var match = molotovEnds
                    .Where(m => m.Thrower == fire.Thrower && Math.Abs(m.Tick - fire.Tick) <= 2)
                    .OrderBy(m => Vector3.DistanceSquared(m.Position, fire.Position))
                    .FirstOrDefault();
                if (match.GrenadeId == 0)
                    continue;

                builder.DetonateGrenade(match.GrenadeId, fire.Tick, fire.Position);
                molotovEnds.Remove(match);
                infernoStarts.Remove(fire);
            }
        }

        demo.EntityEvents.CMolotovProjectile.Delete += projectile =>
        {
            if (!byEntity.Remove((int)projectile.EntityIndex.Value, out var id) ||
                demo.GetEntityByHandle(projectile.ThrowerHandle) is not CCSPlayerPawn { Controller: { } controller })
                return;

            molotovEnds.Add((Tick(), ToVector(projectile.Origin), KeyOf(controller), id));
            TryLinkMolotov();
        };

        demo.EntityEvents.CInferno.Create += inferno =>
        {
            var owner = demo.GetEntityByHandle(inferno.OwnerEntityHandle);
            var controller = (owner as CCSPlayerPawn)?.Controller ?? owner as CCSPlayerController;
            if (controller is null)
                return;

            infernoStarts.Add((Tick(), ToVector(inferno.Origin), KeyOf(controller)));
            TryLinkMolotov();
        };
    }

    /// <summary>
    /// Бомба и урон. Буква точки берётся из CPlantedC4.BombSite (0 — A, 1 — B): поле Site в событии —
    /// это entity id триггера точки, а не буква.
    /// </summary>
    private static void SubscribeBombAndDamage(CsDemoParser demo, MatchBuilder builder)
    {
        int Tick() => (int)demo.CurrentGameTick.Value;
        var lastSite = BombSite.Unknown;

        BombSite PlantedSite()
        {
            var planted = demo.Entities.OfType<CPlantedC4>().LastOrDefault();
            return planted is null ? BombSite.Unknown : planted.BombSite == 1 ? BombSite.B : BombSite.A;
        }

        demo.Source1GameEvents.BombPlanted += e =>
        {
            var site = PlantedSite();
            if (site == BombSite.Unknown && e.PlayerPawn?.LastPlaceName is { } place)
                site = place.EndsWith('A') ? BombSite.A : place.EndsWith('B') ? BombSite.B : BombSite.Unknown;
            lastSite = site;

            var position = e.PlayerPawn is { } pawn ? ToVector(pawn.Origin) : Vector3.Zero;
            builder.Bomb(Tick(), BombEventType.Planted, site, position, e.Player is { } p ? KeyOf(p) : null);
        };

        // CPlantedC4 может появиться после события bomb_planted в том же тике — уточняем точку.
        demo.EntityEvents.CPlantedC4.Create += c4 =>
            lastSite = c4.BombSite == 1 ? BombSite.B : BombSite.A;

        demo.Source1GameEvents.BombDefused += e =>
            builder.Bomb(Tick(), BombEventType.Defused, lastSite,
                e.PlayerPawn is { } pawn ? ToVector(pawn.Origin) : Vector3.Zero, e.Player is { } p ? KeyOf(p) : null);

        demo.Source1GameEvents.BombExploded += _ =>
        {
            var c4 = demo.Entities.OfType<CPlantedC4>().LastOrDefault();
            builder.Bomb(Tick(), BombEventType.Exploded, lastSite, c4 is null ? Vector3.Zero : ToVector(c4.Origin), null);
        };

        demo.Source1GameEvents.PlayerHurt += e =>
        {
            if (e.Player is not { } victim)
                return;
            builder.Damage(Tick(), e.Attacker is { } attacker ? KeyOf(attacker) : null, KeyOf(victim), e.DmgHealth, e.Weapon);
        };
    }

    private static GrenadeType? GrenadeTypeOf(CBaseCSGrenadeProjectile projectile) => projectile switch
    {
        CHEGrenadeProjectile => GrenadeType.HighExplosive,
        CFlashbangProjectile => GrenadeType.Flashbang,
        CSmokeGrenadeProjectile => GrenadeType.Smoke,
        CMolotovProjectile { IsIncGrenade: true } => GrenadeType.Incendiary,
        CMolotovProjectile => GrenadeType.Molotov,
        CDecoyProjectile => GrenadeType.Decoy,
        _ => null,
    };

    /// <summary>
    /// Выборка состояния живых игроков каждые <paramref name="step"/> тиков во время раунда.
    /// В DemoFile.Net 0.44.1 нет события TickEnd (упомянуто в README) — используется OnCommandFinishPersistent.
    /// </summary>
    private static void SubscribeSampling(CsDemoParser demo, MatchBuilder builder, int step)
    {
        var lastSampleTick = int.MinValue;
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string value) =>
            strings.TryGetValue(value, out var existing) ? existing : strings[value] = value;

        demo.Source1GameEvents.RoundFreezeEnd += _ => lastSampleTick = int.MinValue;

        demo.OnCommandFinishPersistent += () =>
        {
            if (builder.LiveRound is not var (round, freezeEndTick))
                return;

            var tick = (int)demo.CurrentGameTick.Value;
            if (tick < lastSampleTick + step)
                return;
            lastSampleTick = tick;

            var timeInRound = (tick - freezeEndTick) / (float)CsDemoParser.TickRate;
            foreach (var controller in demo.PlayersIncludingDisconnected)
            {
                if (!controller.PawnIsAlive || controller.PlayerPawn is not { IsAlive: true } pawn)
                    continue;

                builder.AddSample(KeyOf(controller), CreateSample(controller, pawn, tick, round, timeInRound, Intern));
            }
        };
    }

    private static PlayerSample CreateSample(CCSPlayerController controller, CCSPlayerPawn pawn, int tick, int round,
        float timeInRound, Func<string, string> intern)
    {
        var flags = MovementFlags.None;
        if (pawn.MovementServices is { } movement && (movement.Ducked || movement.Ducking))
            flags |= MovementFlags.Ducking;
        if ((pawn.Flags & FlOnGround) == 0)
            flags |= MovementFlags.Airborne;
        if (pawn.IsWalking)
            flags |= MovementFlags.Walking;
        if (pawn.IsScoped)
            flags |= MovementFlags.Scoped;

        var velocity = pawn.Velocity;
        var eye = pawn.EyeAngles;

        return new PlayerSample(
            tick,
            round,
            timeInRound,
            ToVector(pawn.Origin),
            eye.Pitch,
            eye.Yaw,
            MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y),
            flags,
            intern(WeaponName(pawn.ActiveWeapon)),
            (short)Math.Clamp(pawn.Health, 0, short.MaxValue),
            (short)Math.Clamp(pawn.ArmorValue, 0, short.MaxValue),
            controller.InGameMoneyServices?.Account ?? 0,
            pawn.CurrentEquipmentValue,
            intern(pawn.LastPlaceName ?? ""));
    }

    private const uint FlOnGround = 1;

    /// <summary>Имя оружия по определению предмета (weapon_ak47 → ak47); все ножи — knife.</summary>
    internal static string WeaponName(CCSWeaponBase? weapon)
    {
        if (weapon is null)
            return "";

        var index = weapon.AttributeManager.Item.ItemDefinitionIndex;
        if (!GameItems.ItemDefinitions.TryGetValue(index, out var definition))
            return weapon.ServerClass.Name;

        return NormalizeWeaponName(definition.Name);
    }

    internal static string NormalizeWeaponName(string itemName)
    {
        var name = itemName.StartsWith("weapon_", StringComparison.Ordinal) ? itemName["weapon_".Length..] : itemName;
        return name.StartsWith("knife", StringComparison.Ordinal) || name == "bayonet" ? "knife" : name;
    }

    private static PlayerSnapshot Snapshot(CCSPlayerController controller) =>
        new(KeyOf(controller), controller.PlayerName, controller.SteamID == 0, ToSide((int)controller.CSTeamNum));

    /// <summary>Ключ игрока: SteamID64, для ботов — синтетический ключ по нику.</summary>
    internal static ulong KeyOf(CCSPlayerController controller) =>
        controller.SteamID != 0 ? controller.SteamID : BotKey(controller.PlayerName);

    internal static ulong BotKey(string name)
    {
        // FNV-1a — стабильный между запусками (в отличие от string.GetHashCode).
        var hash = 14695981039346656037UL;
        foreach (var ch in name)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return hash | 0x8000_0000_0000_0000UL;
    }

    private static Side ToSide(int team) => team switch
    {
        (int)CSTeamNumber.Terrorist => Side.T,
        (int)CSTeamNumber.CounterTerrorist => Side.CT,
        _ => Side.None,
    };

    private static Vector3 ToVector(DemoFile.Vector v) => new(v.X, v.Y, v.Z);

    /// <summary>Название клана; у безымянной команды — пустая строка (подпись по стороне подставляет UI).</summary>
    private static string TeamName(string clanName) =>
        string.IsNullOrWhiteSpace(clanName) ? "" : clanName.Trim();

    /// <summary>Сверка K/D/A, посчитанных по событиям, с табло игры — для диагностики.</summary>
    private void LogScoreboardMismatch(CsDemoParser demo, MatchData match)
    {
        foreach (var controller in demo.PlayersIncludingDisconnected)
        {
            if (controller.ActionTrackingServices?.MatchStats is not { } stats || match.FindPlayer(KeyOf(controller)) is not { } player)
                continue;

            if (stats.Kills != player.Kills || stats.Deaths != player.Deaths || stats.Assists != player.Assists)
            {
                logger.LogWarning("K/D/A игрока {Name} по событиям {K}/{D}/{A}, на табло {SK}/{SD}/{SA}",
                    player.Name, player.Kills, player.Deaths, player.Assists, stats.Kills, stats.Deaths, stats.Assists);
            }
        }
    }
}
