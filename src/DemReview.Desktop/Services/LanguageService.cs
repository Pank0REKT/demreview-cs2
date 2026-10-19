using System.Globalization;
using DemReview.Application;
using DemReview.Application.Localization;
using Microsoft.Extensions.Logging;

namespace DemReview.Desktop.Services;

/// <summary>Язык интерфейса: сохранённый выбор, применение (строки и культура форматирования).</summary>
public sealed class LanguageService(ISettingsService settings, ILogger<LanguageService> logger)
{
    /// <summary>Язык из settings.json или null, если пользователь его ещё не выбирал.</summary>
    public UiLanguage? Saved => Loc.FromCode(settings.Current.Language);

    /// <summary>Применить язык; <paramref name="save"/> — запомнить выбор.</summary>
    public void Apply(UiLanguage language, bool save)
    {
        var culture = Loc.CultureOf(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (save)
            settings.Update(s => s with { Language = Loc.Code(language) });

        if (Loc.Language != language)
            logger.LogInformation("Язык интерфейса: {Language}", Loc.Code(language));
        Loc.Language = language;
    }
}
