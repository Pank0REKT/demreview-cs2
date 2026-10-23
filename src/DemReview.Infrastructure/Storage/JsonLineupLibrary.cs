using System.Text.Json;
using System.Text.Json.Serialization;
using DemReview.Application;
using DemReview.Application.Lineups;
using Microsoft.Extensions.Logging;

namespace DemReview.Infrastructure.Storage;

/// <summary>
/// Библиотека раскидок в JSON-файле в каталоге данных пользователя (lineups.json). Раскидок немного,
/// поэтому файл читается целиком при запуске и перезаписывается целиком при каждом изменении.
/// Нечитаемый файл не перезаписывается, а откладывается в сторону (.broken).
/// </summary>
public sealed class JsonLineupLibrary : ILineupLibrary
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<JsonLineupLibrary> _logger;
    private readonly Lock _lock = new();
    private List<Lineup>? _lineups;

    public JsonLineupLibrary(IPlatformService platform, ILogger<JsonLineupLibrary> logger)
        : this(Path.Combine(platform.AppDataDirectory, "lineups.json"), logger)
    {
    }

    public JsonLineupLibrary(string filePath, ILogger<JsonLineupLibrary> logger)
    {
        FilePath = filePath;
        _logger = logger;
    }

    public string FilePath { get; }

    public IReadOnlyList<Lineup> List()
    {
        lock (_lock)
            return Lineups().OrderByDescending(l => l.SavedUtc).ThenByDescending(l => l.Id).ToList();
    }

    public (Lineup Lineup, bool Added) Add(Lineup lineup)
    {
        lock (_lock)
        {
            var lineups = Lineups();
            if (lineups.FirstOrDefault(l => l.IsSameThrow(lineup)) is { } existing)
                return (existing, false);

            var saved = lineup with { Id = lineups.Count == 0 ? 1 : lineups.Max(l => l.Id) + 1 };
            lineups.Add(saved);
            Save(lineups);
            return (saved, true);
        }
    }

    public void Rename(int id, string title)
    {
        lock (_lock)
        {
            var lineups = Lineups();
            var index = lineups.FindIndex(l => l.Id == id);
            if (index < 0 || lineups[index].Title == title)
                return;
            lineups[index] = lineups[index] with { Title = title };
            Save(lineups);
        }
    }

    public void Remove(int id)
    {
        lock (_lock)
        {
            var lineups = Lineups();
            if (lineups.RemoveAll(l => l.Id == id) > 0)
                Save(lineups);
        }
    }

    private List<Lineup> Lineups() => _lineups ??= Load();

    private List<Lineup> Load()
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            var file = JsonSerializer.Deserialize<LineupFile>(File.ReadAllText(FilePath), Json);
            return file?.Lineups ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Файл раскидок {Path} не читается, он отложен в сторону", FilePath);
            TryMoveAside();
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать раскидки {Path}", FilePath);
            return [];
        }
    }

    private void Save(List<Lineup> lineups)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new LineupFile(FormatVersion, lineups), Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить раскидки {Path}", FilePath);
        }
    }

    private void TryMoveAside()
    {
        try
        {
            File.Move(FilePath, FilePath + ".broken", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось отложить файл раскидок {Path}", FilePath);
        }
    }

    private sealed record LineupFile(int Version, List<Lineup> Lineups);
}
