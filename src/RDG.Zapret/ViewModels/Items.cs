using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
namespace RDG.Zapret.ViewModels;

public sealed class ServiceItemViewModel : ObservableObject
{
    private ProbeResult _result;
    private bool _isChecking;

    public ServiceItemViewModel(ServiceTarget target)
    {
        Target = target;
        _result = ProbeResult.Unknown(target.Id);
    }

    public ServiceTarget Target { get; }
    public string Name => Target.Name;
    public string Subtitle => Target.Subtitle;
    public string Badge => Target.Badge;
    public string ColorFrom => Target.ColorFrom;
    public string ColorTo => Target.ColorTo;

    public ProbeResult Result => _result;

    public ProbeStatus Status => _isChecking ? ProbeStatus.Checking : _result.Status;

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (!SetProperty(ref _isChecking, value))
                return;
            OnPropertyChanged(nameof(Status), nameof(LatencyText), nameof(Detail),
                nameof(MetricsText), nameof(MetricsTooltip), nameof(HasMetrics));
        }
    }

    public string LatencyText => _isChecking
        ? "…"
        : _result.LatencyMs is int ms ? $"{ms} мс" : "—";

    public string Detail => _isChecking ? "Проверяю соединение…" : _result.Detail;
    public string MetricsText => _isChecking || !_result.HasMetrics ? string.Empty : _result.MetricsText;
    public string MetricsTooltip => _isChecking ? string.Empty : _result.MetricsText;
    public bool HasMetrics => !_isChecking && _result.HasMetrics;
    public string Url => Target.Url;

    public bool IsAvailable => !_isChecking && _result.IsSuccess;

    public void MarkChecking()
    {
        IsChecking = true;
        OnPropertyChanged(nameof(Status), nameof(LatencyText), nameof(Detail));
    }

    public void Apply(ProbeResult result)
    {
        _result = result;
        _isChecking = false;
        OnPropertyChanged(nameof(Result), nameof(Status), nameof(IsChecking), nameof(LatencyText), nameof(Detail), nameof(IsAvailable),
            nameof(MetricsText), nameof(MetricsTooltip), nameof(HasMetrics));
    }

    public void Reset()
    {
        _result = ProbeResult.Unknown(Target.Id);
        _isChecking = false;
        OnPropertyChanged(nameof(Result), nameof(Status), nameof(IsChecking), nameof(LatencyText), nameof(Detail), nameof(IsAvailable),
            nameof(MetricsText), nameof(MetricsTooltip), nameof(HasMetrics));
    }
}

public sealed class DiagnosticItemViewModel
{
    public DiagnosticItemViewModel(DiagnosticItem item) => Item = item;

    public DiagnosticItem Item { get; }
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public DiagnosticStatus Status => Item.Status;
}

public sealed class CustomServiceItemViewModel
{
    public CustomServiceItemViewModel(string id, string name, string url)
    {
        Id = id;
        Name = name;
        Url = url;
    }

    public string Id { get; }
    public string Name { get; }
    public string Url { get; }
}

public sealed class YoutubeNodeViewModel
{
    public YoutubeNodeViewModel(YoutubeNodeResult node)
    {
        Host = node.Host;
        SpeedText = node.SpeedText;
        Detail = node.Detail;
        IsThrottled = node.IsThrottled;
    }

    public string Host { get; }
    public string SpeedText { get; }
    public string Detail { get; }
    public bool IsThrottled { get; }
    public bool IsProblem => IsThrottled || SpeedText == "нет данных";
    public string StateText => IsThrottled ? "медленно" : SpeedText == "нет данных" ? "недоступен" : "норма";
}

public sealed class NavItem
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Glyph { get; init; }
    public required object Page { get; init; }
}

public sealed class FilterOption : ObservableObject
{
    private int _count;

    public required string Key { get; init; }
    public required string Label { get; init; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
                OnPropertyChanged(nameof(DisplayLabel));
        }
    }

    public string DisplayLabel => Count > 0 ? $"{Label} · {Count}" : Label;
}

