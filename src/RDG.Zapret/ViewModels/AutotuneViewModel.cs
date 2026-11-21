using System.Text.Json;
using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class AutotuneViewModel : ObservableObject, IPageActivation
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly AutoTuner _tuner;
    private readonly ResultsService _results;
    private readonly LogService _log;
    private readonly OperationCoordinator _operations;
    private readonly Action<string, bool> _toast;
    private readonly Action<string> _navigate;

    private readonly Dictionary<string, TuneRowViewModel> _rowsByFile = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;
    private TuneReport? _lastReport;

    private bool _isTuning;
    private bool _isCanceling;
    private double _progress;
    private string _currentStrategy = string.Empty;
    private string _statusText = "Сравню стратегии по доступности выбранных сервисов";
    private string _progressCaption = string.Empty;
    private string _etaText = string.Empty;
    private string _baselineText = "Baseline будет проверен перед подбором";
    private string _baselineRatio = "—";
    private string _bestName = string.Empty;
    private string _bestScoreText = string.Empty;
    private bool _hasBest;
    private string _summaryText = string.Empty;
    private string _idleHeadline = "Готов к запуску";
    private string _stageText = string.Empty;
    private string _screenText = string.Empty;
    private string _screenBestText = string.Empty;
    private int _bestScreenLatency = int.MaxValue;
    private string _bestScreenName = string.Empty;

    public AutotuneViewModel(
        SettingsService settings,
        PresetScanner scanner,
        WinwsController winws,
        AutoTuner tuner,
        ResultsService results,
        LogService log,
        OperationCoordinator operations,
        Action<string, bool> toast,
        Action<string> navigate)
    {
        _settings = settings;
        _scanner = scanner;
        _winws = winws;
        _tuner = tuner;
        _results = results;
        _log = log;
        _operations = operations;
        _toast = toast;
        _navigate = navigate;

        Services = new ObservableCollection<ServiceToggleViewModel>(
            ServiceCatalog.All.Select(target => new ServiceToggleViewModel(
                target,
                !_settings.Current.TuneDisabledServices.Contains(target.Id),
                OnServiceToggleChanged)));

        StartCommand = new AsyncRelayCommand(StartTuningAsync,
            () => !IsTuning && HasInstallation && SelectedServicesCount > 0 && CandidateCount > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsTuning && !IsCanceling);
        ApplyBestCommand = new AsyncRelayCommand(ApplyBestAsync, () => HasBest && !IsTuning);
        CopyReportCommand = new RelayCommand(CopyReport, () => _lastReport is not null);
        OpenReportFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_settings.DirectoryPath));
        WarmupPlusCommand = new RelayCommand(() => AdjustWarmup(1), () => !IsTuning);
        WarmupMinusCommand = new RelayCommand(() => AdjustWarmup(-1), () => !IsTuning);
        SelectAllServicesCommand = new RelayCommand(SelectAllServices, () => !IsTuning);
        SelectNoServicesCommand = new RelayCommand(SelectNoServices, () => !IsTuning);
        NavigateSettingsCommand = new RelayCommand(() => _navigate("settings"));
        NavigateStrategiesCommand = new RelayCommand(() => _navigate("strategies"));
        ResetExcludedCommand = new RelayCommand(ResetExcluded, () => HasExcluded && !IsTuning);

        scanner.Changed += (_, _) => Ui.Invoke(HandleScannerChanged);
        ServiceCatalog.Changed += (_, _) => Ui.Invoke(RebuildServiceToggles);
    }

    private void HandleScannerChanged()
    {
        var reportEngine = _lastReport?.Best?.Preset.EngineKind
                           ?? _lastReport?.Results.FirstOrDefault()?.Preset.EngineKind;

        if (!IsTuning && reportEngine is not null && reportEngine != _scanner.Engine)
        {
            Rows.Clear();
            _rowsByFile.Clear();
            _lastReport = null;
            HasBest = false;
            BestName = string.Empty;
            BestScoreText = string.Empty;
            SummaryText = string.Empty;
            ProgressCaption = string.Empty;
            EtaText = string.Empty;
            StageText = string.Empty;
            ScreenText = string.Empty;
            ScreenBestText = string.Empty;
            Progress = 0;
            _idleHeadline = "Готов к запуску";
            StatusText = "Ядро изменено — запустите подбор заново";
            OnPropertyChanged(nameof(Headline), nameof(SavedBestText));
        }

        UpdateEstimate();
        OnPropertyChanged(nameof(SavedBestText));
    }

    public ObservableCollection<ServiceToggleViewModel> Services { get; }
    public ObservableCollection<TuneRowViewModel> Rows { get; } = new();

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ApplyBestCommand { get; }
    public RelayCommand CopyReportCommand { get; }
    public RelayCommand OpenReportFolderCommand { get; }
    public RelayCommand WarmupPlusCommand { get; }
    public RelayCommand WarmupMinusCommand { get; }
    public RelayCommand SelectAllServicesCommand { get; }
    public RelayCommand SelectNoServicesCommand { get; }
    public RelayCommand NavigateSettingsCommand { get; }
    public RelayCommand NavigateStrategiesCommand { get; }
    public RelayCommand ResetExcludedCommand { get; }

    public bool HasInstallation => _scanner.Current is not null;
    public string InstallationHint => _scanner.Current is null
        ? "Укажите папку zapret в настройках"
        : $"{_scanner.Current.FolderName} · {_scanner.ActiveEngineFile}";

    public int CandidateCount => GetCandidates().Count;

    public bool TuneBestOnly
    {
        get => _settings.Current.TuneBestOnly;
        set
        {
            if (value == _settings.Current.TuneBestOnly)
                return;
            _settings.Update(s => s.TuneBestOnly = value);
            OnPropertyChanged();
            UpdateEstimate();
        }
    }

    private List<StrategyPreset> GetCandidates()
    {
        var all = _scanner.Presets
            .Where(p => !IsExcluded(p.FileName))
            .ToList();

        if (!_settings.Current.TuneBestOnly || all.Count == 0)
            return all;

        var ranked = all
            .Select(p => (Preset: p, Score: _results.Get(p.FileName)?.Score))
            .ToList();

        var known = ranked
            .Where(x => x.Score.HasValue)
            .OrderByDescending(x => x.Score!.Value)
            .Select(x => x.Preset)
            .ToList();

        // First run: no history yet, so check everything.
        if (known.Count == 0)
            return all;

        var limit = Math.Clamp(_settings.Current.TuneBestLimit, 3, 30);
        var newSlots = Math.Min(3, Math.Max(0, limit - 1));
        var knownTake = Math.Max(1, limit - newSlots);

        var selected = known.Take(knownTake).ToList();

        // New strategies from the server always get a few slots.
        selected.AddRange(ranked
            .Where(x => !x.Score.HasValue)
            .Select(x => x.Preset)
            .Take(limit - selected.Count));

        // If there are no new ones, fill the rest with the next best known.
        if (selected.Count < limit)
            selected.AddRange(known.Skip(knownTake).Take(limit - selected.Count));

        return selected;
    }

    public int ExcludedCount => _settings.Current.TuneExcludedPresets.Count;
    public bool HasExcluded => ExcludedCount > 0;
    public string ExcludedText => $"Исключено нерабочих стратегий: {ExcludedCount}";
    public string MinimumScoreText => "Показаны стратегии, где работают все выбранные сервисы";
    public string ExcludedNames => string.Join(", ", _settings.Current.TuneExcludedPresets);

    private bool IsExcluded(string fileName) =>
        _settings.Current.TuneExcludedPresets.Any(
            x => x.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    public int SelectedServicesCount => Services.Count(s => s.IsSelected);

    public string EstimateText
    {
        get
        {
            if (CandidateCount == 0)
                return ExcludedCount > 0 ? "Все стратегии исключены" : "Нет доступных стратегий";

            // Every candidate is fully checked against all selected services.
            var perStrategy = _settings.Current.TuneWarmupSeconds +
                              _settings.Current.ProbeTimeoutSeconds + 4.0;
            if (_settings.Current.TuneMode == "deep") perStrategy += _settings.Current.ProbeTimeoutSeconds;
            var seconds = CandidateCount * perStrategy + 15;
            return $"{CandidateCount} {Plural.Ru(CandidateCount, "стратегия", "стратегии", "стратегий")} · ≈ {FormatDuration(seconds)}";
        }
    }

    public string ModeLabel => _settings.Current.TuneMode switch
    {
        "quick" => "Быстрый",
        "deep" => "Глубокий",
        _ => "Стандартный"
    };

    public bool IsQuick
    {
        get => _settings.Current.TuneMode == "quick";
        set { if (value) SetMode("quick"); }
    }

    public bool IsStandard
    {
        get => _settings.Current.TuneMode == "standard";
        set { if (value) SetMode("standard"); }
    }

    public bool IsDeep
    {
        get => _settings.Current.TuneMode == "deep";
        set { if (value) SetMode("deep"); }
    }

    public string WarmupText => $"{_settings.Current.TuneWarmupSeconds} с";

    public bool SkipBaselineOk
    {
        get => _settings.Current.TuneSkipBaselineOk;
        set
        {
            if (value == _settings.Current.TuneSkipBaselineOk)
                return;
            _settings.Update(s => s.TuneSkipBaselineOk = value);
            OnPropertyChanged();
        }
    }

    public bool KeepBestRunning
    {
        get => _settings.Current.TuneKeepBestRunning;
        set
        {
            if (value == _settings.Current.TuneKeepBestRunning)
                return;
            _settings.Update(s => s.TuneKeepBestRunning = value);
            OnPropertyChanged();
        }
    }

    public bool IsTuning
    {
        get => _isTuning;
        private set
        {
            if (SetProperty(ref _isTuning, value))
            {
                OnPropertyChanged(nameof(Headline));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string Headline => IsTuning
        ? (string.IsNullOrWhiteSpace(CurrentStrategy) ? "Идёт подбор…" : CurrentStrategy)
        : _idleHeadline;

    public bool IsCanceling
    {
        get => _isCanceling;
        private set
        {
            if (SetProperty(ref _isCanceling, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string CurrentStrategy
    {
        get => _currentStrategy;
        private set
        {
            if (SetProperty(ref _currentStrategy, value))
                OnPropertyChanged(nameof(Headline));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProgressCaption
    {
        get => _progressCaption;
        private set => SetProperty(ref _progressCaption, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    public string BaselineText
    {
        get => _baselineText;
        private set => SetProperty(ref _baselineText, value);
    }

    public string BaselineRatio
    {
        get => _baselineRatio;
        private set => SetProperty(ref _baselineRatio, value);
    }

    public string BestName
    {
        get => _bestName;
        private set => SetProperty(ref _bestName, value);
    }

    public string BestScoreText
    {
        get => _bestScoreText;
        private set => SetProperty(ref _bestScoreText, value);
    }

    public bool HasBest
    {
        get => _hasBest;
        private set
        {
            if (SetProperty(ref _hasBest, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    public string ScreenText
    {
        get => _screenText;
        private set => SetProperty(ref _screenText, value);
    }

    public string ScreenBestText
    {
        get => _screenBestText;
        private set => SetProperty(ref _screenBestText, value);
    }

    public bool HasScreenText => !string.IsNullOrWhiteSpace(ScreenText);

    public string SavedBestText
    {
        get
        {
            var best = _scanner.Find(_settings.Current.BestPresetFile);
            return best is null
                ? "Рабочая стратегия ещё не выбрана"
                : $"Сохранено: {best.DisplayName} · {_settings.Current.BestScore:0}/100";
        }
    }

    public string ReportPath => Path.Combine(_settings.DirectoryPath, "last-tune.json");

    public Task OnActivatedAsync()
    {
        UpdateEstimate();
        OnPropertyChanged(nameof(SavedBestText));
        return Task.CompletedTask;
    }

    private void SetMode(string mode)
    {
        if (_settings.Current.TuneMode == mode)
            return;

        _settings.Update(s => s.TuneMode = mode);
        OnPropertyChanged(nameof(IsQuick), nameof(IsStandard), nameof(IsDeep), nameof(ModeLabel));
        UpdateEstimate();
    }

    private void AdjustWarmup(int delta)
    {
        var value = Math.Clamp(_settings.Current.TuneWarmupSeconds + delta, 2, 10);
        if (value == _settings.Current.TuneWarmupSeconds)
            return;

        _settings.Update(s => s.TuneWarmupSeconds = value);
        OnPropertyChanged(nameof(WarmupText));
        UpdateEstimate();
    }

    private void RebuildServiceToggles()
    {
        var disabled = new HashSet<string>(
            _settings.Current.TuneDisabledServices,
            StringComparer.OrdinalIgnoreCase);

        Services.Clear();
        foreach (var target in ServiceCatalog.All)
        {
            Services.Add(new ServiceToggleViewModel(
                target,
                !disabled.Contains(target.Id),
                OnServiceToggleChanged));
        }

        OnPropertyChanged(nameof(SelectedServicesCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnServiceToggleChanged()
    {
        var disabled = Services.Where(s => !s.IsSelected).Select(s => s.Target.Id).ToList();
        _settings.Update(s => s.TuneDisabledServices = disabled);
        OnPropertyChanged(nameof(SelectedServicesCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectAllServices()
    {
        foreach (var service in Services)
            service.IsSelected = true;
    }

    private void SelectNoServices()
    {
        foreach (var service in Services)
            service.IsSelected = false;
    }

    private void UpdateEstimate()
    {
        OnPropertyChanged(nameof(CandidateCount), nameof(EstimateText), nameof(HasInstallation), nameof(InstallationHint));
    }

    private async Task StartTuningAsync()
    {
        if (IsTuning)
            return;

        if (!HasInstallation)
        {
            _toast("Сначала укажите папку zapret", true);
            _navigate("settings");
            return;
        }

        if (await DiagnosticsService.IsServiceRunningAsync("zapret"))
        {
            _toast("Служба zapret запущена. Остановите её в разделе «Диагностика».", true);
            _navigate("diagnostics");
            return;
        }

        if (_winws.IsRunning && _winws.IsExternal)
        {
            _toast("Обход запущен внешним процессом winws. Остановите его, чтобы не прерывать чужую работу.", true);
            return;
        }

        var candidates = GetCandidates();

        if (candidates.Count == 0)
        {
            _toast(ExcludedCount > 0
                ? "Все стратегии исключены. Нажмите «Вернуть исключённые»."
                : "Нет стратегий для проверки", true);
            return;
        }

        // Strong strategies from previous runs go first so stable winners show
        // up early, but every strategy is still fully checked.
        candidates = candidates
            .OrderByDescending(p => _results.Get(p.FileName)?.Score ?? 0)
            .ThenBy(p => p.FileName, NaturalStringComparer.Instance)
            .ToList();

        var mode = _settings.Current.TuneMode;
        var enabledTargets = Services.Where(s => s.IsSelected).Select(s => s.Target).ToList();
        if (enabledTargets.Count == 0)
        {
            _toast("Выберите хотя бы один сервис", true);
            return;
        }

        // Every selected service is checked for every strategy.
        var targets = enabledTargets;

        if (!_operations.TryBegin("autotune"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        Rows.Clear();
        _rowsByFile.Clear();
        _lastReport = null;
        HasBest = false;
        BestName = string.Empty;
        BestScoreText = string.Empty;
        SummaryText = string.Empty;
        Progress = 0;
        CurrentStrategy = string.Empty;
        ProgressCaption = string.Empty;
        EtaText = string.Empty;
        StageText = string.Empty;
        ScreenText = string.Empty;
        ScreenBestText = string.Empty;
        _bestScreenLatency = int.MaxValue;
        _bestScreenName = string.Empty;
        BaselineText = "Проверяю доступность без обхода…";
        BaselineRatio = "—";
        StatusText = "Подготовка…";
        IsCanceling = false;
        IsTuning = true;
        _startedAt = DateTime.Now;
        _cts = new CancellationTokenSource();

        var request = new TuneRequest
        {
            Targets = targets,
            WarmupSeconds = _settings.Current.TuneWarmupSeconds,
            TimeoutSeconds = mode == "quick"
                ? Math.Min(3, _settings.Current.ProbeTimeoutSeconds)
                : _settings.Current.ProbeTimeoutSeconds,
            Passes = mode == "deep" ? 2 : 1,
            SkipBaselineOk = _settings.Current.TuneSkipBaselineOk,
            KeepBestRunning = _settings.Current.TuneKeepBestRunning,
            Mode = mode,
            AllowEarlyExit = false,
            EarlyExitScore = 100,
            TwoStage = false
        };

        var progress = new Progress<TuneUpdate>(HandleUpdate);

        try
        {
            var report = await _tuner.RunAsync(candidates, request, progress, _cts.Token);
            ProcessReport(report);
        }
        catch (Exception ex)
        {
            _log.Error("tuner", "Не удалось выполнить автоподбор: " + ex.Message);
            _toast("Ошибка автоподбора: " + ex.Message, true);
            StatusText = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsTuning = false;
            IsCanceling = false;
            _cts?.Dispose();
            _cts = null;
            _operations.End("autotune");
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void HandleUpdate(TuneUpdate update)
    {
        switch (update.Phase)
        {
            case TunePhase.Preparing:
                StatusText = update.Message ?? "Подготовка…";
                break;

            case TunePhase.Baseline:
                if (update.Baseline is not null)
                {
                    var ok = update.Baseline.Where(r => r.IsSuccess).ToList();
                    BaselineRatio = $"{ok.Count} / {update.Baseline.Count}";
                    var names = ok.Select(r => ServiceCatalog.TryById(r.ServiceId)?.Name ?? r.ServiceId).ToList();
                    BaselineText = names.Count == 0
                        ? "Без обхода все сервисы заблокированы"
                        : "Без обхода уже работают: " + string.Join(", ", names);
                }

                StatusText = update.Message ?? "Проверяю без обхода…";
                break;

            case TunePhase.Screening:
                CurrentStrategy = update.Current?.DisplayName ?? string.Empty;
                StageText = "Этап 1 из 2 · быстрая проверка YouTube";
                ProgressCaption = update.Total > 0
                    ? $"Проверено {update.Index} из {update.Total}"
                    : string.Empty;
                ScreenText = update.Total > 0
                    ? $"Быстро проверено: {update.Screened} из {update.Total}"
                    : string.Empty;

                if (update.ScreenCompleted is { } screen &&
                    screen.Status is ProbeStatus.Available or ProbeStatus.Partial &&
                    (screen.Youtube.LatencyMs ?? int.MaxValue) < _bestScreenLatency)
                {
                    _bestScreenLatency = screen.Youtube.LatencyMs ?? int.MaxValue;
                    _bestScreenName = screen.Preset.DisplayName;
                    ScreenBestText = $"Лучший на быстрой проверке: {_bestScreenName} · {_bestScreenLatency} мс";
                }
                StatusText = update.Message ?? string.Empty;
                if (update.Total > 0)
                    Progress = Math.Max(Progress, update.Index * 55.0 / update.Total);
                UpdateEta(update.Index, update.Total);
                OnPropertyChanged(nameof(HasScreenText));
                break;

            case TunePhase.Finalists:
                CurrentStrategy = update.Current?.DisplayName ?? string.Empty;
                StageText = "Этап 2 из 2 · полная проверка финалистов";
                ProgressCaption = update.Total > 0
                    ? $"Финалист {update.Index} из {update.Total}"
                    : string.Empty;
                if (update.Screened > 0)
                    ScreenText = $"Быстро проверено: {update.Screened} · в финал вышли: {update.Total}";
                StatusText = update.Message ?? string.Empty;

                if (update.Completed is not null)
                {
                    AddRow(update.Completed);
                    if (update.Total > 0)
                        Progress = Math.Max(Progress, 55 + update.Index * 45.0 / update.Total);
                    UpdateEta(update.Index, update.Total);
                }
                else if (update.Total > 0)
                {
                    Progress = Math.Max(Progress, 55 + Math.Max(0, update.Index - 1) * 45.0 / update.Total);
                }

                OnPropertyChanged(nameof(HasScreenText));
                break;

            case TunePhase.Testing:
                CurrentStrategy = update.Current?.DisplayName ?? string.Empty;
                ProgressCaption = update.Total > 0
                    ? $"Стратегия {update.Index} из {update.Total}"
                    : string.Empty;
                StatusText = update.Message ?? string.Empty;

                if (update.Completed is not null)
                {
                    AddRow(update.Completed);
                    if (update.Total > 0)
                        Progress = Math.Max(Progress, update.Index * 100.0 / update.Total);
                    UpdateEta(update.Index, update.Total);
                }
                else if (update.Total > 0)
                {
                    Progress = Math.Max(Progress, (update.Index - 0.7) * 100.0 / update.Total);
                }

                break;

            case TunePhase.Applying:
                StatusText = update.Message ?? "Запускаю лучшую стратегию…";
                Progress = 100;
                break;

            case TunePhase.Finished:
                StatusText = update.Message ?? "Автоподбор завершён";
                Progress = 100;
                ProgressCaption = string.Empty;
                EtaText = string.Empty;
                StageText = string.Empty;
                break;

            case TunePhase.Canceled:
                StatusText = "Автоподбор отменён";
                ProgressCaption = string.Empty;
                EtaText = string.Empty;
                StageText = string.Empty;
                break;

            case TunePhase.Failed:
                StatusText = "Ошибка: " + (update.Message ?? "неизвестная ошибка");
                EtaText = string.Empty;
                StageText = string.Empty;
                break;
        }
    }

    private void UpdateEta(int completed, int total)
    {
        if (completed <= 0 || total <= completed)
        {
            EtaText = total <= completed ? string.Empty : "Оценка времени…";
            return;
        }

        var elapsed = DateTime.Now - _startedAt;
        var average = elapsed.TotalSeconds / completed;
        var remaining = average * (total - completed);
        EtaText = $"Осталось ≈ {FormatDuration(remaining)}";
    }

    private void AddRow(TuneResult result)
    {
        if (!result.IsStable)
            return;

        if (_rowsByFile.ContainsKey(result.Preset.FileName))
            return;

        var row = new TuneRowViewModel(result, ApplyRow, () => !IsTuning);
        _rowsByFile[result.Preset.FileName] = row;
        Rows.Add(row);
    }

    private void ApplyRow(StrategyPreset preset) => _ = ApplyPresetAsync(preset);

    private async Task ApplyPresetAsync(StrategyPreset preset)
    {
        if (IsTuning)
            return;

        if (!_operations.TryBegin("manual-apply"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        try
        {
            var outcome = await _winws.StartAsync(preset);
            if (outcome.Success)
            {
                _settings.Update(s => s.LastPresetFile = preset.FileName);
                _toast($"Запущено: {preset.DisplayName}", false);
                _log.Success("tuner", "Вручную применена стратегия: " + preset.DisplayName);
            }
            else
            {
                _toast(outcome.Error ?? "Не удалось запустить стратегию", true);
            }
        }
        catch (Exception ex)
        {
            _log.Error("tuner", "Ошибка ручного запуска: " + ex);
            _toast("Ошибка запуска: " + ex.Message, true);
        }
        finally
        {
            _operations.End("manual-apply");
        }
    }

    private void ProcessReport(TuneReport report)
    {
        _lastReport = report;

        if (report.ScreenedCount > 0)
        {
            var passing = report.Results.Count(r => r.Score >= AutoTuner.MinimumUsefulScore);
            ScreenText = $"Быстро проверено: {report.ScreenedCount} · в финал: {report.Results.Count} · прошли порог: {passing}";
        }
        OnPropertyChanged(nameof(HasScreenText));

        foreach (var result in report.Results.Where(r => r.IsStable))
            _results.Record(result, manual: false);

        var newlyExcluded = report.Canceled ? 0 : ApplyExclusions(report);

        if (report.Best is not null)
        {
            BestName = report.Best.Preset.DisplayName;
            BestScoreText = $"{report.Best.Score:0} / 100";
            HasBest = true;
            _idleHeadline = report.Canceled ? "Подбор остановлен" : "Подбор завершён";

            if (_rowsByFile.TryGetValue(report.Best.Preset.FileName, out var bestRow))
                bestRow.IsBest = true;

            var now = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            _settings.Update(s =>
            {
                s.BestPresetFile = report.Best.Preset.FileName;
                s.BestScore = report.Best.Score;
                s.BestScoreDate = now;
                s.LastTuneDate = now;
                if (report.BestApplied)
                    s.LastPresetFile = report.Best.Preset.FileName;
            });

            SummaryText = report.Canceled
                ? $"Подбор остановлен. Лучшая из проверенных: {report.Best.Preset.DisplayName} — нажмите «Применить» или выберите вручную."
                : report.BestApplied
                    ? $"Лучшая стабильная стратегия запущена: {report.Best.Preset.DisplayName}"
                    : $"Лучшая стабильная стратегия: {report.Best.Preset.DisplayName} — нажмите «Применить»";

            if (report.Canceled)
                _toast($"Подбор остановлен. Лучшая из проверенных: {report.Best.Preset.DisplayName}", false);
            else
                _toast($"Автоподбор завершён: {report.Best.Preset.DisplayName} · {report.Best.Score:0}/100",
                    report.Best.Score < AutoTuner.MinimumUsefulScore);
        }
        else
        {
            HasBest = false;

            if (report.Results.Count == 0 && !report.Canceled)
            {
                SummaryText = "Все проверяемые сервисы уже доступны без обхода";
                StatusText = "Автоподбор не требуется";
                _idleHeadline = "Всё уже работает";
                _toast("Все сервисы уже доступны без обхода", false);
            }
            else
            {
                if (report.Results.Count > 0 && !report.Canceled)
                {
                    SummaryText = "Стабильных стратегий не найдено: на каждой хотя бы один выбранный сервис недоступен.";
                    _idleHeadline = "Стабильных стратегий нет";
                    StatusText = "Нужны все выбранные сервисы";
                    _toast("Стабильных стратегий не найдено", true);
                }
                else
                {
                    SummaryText = report.Canceled
                        ? "Подбор остановлен. Стабильных стратегий пока нет."
                        : "Стабильная стратегия не найдена. Попробуйте глубокий режим или другие сервисы.";
                    _idleHeadline = report.Canceled ? "Подбор остановлен" : "Стабильная стратегия не найдена";
                    _toast(report.Canceled ? "Автоподбор остановлен" : "Стабильная стратегия не найдена", true);
                }
            }
        }

        if (newlyExcluded > 0)
            SummaryText += $" Нерабочих исключено: {newlyExcluded}.";

        OnPropertyChanged(
            nameof(Headline),
            nameof(SavedBestText),
            nameof(ExcludedCount),
            nameof(HasExcluded),
            nameof(ExcludedText),
            nameof(ExcludedNames),
            nameof(CandidateCount),
            nameof(EstimateText));

        if (report.Canceled)
        {
            StatusText = "Автоподбор остановлен";
            Progress = 0;
        }

        SaveReport(report);
    }

    private int ApplyExclusions(TuneReport report)
    {
        var failed = report.Results
            .Where(r => r.Services.Count == 0 ||
                        r.Services.All(s => !s.IsSuccess))
            .Select(r => r.Preset.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (failed.Count == 0)
            return 0;

        var added = 0;
        _settings.Update(s =>
        {
            foreach (var file in failed)
            {
                if (s.TuneExcludedPresets.Any(x => x.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    continue;

                s.TuneExcludedPresets.Add(file);
                added++;
            }
        });

        if (added > 0)
            _log.Info("tuner", $"Исключено полностью нерабочих стратегий: {added}");

        CommandManager.InvalidateRequerySuggested();
        return added;
    }

    private void ResetExcluded()
    {
        if (!HasExcluded)
            return;

        _settings.Update(s => s.TuneExcludedPresets.Clear());
        OnPropertyChanged(
            nameof(ExcludedCount),
            nameof(HasExcluded),
            nameof(ExcludedText),
            nameof(ExcludedNames),
            nameof(CandidateCount),
            nameof(EstimateText));
        _toast("Исключённые стратегии возвращены в автоподбор", false);
        CommandManager.InvalidateRequerySuggested();
    }

    private void SaveReport(TuneReport report)
    {
        try
        {
            Directory.CreateDirectory(_settings.DirectoryPath);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ReportPath, json);
            _log.Debug("tuner", $"Отчёт сохранён: {ReportPath}");
        }
        catch (Exception ex)
        {
            _log.Warn("tuner", "Не удалось сохранить отчёт: " + ex.Message);
        }
    }

    private void Cancel()
    {
        if (!IsTuning || _cts is null)
            return;

        IsCanceling = true;
        StatusText = "Останавливаю подбор…";
        _cts.Cancel();
    }

    private async Task ApplyBestAsync()
    {
        var best = _lastReport?.Best;
        if (best is null)
            return;

        if (best.Preset.EngineKind != _scanner.Engine)
        {
            _toast("Эта стратегия относится к другому ядру — запустите подбор заново", true);
            return;
        }

        if (_winws.IsRunning && _winws.ActivePreset?.FileName.Equals(best.Preset.FileName, StringComparison.OrdinalIgnoreCase) == true)
        {
            _toast("Эта стратегия уже запущена", false);
            return;
        }

        if (!_operations.TryBegin("apply"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        try
        {
            var outcome = await _winws.StartAsync(best.Preset);
            if (outcome.Success)
            {
                _settings.Update(s => s.LastPresetFile = best.Preset.FileName);
                _toast($"Запущено: {best.Preset.DisplayName}", false);
            }
            else
            {
                _toast(outcome.Error ?? "Не удалось запустить стратегию", true);
            }
        }
        finally
        {
            _operations.End("apply");
        }
    }

    private void CopyReport()
    {
        if (_lastReport is null)
            return;
        ShellHelper.CopyText(_lastReport.ToMarkdown());
        _toast("Отчёт скопирован в буфер обмена", false);
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
            return $"{Math.Max(1, (int)Math.Round(seconds))} с";
        var minutes = (int)(seconds / 60);
        var rest = (int)Math.Round(seconds % 60);
        return rest == 0 ? $"{minutes} мин" : $"{minutes} мин {rest} с";
    }
}
