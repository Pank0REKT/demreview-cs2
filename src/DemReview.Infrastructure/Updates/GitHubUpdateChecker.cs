using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DemReview.Application.Updates;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Updates;

/// <summary>
/// Последний стабильный релиз из GitHub (releases/latest не возвращает черновики и предрелизы).
/// Один запрос без авторизации; при отсутствии сети или ошибке — <see cref="UpdateStatus.Failed"/>.
/// </summary>
public sealed class GitHubUpdateChecker(AppInfo app, ILogger<GitHubUpdateChecker> logger) : IUpdateChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!app.TryGetGitHubRepository(out var owner, out var name))
            return new UpdateCheckResult(UpdateStatus.NotConfigured);

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DemReview", app.VersionText));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.GetAsync($"https://api.github.com/repos/{owner}/{name}/releases/latest", cancellationToken);
            // 404 — опубликованных релизов ещё нет.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateStatus.UpToDate);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Evaluate(ParseRelease(json), app.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            logger.LogInformation("Проверка обновлений не удалась: {Message}", ex.Message);
            return new UpdateCheckResult(UpdateStatus.Failed, Error: ex.Message);
        }
    }

    internal static UpdateCheckResult Evaluate(ReleaseInfo latest, Version current) =>
        new(ReleaseVersion.IsNewer(latest.Version, current) ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate, latest);

    /// <summary>tag_name и html_url из ответа releases/latest.</summary>
    internal static ReleaseInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        var version = ReleaseVersion.Parse(tag);
        if (version is null || url is null || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected release: tag '{tag}', url '{url}'");
        return new ReleaseInfo(version, tag!, url);
    }
}
