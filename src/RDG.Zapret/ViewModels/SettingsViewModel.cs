using System.Reflection;
using System.Windows;
using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IPageActivation
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly DiagnosticsService _diagnostics;
    private readonly AutostartService _autostart;
    private readonly WinwsController _winws;
    private readonly LogService _log;
    private readonly UpdateService _updates;
    private readonly Func<Task> _download;
    private readonly Action<string, bool> _toast;
    private readonly Action<string> _navigate;

    private AccentOption _selectedAccent;
    private string _zapretPath;
    private bool _isPathValid;
    private bool _isPathChecking;
    private string _pathStatus = string.Empty;
    private bool _isDiagnosticsLoading;
    private string _diagnosticSummary = "Проверка ещё не выполнялась";
    private bool _isAutostartBusy;
    private bool _syncingAutostart;
    private string _newServiceName = string.Empty;
    private string _newServiceUrl = string.Empty;

    private bool _isUpdateBusy;
    private bool _isUpdateAvailable;
    private double _updateProgress;
    private string _updateStatus = "Проверка ещё не выполнялась";
    private string _latestVersionText = "—";
    private string _updateNotes = string.Empty;
    private UpdateInfo? _availableUpdate;
    private CancellationTokenSource? _updateCts;
    private bool _updateCheckRequested;

    public SettingsViewModel(
        SettingsService settings,
        PresetScanner scanner,
        DiagnosticsService diagnostics,
        AutostartService autostart,
        WinwsController winws,
        UpdateService updates,
        LogService log,
        Func<Task> download,
        Action<string, bool> toast,
        Action<string> navigate)
    {
        _settings = settings;
        _scanner = scanner;
        _diagnostics = diagnostics;
        _autostart = autostart;
        _winws = winws;
        _updates = updates;
        _log = log;
        _download = download;
        _toast = toast;
        _navigate = navigate;

        Accents = new ObservableCollection<AccentOption>
        {
            new() { Key = "violet", Name = "Violet", ColorFrom = "#7C5CFF", ColorTo = "#22D3EE" },
            new() { Key = "ocean", Name = "Ocean", ColorFrom = "#2F80ED", ColorTo = "#56CCF2" },
            new() { Key = "emerald", Name = "Emerald", ColorFrom = "#10B981", ColorTo = "#34D399" },
            new() { Key = "sunset", Name = "Sunset", ColorFrom = "#F97316", ColorTo = "#F43F5E" }
        };

        _selectedAccent = Accents.FirstOrDefault(a => a.Key == settings.Current.Accent) ?? Accents[0];
        _zapretPath = settings.Current.ZapretPath;

        Diagnostics = new ObservableCollection<DiagnosticItemViewModel>();
        CustomServices = new ObservableCollection<CustomServiceItemViewModel>();
        RefreshCustomServices();

        BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => !IsPathChecking);
        ApplyPathCommand = new AsyncRelayCommand(ApplyPathAsync, () => !IsPathChecking);
        AutoDetectCommand = new AsyncRelayCommand(AutoDetectAsync, () => !IsPathChecking);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync, () => !IsDiagnosticsLoading);
        OpenReleasesCommand = new RelayCommand(() => ShellHelper.OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases/latest"));
        DownloadCommand = new AsyncRelayCommand(download);
        OpenFlowsealCommand = new RelayCommand(() => ShellHelper.OpenUrl("https://github.com/Flowseal/zapret-discord-youtube"));
        OpenOriginalCommand = new RelayCommand(() => ShellHelper.OpenUrl("https://github.com/bol-van/zapret2"));
        OpenDataFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_settings.DirectoryPath));
        OpenInstallFolderCommand = new RelayCommand(OpenInstallFolder, () => _scanner.Current is not null);
        TimeoutPlusCommand = new RelayCommand(() => AdjustTimeout(1), () => TimeoutSeconds < 10);
        TimeoutMinusCommand = new RelayCommand(() => AdjustTimeout(-1), () => TimeoutSeconds > 2);
        StopZapretServiceCommand = new AsyncRelayCommand(StopZapretServiceAsync, () => !IsPathChecking);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        MonitorMinusCommand = new RelayCommand(() => AdjustMonitorInterval(-1), () => BackgroundCheckIntervalMinutes > 1);
        MonitorPlusCommand = new RelayCommand(() => AdjustMonitorInterval(1), () => BackgroundCheckIntervalMinutes < 120);
        CheckUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(silent: false), () => !IsUpdateBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => !IsUpdateBusy && IsUpdateAvailable);
        AddCustomServiceCommand = new RelayCommand(AddCustomService, () => !string.IsNullOrWhiteSpace(NewServiceUrl));
        RemoveCustomServiceCommand = new RelayCommand(obj => RemoveCustomService(obj as string));
        OpenDiagnosticsCommand = new RelayCommand(() => _navigate("diagnostics"));

        UpdateInstallation();
        scanner.Changed += (_, _) => Ui.Invoke(UpdateInstallation);
    }

    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; }
    public ObservableCollection<CustomServiceItemViewModel> CustomServices { get; }
    public RelayCommand AddCustomServiceCommand { get; }
    public RelayCommand RemoveCustomServiceCommand { get; }
    public ObservableCollection<AccentOption> Accents { get; }

    public AsyncRelayCommand BrowseCommand { get; }
    public AsyncRelayCommand ApplyPathCommand { get; }
    public AsyncRelayCommand AutoDetectCommand { get; }
    public AsyncRelayCommand RefreshDiagnosticsCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand OpenFlowsealCommand { get; }
    public RelayCommand OpenOriginalCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenInstallFolderCommand { get; }
    public RelayCommand TimeoutPlusCommand { get; }
    public RelayCommand TimeoutMinusCommand { get; }
    public AsyncRelayCommand StopZapretServiceCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public RelayCommand MonitorMinusCommand { get; }
    public RelayCommand MonitorPlusCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand OpenDiagnosticsCommand { get; }

    public AccentOption SelectedAccent
    {
        get => _selectedAccent;
        set
        {
            if (value is null || !SetProperty(ref _selectedAccent, value))
                return;
            AccentService.Apply(value.Key);
            _settings.Update(s => s.Accent = value.Key);
        }
    }

    public string ZapretPath
    {
        get => _zapretPath;
        set => SetProperty(ref _zapretPath, value ?? string.Empty);
    }

    public bool IsPathValid
    {
        get => _isPathValid;
        private set => SetProperty(ref _isPathValid, value);
    }

    public bool IsPathChecking
    {
        get => _isPathChecking;
        private set => SetProperty(ref _isPathChecking, value);
    }

    public string PathStatus
    {
        get => _pathStatus;
        private set => SetProperty(ref _pathStatus, value);
    }

    public bool HasInstallation => _scanner.Current is not null;
    public string InstallationTitle => _scanner.Current?.FolderName ?? "zapret не найден";
    public string InstallationDetail => _scanner.Current is null
        ? "Выберите папку, в которую распакован архив zapret"
        : $"{_scanner.Presets.Count} " +
          $"{Plural.Ru(_scanner.Presets.Count, "стратегия", "стратегии", "стратегий")} · " +
          $"{_scanner.ActiveEngineFile} · {_scanner.Current.RootPath}";

    public string EngineAvailabilityText
    {
        get
        {
            if (_scanner.Current is null)
                return "Ядро не обнаружено — укажите папку zapret.";

            var zapret = _scanner.CountFor(ZapretEngine.Zapret);
            var zapret2 = _scanner.CountFor(ZapretEngine.Zapret2);
            return $"Найдено: zapret — {zapret} {Plural.Ru(zapret, "стратегия", "стратегии", "стратегий")}, " +
                   $"zapret2 — {zapret2} {Plural.Ru(zapret2, "стратегия", "стратегии", "стратегий")}.";
        }
    }

    public bool StartWithWindows
    {
        get => _settings.Current.StartWithWindows;
        set
        {
            if (_syncingAutostart || value == _settings.Current.StartWithWindows)
                return;
            _ = SetAutostartAsync(value);
        }
    }

    public bool StartLastStrategyOnLaunch
    {
        get => _settings.Current.StartLastStrategyOnLaunch;
        set
        {
            if (value == _settings.Current.StartLastStrategyOnLaunch)
                return;
            _settings.Update(s => s.StartLastStrategyOnLaunch = value);
            OnPropertyChanged();
        }
    }

    public bool MinimizeOnAutostart
    {
        get => _settings.Current.MinimizeOnAutostart;
        set
        {
            if (value == _settings.Current.MinimizeOnAutostart)
                return;
            _settings.Update(s => s.MinimizeOnAutostart = value);
            OnPropertyChanged();
        }
    }

    public bool LeaveBypassOnExit
    {
        get => _settings.Current.LeaveBypassOnExit;
        set
        {
            if (value == _settings.Current.LeaveBypassOnExit)
                return;
            _settings.Update(s => s.LeaveBypassOnExit = value);
            OnPropertyChanged();
        }
    }

    public bool AutoDownloadStrategies
    {
        get => _settings.Current.AutoDownloadStrategies;
        set
        {
            if (value == _settings.Current.AutoDownloadStrategies)
                return;
            _settings.Update(s => s.AutoDownloadStrategies = value);
            OnPropertyChanged();
        }
    }

    public bool AutoCheckStrategyPack
    {
        get => _settings.Current.AutoCheckStrategyPack;
        set
        {
            if (value == _settings.Current.AutoCheckStrategyPack)
                return;
            _settings.Update(s => s.AutoCheckStrategyPack = value);
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _settings.Current.CloseToTray;
        set
        {
            if (value == _settings.Current.CloseToTray)
                return;
            _settings.Update(s => s.CloseToTray = value);
            OnPropertyChanged();
        }
    }

    public bool BackgroundMonitorEnabled
    {
        get => _settings.Current.BackgroundMonitorEnabled;
        set
        {
            if (value == _settings.Current.BackgroundMonitorEnabled)
                return;
            _settings.Update(s => s.BackgroundMonitorEnabled = value);
            OnPropertyChanged();
        }
    }

    public bool BackgroundRecoveryEnabled
    {
        get => _settings.Current.BackgroundRecoveryEnabled;
        set
        {
            if (value == _settings.Current.BackgroundRecoveryEnabled)
                return;
            _settings.Update(s => s.BackgroundRecoveryEnabled = value);
            OnPropertyChanged();
        }
    }

    public int BackgroundCheckIntervalMinutes =>
        Math.Clamp(_settings.Current.BackgroundCheckIntervalMinutes, 1, 120);

    public string MonitorIntervalText =>
        $"каждые {BackgroundCheckIntervalMinutes} " +
        $"{Plural.Ru(BackgroundCheckIntervalMinutes, "минуту", "минуты", "минут")}";

    public string MonitorIntervalShort => $"{BackgroundCheckIntervalMinutes} мин";
    public string BackgroundRetryText =>
        $"При сбое: {_settings.Current.BackgroundRetryAttempts} повторные проверки с паузой {_settings.Current.BackgroundRetryDelaySeconds} с, затем автоподбор";

    private void AdjustMonitorInterval(int delta)
    {
        var value = Math.Clamp(BackgroundCheckIntervalMinutes + delta, 1, 120);
        if (value == BackgroundCheckIntervalMinutes)
            return;

        _settings.Update(s => s.BackgroundCheckIntervalMinutes = value);
        OnPropertyChanged(nameof(BackgroundCheckIntervalMinutes), nameof(MonitorIntervalText), nameof(MonitorIntervalShort));
        CommandManager.InvalidateRequerySuggested();
    }

    public bool AutoCheckUpdates
    {
        get => _settings.Current.AutoCheckUpdates;
        set
        {
            if (value == _settings.Current.AutoCheckUpdates)
                return;
            _settings.Update(s => s.AutoCheckUpdates = value);
            OnPropertyChanged();
        }
    }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set
        {
            if (!SetProperty(ref _isUpdateBusy, value))
                return;
            OnPropertyChanged(nameof(IsUpdateIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUpdateIdle => !IsUpdateBusy;

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetProperty(ref _isUpdateAvailable, value);
    }

    public double UpdateProgress
    {
        get => _updateProgress;
        private set => SetProperty(ref _updateProgress, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public string LatestVersionText
    {
        get => _latestVersionText;
        private set => SetProperty(ref _latestVersionText, value);
    }

    public string UpdateNotes
    {
        get => _updateNotes;
        private set => SetProperty(ref _updateNotes, value);
    }

    public string AppVersionText => "v" + UpdateService.CurrentVersionText;
    public bool HasUpdateNotes => !string.IsNullOrWhiteSpace(UpdateNotes);

    public async Task CheckForUpdatesAsync(bool silent)
    {
        if (IsUpdateBusy)
            return;

        IsUpdateBusy = true;
        UpdateProgress = 0;
        UpdateStatus = "Проверяю обновления…";
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();

        try
        {
            var result = await _updates.CheckAsync(_updateCts.Token);
            if (!result.Success)
            {
                UpdateStatus = "Не удалось проверить: " + (result.Error ?? "неизвестная ошибка");
                if (!silent)
                    _toast("Не удалось проверить обновления: " + (result.Error ?? "ошибка сети"), true);
                return;
            }

            if (result.Update is null)
            {
                UpdateStatus = "Обновлений нет";
                IsUpdateAvailable = false;
                return;
            }

            _availableUpdate = result.Update;
            LatestVersionText = result.Update.Version;
            UpdateNotes = result.Update.Notes;
            OnPropertyChanged(nameof(HasUpdateNotes));
            IsUpdateAvailable = result.IsNewer;

            _settings.Update(s => s.LastUpdateCheckDate = DateTime.Now.ToString("dd.MM.yyyy HH:mm"), notify: false);

            if (result.IsNewer)
            {
                UpdateStatus = $"Доступна версия {result.Update.Version}";
                if (!silent)
                    _toast($"Доступно обновление {result.Update.Version}", false);
            }
            else
            {
                UpdateStatus = "Установлена последняя версия";
            }
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (IsUpdateBusy)
            return;

        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(silent: false);
            if (_availableUpdate is null)
                return;
        }

        var info = _availableUpdate;
        var answer = MessageBox.Show(
            $"Скачать и установить RDG.Zapret {info.Version}?\n\n" +
            "Приложение закроется, заменит exe и запустится снова. " +
            "Если обход запущен, он будет сохранён согласно настройке «Оставлять обход при выходе».",
            "RDG.Zapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        IsUpdateBusy = true;
        UpdateProgress = 0;
        UpdateStatus = "Подготовка…";
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();

        var installed = false;
        try
        {
            var progress = new Progress<UpdateProgress>(update =>
            {
                UpdateProgress = update.Percent;
                UpdateStatus = update.Status;
            });

            var download = await _updates.DownloadAsync(info, progress, _updateCts.Token);
            if (!download.Success || string.IsNullOrWhiteSpace(download.FilePath))
            {
                UpdateStatus = "Не удалось скачать: " + (download.Error ?? "ошибка");
                _toast("Не удалось скачать обновление: " + (download.Error ?? "ошибка"), true);
                return;
            }

            UpdateStatus = "Запускаю установку…";
            UpdateProgress = 100;

            if (!_updates.TryStartInstaller(download.FilePath, info.Version, out var error))
            {
                UpdateStatus = "Не удалось установить: " + (error ?? "ошибка");
                _toast("Не удалось установить обновление: " + (error ?? "ошибка"), true);
                return;
            }

            installed = true;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = "Обновление отменено";
        }
        catch (Exception ex)
        {
            _log.Error("update", "Ошибка установки обновления: " + ex);
            UpdateStatus = "Ошибка: " + ex.Message;
            _toast("Ошибка обновления: " + ex.Message, true);
        }
        finally
        {
            if (!installed)
                IsUpdateBusy = false;
        }

        if (installed)
        {
            _log.Info("update", "Завершаю работу для установки обновления " + info.Version);
            _updates.RequestRestart();
        }
    }

    public string NewServiceName
    {
        get => _newServiceName;
        set
        {
            if (SetProperty(ref _newServiceName, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string NewServiceUrl
    {
        get => _newServiceUrl;
        set
        {
            if (SetProperty(ref _newServiceUrl, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private void AddCustomService()
    {
        var created = ServiceCatalog.CreateCustom(new CustomService
        {
            Name = NewServiceName,
            Url = NewServiceUrl
        });

        if (created.Target is null)
        {
            _toast(created.Error ?? "Некорректный адрес", true);
            return;
        }

        _settings.Update(s =>
        {
            s.CustomServices.Add(new CustomService
            {
                Id = created.Target.Id,
                Name = created.Target.Name,
                Url = created.Target.Url
            });
        });

        ServiceCatalog.SetCustomServices(_settings.Current.CustomServices);
        RefreshCustomServices();
        NewServiceName = string.Empty;
        NewServiceUrl = string.Empty;
        _toast($"Сервис добавлен: {created.Target.Name}", false);
        _log.Info("services", "Добавлен свой сервис: " + created.Target.Name);
    }

    private void RemoveCustomService(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        _settings.Update(s => s.CustomServices.RemoveAll(c =>
            c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        ServiceCatalog.SetCustomServices(_settings.Current.CustomServices);
        RefreshCustomServices();
        _toast("Сервис удалён", false);
    }

    private void RefreshCustomServices()
    {
        CustomServices.Clear();
        foreach (var item in _settings.Current.CustomServices)
        {
            var created = ServiceCatalog.CreateCustom(item);
            if (created.Target is not null)
                CustomServices.Add(new CustomServiceItemViewModel(
                    created.Target.Id, created.Target.Name, created.Target.Url));
        }
    }

    public bool DeepClassification
    {
        get => _settings.Current.DeepClassification;
        set
        {
            if (value == _settings.Current.DeepClassification)
                return;
            _settings.Update(s => s.DeepClassification = value);
            OnPropertyChanged();
        }
    }

    public int TimeoutSeconds => _settings.Current.ProbeTimeoutSeconds;
    public string TimeoutText => $"{TimeoutSeconds} с";

    public bool IsAutostartBusy
    {
        get => _isAutostartBusy;
        private set => SetProperty(ref _isAutostartBusy, value);
    }

    public bool IsDiagnosticsLoading
    {
        get => _isDiagnosticsLoading;
        private set => SetProperty(ref _isDiagnosticsLoading, value);
    }

    public string DiagnosticSummary
    {
        get => _diagnosticSummary;
        private set => SetProperty(ref _diagnosticSummary, value);
    }

    public bool IsAdministrator => DiagnosticsService.IsAdministrator();

    public string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string DataFolder => _settings.DirectoryPath;

    public async Task OnActivatedAsync()
    {
        await SyncAutostartAsync();
        UpdateInstallation();
        await AutoCheckIfNeededAsync();
    }

    /// <summary>Runs the startup update check only once per session.</summary>
    public Task AutoCheckIfNeededAsync()
    {
        if (_updateCheckRequested || !_settings.Current.AutoCheckUpdates)
            return Task.CompletedTask;

        _updateCheckRequested = true;
        return CheckForUpdatesAsync(silent: true);
    }

    private void UpdateInstallation()
    {
        OnPropertyChanged(
            nameof(HasInstallation),
            nameof(InstallationTitle),
            nameof(InstallationDetail),
            nameof(EngineAvailabilityText));

        if (_scanner.Current is not null)
        {
            IsPathValid = true;
            PathStatus = $"Подключено: {_scanner.Presets.Count} " +
                         $"{Plural.Ru(_scanner.Presets.Count, "стратегия", "стратегии", "стратегий")} · " +
                         $"{_scanner.ActiveEngineFile}";
            if (string.IsNullOrWhiteSpace(ZapretPath))
                ZapretPath = _scanner.Current.RootPath;
        }
        else if (!IsPathChecking && string.IsNullOrWhiteSpace(PathStatus))
        {
            IsPathValid = false;
            PathStatus = "Укажите папку с распакованным zapret";
        }
    }

    private async Task BrowseAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку zapret",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            ZapretPath = dialog.FolderName;
            await ApplyPathAsync();
        }
    }

    private async Task ApplyPathAsync()
    {
        var path = Environment.ExpandEnvironmentVariables(ZapretPath.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(path))
        {
            IsPathValid = false;
            PathStatus = "Путь не указан";
            return;
        }

        IsPathChecking = true;
        PathStatus = "Проверяю папку…";

        try
        {
            var result = await _scanner.ScanAsync(path);
            if (result.Installation is not null)
            {
                ZapretPath = result.Installation.RootPath;
                _settings.Update(s => s.ZapretPath = result.Installation.RootPath);
                IsPathValid = true;
                PathStatus = $"Готово: {_scanner.Presets.Count} " +
                             $"{Plural.Ru(_scanner.Presets.Count, "стратегия", "стратегии", "стратегий")} · " +
                             $"{_scanner.ActiveEngineFile}";
                _toast("Папка zapret подключена", false);
            }
            else
            {
                IsPathValid = false;
                PathStatus = result.Error ?? "Папка не распознана";
                _toast(result.Error ?? "Не удалось найти zapret в выбранной папке", true);
            }
        }
        finally
        {
            IsPathChecking = false;
        }
    }

    private async Task AutoDetectAsync()
    {
        IsPathChecking = true;
        PathStatus = "Ищу установку zapret…";
        try
        {
            var found = await Task.Run(() => ZapretLocator.Find(ZapretPath));
            if (found is not null)
            {
                ZapretPath = found;
                IsPathChecking = false;
                await ApplyPathAsync();
            }
            else
            {
                IsPathValid = false;
                PathStatus = "Автоматически найти не удалось. Укажите папку вручную.";
                _toast("zapret не найден в типичных местах", true);
            }
        }
        finally
        {
            IsPathChecking = false;
        }
    }

    private async Task SyncAutostartAsync()
    {
        try
        {
            var enabled = await _autostart.IsEnabledAsync();
            _syncingAutostart = true;
            if (_settings.Current.StartWithWindows != enabled)
                _settings.Update(s => s.StartWithWindows = enabled, notify: false);
            OnPropertyChanged(nameof(StartWithWindows));
        }
        finally
        {
            _syncingAutostart = false;
        }
    }

    private async Task SetAutostartAsync(bool enabled)
    {
        IsAutostartBusy = true;
        try
        {
            var outcome = enabled
                ? await _autostart.EnableAsync()
                : await _autostart.DisableAsync();

            if (outcome.Success)
            {
                _settings.Update(s => s.StartWithWindows = enabled);
                _toast(enabled ? "Автозапуск включён" : "Автозапуск отключён", false);
            }
            else
            {
                _toast("Не удалось изменить автозапуск: " + outcome.Message, true);
            }

            OnPropertyChanged(nameof(StartWithWindows));
        }
        finally
        {
            IsAutostartBusy = false;
        }
    }

    private void AdjustTimeout(int delta)
    {
        var value = Math.Clamp(TimeoutSeconds + delta, 2, 10);
        if (value == TimeoutSeconds)
            return;

        _settings.Update(s => s.ProbeTimeoutSeconds = value);
        OnPropertyChanged(nameof(TimeoutSeconds), nameof(TimeoutText));
    }

    private async Task RefreshDiagnosticsAsync()
    {
        IsDiagnosticsLoading = true;
        try
        {
            var items = await _diagnostics.RunAsync(_scanner.Current);
            Diagnostics.Clear();
            foreach (var item in items)
                Diagnostics.Add(new DiagnosticItemViewModel(item));

            var ok = items.Count(i => i.Status == DiagnosticStatus.Ok);
            var warnings = items.Count(i => i.Status == DiagnosticStatus.Warn);
            var errors = items.Count(i => i.Status == DiagnosticStatus.Fail);
            DiagnosticSummary = errors > 0
                ? $"{errors} проблем · {warnings} предупреждений"
                : warnings > 0 ? $"{warnings} предупреждений" : "Все проверки пройдены";
        }
        catch (Exception ex)
        {
            _log.Warn("diagnostics", "Ошибка диагностики: " + ex.Message);
        }
        finally
        {
            IsDiagnosticsLoading = false;
        }
    }

    private async Task StopZapretServiceAsync()
    {
        var answer = MessageBox.Show(
            "Остановить и удалить службу «zapret»?\n\nЭто отключит автозапуск, установленный через service.bat. " +
            "Драйвер WinDivert останется, и обход можно будет запускать из RDG.Zapret.",
            "RDG.Zapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        var stop = await ProcessRunner.RunAsync("sc.exe", "stop \"zapret\"", 10000);
        _log.Info("service", "sc stop zapret: " + (stop.Success ? "OK" : stop.Output));
        await Task.Delay(800);
        var delete = await ProcessRunner.RunAsync("sc.exe", "delete \"zapret\"", 10000);
        _log.Info("service", "sc delete zapret: " + (delete.Success ? "OK" : delete.Output));

        if (delete.Success)
            _toast("Служба zapret остановлена и удалена", false);
        else
            _toast("Не удалось удалить службу: " + delete.Output, true);

        await RefreshDiagnosticsAsync();
    }

    private async Task ResetAsync()
    {
        var answer = MessageBox.Show(
            "Сбросить все настройки RDG.Zapret?\n\nПуть к zapret, автозапуск и параметры приложения будут очищены.",
            "RDG.Zapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        await _autostart.DisableAsync();

        _settings.Reset();
        _scanner.Clear();

        _syncingAutostart = true;
        OnPropertyChanged(
            nameof(StartWithWindows),
            nameof(StartLastStrategyOnLaunch),
            nameof(MinimizeOnAutostart),
            nameof(LeaveBypassOnExit),
            nameof(AutoDownloadStrategies),
            nameof(CloseToTray),
            nameof(BackgroundMonitorEnabled),
            nameof(BackgroundRecoveryEnabled),
            nameof(BackgroundCheckIntervalMinutes),
            nameof(MonitorIntervalText),
            nameof(MonitorIntervalShort),
            nameof(AutoCheckUpdates),
            nameof(DeepClassification),
            nameof(TimeoutSeconds),
            nameof(TimeoutText),
            nameof(SelectedAccent));

        _syncingAutostart = false;

        ZapretPath = string.Empty;
        IsPathValid = false;
        PathStatus = "Настройки сброшены. Укажите папку zapret.";
        SelectedAccent = Accents[0];
        AccentService.Apply("violet");
        _toast("Настройки сброшены", false);
    }

    private void OpenInstallFolder()
    {
        var path = _scanner.Current?.RootPath;
        if (!string.IsNullOrWhiteSpace(path))
            ShellHelper.OpenFolder(path);
    }
}
