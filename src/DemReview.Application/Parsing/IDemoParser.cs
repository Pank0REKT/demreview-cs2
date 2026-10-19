using DemReview.Domain;

namespace DemReview.Application.Parsing;

/// <summary>Разбор .dem файла CS2 в доменную модель.</summary>
public interface IDemoParser
{
    /// <exception cref="DemoParseException">Файл не читается, повреждён или не является демкой CS2.</exception>
    /// <exception cref="OperationCanceledException">Разбор отменён.</exception>
    Task<MatchData> ParseAsync(string path, ParseOptions options, IProgress<ParseProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Параметры разбора.</summary>
/// <param name="SampleTickStep">Шаг выборки позиций игроков в тиках (8–16 рекомендуется).</param>
public sealed record ParseOptions(int SampleTickStep = 16);

/// <summary>Прогресс разбора: доля 0..1 и текстовое описание этапа.</summary>
public readonly record struct ParseProgress(double Fraction, string Stage);

/// <summary>Понятная пользователю ошибка разбора демки.</summary>
public sealed class DemoParseException(string message, Exception? inner = null) : Exception(message, inner);
