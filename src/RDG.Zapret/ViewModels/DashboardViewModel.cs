using System.Windows.Input;
using System.Windows.Threading;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IPageActivation
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly WinwsController _winws;
    private readonly ServiceProbe _probe;
    private readonly DiagnosticsService _diagnostics;
    private readonly YoutubeThrottleService _youtube;
    private readonly HostsFixService _hostsFix;
    private readonly UserListService _userLists;
    private readonly LogService _log;
    private readonly Func<Task> _startBypass;
    private readonly Func<Task> _stopBypass;
    private readonly Func<Task> _download;
    private readonly Action<string, bool> _toast;
    private readonly Action<string> _navigate;
    private readonly DispatcherTimer _uptimeTimer;

    private bool _isStarting;
    private bool _isCheckingAll;
    private bool _isDiagnosticsLoading;
    private string _lastCheckText = string.Empty;
    private string _availabilitySummary = "Доступность ещё не проверялась";
    private string _installationPath = string.Empty;
    private string _engineName = string.Empty;
    private int _presetCount;
    private string _diagnosticSummary = "Проверка ещё не выполнялась";
    private string _lastTuneTitle = "Автоподбор не выполнялся";
    private string _lastTuneDetail = "Запустите поиск рабочей стратегии";
    private bool _isYoutubeChecking;
    private bool _isYoutubeFixing;
    private string _youtubeTitle = "Скорость видео не проверялась";
    private string _youtubeMessage = "Проверьте, если YouTube открывается, но видео тормозит или долго буферизуется.";
    private bool _youtubeHasResult;
    private bool _youtubeCanFix;
    private string _youtubeFixText = "Исправление выключено";
    private bool _isHostsFixApplied;
    private bool _isYoutubeVideoChecking;
    private string _youtubeVideoUrl = string.Empty;
    private List<string> _youtubeFixHosts = new();

    public DashboardViewModel(
        SettingsService settings,
        PresetScanner scanner,
        WinwsController winws,
        ServiceProbe probe,
        DiagnosticsService diagnostics,
        YoutubeThrottleService youtube,
        HostsFixService hostsFix,
        UserListService userLists,
        BannerService banner,
        LogService log,
        Func<Task> startBypass,
        Func<Task> stopBypass,
        Func<Task> download,
        Action<string, bool> toast,
        Action<string> navigate)
    {
        _settings = settings;
        _scanner = scanner;
        _winws = winws;
        _probe = probe;
        _diagnostics = diagnostics;
        _youtube = youtube;
        _hostsFix = hostsFix;
        _userLists = userLists;
        _log = log;
        _startBypass = startBypass;
        _stopBypass = stopBypass;
        _download = download;
        _toast = toast;
        _navigate = navigate;
        Banner = new BannerViewModel(banner, log);

        Services = new ObservableCollection<ServiceItemViewModel>(ServiceCatalog.All.Select(t => new ServiceItemViewModel(t)));
        Diagnostics = new ObservableCollection<DiagnosticItemViewModel>();

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && !IsStarting && HasInstallation);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning && !IsStarting);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => !IsCheckingAll);
        CheckServiceCommand = new AsyncRelayCommand<ServiceItemViewModel>(CheckOneAsync,
            service => service is not null && !service.IsChecking && !IsCheckingAll);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync, () => !IsDiagnosticsLoading);
        NavigateCommand = new RelayCommand(parameter => _navigate(parameter as string ?? "overview"));
        OpenInstallFolderCommand = new RelayCommand(OpenInstallFolder, () => HasInstallation);
        OpenSettingsCommand = new RelayCommand(() => _navigate("settings"));
        OpenStrategiesCommand = new RelayCommand(() => _navigate("strategies"));
        OpenAutotuneCommand = new RelayCommand(() => _navigate("autotune"));
        OpenReleasesCommand = new RelayCommand(() => ShellHelper.OpenUrl("https://github.com/Flowseal/zapret-discord-youtube/releases/latest"));
        DownloadCommand = new AsyncRelayCommand(download, () => !IsBusy);
        CheckYoutubeCommand = new AsyncRelayCommand(CheckYoutubeAsync, () => !IsYoutubeChecking && !IsYoutubeFixing && !IsYoutubeVideoChecking);
        CheckYoutubeVideoCommand = new AsyncRelayCommand(CheckYoutubeVideoAsync, () => !IsYoutubeChecking && !IsYoutubeFixing && !IsYoutubeVideoChecking);
        FixYoutubeCommand = new AsyncRelayCommand(FixYoutubeAsync, () => YoutubeCanFix && !IsYoutubeChecking && !IsYoutubeFixing && !IsYoutubeVideoChecking);
        RemoveYoutubeFixCommand = new AsyncRelayCommand(RemoveYoutubeFixAsync, () => IsHostsFixApplied && !IsYoutubeFixing);
        YoutubeQuicFixCommand = new AsyncRelayCommand(YoutubeQuicFixAsync, () => !IsYoutubeChecking && !IsYoutubeFixing && !IsYoutubeVideoChecking);
        OpenDiagnosticsCommand = new RelayCommand(() => _navigate("diagnostics"));

        RefreshHostsFixStatus();

        scanner.Changed += (_, _) => Ui.Invoke(HandleScannerChanged);
        winws.StateChanged += (_, _) => Ui.Invoke(HandleStateChanged);
        ServiceCatalog.Changed += (_, _) => Ui.Invoke(RebuildServices);

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) =>
        {
            if (IsRunning)
                OnPropertyChanged(nameof(UptimeText));
        };
        _uptimeTimer.Start();

        UpdateInstallation();
        UpdateTuneSummary();
    }

    public ObservableCollection<ServiceItemViewModel> Services { get; }
    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; }
    public BannerViewModel Banner { get; }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand CheckAllCommand { get; }
    public AsyncRelayCommand<ServiceItemViewModel> CheckServiceCommand { get; }
    public AsyncRelayCommand RefreshDiagnosticsCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenInstallFolderCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenStrategiesCommand { get; }
    public RelayCommand OpenAutotuneCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand CheckYoutubeCommand { get; }
    public AsyncRelayCommand CheckYoutubeVideoCommand { get; }
    public AsyncRelayCommand FixYoutubeCommand { get; }
    public AsyncRelayCommand RemoveYoutubeFixCommand { get; }
    public AsyncRelayCommand YoutubeQuicFixCommand { get; }
    public RelayCommand OpenDiagnosticsCommand { get; }
    public ObservableCollection<YoutubeNodeViewModel> YoutubeNodes { get; } = new();

    public bool HasInstallation => _scanner.Current is not null;
    public string InstallationName => _scanner.Current?.FolderName ?? "zapret не найден";
    public string InstallationPath => _installationPath;
    public string EngineName => _engineName;
    public string EngineLabel => _scanner.Engine.DisplayName();
    public int PresetCount => _presetCount;
    public string PresetCountText => HasInstallation
        ? $"{_presetCount} {Plural.Ru(_presetCount, "стратегия", "стратегии", "стратегий")}"
        : "нет данных";

    public bool IsRunning => _winws.IsRunning;
    public bool IsStarting
    {
        get => _isStarting;
        private set
        {
            if (SetProperty(ref _isStarting, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsBusy => IsStarting || IsCheckingAll;

    public bool IsCheckingAll
    {
        get => _isCheckingAll;
        private set
        {
            if (SetProperty(ref _isCheckingAll, value))
                OnPropertyChanged(nameof(IsBusy), nameof(CheckAllButtonText));
        }
    }

    public string CheckAllButtonText => IsCheckingAll ? "Проверяю…" : "Проверить всё";

    public string StatusTitle => IsRunning ? "Обход активен" : "Обход выключен";
    public string StatusSubtitle => IsRunning
        ? _winws.ActivePreset?.DisplayName ?? (_winws.IsExternal ? "Внешний процесс winws.exe" : "Стратегия запущена")
        : "Запустите стратегию, чтобы вернуть доступ к сервисам";
    public string ActiveStrategyName => _winws.ActivePreset?.DisplayName ?? (_winws.IsExternal ? "Внешний winws.exe" : "—");
    public string UptimeText => IsRunning ? FormatDuration(_winws.Uptime) : "—";
    public string StatusBadge => IsRunning ? "ONLINE" : "OFFLINE";

    public string AvailabilitySummary
    {
        get => _availabilitySummary;
        private set => SetProperty(ref _availabilitySummary, value);
    }

    public string LastCheckText
    {
        get => _lastCheckText;
        private set => SetProperty(ref _lastCheckText, value);
    }

    public string DiagnosticSummary
    {
        get => _diagnosticSummary;
        private set => SetProperty(ref _diagnosticSummary, value);
    }

    public bool IsDiagnosticsLoading
    {
        get => _isDiagnosticsLoading;
        private set => SetProperty(ref _isDiagnosticsLoading, value);
    }

    public bool IsYoutubeChecking
    {
        get => _isYoutubeChecking;
        private set
        {
            if (SetProperty(ref _isYoutubeChecking, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsYoutubeFixing
    {
        get => _isYoutubeFixing;
        private set
        {
            if (SetProperty(ref _isYoutubeFixing, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsYoutubeVideoChecking
    {
        get => _isYoutubeVideoChecking;
        private set
        {
            if (SetProperty(ref _isYoutubeVideoChecking, value))
            {
                OnPropertyChanged(nameof(YoutubeVideoButtonText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string YoutubeVideoUrl
    {
        get => _youtubeVideoUrl;
        set => SetProperty(ref _youtubeVideoUrl, value ?? string.Empty);
    }

    public string YoutubeVideoButtonText => IsYoutubeVideoChecking ? "Проверяю…" : "Проверить видео";

    public string YoutubeQuicHint =>
        "Если ролик не грузится ни с одной стратегией — нажмите «YouTube QUIC fix», " +
        "затем отключите QUIC/HTTP3 в браузере и перезапустите его.";

    public string YoutubeTitle
    {
        get => _youtubeTitle;
        private set => SetProperty(ref _youtubeTitle, value);
    }

    public string YoutubeMessage
    {
        get => _youtubeMessage;
        private set => SetProperty(ref _youtubeMessage, value);
    }

    public bool YoutubeHasResult
    {
        get => _youtubeHasResult;
        private set => SetProperty(ref _youtubeHasResult, value);
    }

    public bool YoutubeCanFix
    {
        get => _youtubeCanFix;
        private set
        {
            if (SetProperty(ref _youtubeCanFix, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsHostsFixApplied
    {
        get => _isHostsFixApplied;
        private set
        {
            if (SetProperty(ref _isHostsFixApplied, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string YoutubeFixText
    {
        get => _youtubeFixText;
        private set => SetProperty(ref _youtubeFixText, value);
    }

    public bool HasYoutubeNodes => YoutubeNodes.Count > 0;

    public string YoutubeButtonText => IsYoutubeChecking ? "Проверяю…" : "Проверить скорость видео";

    public string LastTuneTitle
    {
        get => _lastTuneTitle;
        private set => SetProperty(ref _lastTuneTitle, value);
    }

    public string LastTuneDetail
    {
        get => _lastTuneDetail;
        private set => SetProperty(ref _lastTuneDetail, value);
    }

    public string ServicesFraction
    {
        get
        {
            var available = Services.Count(s => s.IsAvailable);
            return $"{available} / {Services.Count}";
        }
    }

    public async Task OnActivatedAsync()
    {
        UpdateInstallation();
        UpdateTuneSummary();
        RefreshHostsFixStatus();
        await Banner.RefreshAsync();
        await RefreshDiagnosticsAsync();
    }

    public void ApplyBackgroundResults(IReadOnlyList<ProbeResult> results)
    {
        foreach (var result in results)
        {
            var service = Services.FirstOrDefault(s => s.Target.Id == result.ServiceId);
            if (service is null)
                continue;

            var merged = result;
            if (!result.HasMetrics && service.Result.HasMetrics)
            {
                merged = new ProbeResult
                {
                    ServiceId = result.ServiceId,
                    Status = result.Status,
                    LatencyMs = result.LatencyMs,
                    DnsMs = service.Result.DnsMs,
                    TcpMs = service.Result.TcpMs,
                    TtfbMs = service.Result.TtfbMs,
                    Detail = result.Detail,
                    Timestamp = result.Timestamp
                };
            }

            service.Apply(merged);
        }

        LastCheckText = "Фоновая проверка · " + DateTime.Now.ToString("HH:mm:ss");
        UpdateAvailability();
        OnPropertyChanged(nameof(ServicesFraction));
    }

    private void RebuildServices()
    {
        var previous = Services.ToDictionary(
            s => s.Target.Id,
            s => s.Result,
            StringComparer.OrdinalIgnoreCase);

        Services.Clear();
        foreach (var target in ServiceCatalog.All)
        {
            var item = new ServiceItemViewModel(target);
            if (previous.TryGetValue(target.Id, out var result) && result.Status != ProbeStatus.Unknown)
                item.Apply(result);
            Services.Add(item);
        }

        UpdateAvailability();
        OnPropertyChanged(nameof(ServicesFraction));
    }

    private void HandleScannerChanged()
    {
        UpdateInstallation();
        UpdateTuneSummary();
        _ = RefreshDiagnosticsAsync();
    }

    private void HandleStateChanged()
    {
        OnPropertyChanged(
            nameof(IsRunning),
            nameof(StatusTitle),
            nameof(StatusSubtitle),
            nameof(ActiveStrategyName),
            nameof(UptimeText),
            nameof(StatusBadge));
    }

    private void UpdateInstallation()
    {
        var installation = _scanner.Current;
        var rootChanged = !string.Equals(_installationPath, installation?.RootPath, StringComparison.OrdinalIgnoreCase);

        _installationPath = installation?.RootPath ?? string.Empty;
        _engineName = installation is not null
            ? Path.GetFileName(_scanner.ActiveEnginePath ?? installation.EnginePath)
            : string.Empty;
        _presetCount = _scanner.Presets.Count;

        OnPropertyChanged(
            nameof(HasInstallation),
            nameof(InstallationName),
            nameof(InstallationPath),
            nameof(EngineName),
            nameof(EngineLabel),
            nameof(PresetCount),
            nameof(PresetCountText));

        if (rootChanged)
        {
            foreach (var service in Services)
                service.Reset();
            LastCheckText = string.Empty;
            UpdateAvailability();
        }
    }

    private void UpdateTuneSummary()
    {
        var best = _scanner.Find(_settings.Current.BestPresetFile);
        if (best is not null)
        {
            LastTuneTitle = best.DisplayName;
            LastTuneDetail = $"{_settings.Current.BestScore:0} / 100 · {_settings.Current.BestScoreDate}";
        }
        else
        {
            LastTuneTitle = "Автоподбор не выполнялся";
            LastTuneDetail = "Запустите поиск рабочей стратегии";
        }
    }

    private async Task StartAsync()
    {
        if (!HasInstallation)
        {
            _toast("Сначала укажите папку zapret", true);
            _navigate("settings");
            return;
        }

        IsStarting = true;
        try
        {
            await _startBypass();
            HandleStateChanged();
        }
        finally
        {
            IsStarting = false;
        }

        if (IsRunning)
            await CheckAllAsync();
    }

    private async Task StopAsync()
    {
        IsStarting = true;
        try
        {
            await _stopBypass();
            HandleStateChanged();
        }
        finally
        {
            IsStarting = false;
        }
    }

    private ProbeOptions BuildOptions() => new()
    {
        TimeoutSeconds = _settings.Current.ProbeTimeoutSeconds,
        ClassifyWithTls = _settings.Current.DeepClassification,
        MeasurePhases = true
    };

    private async Task CheckAllAsync()
    {
        if (IsCheckingAll)
            return;

        IsCheckingAll = true;
        foreach (var service in Services)
            service.MarkChecking();

        try
        {
            var results = await _probe.CheckManyAsync(
                Services.Select(s => s.Target).ToList(),
                BuildOptions(),
                CancellationToken.None);

            foreach (var result in results)
            {
                var service = Services.FirstOrDefault(s => s.Target.Id == result.ServiceId);
                service?.Apply(result);
            }

            LastCheckText = "Обновлено в " + DateTime.Now.ToString("HH:mm:ss");
            UpdateAvailability();

            var available = results.Count(r => r.IsSuccess);
            _log.Info("probe", $"Проверка сервисов: {available}/{results.Count} доступны");
        }
        catch (Exception ex)
        {
            _log.Error("probe", "Ошибка проверки сервисов: " + ex.Message);
            _toast("Не удалось проверить сервисы: " + ex.Message, true);
            foreach (var service in Services.Where(s => s.IsChecking))
                service.Reset();
        }
        finally
        {
            IsCheckingAll = false;
            OnPropertyChanged(nameof(ServicesFraction));
        }
    }

    private async Task CheckOneAsync(ServiceItemViewModel? service)
    {
        if (service is null)
            return;

        service.MarkChecking();
        try
        {
            var result = await _probe.CheckAsync(service.Target, BuildOptions(), CancellationToken.None);
            service.Apply(result);
            _log.Debug("probe", $"{service.Name}: {result.Status} · {result.Detail}");
        }
        catch (Exception ex)
        {
            service.Apply(new ProbeResult
            {
                ServiceId = service.Target.Id,
                Status = ProbeStatus.Error,
                Detail = ex.Message
            });
        }

        LastCheckText = "Обновлено в " + DateTime.Now.ToString("HH:mm:ss");
        UpdateAvailability();
        OnPropertyChanged(nameof(ServicesFraction));
    }

    private void UpdateAvailability()
    {
        var available = Services.Count(s => s.IsAvailable);
        AvailabilitySummary = $"{available} из {Services.Count} сервисов доступны";
        OnPropertyChanged(nameof(ServicesFraction));
    }

    private async Task RefreshDiagnosticsAsync()
    {
        if (IsDiagnosticsLoading)
            return;

        IsDiagnosticsLoading = true;
        try
        {
            var items = await _diagnostics.RunAsync(_scanner.Current);
            Diagnostics.Clear();
            foreach (var item in items)
                Diagnostics.Add(new DiagnosticItemViewModel(item));

            var ok = items.Count(i => i.Status == DiagnosticStatus.Ok);
            var warnings = items.Count(i => i.Status == DiagnosticStatus.Warn);
            DiagnosticSummary = warnings == 0
                ? $"{ok} из {items.Count} проверок в порядке"
                : $"{ok} в порядке · {warnings} предупреждений";
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

    private void RefreshHostsFixStatus()
    {
        var status = _hostsFix.GetStatus();
        IsHostsFixApplied = status.Applied;
        YoutubeFixText = status.Applied
            ? $"Исправление включено · {status.NodeCount} {Plural.Ru(status.NodeCount, "сервер", "сервера", "серверов")}"
            : "Исправление выключено";
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CheckYoutubeAsync()
    {
        if (IsYoutubeChecking)
            return;

        IsYoutubeChecking = true;
        YoutubeTitle = "Проверяю скорость видео…";
        YoutubeMessage = "Скачиваю небольшой фрагмент видео с нескольких серверов YouTube. Это занимает до 30 секунд.";
        OnPropertyChanged(nameof(YoutubeButtonText));

        try
        {
            var report = await _youtube.CheckAsync(CancellationToken.None);
            YoutubeTitle = report.Title;
            YoutubeMessage = report.Message;
            YoutubeHasResult = true;
            _youtubeFixHosts = report.FixableHosts.ToList();
            YoutubeCanFix = report.HasFixableNodes;
            YoutubeNodes.Clear();
            foreach (var node in report.Nodes)
                YoutubeNodes.Add(new YoutubeNodeViewModel(node));
            OnPropertyChanged(nameof(HasYoutubeNodes));

            _log.Info("youtube", $"{report.Title}: {report.Message}");
            if (report.HasFixableNodes)
                _toast("Найдены проблемные серверы YouTube", false);
            else if (report.Verdict == YoutubeVerdict.Fast)
                _toast("YouTube работает быстро", false);
        }
        catch (Exception ex)
        {
            YoutubeTitle = "Не удалось проверить скорость";
            YoutubeMessage = ex.Message;
            YoutubeCanFix = false;
            _log.Warn("youtube", "Ошибка проверки скорости: " + ex.Message);
        }
        finally
        {
            IsYoutubeChecking = false;
            OnPropertyChanged(nameof(YoutubeButtonText));
        }
    }

    private async Task CheckYoutubeVideoAsync()
    {
        if (IsYoutubeVideoChecking)
            return;

        var input = (YoutubeVideoUrl ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            _toast("Вставьте ссылку на видео или ID ролика", true);
            return;
        }

        IsYoutubeVideoChecking = true;
        YoutubeTitle = "Проверяю видео…";
        YoutubeMessage = "Запрашиваю у YouTube реальные серверы этого ролика и скачиваю небольшой фрагмент. Это занимает до 30 секунд.";

        try
        {
            var report = await _youtube.CheckVideoAsync(input, CancellationToken.None);
            YoutubeTitle = report.Title;
            YoutubeMessage = report.Message;
            YoutubeHasResult = true;
            _youtubeFixHosts = report.FixableHosts.ToList();
            YoutubeCanFix = report.HasFixableNodes;
            YoutubeNodes.Clear();
            foreach (var node in report.Nodes)
                YoutubeNodes.Add(new YoutubeNodeViewModel(node));
            OnPropertyChanged(nameof(HasYoutubeNodes));

            _log.Info("youtube", $"Видео {input}: {report.Title}. {report.Message}");
            if (report.HasFixableNodes)
                _toast("Проблемные серверы этого видео найдены", false);
            else if (report.Verdict == YoutubeVerdict.Fast)
                _toast("Видео загружается нормально", false);
        }
        catch (Exception ex)
        {
            YoutubeTitle = "Не удалось проверить видео";
            YoutubeMessage = ex.Message;
            YoutubeCanFix = false;
            _youtubeFixHosts = new List<string>();
            _log.Warn("youtube", "Ошибка проверки видео: " + ex.Message);
        }
        finally
        {
            IsYoutubeVideoChecking = false;
        }
    }

    private async Task FixYoutubeAsync()
    {
        if (IsYoutubeFixing || !YoutubeCanFix)
            return;

        var slowHosts = _youtubeFixHosts.ToList();
        if (slowHosts.Count == 0)
            return;

        var answer = System.Windows.MessageBox.Show(
            "Включить исправление проблемных серверов YouTube?\n\n" +
            $"Будут пропущены {slowHosts.Count} {Plural.Ru(slowHosts.Count, "сервер", "сервера", "серверов")}, " +
            "которые не отдают видео или отдают его слишком медленно. Плеер быстрее переключится на рабочие. " +
            "Изменения вносятся только в отдельный помеченный блок файла hosts и легко отключаются.\n\n" +
            "Потребуются права администратора.",
            "RDG.Zapret",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsYoutubeFixing = true;
        try
        {
            var result = await _hostsFix.ApplyAsync(slowHosts);
            _toast(result.Message, !result.Success);
            if (result.Success)
            {
                _settings.Update(s => s.YoutubeHostsFixAppliedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm"));
                YoutubeCanFix = false;
                RefreshHostsFixStatus();
            }
        }
        finally
        {
            IsYoutubeFixing = false;
        }
    }

    private async Task RemoveYoutubeFixAsync()
    {
        if (IsYoutubeFixing)
            return;

        IsYoutubeFixing = true;
        try
        {
            var result = await _hostsFix.RemoveAsync();
            _toast(result.Message, !result.Success);
            if (result.Success)
            {
                _settings.Update(s => s.YoutubeHostsFixAppliedAt = string.Empty);
                RefreshHostsFixStatus();
            }
        }
        finally
        {
            IsYoutubeFixing = false;
        }
    }

    private async Task YoutubeQuicFixAsync()
    {
        if (IsYoutubeFixing)
            return;

        var installation = _scanner.Current;
        if (installation is null)
        {
            _toast("Сначала укажите папку zapret", true);
            _navigate("settings");
            return;
        }

        const string domains =
            "youtube.com\ngooglevideo.com\nytimg.com\nggpht.com\nyoutube-nocookie.com";

        IsYoutubeFixing = true;
        try
        {
            var added = 0;
            var messages = new List<string>();

            foreach (var engine in new[] { ZapretEngine.Zapret, ZapretEngine.Zapret2 })
            {
                if (engine != _scanner.Engine &&
                    !Directory.Exists(_userLists.ListsDirectory(installation.RootPath, engine)))
                    continue;

                var change = _userLists.AddMany(
                    installation.RootPath,
                    engine,
                    UserListKind.DomainBypass,
                    domains);

                if (change.Success)
                    added++;
                else
                    messages.Add(change.Message);
            }

            if (added == 0)
            {
                _toast(messages.FirstOrDefault() ?? "Не удалось добавить домены YouTube", true);
                return;
            }

            _log.Info("youtube", "YouTube QUIC fix: домены добавлены в list-general-user.txt");

            // The lists are read when winws starts, so restart the bypass.
            if (_winws.IsRunning && !_winws.IsExternal)
            {
                await _stopBypass();
                await _startBypass();
            }

            YoutubeTitle = "YouTube QUIC fix применён";
            YoutubeMessage =
                "Домены YouTube добавлены в обход QUIC. Теперь отключите QUIC/HTTP3 в браузере: " +
                "Chrome/Edge — chrome://flags/#enable-quic → Disabled, " +
                "Firefox — about:config → network.http.http3.enable = false. Затем перезапустите браузер.";
            YoutubeHasResult = true;

            _toast("YouTube QUIC fix применён. Отключите QUIC в браузере.", false);
        }
        catch (Exception ex)
        {
            _log.Warn("youtube", "Ошибка YouTube QUIC fix: " + ex.Message);
            _toast("Не удалось применить QUIC fix: " + ex.Message, true);
        }
        finally
        {
            IsYoutubeFixing = false;
        }
    }

    private void OpenInstallFolder()
    {
        var path = _scanner.Current?.RootPath;
        if (!string.IsNullOrWhiteSpace(path))
            ShellHelper.OpenFolder(path);
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
