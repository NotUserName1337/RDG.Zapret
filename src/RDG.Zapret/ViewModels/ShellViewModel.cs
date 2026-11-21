using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly LogService _log;
    private readonly ZapretDownloadService _download;
    private readonly StrategyPackService _strategyPack;
    private readonly UserListService _userLists;
    private readonly Zapret2PackService _zapret2;
    private readonly UpdateService _updates;
    private readonly OperationCoordinator _operations;
    private readonly bool _autoStart;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _updateTimer;

    private bool _isZapret2Installing;
    private double _zapret2InstallProgress;
    private string _zapret2InstallStatus = string.Empty;
    private bool _isCheckingStrategyPack;

    private NavItem _selectedNav;
    private bool _isBypassBusy;
    private bool _isToastVisible;
    private bool _toastIsError;
    private string _toastText = string.Empty;
    private CancellationTokenSource? _toastCts;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _downloadStatus = string.Empty;
    private string _downloadTargetPath = string.Empty;
    private CancellationTokenSource? _downloadCts;

    public ShellViewModel(AppServices services, bool autoStart, bool startMinimized)
    {
        _services = services;
        _settings = services.Settings;
        _scanner = services.Scanner;
        _winws = services.Winws;
        _log = services.Log;
        _download = services.Download;
        _strategyPack = services.StrategyPack;
        _userLists = services.UserLists;
        _zapret2 = services.Zapret2Pack;
        _updates = services.Updates;
        _operations = services.Operations;
        _autoStart = autoStart;
        StartMinimized = startMinimized;

        AccentService.Apply(_settings.Current.Accent);

        Dashboard = new DashboardViewModel(
            _settings, _scanner, _winws, services.Probe, services.Diagnostics,
            services.YoutubeThrottle, services.HostsFix, services.UserLists,
            services.Banner, _log,
            StartBypassAsync, StopBypassAsync, () => DownloadStrategiesAsync(auto: false), ShowToast, Navigate);

        Strategies = new StrategiesViewModel(
            _settings, _scanner, _winws, services.Probe, services.Results, _log, services.Operations,
            ShowToast, Navigate);

        Lists = new ListsViewModel(
            _scanner, _winws, services.UserLists, _operations, _log, ShowToast);

        Autotune = new AutotuneViewModel(
            _settings, _scanner, _winws, services.Tuner, services.Results, _log, services.Operations,
            ShowToast, Navigate);

        Generator = new GeneratorViewModel(
            _settings, _scanner, _winws, services.Generator, services.Results, _log, services.Operations,
            ShowToast, Navigate);

        Logs = new LogsViewModel(_log);

        SettingsPage = new SettingsViewModel(
            _settings, _scanner, services.Diagnostics, services.Autostart, _winws, _updates, _log,
            () => DownloadStrategiesAsync(auto: false), ShowToast, Navigate);

        Diagnostics = new DiagnosticsViewModel(
            _settings, _scanner, services.Diagnostics, services.Doctor, services.Dns,
            services.Classifier, services.UserLists, _winws, _log, ShowToast);

        Telegram = new TelegramViewModel(_settings, services.TgProxy, _log, ShowToast);

        NavItems = new List<NavItem>
        {
            new()
            {
                Key = "overview", Title = "Обзор", Glyph = "\uE80F",
                Subtitle = "Состояние обхода и доступность сервисов", Page = Dashboard
            },
            new()
            {
                Key = "strategies", Title = "Стратегии", Glyph = "\uE8FD",
                Subtitle = "Все найденные конфигурации zapret", Page = Strategies
            },
            new()
            {
                Key = "lists", Title = "Списки", Glyph = "\uE8EF",
                Subtitle = "Свои домены и IP для обхода", Page = Lists
            },
            new()
            {
                Key = "autotune", Title = "Автоподбор", Glyph = "\uE945",
                Subtitle = "Автоматический поиск рабочей стратегии", Page = Autotune
            },
            new()
            {
                Key = "generator", Title = "Генератор", Glyph = "\uE90F",
                Subtitle = "Подбор параметров winws до полной доступности", Page = Generator
            },
            new()
            {
                Key = "telegram", Title = "Telegram", Glyph = "\uE8BD",
                Subtitle = "Локальный MTProto-прокси через WebSocket", Page = Telegram
            },
            new()
            {
                Key = "logs", Title = "Журнал", Glyph = "\uE8A5",
                Subtitle = "События приложения и вывод winws", Page = Logs
            },
            new()
            {
                Key = "diagnostics", Title = "Диагностика", Glyph = "\uE9D9",
                Subtitle = "Проверка сети, конфликты и ремонт", Page = Diagnostics
            },
            new()
            {
                Key = "settings", Title = "Настройки", Glyph = "\uE713",
                Subtitle = "Папка zapret, запуск и диагностика", Page = SettingsPage
            }
        };

        _selectedNav = NavItems[0];

        StartCommand = new AsyncRelayCommand(StartBypassAsync, () => !IsBypassBusy && !IsRunning);
        StopCommand = new AsyncRelayCommand(StopBypassAsync, () => !IsBypassBusy && IsRunning);
        DownloadCommand = new AsyncRelayCommand(() => DownloadStrategiesAsync(auto: false), () => !IsDownloading);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsDownloading);
        InstallZapret2Command = new AsyncRelayCommand(InstallZapret2Async,
            () => !IsZapret2Installing && _scanner.Current is not null);
        CheckStrategyPackCommand = new AsyncRelayCommand(
            () => CheckStrategyPackAsync(manual: true),
            () => !IsCheckingStrategyPack && _scanner.Current is not null);
        OpenDataFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_settings.DirectoryPath));
        OpenGithubCommand = new RelayCommand(() => ShellHelper.OpenUrl("https://github.com/Flowseal/zapret-discord-youtube"));

        _winws.StateChanged += (_, _) => Ui.Invoke(RefreshState);
        _scanner.Changed += (_, _) => Ui.Invoke(OnScannerStateChanged);

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) =>
        {
            if (IsRunning)
                OnPropertyChanged(nameof(UptimeText));
        };
        _uptimeTimer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += (_, _) =>
        {
            if (_settings.Current.AutoCheckUpdates)
                _ = SettingsPage.CheckForUpdatesAsync(silent: true);
        };
        _updateTimer.Start();
    }

    public DashboardViewModel Dashboard { get; }
    public StrategiesViewModel Strategies { get; }
    public ListsViewModel Lists { get; }
    public AutotuneViewModel Autotune { get; }
    public GeneratorViewModel Generator { get; }
    public LogsViewModel Logs { get; }
    public SettingsViewModel SettingsPage { get; }
    public TelegramViewModel Telegram { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public List<NavItem> NavItems { get; }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !SetProperty(ref _selectedNav, value))
                return;

            OnPropertyChanged(nameof(CurrentPage), nameof(PageTitle), nameof(PageSubtitle));
            if (value.Page is IPageActivation activation)
                _ = ActivateAsync(activation);
        }
    }

    public object CurrentPage => _selectedNav.Page;
    public string PageTitle => _selectedNav.Title;
    public string PageSubtitle => _selectedNav.Subtitle;

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public AsyncRelayCommand InstallZapret2Command { get; }
    public AsyncRelayCommand CheckStrategyPackCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenGithubCommand { get; }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public string DownloadTargetPath
    {
        get => _downloadTargetPath;
        private set => SetProperty(ref _downloadTargetPath, value);
    }

    public bool IsRunning => _winws.IsRunning;
    public bool IsExternal => _winws.IsExternal;
    public bool IsBypassBusy
    {
        get => _isBypassBusy;
        private set => SetProperty(ref _isBypassBusy, value);
    }

    public string StatusTitle => IsRunning ? "Обход активен" : "Обход выключен";
    public string StatusSubtitle => IsRunning
        ? _winws.ActivePreset?.DisplayName ?? (_winws.IsExternal ? "Внешний winws.exe" : "Стратегия запущена")
        : "Защита не запущена";
    public string ActiveStrategyName => _winws.ActivePreset?.DisplayName ?? (_winws.IsExternal ? "Внешний winws.exe" : "—");
    public string UptimeText => IsRunning ? FormatDuration(_winws.Uptime) : "—";
    public string StatusBadge => IsRunning ? "ONLINE" : "OFFLINE";
    public bool IsAdmin => DiagnosticsService.IsAdministrator();

    public bool IsZapretSelected
    {
        get => _scanner.Engine == ZapretEngine.Zapret;
        set
        {
            if (value)
                SelectEngine(ZapretEngine.Zapret);
        }
    }

    public bool IsZapret2Selected
    {
        get => _scanner.Engine == ZapretEngine.Zapret2;
        set
        {
            if (!value)
                return;

            if (!CanUseZapret2 && !IsZapret2Installing)
            {
                _ = InstallZapret2Async();
                RaiseEngineState();
                return;
            }

            if (CanUseZapret2)
                SelectEngine(ZapretEngine.Zapret2);
            else
                RaiseEngineState();
        }
    }

    public bool CanUseZapret => _scanner.Current is not null && _scanner.HasEngine(ZapretEngine.Zapret);
    public bool CanUseZapret2 => _scanner.Current is not null && _scanner.HasEngine(ZapretEngine.Zapret2);
    public string EngineLabel => _scanner.Engine.DisplayName();

    public bool IsZapret2Installing
    {
        get => _isZapret2Installing;
        private set
        {
            if (!SetProperty(ref _isZapret2Installing, value))
                return;
            OnPropertyChanged(nameof(Zapret2EngineTooltip), nameof(EngineHint));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsCheckingStrategyPack
    {
        get => _isCheckingStrategyPack;
        private set
        {
            if (!SetProperty(ref _isCheckingStrategyPack, value))
                return;
            OnPropertyChanged(nameof(CheckStrategyPackButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string CheckStrategyPackButtonText => IsCheckingStrategyPack ? "Проверяю…" : "Новые стратегии";

    public double Zapret2InstallProgress
    {
        get => _zapret2InstallProgress;
        private set => SetProperty(ref _zapret2InstallProgress, value);
    }

    public string Zapret2InstallStatus
    {
        get => _zapret2InstallStatus;
        private set
        {
            if (!SetProperty(ref _zapret2InstallStatus, value))
                return;
            OnPropertyChanged(nameof(EngineHint));
        }
    }

    public string ZapretEngineTooltip => CanUseZapret
        ? "Классическое ядро zapret — winws.exe"
        : "zapret не найден: нужны winws.exe и .bat-стратегии. Нажмите, чтобы узнать подробнее.";

    public string Zapret2EngineTooltip
    {
        get
        {
            if (IsZapret2Installing)
                return "Устанавливаю zapret2…";

            return CanUseZapret2
                ? "Новое ядро zapret2 — winws2.exe"
                : "zapret2 не установлен. Нажмите, чтобы скачать и установить автоматически.";
        }
    }

    public string EngineHint
    {
        get
        {
            if (IsZapret2Installing)
                return string.IsNullOrWhiteSpace(Zapret2InstallStatus)
                    ? "Устанавливаю zapret2…"
                    : Zapret2InstallStatus;

            if (_scanner.Current is null)
                return "Ядро не обнаружено";

            var count = _scanner.CountFor(_scanner.Engine);
            return count == 0
                ? $"{_scanner.Engine.EngineFile()} · нет стратегий"
                : $"{_scanner.Engine.EngineFile()} · {count} " +
                  $"{Plural.Ru(count, "стратегия", "стратегии", "стратегий")}";
        }
    }

    public string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public bool StartMinimized { get; }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public bool ToastIsError
    {
        get => _toastIsError;
        private set => SetProperty(ref _toastIsError, value);
    }

    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    public async Task InitializeAsync()
    {
        _log.Info("app", $"RDG.Zapret {VersionText} запущен · {(IsAdmin ? "администратор" : "БЕЗ прав администратора")}");
        _log.Info("app", $"{Environment.OSVersion.VersionString} · .NET {Environment.Version}");

        _scanner.SetEngine(ZapretEngineExtensions.Parse(_settings.Current.Engine), notify: false);

        try
        {
            var autostartEnabled = await _services.Autostart.IsEnabledAsync();
            if (autostartEnabled != _settings.Current.StartWithWindows)
                _settings.Update(s => s.StartWithWindows = autostartEnabled, notify: false);
        }
        catch (Exception ex)
        {
            _log.Debug("autostart", "Не удалось проверить задачу автозапуска: " + ex.Message);
        }

        await EnsureInstallationAsync();
        await EnsureZapret2ExtrasAsync();

        if (_scanner.Current is not null)
            _userLists.SyncAll(_scanner.Current.RootPath);

        if (_winws.TryAdoptExisting())
            RefreshState();

        if (_scanner.Current is null && _settings.Current.AutoDownloadStrategies)
        {
            var portableBootstrap = PortablePresets.Find(AppContext.BaseDirectory).Count > 0;
            if (!_winws.IsRunning || portableBootstrap)
            {
                _log.Info("download", "Стратегии не найдены — запускаю автозагрузку с GitHub");
                await DownloadStrategiesAsync(auto: true);
            }
        }

        if (_autoStart && _settings.Current.StartLastStrategyOnLaunch && !_winws.IsRunning)
        {
            _log.Info("app", "Автозапуск: запускаю последнюю стратегию");
            await StartLastPresetAsync();
        }

        await Dashboard.OnActivatedAsync();
        RaiseEngineState();
        RefreshState();

        if (_settings.Current.AutoCheckUpdates)
            _ = SettingsPage.AutoCheckIfNeededAsync();

        _ = Telegram.TryAutoStartAsync();

        if (_settings.Current.AutoCheckStrategyPack)
            _ = CheckStrategyPackAsync(manual: false);
    }

    private void SelectEngine(ZapretEngine engine)
    {
        if (_scanner.Engine == engine)
        {
            RaiseEngineState();
            return;
        }

        if (_operations.IsBusy || _winws.IsRunning || IsBypassBusy)
        {
            ShowToast("Дождитесь завершения операции или остановите обход", true);
            RaiseEngineState();
            return;
        }

        if (Autotune.IsTuning || Strategies.IsBusy)
        {
            ShowToast("Дождитесь завершения проверки или подбора", true);
            RaiseEngineState();
            return;
        }

        if (!_scanner.SetEngine(engine))
        {
            var message = engine == ZapretEngine.Zapret2
                ? "zapret2 не найден: нужны winws2.exe и .cmd-пресеты. Скачайте zapret2 и укажите его папку в Настройках."
                : "zapret не найден: нужны winws.exe и .bat-стратегии. Укажите папку zapret в Настройках.";
            ShowToast(message, true);
            RaiseEngineState();
            return;
        }

        _settings.Update(s => s.Engine = engine.Id());
        ShowToast($"Ядро обхода: {engine.DisplayName()}", false);
        RaiseEngineState();
    }

    private async Task InstallZapret2Async()
    {
        if (IsZapret2Installing)
            return;

        if (_winws.IsRunning && _scanner.Engine == ZapretEngine.Zapret2)
        {
            ShowToast("Остановите обход перед установкой или обновлением zapret2", true);
            return;
        }

        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ShowToast("Сначала укажите папку zapret в Настройках", true);
            return;
        }

        IsZapret2Installing = true;
        Zapret2InstallProgress = 0;
        Zapret2InstallStatus = "Скачиваю zapret2…";
        _log.Info("zapret2", "Начинаю установку zapret2 в " + root);

        try
        {
            var progress = new Progress<DownloadProgress>(update =>
            {
                Zapret2InstallProgress = update.Percent;
                Zapret2InstallStatus = update.Status;
            });

            var result = await _zapret2.InstallAsync(root, progress, CancellationToken.None);
            if (!result.Success)
            {
                _log.Warn("zapret2", "Установка не удалась: " + result.Error);
                ShowToast("Не удалось установить zapret2: " + (result.Error ?? "ошибка"), true);
                return;
            }

            var scan = await _scanner.ScanAsync(root);
            if (scan.Installation is null)
            {
                ShowToast(scan.Error ?? "zapret2 установлен, но папка не распознана", true);
                return;
            }

            if (!_scanner.HasEngine(ZapretEngine.Zapret2))
            {
                ShowToast("zapret2 установлен, но стратегии не найдены", true);
                return;
            }

            _userLists.SyncAll(scan.Installation.RootPath);

            _log.Success("zapret2",
                $"Установлено пресетов: {result.PresetCount} · версия {result.Version ?? "—"}");

            Zapret2InstallStatus = "zapret2 установлен";

            if (_operations.IsBusy || _winws.IsRunning || IsBypassBusy)
            {
                ShowToast("zapret2 установлен. Остановите обход и нажмите zapret2 ещё раз.", false);
                return;
            }

            SelectEngine(ZapretEngine.Zapret2);

            var count = result.PresetCount;
            ShowToast(
                $"zapret2 готов: {count} {Plural.Ru(count, "стратегия", "стратегии", "стратегий")}",
                false);
        }
        catch (Exception ex)
        {
            _log.Error("zapret2", "Ошибка установки zapret2: " + ex);
            ShowToast("Ошибка установки zapret2: " + ex.Message, true);
        }
        finally
        {
            IsZapret2Installing = false;
            Zapret2InstallProgress = 0;
            Zapret2InstallStatus = string.Empty;
            RaiseEngineState();
        }
    }

    private void CancelDownload()
    {
        if (!IsDownloading)
            return;
        DownloadStatus = "Отмена…";
        _downloadCts?.Cancel();
    }

    private async Task DownloadStrategiesAsync(bool auto)
    {
        if (IsDownloading)
            return;

        var portableBootstrap = auto && _scanner.Current is null &&
                                PortablePresets.Find(AppContext.BaseDirectory).Count > 0;

        if (IsBypassBusy || (_winws.IsRunning && !portableBootstrap))
        {
            if (!auto)
                ShowToast("Остановите обход перед загрузкой стратегий", true);
            return;
        }

        if (await DiagnosticsService.IsServiceRunningAsync("zapret"))
        {
            if (!auto)
            {
                ShowToast("Служба zapret запущена. Остановите её в разделе «Диагностика».", true);
                Navigate("diagnostics");
            }
            return;
        }

        if (!auto && _scanner.Current is not null)
        {
            var answer = System.Windows.MessageBox.Show(
                "Скачать последнюю версию стратегий и заменить файлы zapret?\n\n" +
                _scanner.Current.RootPath +
                "\n\nПользовательские списки lists/*-user.txt будут сохранены.",
                "RDG.Zapret",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }

        if (!_operations.TryBegin("download"))
        {
            if (!auto)
                ShowToast("Дождитесь завершения текущей операции", true);
            return;
        }

        var target = ResolveDownloadTargetPath();
        DownloadTargetPath = target;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = auto ? "Автозагрузка стратегий с GitHub…" : "Подготовка…";
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<DownloadProgress>(update =>
            {
                DownloadProgress = update.Percent;
                DownloadStatus = update.Status;
            });

            var result = await _download.EnsureLatestAsync(target, progress, _downloadCts.Token);
            if (!result.Success)
            {
                var canceled = result.Error?.Contains("отменена", StringComparison.OrdinalIgnoreCase) == true;
                if (!canceled || !auto)
                    ShowToast(result.Error ?? "Не удалось скачать стратегии", !canceled);
                return;
            }

            var installationPath = result.InstallationPath ?? target;

            // Portable distribution: the strategies live next to the exe, while the core may be
            // installed to ProgramData (read-only app folder). Import them before scanning.
            var baseDirectory = AppContext.BaseDirectory;
            var portablePresets = PortablePresets.Find(baseDirectory);
            if (portablePresets.Count > 0 &&
                !string.Equals(
                    Path.GetFullPath(installationPath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                var copied = PortablePresets.CopyTo(portablePresets, installationPath, _log);
                if (copied > 0)
                    _log.Info("portable", $"Импортировано портативных стратегий: {copied} → {installationPath}");
            }

            _settings.Update(s => s.ZapretPath = installationPath);

            var scan = await _scanner.ScanAsync(installationPath);
            if (scan.Installation is null)
            {
                ShowToast(scan.Error ?? "Архив скачан, но папка не распознана", true);
                return;
            }

            _settings.Update(s => s.ZapretPath = scan.Installation.RootPath);
            _userLists.SyncAll(scan.Installation.RootPath);
            OnScannerStateChanged();

            var count = scan.Installation.Presets.Count;
            ShowToast(
                $"Стратегии {result.Version} установлены: {count} " +
                $"{Plural.Ru(count, "стратегия", "стратегии", "стратегий")}",
                false);
            _log.Success("download", $"Установлено стратегий: {count} · версия {result.Version} · {scan.Installation.RootPath}");
        }
        catch (OperationCanceledException)
        {
            ShowToast("Загрузка отменена", false);
        }
        catch (Exception ex)
        {
            _log.Error("download", "Ошибка загрузки стратегий: " + ex);
            ShowToast("Ошибка загрузки: " + ex.Message, true);
        }
        finally
        {
            IsDownloading = false;
            DownloadStatus = string.Empty;
            _downloadCts?.Dispose();
            _downloadCts = null;
            _operations.End("download");
            RaiseEngineState();
        }
    }

    private string ResolveDownloadTargetPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (PortablePresets.Find(baseDirectory).Count > 0)
        {
            return PortablePresets.IsDirectoryWritable(baseDirectory)
                ? baseDirectory
                : ZapretDownloadService.ManagedInstallPath;
        }

        var configured = _settings.Current.ZapretPath?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
            return ZapretDownloadService.ManagedInstallPath;

        try
        {
            if (Directory.Exists(configured) &&
                (ZapretLocator.LooksLikeZapret(configured) ||
                 !Directory.EnumerateFileSystemEntries(configured).Any()))
                return configured;
        }
        catch
        {
            // ignored
        }

        return ZapretDownloadService.ManagedInstallPath;
    }

    private void OnScannerStateChanged()
    {
        var engineId = _scanner.Engine.Id();
        if (!string.Equals(_settings.Current.Engine, engineId, StringComparison.OrdinalIgnoreCase))
            _settings.Update(s => s.Engine = engineId, notify: false);

        RaiseEngineState();
    }

    private void RaiseEngineState()
    {
        OnPropertyChanged(
            nameof(IsZapretSelected),
            nameof(IsZapret2Selected),
            nameof(CanUseZapret),
            nameof(CanUseZapret2),
            nameof(ZapretEngineTooltip),
            nameof(Zapret2EngineTooltip),
            nameof(EngineLabel),
            nameof(EngineHint));
    }

    public void ShowToast(string message, bool isError)
    {
        ToastText = message;
        ToastIsError = isError;
        IsToastVisible = true;

        _toastCts?.Cancel();
        var cts = new CancellationTokenSource();
        _toastCts = cts;
        _ = HideToastAsync(cts.Token);
    }

    private async Task HideToastAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(5000, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Ui.Invoke(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
                IsToastVisible = false;
        });
    }

    public void Navigate(string key)
    {
        var item = NavItems.FirstOrDefault(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            SelectedNav = item;
    }

    private static async Task ActivateAsync(IPageActivation activation)
    {
        try
        {
            await activation.OnActivatedAsync();
        }
        catch
        {
            // individual pages handle their own errors
        }
    }

    private async Task CheckStrategyPackAsync(bool manual)
    {
        if (IsCheckingStrategyPack)
            return;

        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            if (manual)
                ShowToast("Сначала укажите папку zapret", true);
            return;
        }

        if (_operations.IsBusy)
        {
            if (manual)
                ShowToast("Дождитесь завершения текущей операции", true);
            return;
        }

        IsCheckingStrategyPack = true;
        try
        {
            var progress = new Progress<StrategyPackProgress>(_ => { });
            var result = await _strategyPack.CheckAndInstallAsync(
                root, manual, progress, CancellationToken.None);

            if (!result.Success)
            {
                if (manual)
                {
                    _log.Warn("strategies", result.Error ?? "Не удалось проверить стратегии");
                    ShowToast(result.Error ?? "Не удалось проверить стратегии", true);
                }
                else
                {
                    _log.Debug("strategies", result.Error ?? "Не удалось проверить стратегии");
                }
                return;
            }

            if (result.HasChanges)
            {
                if (!_operations.IsBusy)
                    await _scanner.ScanAsync(root);
                else
                    _log.Info("strategies", "Стратегии установлены; список обновится после текущей операции");

                var message = $"Стратегии обновлены: добавлено {result.Added}, обновлено {result.Updated}" +
                              (result.RemovedDuplicates > 0 ? $", убрано дублей: {result.RemovedDuplicates}" : string.Empty);
                _log.Success("strategies", message);
                ShowToast(message, false);
            }
            else if (manual)
            {
                ShowToast("Новых стратегий нет", false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("strategies", "Ошибка обновления стратегий: " + ex.Message);
            if (manual)
                ShowToast("Ошибка обновления стратегий: " + ex.Message, true);
        }
        finally
        {
            IsCheckingStrategyPack = false;
        }
    }

    private async Task EnsureZapret2ExtrasAsync()
    {
        var root = _scanner.Current?.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Zapret2PackService.ExePath(root)))
            return;

        try
        {
            var added = await _zapret2.EnsureExtraStrategiesAsync(root);
            if (added <= 0)
                return;

            _log.Info("zapret2", $"Добавлены стратегии сообщества: {added}");
            await _scanner.ScanAsync(root);
        }
        catch (Exception ex)
        {
            _log.Debug("zapret2", "Не удалось проверить стратегии сообщества: " + ex.Message);
        }
    }

    private async Task EnsureInstallationAsync()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var portablePresets = PortablePresets.Find(baseDirectory);

        if (portablePresets.Count > 0)
        {
            // Portable layout: strategies (and optionally the whole zapret core) live next to the exe.
            var portableScan = await _scanner.ScanAsync(baseDirectory);
            if (portableScan.Installation is not null)
            {
                _settings.Update(s => s.ZapretPath = portableScan.Installation.RootPath, notify: false);
                _log.Success("scan",
                    $"Портативный режим: {portableScan.Installation.Presets.Count} стратегий из папки программы");
                return;
            }

            // No engine next to the exe. Reuse an already downloaded managed core and import the strategies.
            var managedPath = ZapretDownloadService.ManagedInstallPath;
            if (ZapretLocator.LooksLikeZapret(managedPath))
            {
                var copied = PortablePresets.CopyTo(portablePresets, managedPath, _log);
                var managedScan = await _scanner.ScanAsync(managedPath);
                if (managedScan.Installation is not null)
                {
                    _settings.Update(s => s.ZapretPath = managedScan.Installation.RootPath, notify: false);
                    _log.Success("scan",
                        $"Портативные стратегии: {portablePresets.Count} (скопировано {copied}) · ядро {managedPath}");
                    return;
                }
            }

            _log.Info("portable",
                $"Рядом с программой найдено стратегий: {portablePresets.Count}. Ядро winws.exe не найдено — потребуется автозагрузка.");
            return;
        }

        var configuredPath = _settings.Current.ZapretPath;
        ScanResult? result = null;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            result = await _scanner.ScanAsync(configuredPath);
        }

        if (result?.Installation is null)
        {
            _log.Info("scan", "Ищу папку zapret автоматически…");
            var found = await Task.Run(() => ZapretLocator.Find(configuredPath));
            if (found is not null)
            {
                var autoResult = await _scanner.ScanAsync(found);
                if (autoResult.Installation is not null)
                {
                    _settings.Update(s => s.ZapretPath = autoResult.Installation.RootPath, notify: false);
                    _log.Success("scan", "zapret найден автоматически: " + autoResult.Installation.RootPath);
                }
            }
        }

        if (_scanner.Current is null)
            _log.Warn("scan", "zapret не найден. Укажите папку в настройках — без неё запуск невозможен.");
    }

    private StrategyPreset? ResolveStartPreset() =>
        _scanner.Find(_settings.Current.LastPresetFile)
        ?? _scanner.Find(_settings.Current.BestPresetFile)
        ?? _scanner.Presets.FirstOrDefault();

    private async Task StartBypassAsync()
    {
        if (_winws.IsRunning || IsBypassBusy)
            return;

        if (await DiagnosticsService.IsServiceRunningAsync("zapret"))
        {
            ShowToast("Служба zapret уже запущена. Остановите её в разделе «Диагностика».", true);
            Navigate("diagnostics");
            return;
        }

        var preset = ResolveStartPreset();
        if (preset is null)
        {
            ShowToast("Сначала укажите папку zapret", true);
            Navigate("settings");
            return;
        }

        if (!_operations.TryBegin("bypass"))
        {
            ShowToast("Дождитесь завершения текущей операции", true);
            return;
        }

        IsBypassBusy = true;
        try
        {
            var outcome = await _winws.StartAsync(preset);
            if (outcome.Success)
            {
                _settings.Update(s => s.LastPresetFile = preset.FileName);
                ShowToast($"Запущено: {preset.DisplayName}", false);
                _log.Success("app", "Обход запущен: " + preset.DisplayName);
            }
            else
            {
                ShowToast(outcome.Error ?? "Не удалось запустить стратегию", true);
                _log.Error("app", "Не удалось запустить обход: " + outcome.Error);
            }
        }
        finally
        {
            IsBypassBusy = false;
            _operations.End("bypass");
            RefreshState();
        }
    }

    public void ApplyBackgroundResults(IReadOnlyList<ProbeResult> results) =>
        Dashboard.ApplyBackgroundResults(results);

    private async Task StopBypassAsync()
    {
        if (IsBypassBusy)
            return;

        _services.Monitor.CancelRecovery();

        if (_winws.IsExternal && _winws.IsRunning)
        {
            var answer = System.Windows.MessageBox.Show(
                "Остановить внешний процесс winws.exe?\n\nОн может быть запущен службой «zapret» — тогда обход перестанет работать до повторного запуска службы.",
                "RDG.Zapret",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }

        IsBypassBusy = true;
        try
        {
            await _winws.StopAsync();
            ShowToast("Обход остановлен", false);
        }
        finally
        {
            IsBypassBusy = false;
            RefreshState();
        }
    }

    private async Task StartLastPresetAsync()
    {
        var preset = ResolveStartPreset();
        if (preset is null)
            return;

        var outcome = await _winws.StartAsync(preset);
        if (outcome.Success)
        {
            _settings.Update(s => s.LastPresetFile = preset.FileName, notify: false);
            _log.Success("app", "Автозапуск выполнен: " + preset.DisplayName);
        }
        else
        {
            _log.Warn("app", "Автозапуск не удался: " + outcome.Error);
        }

        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(
            nameof(IsRunning),
            nameof(IsExternal),
            nameof(StatusTitle),
            nameof(StatusSubtitle),
            nameof(ActiveStrategyName),
            nameof(UptimeText),
            nameof(StatusBadge));
    }

    /// <summary>Called from App.OnExit (UI thread). Never deadlocks: process work runs on the thread pool.</summary>
    public void PrepareShutdown()
    {
        try
        {
            if (!_settings.Current.LeaveBypassOnExit)
            {
                _log.Info("app", "Останавливаю обход перед выходом");
                Task.Run(async () => await _winws.StopAsync()).Wait(TimeSpan.FromSeconds(4));
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            try
            {
                if (_settings.Current.TgProxyStopOnExit)
                {
                    _log.Info("app", "Останавливаю Telegram-прокси перед выходом");
                    Task.Run(async () => await _services.TgProxy.StopAsync()).Wait(TimeSpan.FromSeconds(3));
                }
            }
            catch
            {
                // ignored
            }

            _winws.Detach();
            _log.Info("app", "Выход");
        }
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return "—";
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }
}
