using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application.Localization;
using DemReview.Application.Players;

namespace DemReview.Desktop.ViewModels;

/// <summary>Основной игрок — сам пользователь: SteamID вводится вручную или выбирается в открытой демке.</summary>
public partial class SettingsViewModel
{
    /// <summary>Основной игрок выбран или сброшен (null) — главное окно обновляет отметки и выбор в списке игроков.</summary>
    public event Action<ulong?>? MainPlayerChanged;

    /// <summary>Нажата «Выбрать в открытой демке» — главное окно показывает панель выбора.</summary>
    public event Action? MainPlayerPickRequested;

    public ulong? MainPlayerSteamId => _settings.Current.MainPlayerSteamId;

    public bool HasMainPlayer => MainPlayerSteamId is not null;

    public string MainPlayerText => _settings.Current switch
    {
        { MainPlayerSteamId: { } id, MainPlayerName: { Length: > 0 } name } => Loc.T($"Основной игрок: {name} ({id})", $"Main player: {name} ({id})"),
        { MainPlayerSteamId: { } id } => Loc.T($"Основной игрок: {id}", $"Main player: {id}"),
        _ => Loc.T("Основной игрок не выбран.", "No main player selected."),
    };

    /// <summary>Предлагать выбрать себя при открытии демки, пока основной игрок не выбран.</summary>
    public bool ShouldPromptForMainPlayer => MainPlayerSteamId is null && AskForMainPlayer;

    [ObservableProperty]
    public partial string MainPlayerInput { get; set; } = "";

    [ObservableProperty]
    public partial string? MainPlayerStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldPromptForMainPlayer))]
    public partial bool AskForMainPlayer { get; set; }

    partial void OnAskForMainPlayerChanged(bool value) => _settings.Update(s => s with { MainPlayerPromptDisabled = !value });

    [RelayCommand]
    private void SaveMainPlayer()
    {
        if (SteamIds.TryParse(MainPlayerInput, out var steamId))
        {
            SetMainPlayer(steamId, name: null);
            MainPlayerStatus = Loc.T("Сохранено.", "Saved.");
            return;
        }

        MainPlayerStatus = SteamIds.IsVanityUrl(MainPlayerInput)
            ? Loc.T("В ссылке вида /id/имя нет SteamID. Вставьте SteamID64 (17 цифр, начинается с 7656119) — его показывают steamid.io и подобные сайты — или выберите себя в открытой демке.",
                "A /id/name link has no SteamID in it. Paste your SteamID64 (17 digits starting with 7656119) — sites like steamid.io show it — or pick yourself in an open demo.")
            : Loc.T("Не похоже на SteamID. Подходят SteamID64 (7656119…), ссылка steamcommunity.com/profiles/…, [U:1:…] и STEAM_0:…",
                "This does not look like a SteamID. Accepted: SteamID64 (7656119…), a steamcommunity.com/profiles/… link, [U:1:…] and STEAM_0:…");
    }

    [RelayCommand]
    private void ClearMainPlayer()
    {
        _settings.Update(s => s with { MainPlayerSteamId = null, MainPlayerName = null });
        MainPlayerInput = "";
        MainPlayerStatus = null;
        NotifyMainPlayerChanged(null);
    }

    [RelayCommand]
    private void PickMainPlayer() => MainPlayerPickRequested?.Invoke();

    /// <summary>Запомнить основного игрока; ник известен, если он выбран в демке.</summary>
    public void SetMainPlayer(ulong steamId, string? name)
    {
        var current = _settings.Current;
        var keptName = name ?? (current.MainPlayerSteamId == steamId ? current.MainPlayerName : null);
        _settings.Update(s => s with { MainPlayerSteamId = steamId, MainPlayerName = keptName });
        MainPlayerInput = steamId.ToString();
        NotifyMainPlayerChanged(steamId);
    }

    /// <summary>Основной игрок найден в открытой демке — обновить ник для подписи в настройках.</summary>
    public void RememberMainPlayerName(string name)
    {
        if (string.IsNullOrEmpty(name) || name == _settings.Current.MainPlayerName)
            return;
        _settings.Update(s => s with { MainPlayerName = name });
        OnPropertyChanged(nameof(MainPlayerText));
        OnPropertyChanged(nameof(MainPlayerName));
        OnPropertyChanged(nameof(MainPlayerInitials));
    }

    private void NotifyMainPlayerChanged(ulong? steamId)
    {
        OnPropertyChanged(nameof(MainPlayerSteamId));
        OnPropertyChanged(nameof(HasMainPlayer));
        OnPropertyChanged(nameof(MainPlayerText));
        OnPropertyChanged(nameof(MainPlayerName));
        OnPropertyChanged(nameof(MainPlayerInitials));
        OnPropertyChanged(nameof(ShouldPromptForMainPlayer));
        MainPlayerChanged?.Invoke(steamId);
    }
}
