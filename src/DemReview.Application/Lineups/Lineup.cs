using System.Globalization;
using System.Numerics;
using DemReview.Domain;

namespace DemReview.Application.Lineups;

/// <summary>Сохранённая раскидка: откуда и под каким углом бросить гранату, чтобы повторить бросок из демки.</summary>
/// <param name="Id">Идентификатор в библиотеке раскидок (0 — ещё не сохранена).</param>
/// <param name="Title">Название, которое можно изменить; по умолчанию «откуда → куда».</param>
/// <param name="Position">Позиция ног игрока в момент броска (то, что принимает setpos).</param>
/// <param name="Movement">Как стоял бросивший; null — неизвестно (демка разобрана старой версией).</param>
/// <param name="Speed">Горизонтальная скорость бросившего, units/s; null — неизвестно.</param>
/// <param name="SourceFile">Демка, из которой взят бросок (только имя файла).</param>
/// <param name="Round">Раунд в этой демке.</param>
public sealed record Lineup(
    int Id,
    string MapName,
    GrenadeType Type,
    Side Side,
    string Title,
    Vector3 Position,
    float Pitch,
    float Yaw,
    MovementFlags? Movement,
    float? Speed,
    Vector3? LandPosition,
    string FromZone,
    string ToZone,
    string ThrowerName,
    ulong ThrowerSteamId,
    string SourceFile,
    int Round,
    int ThrowTick,
    DateTime SavedUtc)
{
    /// <summary>Тот же бросок: та же карта и граната, точка и угол совпадают с точностью до округления.</summary>
    public bool IsSameThrow(Lineup other) =>
        MapName == other.MapName && Type == other.Type &&
        Vector3.Distance(Position, other.Position) < 1 &&
        MathF.Abs(LineupCommands.NormalizePitch(Pitch) - LineupCommands.NormalizePitch(other.Pitch)) < 0.1f &&
        MathF.Abs(LineupCommands.AngleDelta(Yaw, other.Yaw)) < 0.1f;
}

/// <summary>Команды консоли CS2, чтобы встать в точку броска на локальном сервере (нужен sv_cheats 1).</summary>
public static class LineupCommands
{
    /// <summary>«setpos x y z;setang pitch yaw 0» — как выводит getpos.</summary>
    public static string SetPos(Lineup lineup) => SetPos(lineup.Position, lineup.Pitch, lineup.Yaw);

    public static string SetPos(Vector3 position, float pitch, float yaw) => string.Create(CultureInfo.InvariantCulture,
        $"setpos {position.X:F2} {position.Y:F2} {position.Z:F2};setang {NormalizePitch(pitch):F2} {NormalizeYaw(yaw):F2} 0");

    /// <summary>Наклон взгляда в диапазоне −90…90 (в демке он может прийти как 270…360).</summary>
    public static float NormalizePitch(float pitch)
    {
        var p = NormalizeYaw(pitch);
        return Math.Clamp(p, -89f, 89f);
    }

    /// <summary>Угол в диапазоне −180…180.</summary>
    public static float NormalizeYaw(float yaw)
    {
        var y = yaw % 360;
        return y switch
        {
            > 180 => y - 360,
            <= -180 => y + 360,
            _ => y,
        };
    }

    public static float AngleDelta(float a, float b) => NormalizeYaw(a - b);
}

/// <summary>Библиотека раскидок пользователя: хранится постоянно, отдельно от кеша и библиотеки демок.</summary>
public interface ILineupLibrary
{
    /// <summary>Все раскидки, новые первыми.</summary>
    IReadOnlyList<Lineup> List();

    /// <summary>Сохранить новую раскидку; если такой бросок уже есть — вернуть существующую.</summary>
    /// <returns>Сохранённая раскидка и признак, что она добавлена сейчас.</returns>
    (Lineup Lineup, bool Added) Add(Lineup lineup);

    /// <summary>Изменить название раскидки.</summary>
    void Rename(int id, string title);

    void Remove(int id);

    /// <summary>Файл библиотеки — чтобы показать пользователю, где она лежит.</summary>
    string FilePath { get; }
}
