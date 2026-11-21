using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class StrategiesViewModel : ObservableObject, IPageActivation
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly ServiceProbe _probe;
    private readonly ResultsService _results;
    private readonly LogService _log;
    private readonly OperationCoordinator _operations;
    private readonly Action<string, bool> _toast;
    private readonly Action<string> _navigate;

    private readonly List<StrategyItemViewModel> _all = new();
    private StrategyItemViewModel? _selectedItem;
    private string _searchText = string.Empty;
    private bool _isTesting;
    private bool _isStarting;
    private double _testingProgress;
    private string _testingName = string.Empty;
    private string _testingStatus = string.Empty;

    public StrategiesViewModel(
        SettingsService settings,
        PresetScanner scanner,
        WinwsController winws,
        ServiceProbe probe,
        ResultsService results,
        LogService log,
        OperationCoordinator operations,
        Action<string, bool> toast,
        Action<string> navigate)
    {
        _settings = settings;
        _scanner = scanner;
        _winws = winws;
        _probe = probe;
        _results = results;
        _log = log;
        _operations = operations;
        _toast = toast;
        _navigate = navigate;

        Filters = new ObservableCollection<FilterOption>
        {
            new() { Key = "all", Label = "Все" },
            new() { Key = "general", Label = "Общие" },
            new() { Key = "alt", Label = "ALT" },
            new() { Key = "fake", Label = "Fake" },
            new() { Key = "other", Label = "Прочие" }
        };
        _selectedFilter = Filters[0];

        StartCommand = new AsyncRelayCommand(StartSelectedAsync, () => SelectedItem is not null && !IsBusy);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _winws.IsRunning && !IsBusy);
        TestCommand = new AsyncRelayCommand(TestSelectedAsync, () => SelectedItem is not null && !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RescanAsync, () => !IsBusy && _scanner.Current is not null);
        OpenFileCommand = new RelayCommand(OpenFile, () => SelectedItem is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedItem is not null);
        CopyArgsCommand = new RelayCommand(CopyArgs, () => SelectedItem is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => SearchText.Length > 0);

        scanner.Changed += (_, _) => Ui.Invoke(ReloadPresets);
        winws.StateChanged += (_, _) => Ui.Invoke(RefreshActive);
        results.Changed += (_, _) => Ui.Invoke(RefreshScores);
        settings.Changed += (_, _) => Ui.Invoke(RefreshFlags);

        ReloadPresets();
    }

    public ObservableCollection<StrategyItemViewModel> Strategies { get; } = new();
    public ObservableCollection<FilterOption> Filters { get; }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyArgsCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    private FilterOption _selectedFilter;
    public FilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is null || !SetProperty(ref _selectedFilter, value))
                return;
            ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;
            UpdateCounts();
            ApplyFilter();
        }
    }

    public StrategyItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasSelection => SelectedItem is not null;

    public bool HasInstallation => _scanner.Current is not null;
    public bool IsRunning => _winws.IsRunning;
    public string InstallationHint => _scanner.Current is null
        ? "Папка zapret не выбрана"
        : $"{_scanner.Current.FolderName} · {_scanner.Presets.Count} " +
          $"{Plural.Ru(_scanner.Presets.Count, "стратегия", "стратегии", "стратегий")} · " +
          $"{_scanner.ActiveEngineFile}";

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsStarting
    {
        get => _isStarting;
        private set
        {
            if (SetProperty(ref _isStarting, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsBusy => IsTesting || IsStarting;

    public double TestingProgress
    {
        get => _testingProgress;
        private set => SetProperty(ref _testingProgress, value);
    }

    public string TestingName
    {
        get => _testingName;
        private set => SetProperty(ref _testingName, value);
    }

    public string TestingStatus
    {
        get => _testingStatus;
        private set => SetProperty(ref _testingStatus, value);
    }

    public string ListSummary
    {
        get
        {
            var shown = Strategies.Count;
            var total = _all.Count;
            return shown == total
                ? $"{total} {Plural.Ru(total, "стратегия", "стратегии", "стратегий")}"
                : $"{shown} из {total}";
        }
    }

    public async Task OnActivatedAsync()
    {
        RefreshScores();
        RefreshActive();
        await Task.CompletedTask;
    }

    private void ReloadPresets()
    {
        var previousFile = SelectedItem?.FileName;
        var cached = _all.ToDictionary(i => i.FileName, StringComparer.OrdinalIgnoreCase);
        _all.Clear();

        foreach (var preset in _scanner.Presets)
        {
            if (!cached.TryGetValue(preset.FileName, out var item))
                item = new StrategyItemViewModel(preset);
            item.IsTesting = false;
            _all.Add(item);
        }

        RefreshScores();
        UpdateCounts();
        ApplyFilter();

        SelectedItem = _all.FirstOrDefault(i => i.FileName.Equals(previousFile, StringComparison.OrdinalIgnoreCase))
                       ?? _all.FirstOrDefault(i => i.IsBest)
                       ?? Strategies.FirstOrDefault();

        OnPropertyChanged(nameof(HasInstallation), nameof(InstallationHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshScores()
    {
        foreach (var item in _all)
        {
            item.ApplyScore(_results.Get(item.FileName));
            item.IsBest = _settings.Current.BestPresetFile.Equals(item.FileName, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(ListSummary));
    }

    private void RefreshFlags()
    {
        foreach (var item in _all)
            item.IsBest = _settings.Current.BestPresetFile.Equals(item.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshActive()
    {
        OnPropertyChanged(nameof(IsRunning));
        var activeFile = _winws.IsRunning ? _winws.ActivePreset?.FileName : null;
        foreach (var item in _all)
            item.IsActive = activeFile is not null && item.FileName.Equals(activeFile, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        var selectedFile = SelectedItem?.FileName;
        Strategies.Clear();

        foreach (var item in _all)
        {
            if (MatchesFilter(item, SelectedFilter.Key) && MatchesSearch(item))
                Strategies.Add(item);
        }

        if (selectedFile is not null)
        {
            var restored = Strategies.FirstOrDefault(i => i.FileName.Equals(selectedFile, StringComparison.OrdinalIgnoreCase));
            if (restored is not null)
                SelectedItem = restored;
        }

        if (SelectedItem is null || !Strategies.Contains(SelectedItem))
            SelectedItem = Strategies.FirstOrDefault();

        OnPropertyChanged(nameof(ListSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateCounts()
    {
        foreach (var filter in Filters)
            filter.Count = _all.Count(i => MatchesFilter(i, filter.Key) && MatchesSearch(i));
    }

    private bool MatchesSearch(StrategyItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;
        var query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Tag.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilter(StrategyItemViewModel item, string key) => key switch
    {
        "general" => item.Tag == "Базовый",
        "alt" => item.Tag.StartsWith("ALT", StringComparison.OrdinalIgnoreCase),
        "fake" => item.Tag.Contains("Fake", StringComparison.OrdinalIgnoreCase),
        "other" => item.Tag != "Базовый"
                   && !item.Tag.StartsWith("ALT", StringComparison.OrdinalIgnoreCase)
                   && !item.Tag.Contains("Fake", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private async Task StartSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null)
            return;

        if (await DiagnosticsService.IsServiceRunningAsync("zapret"))
        {
            _toast("Служба zapret запущена. Остановите её в разделе «Диагностика».", true);
            _navigate("diagnostics");
            return;
        }

        if (!_operations.TryBegin("strategy"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        IsStarting = true;
        try
        {
            var outcome = await _winws.StartAsync(item.Preset);
            if (outcome.Success)
            {
                _settings.Update(s => s.LastPresetFile = item.FileName);
                _toast($"Запущено: {item.Name}", false);
            }
            else
            {
                _toast(outcome.Error ?? "Не удалось запустить стратегию", true);
            }
        }
        finally
        {
            IsStarting = false;
            _operations.End("strategy");
            RefreshActive();
        }
    }

    private async Task StopAsync()
    {
        IsStarting = true;
        try
        {
            await _winws.StopAsync();
            _toast("Обход остановлен", false);
        }
        finally
        {
            IsStarting = false;
            RefreshActive();
        }
    }

    private async Task TestSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null || IsTesting)
            return;

        if (await DiagnosticsService.IsServiceRunningAsync("zapret"))
        {
            _toast("Служба zapret запущена. Остановите её в разделе «Диагностика».", true);
            _navigate("diagnostics");
            return;
        }

        if (!_operations.TryBegin("strategy-test"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        var previous = _winws.ActivePreset;
        var wasRunning = _winws.IsRunning;

        IsTesting = true;
        item.IsTesting = true;
        TestingName = item.Name;
        TestingStatus = "Запускаю стратегию…";
        TestingProgress = 12;
        _log.Info("test", $"Ручная проверка стратегии «{item.Name}»");

        try
        {
            await _winws.StopAsync(quiet: true);
            var outcome = await _winws.StartAsync(item.Preset);
            if (!outcome.Success)
            {
                TestingStatus = "Не удалось запустить";
                _toast(outcome.Error ?? "Стратегия не запустилась", true);
                return;
            }

            TestingStatus = "Прогрев…";
            TestingProgress = 40;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_settings.Current.TuneWarmupSeconds, 2, 10)));

            TestingStatus = "Проверяю сервисы…";
            TestingProgress = 65;

            var results = await _probe.CheckManyAsync(
                ServiceCatalog.All,
                new ProbeOptions
                {
                    TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
                    ClassifyWithTls = _settings.Current.DeepClassification
                },
                CancellationToken.None);

            var score = CalculateScore(results);
            var tuneResult = new TuneResult
            {
                Preset = item.Preset,
                Score = score,
                Services = results,
                Note = "Ручная проверка"
            };

            _results.Record(tuneResult, manual: true);
            TestingProgress = 100;
            TestingStatus = $"Готово: {score:0}/100";

            var ok = results.Count(r => r.IsSuccess);
            _toast($"«{item.Name}»: {score:0}/100 · {ok}/{results.Count} сервисов", score >= 55);
            _log.Success("test", $"«{item.Name}»: {score:0}/100 · {ok}/{results.Count}");
        }
        catch (Exception ex)
        {
            _log.Error("test", "Ошибка ручной проверки: " + ex.Message);
            _toast("Ошибка проверки: " + ex.Message, true);
        }
        finally
        {
            await _winws.StopAsync(quiet: true);
            if (wasRunning && previous is not null)
            {
                var restore = await _winws.StartAsync(previous);
                if (!restore.Success)
                    _log.Warn("test", "Не удалось вернуть предыдущую стратегию: " + restore.Error);
            }

            IsTesting = false;
            item.IsTesting = false;
            TestingProgress = 0;
            _operations.End("strategy-test");
            RefreshActive();
        }
    }

    private static double CalculateScore(IReadOnlyList<ProbeResult> results)
    {
        double earned = 0;
        double max = 0;

        foreach (var result in results)
        {
            var target = ServiceCatalog.TryById(result.ServiceId);
            if (target is null)
                continue;

            max += target.Weight;
            if (result.Status == ProbeStatus.Available)
                earned += target.Weight;
            else if (result.Status == ProbeStatus.Partial)
                earned += target.Weight * 0.5;
        }

        return max <= 0 ? 0 : Math.Clamp(earned / max * 100, 0, 100);
    }

    private async Task RescanAsync()
    {
        var path = _scanner.Current?.RootPath ?? _settings.Current.ZapretPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _navigate("settings");
            return;
        }

        IsStarting = true;
        try
        {
            var result = await _scanner.ScanAsync(path);
            if (result.Installation is null)
                _toast(result.Error ?? "Не удалось обновить список стратегий", true);
            else
                _toast("Список стратегий обновлён", false);
        }
        finally
        {
            IsStarting = false;
        }
    }

    private void OpenFile()
    {
        if (SelectedItem is not null)
            ShellHelper.SelectFile(SelectedItem.Preset.FilePath);
    }

    private void OpenFolder()
    {
        var path = _scanner.Current?.RootPath;
        if (!string.IsNullOrWhiteSpace(path))
            ShellHelper.OpenFolder(path);
    }

    private void CopyArgs()
    {
        if (SelectedItem is null)
            return;
        ShellHelper.CopyText(SelectedItem.Preset.Arguments);
        _toast("Аргументы скопированы", false);
    }
}