public sealed class AccentOption
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string ColorFrom { get; init; }
    public required string ColorTo { get; init; }
}

public sealed class ServiceToggleViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public ServiceToggleViewModel(ServiceTarget target, bool isSelected, Action changed)
    {
        Target = target;
        _changed = changed;
        _isSelected = isSelected;
    }

    public ServiceTarget Target { get; }
    public string Name => Target.Name;
    public string Badge => Target.Badge;
    public string ColorFrom => Target.ColorFrom;
    public string ColorTo => Target.ColorTo;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _changed();
        }
    }
}

public sealed class TuneServiceDotViewModel
{
    public required string Name { get; init; }
    public required ProbeStatus Status { get; init; }
    public required string Tooltip { get; init; }
}

public sealed class TuneRowViewModel : ObservableObject
{
    private bool _isBest;

    public TuneRowViewModel(
        TuneResult result,
        Action<StrategyPreset>? apply = null,
        Func<bool>? canApply = null)
    {
        Result = result;
        Dots = new ObservableCollection<TuneServiceDotViewModel>(
            result.Services.Select(s =>
            {
                var target = ServiceCatalog.TryById(s.ServiceId);
                return new TuneServiceDotViewModel
                {
                    Name = target?.Name ?? s.ServiceId,
                    Status = s.Status,
                    Tooltip = $"{target?.Name ?? s.ServiceId}: {s.Detail}"
                };
            }));

        ApplyCommand = new AsyncRelayCommand(
            () =>
            {
                apply?.Invoke(Result.Preset);
                return Task.CompletedTask;
            },
            () => canApply?.Invoke() ?? false);
    }

    public TuneResult Result { get; }
    public string Name => Result.Preset.DisplayName;
    public double Score => Result.Score;
    public string ScoreText => $"{Result.Score:0}";
    public string Note => Result.Note;
    public string DurationText => $"{Result.Duration.TotalSeconds:0.0} с";
    public string ServicesText => $"{Result.AvailableCount}/{Result.TotalCount}";
    public ObservableCollection<TuneServiceDotViewModel> Dots { get; }
    public AsyncRelayCommand ApplyCommand { get; }

    public bool IsBest
    {
        get => _isBest;
        set
        {
            if (SetProperty(ref _isBest, value))
                OnPropertyChanged(nameof(BadgeText));
        }
    }

    public string BadgeText => IsBest ? "Лучшая" : string.Empty;
}

public sealed class GeneratorRowViewModel : ObservableObject
{
    private bool _isBest;

    public GeneratorRowViewModel(GeneratorAttempt attempt)
    {
        Attempt = attempt;
        Dots = new ObservableCollection<TuneServiceDotViewModel>(
            attempt.Services.Select(s =>
            {
                var target = ServiceCatalog.TryById(s.ServiceId);
                return new TuneServiceDotViewModel
                {
                    Name = target?.Name ?? s.ServiceId,
                    Status = s.Status,
                    Tooltip = $"{target?.Name ?? s.ServiceId}: {s.Detail}"
                };
            }));
    }

    public GeneratorAttempt Attempt { get; }
    public string Name => Attempt.Recipe.Name;
    public string Summary => Attempt.Recipe.Summary;
    public double Score => Attempt.Score;
    public string ScoreText => $"{Attempt.Score:0}";
    public string Note => Attempt.Note;
    public string DurationText => $"{Attempt.Duration.TotalSeconds:0.0} с";
    public string ServicesText => $"{Attempt.AvailableCount}/{Attempt.TotalCount}";
    public ObservableCollection<TuneServiceDotViewModel> Dots { get; }

    public bool IsBest
    {
        get => _isBest;
        set
        {
            if (SetProperty(ref _isBest, value))
                OnPropertyChanged(nameof(BadgeText));
        }
    }

    public string BadgeText => IsBest ? "Лучшая" : string.Empty;
}

public sealed class LogFilterOption
{
    public required string Key { get; init; }
    public required string Label { get; init; }
}
