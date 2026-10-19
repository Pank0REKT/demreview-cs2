using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using DemReview.Application.Localization;
using DemReview.Application.Patterns;
using DemReview.Application.Situations;
using DemReview.Domain;

namespace DemReview.Application.Export;

public enum ExportFormat
{
    Markdown,
    Json,
}

/// <summary>Экспорт профиля игрока в Markdown и JSON.</summary>
public interface IProfileExporter
{
    string Export(PlayerProfile profile, MatchData match, ExportFormat format);

    /// <summary>Формат по расширению файла (.md / .json); по умолчанию Markdown.</summary>
    ExportFormat FormatFor(string path);
}

public sealed class ProfileExporter : IProfileExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Кириллица без \uXXXX — файл читается глазами.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ExportFormat FormatFor(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Json : ExportFormat.Markdown;

    public string Export(PlayerProfile profile, MatchData match, ExportFormat format) =>
        format == ExportFormat.Json ? ToJson(profile, match) : ToMarkdown(profile, match);

    private static string ToJson(PlayerProfile profile, MatchData match)
    {
        var document = new
        {
            format = "demreview-profile",
            version = 1,
            language = Loc.Code(Loc.Language),
            match = new
            {
                map = match.MapName,
                file = Path.GetFileName(match.SourcePath),
                score = match.FinalScore,
                rounds = match.Rounds.Count,
            },
            player = new { steamId = profile.SteamId.ToString(CultureInfo.InvariantCulture), name = profile.Name },
            predictabilityPercent = profile.PredictabilityPercent,
            roundsAnalyzed = profile.RoundsAnalyzed,
            thresholds = new { minShare = profile.Options.MinShare, minExamples = profile.Options.MinExamples },
            roles = profile.Roles.Select(r => new { side = r.Side, role = r.Role, roleName = RoleName(r.Role), r.Confidence, r.Reasons }),
            sides = profile.Sides,
            patterns = profile.Patterns.Select(p => new
            {
                p.Category,
                p.Side,
                p.Text,
                p.Condition,
                p.Action,
                p.Count,
                p.Total,
                share = Math.Round(p.Share, 3),
                confidence = p.Confidence,
                confidenceScore = Math.Round(p.ConfidenceScore, 3),
                p.Rounds,
            }),
        };
        return JsonSerializer.Serialize(document, Json);
    }

    private static string ToMarkdown(PlayerProfile profile, MatchData match)
    {
        var culture = Loc.Culture;
        string F(FormattableString s) => s.ToString(culture);
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T($"# Профиль игрока {Escape(profile.Name)}", $"# Player profile: {Escape(profile.Name)}"));
        sb.AppendLine();
        sb.AppendLine(Loc.T($"- **Карта:** {match.MapName}", $"- **Map:** {match.MapName}"));
        var score = $"{Escape(TeamName(match.FinalScore.TeamTName, Side.T))} {match.FinalScore.TeamTScore} : " +
                    $"{match.FinalScore.TeamCTScore} {Escape(TeamName(match.FinalScore.TeamCTName, Side.CT))}";
        sb.AppendLine(Loc.T($"- **Матч:** {score}, раундов: {match.Rounds.Count}", $"- **Match:** {score}, rounds: {match.Rounds.Count}"));
        sb.AppendLine(Loc.T($"- **Демка:** {Escape(Path.GetFileName(match.SourcePath))}", $"- **Demo:** {Escape(Path.GetFileName(match.SourcePath))}"));
        sb.AppendLine($"- **SteamID:** {profile.SteamId}");
        sb.AppendLine(Loc.T($"- **Предсказуемость:** {profile.PredictabilityPercent}%", $"- **Predictability:** {profile.PredictabilityPercent}%"));
        sb.AppendLine();

        sb.AppendLine(Loc.T("## Роль", "## Role"));
        sb.AppendLine();
        foreach (var role in profile.Roles)
        {
            sb.AppendLine(Loc.T($"- **{Labels.Side(role.Side)}: {RoleName(role.Role)}** (уверенность: {ConfidenceName(role.Confidence)})",
                $"- **{Labels.Side(role.Side)}: {RoleName(role.Role)}** (confidence: {ConfidenceName(role.Confidence)})"));
            foreach (var reason in role.Reasons)
                sb.AppendLine($"  - {Escape(reason)}");
        }
        sb.AppendLine();

        sb.AppendLine(Loc.T("## Сводка по сторонам", "## Summary by side"));
        sb.AppendLine();
        sb.AppendLine(Loc.T(
            "| Сторона | Раундов | У/С | Урон/раунд | Выживаемость | Победы | Первый контакт | Первая дуэль | Гранат/раунд | Агрессия / пассив |",
            "| Side | Rounds | K/D | ADR | Survival | Wins | First contact | Opening duel | Grenades/round | Aggressive / passive |"));
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in profile.Sides)
        {
            var contact = s.MedianContactTime is { } c ? Labels.Time(c) : "—";
            var won = Loc.T("выиграно", "won");
            sb.Append(F($"| {Labels.Side(s.Side)} | {s.Rounds} | {s.Kills}/{s.Deaths} | {s.DamagePerRound:F0} | {s.SurvivalShare:P0} | "))
                .Append(F($"{s.RoundWinShare:P0} | {contact} | {s.FirstDuelShare:P0} ({won} {s.FirstDuelWinShare:P0}) | "))
                .AppendLine(F($"{s.GrenadesPerRound:F1} | {s.AggressiveShare:P0} / {s.PassiveShare:P0} |"));
        }
        sb.AppendLine();

        var minShare = profile.Options.MinShare.ToString("P0", culture);
        sb.AppendLine(Loc.T($"## Паттерны (частота ≥ {minShare}, примеров ≥ {profile.Options.MinExamples})",
            $"## Patterns (frequency ≥ {minShare}, examples ≥ {profile.Options.MinExamples})"));
        sb.AppendLine();
        if (profile.Patterns.Count == 0)
        {
            sb.AppendLine(Loc.T("Устойчивых паттернов при заданных порогах не найдено.", "No stable patterns at the current thresholds."));
        }
        else
        {
            foreach (var p in profile.Patterns)
            {
                var rounds = string.Join(", ", p.Rounds);
                sb.AppendLine(Loc.T(
                    F($"- {Escape(p.Text)} — **{p.Share:P0}**, уверенность: {ConfidenceName(p.Confidence)}. Раунды: {rounds}"),
                    F($"- {Escape(p.Text)} — **{p.Share:P0}**, confidence: {ConfidenceName(p.Confidence)}. Rounds: {rounds}")));
            }
        }
        sb.AppendLine();
        sb.AppendLine(Loc.T("_Одна демка — малая выборка: ориентируйтесь на «N из M» и уверенность, а не только на проценты._",
            "_One demo is a small sample: rely on \"N of M\" and confidence, not just percentages._"));
        return sb.ToString();
    }

    /// <summary>Название команды; у безымянных — «Террористы» / «Спецназ».</summary>
    public static string TeamName(string name, Side side) => name.Length > 0 ? name
        : side == Side.CT ? Loc.T("Спецназ", "Counter-Terrorists") : Loc.T("Террористы", "Terrorists");

    public static string RoleName(PlayerRole role) => role switch
    {
        PlayerRole.Entry => Loc.T("entry — открывает раунд", "entry — opens the round"),
        PlayerRole.Lurker => Loc.T("lurk — играет отдельно от команды", "lurker — plays away from the team"),
        PlayerRole.Support => Loc.T("support — гранаты и поддержка", "support — utility and backup"),
        PlayerRole.Anchor => Loc.T("anchor — держит точку", "anchor — holds a site"),
        PlayerRole.Rotator => Loc.T("rotator — ротируется между точками", "rotator — rotates between sites"),
        _ => Loc.T("rifler — универсал", "rifler — all-rounder"),
    };

    public static string ConfidenceName(Confidence confidence) => confidence switch
    {
        Confidence.High => Loc.T("высокая", "high"),
        Confidence.Medium => Loc.T("средняя", "medium"),
        _ => Loc.T("низкая", "low"),
    };

    /// <summary>Экранирование символов Markdown в именах и названиях.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '*' or '_' or '`' or '|' or '[' or ']' or '<' or '>' or '#')
                sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
