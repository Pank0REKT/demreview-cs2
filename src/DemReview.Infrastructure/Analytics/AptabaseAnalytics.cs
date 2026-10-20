using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DemReview.Application;
using DemReview.Application.Analytics;
using DemReview.Application.Updates;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Analytics;

/// <summary>
/// Анонимная статистика через HTTP API Aptabase (https://aptabase.com): без постоянного идентификатора
/// пользователя, только сессия процесса. Отправка — в фоне; без сети событие просто теряется.
/// </summary>
public sealed class AptabaseAnalytics : IAnalytics
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Сессия заканчивается после часа без событий — как в официальных SDK.</summary>
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(1);

    private readonly AppInfo _app;
    private readonly ISettingsService _settings;
    private readonly IPlatformService _platform;
    private readonly ILogger<AptabaseAnalytics> _logger;
    private readonly Uri? _endpoint;
    private readonly Lock _sessionLock = new();
    private readonly Lazy<HttpClient> _http;
    private string _sessionId = NewSessionId();
    private DateTime _lastEventUtc = DateTime.UtcNow;

    public AptabaseAnalytics(AppInfo app, ISettingsService settings, IPlatformService platform, ILogger<AptabaseAnalytics> logger)
    {
        _app = app;
        _settings = settings;
        _platform = platform;
        _logger = logger;
        _endpoint = app.HasAnalytics ? GetEndpoint(app.AnalyticsKey!) : null;
        _http = new Lazy<HttpClient>(CreateClient);
        if (app.HasAnalytics && _endpoint is null)
            logger.LogWarning("Неизвестный регион ключа статистики — статистика отключена");
    }

    public void Track(string eventName, IReadOnlyDictionary<string, object>? props = null)
    {
        if (_endpoint is null || !_settings.Current.SendUsageStatistics)
            return;

        var body = BuildPayload(eventName, props, DateTime.UtcNow, GetSessionId(), CreateSystemProps());
        _ = Task.Run(() => SendAsync(eventName, body));
    }

    private async Task SendAsync(string eventName, string body)
    {
        try
        {
            var http = _http.Value;
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(_endpoint, content);
            if (!response.IsSuccessStatusCode)
                _logger.LogInformation("Статистика: событие {Event} не принято ({Status})", eventName, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation("Статистика: событие {Event} не отправлено: {Message}", eventName, ex.Message);
        }
    }

    private HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout };
        http.DefaultRequestHeaders.Add("App-Key", _app.AnalyticsKey);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DemReview", _app.VersionText));
        return http;
    }

    private string GetSessionId()
    {
        lock (_sessionLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastEventUtc > SessionTimeout)
                _sessionId = NewSessionId();
            _lastEventUtc = now;
            return _sessionId;
        }
    }

    private SystemProps CreateSystemProps() => new(
        IsDebug:
#if DEBUG
            true,
#else
            false,
#endif
        OsName: _platform.PlatformName,
        OsVersion: Environment.OSVersion.Version.ToString(),
        Locale: CultureInfo.CurrentUICulture.Name,
        AppVersion: _app.VersionText);

    internal sealed record SystemProps(bool IsDebug, string OsName, string OsVersion, string Locale, string AppVersion);

    /// <summary>Адрес приёма событий по региону из ключа: A-EU-… → eu.aptabase.com, A-US-… → us.aptabase.com.</summary>
    internal static Uri? GetEndpoint(string appKey)
    {
        var parts = appKey.Split('-');
        if (parts.Length != 3 || parts[0] != "A")
            return null;
        return parts[1] switch
        {
            "EU" => new Uri("https://eu.aptabase.com/api/v0/events"),
            "US" => new Uri("https://us.aptabase.com/api/v0/events"),
            _ => null,
        };
    }

    /// <summary>Тело запроса: массив из одного события в формате Aptabase.</summary>
    internal static string BuildPayload(string eventName, IReadOnlyDictionary<string, object>? props, DateTime timestampUtc,
        string sessionId, SystemProps system)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            json.WriteStartObject();
            json.WriteString("timestamp", timestampUtc.ToString("o", CultureInfo.InvariantCulture));
            json.WriteString("sessionId", sessionId);
            json.WriteString("eventName", eventName);
            json.WriteStartObject("systemProps");
            json.WriteBoolean("isDebug", system.IsDebug);
            json.WriteString("osName", system.OsName);
            json.WriteString("osVersion", system.OsVersion);
            json.WriteString("locale", system.Locale);
            json.WriteString("appVersion", system.AppVersion);
            json.WriteString("sdkVersion", $"demreview-http@{system.AppVersion}");
            json.WriteEndObject();
            if (props is { Count: > 0 })
            {
                json.WriteStartObject("props");
                foreach (var (key, value) in props)
                {
                    json.WritePropertyName(key);
                    JsonSerializer.Serialize(json, value, value.GetType());
                }
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Идентификатор сессии как в SDK Aptabase: секунды Unix-времени и 8 случайных цифр.</summary>
    private static string NewSessionId() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) +
        Random.Shared.Next(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
}
