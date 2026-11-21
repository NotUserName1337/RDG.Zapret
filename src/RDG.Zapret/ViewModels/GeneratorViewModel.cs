using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class GeneratorViewModel : ObservableObject, IPageActivation
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly StrategyGenerator _generator;
    private readonly ResultsService _results;
    private readonly LogService _log;
    private readonly OperationCoordinator _operations;
    private readonly Action<string, bool> _toast;
    private readonly Action<string> _navigate;

    private readonly Dictionary<string, GeneratorRowViewModel> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;
    private GeneratorReport? _lastReport;
    private GeneratorAttempt? _bestAttempt;

    private bool _isGenerating;
    private bool _isCanceling;
    private double _progress;
    private string _currentRecipe = string.Empty;
    private string _statusText = "Готов к запуску";
    private string _attemptsText = string.Empty;
    private string _etaText = string.Empty;
    private string _baselineText = "Baseline будет проверен перед генерацией";
    private string _baselineRatio = "—";
    private string _bestName = string.Empty;
    private string _bestSummary = string.Empty;
    private string _bestScoreText = string.Empty;
    private string _bestServicesText = string.Empty;
    private bool _hasBest;
    private string _savedPresetPath = string.Empty;
    private string _summaryText = string.Empty;
    private string _idleHeadline = "Готов к запуску";

    public GeneratorViewModel(
        SettingsService settings,
        PresetScanner scanner,
        WinwsController winws,
        StrategyGenerator generator,
        ResultsService results,
        LogService log,
        OperationCoordinator operations,
        Action<string, bool> toast,
        Action<string> navigate)
    {
        _settings = settings;
        _scanner = scanner;
        _winws = winws;
        _generator = generator;
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

        StartCommand = new AsyncRelayCommand(StartAsync,
            () => !IsGenerating && HasInstallation && IsClassicEngine && SelectedServicesCount > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsGenerating && !IsCanceling);
        ApplyBestCommand = new AsyncRelayCommand(ApplyBestAsync, () => HasBest && !IsGenerating && IsClassicEngine);
        SaveBestCommand = new AsyncRelayCommand(SaveBestAsync, () => HasBest && !IsGenerating && IsClassicEngine);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => HasInstallation);
        NavigateSettingsCommand = new RelayCommand(() => _navigate("settings"));

        scanner.Changed += (_, _) => Ui.Invoke(OnScannerChanged);
    }

    public ObservableCollection<ServiceToggleViewModel> Services { get; }
    public ObservableCollection<GeneratorRowViewModel> Rows { get; } = new();

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand ApplyBestCommand { get; }
    public AsyncRelayCommand SaveBestCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand NavigateSettingsCommand { get; }

    public bool HasInstallation => _scanner.Current is not null;
    public bool IsClassicEngine => _scanner.Engine == ZapretEngine.Zapret;
    public string InstallationHint => _scanner.Current is null
        ? "Укажите папку zapret в настройках"
        : $"{_scanner.Current.FolderName} · {_scanner.ActiveEngineFile}";

    public string EngineNote => !HasInstallation
        ? "Сначала укажите или скачайте zapret."
        : IsClassicEngine
            ? "Генератор перебирает fake/split-параметры winws.exe и остановится, когда заработают все выбранные сервисы."
            : "Генератор параметров доступен для ядра zapret (winws.exe). Переключите ядро в левой панели; для zapret2 используйте автоподбор готовых стратегий.";

    public int SelectedServicesCount => Services.Count(s => s.IsSelected);

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                OnPropertyChanged(nameof(Headline));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string Headline => IsGenerating
        ? (string.IsNullOrWhiteSpace(CurrentRecipe) ? "Идёт генерация…" : CurrentRecipe)
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

    public string CurrentRecipe
    {
        get => _currentRecipe;
        private set
        {
            if (SetProperty(ref _currentRecipe, value))
                OnPropertyChanged(nameof(Headline));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string AttemptsText
    {
        get => _attemptsText;
        private set => SetProperty(ref _attemptsText, value);
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

    public string BestSummary
    {
        get => _bestSummary;
        private set => SetProperty(ref _bestSummary, value);
    }

    public string BestScoreText
    {
        get => _bestScoreText;
        private set => SetProperty(ref _bestScoreText, value);
    }

    public string BestServicesText
    {
        get => _bestServicesText;
        private set => SetProperty(ref _bestServicesText, value);
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

    public string SavedPresetPath
    {
        get => _savedPresetPath;
        private set
        {
            if (SetProperty(ref _savedPresetPath, value))
                OnPropertyChanged(nameof(HasSavedPreset), nameof(SavedPresetName));
        }
    }

    public bool HasSavedPreset => !string.IsNullOrEmpty(SavedPresetPath);
    public string SavedPresetName => HasSavedPreset ? Path.GetFileName(SavedPresetPath) : string.Empty;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string ModeLabel => _settings.Current.GeneratorMode switch
    {
        "fast" => "Быстрый",
        "max" => "Максимальный",
        _ => "Полный"
    };

    public int MaxAttempts => _settings.Current.GeneratorMode switch
    {
        "fast" => 30,
        "max" => 120,
        _ => 60
    };

    public string MaxAttemptsText => $"до {MaxAttempts} попыток";

    public string EstimateText
    {
        get
        {
            var perAttempt = Math.Clamp(_settings.Current.TuneWarmupSeconds, 2, 8) +
                             _settings.Current.ProbeTimeoutSeconds + 1.2;
            return $"≈ {FormatDuration(MaxAttempts * perAttempt + 8)}";
        }
    }

    public bool IsFast
    {
        get => _settings.Current.GeneratorMode == "fast";
        set { if (value) SetMode("fast"); }
    }

    public bool IsFull
    {
        get => _settings.Current.GeneratorMode == "full";
        set { if (value) SetMode("full"); }
    }

    public bool IsMax
    {
        get => _settings.Current.GeneratorMode == "max";
        set { if (value) SetMode("max"); }
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

    public Task OnActivatedAsync()
    {
        UpdateEngineState();
        return Task.CompletedTask;
    }

    private void SetMode(string mode)
    {
        if (_settings.Current.GeneratorMode == mode)
            return;
        _settings.Update(s => s.GeneratorMode = mode);
        OnPropertyChanged(nameof(IsFast), nameof(IsFull), nameof(IsMax), nameof(ModeLabel),
            nameof(MaxAttempts), nameof(MaxAttemptsText), nameof(EstimateText));
    }

    private void OnServiceToggleChanged()
    {
        var disabled = Services.Where(s => !s.IsSelected).Select(s => s.Target.Id).ToList();
        _settings.Update(s => s.TuneDisabledServices = disabled);
        OnPropertyChanged(nameof(SelectedServicesCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnScannerChanged()
    {
        UpdateEngineState();
        OnPropertyChanged(nameof(EstimateText), nameof(InstallationHint));
    }

    private void UpdateEngineState()
    {
        OnPropertyChanged(
            nameof(HasInstallation),
            nameof(IsClassicEngine),
            nameof(EngineNote),
            nameof(InstallationHint),
            nameof(SelectedServicesCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task StartAsync()
    {
        if (IsGenerating)
            return;

        if (!HasInstallation)
        {
            _toast("Сначала укажите или скачайте zapret", true);
            _navigate("settings");
            return;
        }

        if (!IsClassicEngine)
        {
            _toast("Генератор работает только с ядром zapret (winws.exe)", true);
            return;
        }

        var targets = Services.Where(s => s.IsSelected).Select(s => s.Target).ToList();
        if (targets.Count == 0)
        {
            _toast("Выберите хотя бы один сервис", true);
            return;
        }

        if (await DiagnosticsService.IsServiceRunningAsync("zapret"))
        {
            _toast("Служба zapret запущена. Остановите её в разделе «Диагностика».", true);
            _navigate("diagnostics");
            return;
        }

        if (_winws.IsRunning)
        {
            var answer = System.Windows.MessageBox.Show(
                "Текущий обход будет остановлен на время генерации.\n\nПродолжить?",
                "RDG.Zapret",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }

        if (!_operations.TryBegin("generator"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        Rows.Clear();
        _rowsByKey.Clear();
        _lastReport = null;
        _bestAttempt = null;
        HasBest = false;
        SavedPresetPath = string.Empty;
        BestName = string.Empty;
        BestSummary = string.Empty;
        BestScoreText = string.Empty;
        BestServicesText = string.Empty;
        SummaryText = string.Empty;
        Progress = 0;
        CurrentRecipe = string.Empty;
        AttemptsText = string.Empty;
        EtaText = string.Empty;
        BaselineText = "Проверяю доступность без обхода…";
        BaselineRatio = "—";
        StatusText = "Подготовка…";
        IsCanceling = false;
        IsGenerating = true;
        _startedAt = DateTime.Now;
        _cts = new CancellationTokenSource();

        var mode = _settings.Current.GeneratorMode;
        var options = new GeneratorOptions
        {
            Targets = targets,
            WarmupSeconds = Math.Clamp(_settings.Current.TuneWarmupSeconds, 2, 8),
            TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
            MaxAttempts = MaxAttempts,
            KeepBestRunning = _settings.Current.TuneKeepBestRunning,
            Mode = mode
        };

        var progress = new Progress<GeneratorProgress>(HandleProgress);

        try
        {
            var installation = _scanner.Current!;
            var root = installation.RootPath;
            var report = await _generator.RunAsync(installation, options, progress, _cts.Token);
            _lastReport = report;

            if (report.Best is not null)
            {
                ApplyBestAttempt(report.Best);
            }
            else
            {
                HasBest = false;
                _bestAttempt = null;
                BestName = string.Empty;
                BestSummary = string.Empty;
                BestScoreText = string.Empty;
                BestServicesText = string.Empty;
                foreach (var row in Rows)
                    row.IsBest = false;
            }

            if (report.AllAvailable && report.SavedPreset is not null)
            {
                await _scanner.ScanAsync(root);
                await PersistGeneratedAsync(report);
                _idleHeadline = "Готово: рабочая стратегия найдена";
            }
            else if (report.Canceled)
            {
                StatusText = "Генерация остановлена";
                SummaryText = "Генерация остановлена пользователем";
                _idleHeadline = "Генерация остановлена";
                _toast("Генератор остановлен", false);
            }
            else if (report.Best is not null)
            {
                SummaryText = $"Все сервисы не заработали, но лучший результат: {report.Best.AvailableCount}/{report.Best.TotalCount}. Можно сохранить его вручную.";
                _idleHeadline = "Лучший результат ниже 100%";
                _toast($"Лучший результат: {report.Best.AvailableCount}/{report.Best.TotalCount} сервисов", true);
            }
            else if (report.Attempts.Count == 0)
            {
                StatusText = "Все выбранные сервисы уже доступны без обхода";
                SummaryText = "Генерация не требуется.";
                _idleHeadline = "Всё уже работает";
                _toast("Все выбранные сервисы уже доступны", false);
            }
            else
            {
                SummaryText = "Рабочая стратегия не найдена. Увеличьте лимит попыток или выберите другой набор сервисов.";
                _idleHeadline = "Стратегия не найдена";
                _toast("Рабочая стратегия не найдена", true);
            }

            OnPropertyChanged(nameof(Headline));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Генерация остановлена";
            SummaryText = "Генерация остановлена пользователем";
        }
        catch (Exception ex)
        {
            _log.Error("generator", "Ошибка UI генератора: " + ex);
            _toast("Ошибка генератора: " + ex.Message, true);
            SummaryText = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsGenerating = false;
            IsCanceling = false;
            _cts?.Dispose();
            _cts = null;
            _operations.End("generator");
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void HandleProgress(GeneratorProgress update)
    {
        switch (update.Phase)
        {
            case GeneratorPhase.Preparing:
                StatusText = update.Message ?? "Подготовка…";
                break;

            case GeneratorPhase.Baseline:
                if (update.Baseline is not null)
                {
                    var available = update.Baseline.Count(s => s.Status == ProbeStatus.Available);
                    BaselineRatio = $"{available} / {update.Baseline.Count}";
                    var names = update.Baseline
                        .Where(s => s.Status == ProbeStatus.Available)
                        .Select(s => ServiceCatalog.TryById(s.ServiceId)?.Name ?? s.ServiceId)
                        .ToList();
                    BaselineText = names.Count == 0
                        ? "Без обхода все выбранные сервисы недоступны"
                        : "Без обхода уже работают: " + string.Join(", ", names);
                }

                StatusText = update.Message ?? "Проверяю без обхода…";
                break;

            case GeneratorPhase.Testing:
                CurrentRecipe = update.Current?.Summary ?? string.Empty;
                AttemptsText = update.Total > 0
                    ? $"Попытка {update.Index} из {update.Total}"
                    : string.Empty;
                StatusText = update.Message ?? string.Empty;

                if (update.Completed is not null)
                {
                    AddRow(update.Completed);
                    ApplyBestAttempt(update.Completed);
                    UpdateBest(update.Completed);

                    if (update.Total > 0)
                        Progress = Math.Max(Progress, update.Index * 100.0 / update.Total);
                    UpdateEta(update.Index, update.Total);
                }
                else if (update.Total > 0)
                {
                    Progress = Math.Max(Progress, (update.Index - 0.7) * 100.0 / update.Total);
                }

                break;

            case GeneratorPhase.Saving:
                StatusText = update.Message ?? "Сохраняю стратегию…";
                Progress = 100;
                break;

            case GeneratorPhase.Finished:
                StatusText = update.Message ?? "Готово";
                Progress = 100;
                AttemptsText = string.Empty;
                EtaText = string.Empty;
                break;

            case GeneratorPhase.Canceled:
                StatusText = "Генерация остановлена";
                AttemptsText = string.Empty;
                EtaText = string.Empty;
                break;

            case GeneratorPhase.Failed:
                StatusText = "Ошибка: " + (update.Message ?? "неизвестная ошибка");
                break;
        }
    }

    private void AddRow(GeneratorAttempt attempt)
    {
        var key = attempt.Recipe.Key + "|" + Rows.Count;
        if (!_rowsByKey.ContainsKey(key))
        {
            var row = new GeneratorRowViewModel(attempt);
            _rowsByKey[key] = row;
            Rows.Add(row);
        }
    }

    private void ApplyBestAttempt(GeneratorAttempt attempt)
    {
        if (_bestAttempt is null ||
            attempt.AllAvailable && !_bestAttempt.AllAvailable ||
            attempt.AllAvailable == _bestAttempt.AllAvailable &&
            (attempt.AvailableCount > _bestAttempt.AvailableCount ||
             attempt.AvailableCount == _bestAttempt.AvailableCount && attempt.Score > _bestAttempt.Score))
        {
            _bestAttempt = attempt;
            BestName = attempt.Recipe.Name;
            BestSummary = attempt.Recipe.Summary;
            BestScoreText = $"{attempt.Score:0} / 100";
            BestServicesText = $"{attempt.AvailableCount} / {attempt.TotalCount}";
            HasBest = true;
        }
    }

    private void UpdateBest(GeneratorAttempt completed)
    {
        foreach (var row in Rows)
            row.IsBest = ReferenceEquals(row.Attempt, _bestAttempt);
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

    private async Task PersistGeneratedAsync(GeneratorReport report)
    {
        if (report.SavedPreset is null || report.Best is null)
            return;

        SavedPresetPath = report.SavedPresetPath ?? string.Empty;
        SummaryText = $"Рабочая стратегия сохранена: {Path.GetFileName(SavedPresetPath)}";

        var result = new TuneResult
        {
            Preset = report.SavedPreset,
            Score = report.Best.Score,
            Services = report.Best.Services,
            Note = "Сгенерировано",
            Duration = report.Best.Duration
        };

        _results.Record(result, manual: true);

        var now = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        _settings.Update(s =>
        {
            s.BestPresetFile = report.SavedPreset.FileName;
            s.BestScore = report.Best.Score;
            s.BestScoreDate = now;
            s.LastTuneDate = now;
            if (report.BestRunning)
                s.LastPresetFile = report.SavedPreset.FileName;
        });

        await Task.CompletedTask;
    }

    private async Task SaveBestAsync()
    {
        var best = _lastReport?.Best;
        if (best is null || _scanner.Current is null)
            return;

        try
        {
            var installation = _scanner.Current;
            var root = installation.RootPath;
            var saved = _generator.SavePreset(installation, best.Recipe);
            SavedPresetPath = saved.Path;

            await _scanner.ScanAsync(root);

            if (saved.Preset is not null)
            {
                _results.Record(new TuneResult
                {
                    Preset = saved.Preset,
                    Score = best.Score,
                    Services = best.Services,
                    Note = "Сгенерировано вручную",
                    Duration = best.Duration
                }, manual: true);
            }

            ShowSaved();
            _toast($"Стратегия сохранена: {Path.GetFileName(saved.Path)}", false);
        }
        catch (Exception ex)
        {
            _log.Error("generator", "Не удалось сохранить стратегию: " + ex);
            _toast("Не удалось сохранить стратегию: " + ex.Message, true);
        }
    }

    private async Task ApplyBestAsync()
    {
        var best = _lastReport?.Best;
        if (best is null)
            return;

        if (!_operations.TryBegin("apply"))
        {
            _toast("Дождитесь завершения текущей операции", true);
            return;
        }

        try
        {
            var preset = _lastReport?.SavedPreset;
            if (preset is null || string.IsNullOrEmpty(SavedPresetPath))
            {
                if (_scanner.Current is null)
                    return;

                var saved = _generator.SavePreset(_scanner.Current, best.Recipe);
                SavedPresetPath = saved.Path;
                preset = saved.Preset;
            }

            if (preset is null)
                return;

            var outcome = await _winws.StartAsync(preset);
            if (outcome.Success)
            {
                _settings.Update(s => s.LastPresetFile = preset.FileName);
                _toast($"Запущено: {preset.DisplayName}", false);
            }
            else
            {
                _toast(outcome.Error ?? "Не удалось запустить стратегию", true);
            }
        }
        catch (Exception ex)
        {
            _toast("Не удалось применить стратегию: " + ex.Message, true);
        }
        finally
        {
            _operations.End("apply");
        }
    }

    private void ShowSaved()
    {
        if (_lastReport?.Best is not null && !string.IsNullOrEmpty(SavedPresetPath))
            SummaryText = $"Стратегия сохранена: {Path.GetFileName(SavedPresetPath)}";
    }

    private void Cancel()
    {
        if (!IsGenerating || _cts is null)
            return;
        IsCanceling = true;
        StatusText = "Останавливаю генератор…";
        _cts.Cancel();
    }

    private void OpenFolder()
    {
        var root = _scanner.Current?.RootPath;
        if (!string.IsNullOrWhiteSpace(root))
            ShellHelper.OpenFolder(root);
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
