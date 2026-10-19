using System.Numerics;
using DemoFile;
using DemoFile.Game.Cs;
using DemoFile.GameStatic;
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

        var reader = DemoFileReader.Create(demo, stream);
        var lastReported = -1;
        reader.OnProgress = e =>
        {
            // OnProgress вызывается на каждую команду демки — сообщаем только при смене процента.
            var percent = (int)(e.ProgressRatio * 100);
            if (percent == lastReported)
                return;
            lastReported = percent;
            progress?.Report(new ParseProgress(e.ProgressRatio, $"Разбор демки: {percent}%"));
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
            throw new DemoParseException("Файл не является демкой CS2 или повреждён.", ex);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException
                                       or IndexOutOfRangeException or ArgumentException or InvalidOperationException
                                       or NullReferenceException or FormatException)
        {
            logger.LogError(ex, "Ошибка разбора демки {Path}", path);
            throw new DemoParseException("Демка повреждена или записана в неподдерживаемом формате — разбор прерван.", ex);
        }

        if (reader.IsIncompleteFile)
            logger.LogWarning("Демка {Path} записана не полностью (обрыв записи) — данные до места обрыва", path);

        var mapName = (demo.ServerInfo?.MapName ?? demo.FileHeader?.MapName ?? "").Trim().ToLowerInvariant();
        var teamT = demo.TeamTerrorist;
        var teamCT = demo.TeamCounterTerrorist;
        var match = builder.Build(path, mapName, CsDemoParser.TickRate, (int)demo.CurrentGameTick.Value,
            TeamName(teamT.ClanTeamname, "Террористы"), TeamName(teamCT.ClanTeamname, "Спецназ"),
            teamT.Score, teamCT.Score, options.SampleTickStep);

        if (match.Rounds.Count == 0)
            throw new DemoParseException("В демке не найдено ни одного сыгранного раунда.");

        LogScoreboardMismatch(demo, match);
        progress?.Report(new ParseProgress(1, "Готово"));
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
            throw new DemoParseException($"Не удалось открыть файл: {ex.Message}", ex);
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
            throw new DemoParseException("Это демка CS:GO (Source 1). Поддерживаются только демки CS2.");

        throw new DemoParseException("Файл не является демкой CS2 (неверная сигнатура .dem).");
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

    private static string TeamName(string clanName, string fallback) =>
        string.IsNullOrWhiteSpace(clanName) ? fallback : clanName;

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
