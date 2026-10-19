using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
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
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var sb = new StringBuilder();
        sb.AppendLine($"# Профиль игрока {Escape(profile.Name)}");
        sb.AppendLine();
        sb.AppendLine($"- **Карта:** {match.MapName}");
        sb.AppendLine($"- **Матч:** {Escape(match.FinalScore.TeamTName)} {match.FinalScore.TeamTScore} : {match.FinalScore.TeamCTScore} {Escape(match.FinalScore.TeamCTName)}, раундов: {match.Rounds.Count}");
        sb.AppendLine($"- **Демка:** {Escape(Path.GetFileName(match.SourcePath))}");
        sb.AppendLine($"- **SteamID:** {profile.SteamId}");
        sb.AppendLine($"- **Предсказуемость:** {profile.PredictabilityPercent}%");
        sb.AppendLine();

        sb.AppendLine("## Роль");
        sb.AppendLine();
        foreach (var role in profile.Roles)
        {
            sb.AppendLine($"- **{Labels.Side(role.Side)}: {RoleName(role.Role)}** (уверенность: {ConfidenceName(role.Confidence)})");
            foreach (var reason in role.Reasons)
                sb.AppendLine($"  - {Escape(reason)}");
        }
        sb.AppendLine();

        sb.AppendLine("## Сводка по сторонам");
        sb.AppendLine();
        sb.AppendLine("| Сторона | Раундов | У/С | Выживаемость | Победы | Первый контакт | Первая дуэль | Гранат/раунд | Агрессия / пассив |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var s in profile.Sides)
        {
            var contact = s.MedianContactTime is { } c ? Labels.Time(c) : "—";
            sb.AppendLine(string.Create(ru,
                $"| {Labels.Side(s.Side)} | {s.Rounds} | {s.Kills}/{s.Deaths} | {s.SurvivalShare:P0} | {s.RoundWinShare:P0} | {contact} | " +
                $"{s.FirstDuelShare:P0} (выиграно {s.FirstDuelWinShare:P0}) | {s.GrenadesPerRound:F1} | {s.AggressiveShare:P0} / {s.PassiveShare:P0} |"));
        }
        sb.AppendLine();

        sb.AppendLine($"## Паттерны (частота ≥ {profile.Options.MinShare.ToString("P0", ru)}, примеров ≥ {profile.Options.MinExamples})");
        sb.AppendLine();
        if (profile.Patterns.Count == 0)
        {
            sb.AppendLine("Устойчивых паттернов при заданных порогах не найдено.");
        }
        else
        {
            foreach (var p in profile.Patterns)
            {
                sb.AppendLine(string.Create(ru,
                    $"- {Escape(p.Text)} — **{p.Share:P0}**, уверенность: {ConfidenceName(p.Confidence)}. Раунды: {string.Join(", ", p.Rounds)}"));
            }
        }
        sb.AppendLine();
        sb.AppendLine("_Одна демка — малая выборка: ориентируйтесь на «N из M» и уверенность, а не только на проценты._");
        return sb.ToString();
    }

    public static string RoleName(PlayerRole role) => role switch
    {
        PlayerRole.Entry => "entry — открывает раунд",
        PlayerRole.Lurker => "lurk — играет отдельно от команды",
        PlayerRole.Support => "support — гранаты и поддержка",
        PlayerRole.Anchor => "anchor — держит точку",
        PlayerRole.Rotator => "rotator — ротируется между точками",
        _ => "rifler — универсал",
    };

    public static string ConfidenceName(Confidence confidence) => confidence switch
    {
        Confidence.High => "высокая",
        Confidence.Medium => "средняя",
        _ => "низкая",
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
