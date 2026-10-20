using System.Text.RegularExpressions;

namespace DemReview.Application.Updates;

/// <summary>Версия приложения, адрес его репозитория и ключ статистики (из метаданных сборки).</summary>
/// <param name="AnalyticsKey">Ключ приложения Aptabase; задаётся только при сборке релиза, иначе статистика не отправляется.</param>
public sealed partial record AppInfo(Version Version, string RepositoryUrl, string? AnalyticsKey = null)
{
    public bool HasAnalytics => !string.IsNullOrWhiteSpace(AnalyticsKey);

    /// <summary>Версия для показа: 0.1.0.</summary>
    public string VersionText => Version.ToString(3);

    /// <summary>Владелец и имя репозитория GitHub; false — адрес не задан (заглушка OWNER) или не GitHub.</summary>
    public bool TryGetGitHubRepository(out string owner, out string name)
    {
        var match = GitHubUrl().Match(RepositoryUrl);
        owner = match.Success ? match.Groups["owner"].Value : "";
        name = match.Success ? match.Groups["name"].Value : "";
        return match.Success && !owner.Equals("OWNER", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasRepository => TryGetGitHubRepository(out _, out _);

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<name>[A-Za-z0-9._-]+?)(\.git)?/?$")]
    private static partial Regex GitHubUrl();
}

/// <summary>Опубликованный релиз.</summary>
/// <param name="Url">Страница релиза на GitHub.</param>
public sealed record ReleaseInfo(Version Version, string Tag, string Url);

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NotConfigured,
    Failed,
}

public sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Latest = null, string? Error = null);

/// <summary>Проверка новых релизов. Работа без сети не нарушается: ошибка — просто результат <see cref="UpdateStatus.Failed"/>.</summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

public static class ReleaseVersion
{
    /// <summary>«v1.2.3», «1.2», «v1.2.3-beta.1» → 1.2.3 (суффикс предрелиза отбрасывается).</summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
            text = text[..dash];
        if (!Version.TryParse(text, out var version))
            return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>Релиз новее текущей версии (сравниваются major.minor.patch).</summary>
    public static bool IsNewer(Version release, Version current) =>
        new Version(release.Major, release.Minor, Math.Max(0, release.Build)) >
        new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
