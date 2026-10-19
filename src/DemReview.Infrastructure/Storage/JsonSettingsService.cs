using System.Text.Json;
using DemReview.Application;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Storage;

/// <summary>Настройки в JSON-файле в каталоге данных пользователя.</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public JsonSettingsService(IPlatformService platform, ILogger<JsonSettingsService> logger)
    {
        _logger = logger;
        _path = Path.Combine(platform.AppDataDirectory, "settings.json");
        _current = Load();
    }

    public AppSettings Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        lock (_lock)
        {
            _current = change(_current);
            Save(_current);
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать настройки {Path}, используются значения по умолчанию", _path);
        }

        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить настройки {Path}", _path);
        }
    }
}
