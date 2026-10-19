using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemReview.Application.Localization;
using DemReview.Domain;

namespace DemReview.Desktop.ViewModels;

/// <summary>
/// Таймлайн раунда: ползунок по тикам от конца фризтайма до конца раунда и воспроизведение (DispatcherTimer).
/// </summary>
public partial class TimelineViewModel : ViewModelBase
{
    private const double FrameSeconds = 1 / 30.0;
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private int _tickRate = 64;

    public TimelineViewModel()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(FrameSeconds), DispatcherPriority.Render, (_, _) => Advance());
    }

    /// <summary>Текущий тик изменился (ползунок или воспроизведение).</summary>
    public event Action<int>? TickChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    public partial RoundInfo? Round { get; private set; }

    [ObservableProperty]
    public partial double MinTick { get; private set; }

    [ObservableProperty]
    public partial double MaxTick { get; private set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    public partial double CurrentTick { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial double Speed { get; set; } = 1;

    [ObservableProperty]
    public partial string ContextText { get; set; } = "";

    public IReadOnlyList<double> Speeds { get; } = [0.5, 1, 2, 4, 8];

    public bool IsEnabled => Round is not null;

    public string PlayButtonText => IsPlaying ? Loc.T("Пауза", "Pause") : Loc.T("Пуск", "Play");

    public string TimeText => Round is null
        ? Loc.T("Выберите раунд в фильтре или в списке раундов — появится таймлайн.", "Pick a round in the filter or in the round list to open the timeline.")
        : $"{Loc.T("Раунд", "Round")} {Round.Number} · {Texts.Time((float)((CurrentTick - MinTick) / _tickRate))} / {Texts.Time((float)((MaxTick - MinTick) / _tickRate))}";

    partial void OnCurrentTickChanged(double value)
    {
        if (Round is not null)
            TickChanged?.Invoke((int)value);
    }

    /// <summary>Раунд для таймлайна; null — таймлайн выключен.</summary>
    public void SetRound(RoundInfo? round, int tickRate)
    {
        if (Equals(round, Round))
            return;

        Pause();
        _tickRate = tickRate;
        Round = round;
        if (round is null)
        {
            ContextText = "";
            OnPropertyChanged(nameof(TimeText));
            return;
        }

        MinTick = round.FreezeEndTick;
        MaxTick = Math.Max(round.EndTick, round.FreezeEndTick + 1);
        CurrentTick = MinTick;
        OnPropertyChanged(nameof(TimeText));
        TickChanged?.Invoke((int)CurrentTick);
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        if (CurrentTick >= MaxTick)
            CurrentTick = MinTick;
        IsPlaying = true;
        _clock.Restart();
        _timer.Start();
    }

    public void Pause()
    {
        _timer.Stop();
        IsPlaying = false;
    }

    private void Advance()
    {
        // Шаг по реально прошедшему времени: таймер не гарантирует ровную частоту кадров.
        var elapsed = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        var next = CurrentTick + _tickRate * elapsed * Speed;
        if (next >= MaxTick)
        {
            CurrentTick = MaxTick;
            Pause();
            return;
        }
        CurrentTick = next;
    }
}
