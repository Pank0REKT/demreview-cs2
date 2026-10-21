using DemReview.Application.Parsing;
using DemReview.Domain;

namespace DemReview.Application.Library;

/// <summary>
/// Библиотека разобранных демок: хранится постоянно (в отличие от кеша, без лимита и не очищается вместе с ним),
/// чтобы сводный профиль строился по готовым анализам — без исходных .dem и без повторного разбора.
/// </summary>
public interface IDemoLibrary
{
    /// <summary>Все демки библиотеки — только метаданные, без данных матча.</summary>
    IReadOnlyList<LibraryEntry> List();

    /// <summary>Есть ли актуальная запись для этой версии файла (тот же размер и время изменения, текущий формат).</summary>
    bool Contains(DemoFileKey key);

    /// <summary>Разобранная демка из библиотеки для этой версии файла (с тем же шагом сэмплов).</summary>
    bool TryGet(DemoFileKey key, out MatchData match);

    /// <summary>Добавить или обновить демку.</summary>
    void Store(DemoFileKey key, MatchData match);

    /// <summary>Данные матча; null — записи нет или она в старом формате (нужно разобрать заново).</summary>
    MatchData? Load(int id);

    void Remove(int id);

    /// <summary>Сколько демок в библиотеке и сколько она занимает на диске.</summary>
    MatchCacheInfo GetInfo();

    /// <summary>Удалить все демки из библиотеки.</summary>
    void Clear();
}

/// <summary>Демка в библиотеке.</summary>
/// <param name="FileTimeUtc">Время изменения файла — примерно, когда сыгран матч.</param>
/// <param name="IsCurrent">Данные в текущем формате; иначе демку нужно разобрать заново (если файл ещё есть).</param>
public sealed record LibraryEntry(
    int Id,
    string FullPath,
    string MapName,
    MatchScore Score,
    int RoundCount,
    DateTime FileTimeUtc,
    DateTime AddedUtc,
    IReadOnlyList<LibraryPlayer> Players,
    bool IsCurrent)
{
    public string FileName => Path.GetFileName(FullPath);

    public LibraryPlayer? FindPlayer(ulong steamId) => Players.FirstOrDefault(p => p.SteamId == steamId);
}

/// <summary>Игрок демки в библиотеке — для поиска демок игрока без загрузки самих матчей.</summary>
public sealed record LibraryPlayer(ulong SteamId, string Name, bool IsBot, Side FinalSide, string TeamName, int Kills, int Deaths, int Assists)
{
    public static LibraryPlayer From(PlayerInfo p) => new(p.SteamId, p.Name, p.IsBot, p.FinalSide, p.TeamName, p.Kills, p.Deaths, p.Assists);
}
