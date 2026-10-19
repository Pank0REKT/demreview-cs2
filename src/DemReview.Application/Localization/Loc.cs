using System.Globalization;

namespace DemReview.Application.Localization;

public enum UiLanguage
{
    Russian,
    English,
}

/// <summary>
/// Язык интерфейса и формулировок анализа. Строки задаются парами прямо в коде: <c>Loc.T("Раунд", "Round")</c> —
/// перевод виден рядом с местом использования и не может «потеряться» по ключу.
/// </summary>
public static class Loc
{
    private static volatile UiLanguage _language = UiLanguage.Russian;

    /// <summary>Язык сменился (вызывается в потоке, который его сменил).</summary>
    public static event Action? Changed;

    public static UiLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
                return;
            _language = value;
            Changed?.Invoke();
        }
    }

    public static bool IsEnglish => _language == UiLanguage.English;

    /// <summary>Строка на текущем языке.</summary>
    public static string T(string ru, string en) => _language == UiLanguage.English ? en : ru;

    /// <summary>Культура форматирования чисел и процентов для текущего языка.</summary>
    public static CultureInfo Culture => CultureOf(_language);

    public static CultureInfo CultureOf(UiLanguage language) =>
        CultureInfo.GetCultureInfo(language == UiLanguage.English ? "en-US" : "ru-RU");

    /// <summary>Код языка для settings.json: «ru» / «en».</summary>
    public static string Code(UiLanguage language) => language == UiLanguage.English ? "en" : "ru";

    public static UiLanguage? FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "ru" => UiLanguage.Russian,
        "en" => UiLanguage.English,
        _ => null,
    };

    /// <summary>Язык по умолчанию — по языку системы.</summary>
    public static UiLanguage FromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? UiLanguage.Russian : UiLanguage.English;

    /// <summary>Название языка на самом этом языке — для списка выбора.</summary>
    public static string NativeName(UiLanguage language) => language == UiLanguage.English ? "English" : "Русский";
}
