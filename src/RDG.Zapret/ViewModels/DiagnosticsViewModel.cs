using System.Windows.Input;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject, IPageActivation
{
    private readonly SettingsService _settings;
    private readonly PresetScanner _scanner;
    private readonly DiagnosticsService _diagnostics;
    private readonly DoctorService _doctor;
    private readonly DnsService _dns;
    private readonly BlockClassifier _classifier;
    private readonly UserListService _userLists;
    private readonly WinwsController _winws;
    private readonly LogService _log;
    private readonly Action<string, bool> _toast;

    private bool _isBusy;
    private string _doctorSummary = "Проверка ещё не выполнялась";
    private string _systemSummary = "Проверка ещё не выполнялась";
    private string _blockHost = "youtube.com";
    private bool _hasBlockResult;
    private string _blockTitle = string.Empty;
    private string _blockExplanation = string.Empty;
    private string _blockRecommendation = string.Empty;
    private string? _blockAddress;
    private BlockReason? _blockReason;
    private DnsProvider? _selectedDnsProvider;
    private string _dnsStatusText = "DNS ещё не проверялся";
    private string _dnsBackupText = "Резервной копии нет";
    private bool _isDnsBusy;

    public DiagnosticsViewModel(
        SettingsService settings,
        PresetScanner scanner,
        DiagnosticsService diagnostics,
        DoctorService doctor,
        DnsService dns,
        BlockClassifier classifier,
        UserListService userLists,
        WinwsController winws,
        LogService log,
        Action<string, bool> toast)
    {
        _settings = settings;
        _scanner = scanner;
        _diagnostics = diagnostics;
        _doctor = doctor;
        _dns = dns;
        _classifier = classifier;
        _userLists = userLists;
        _winws = winws;
        _log = log;
        _toast = toast;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        RepairCommand = new AsyncRelayCommand<string>(RepairAsync, key => !IsBusy && !string.IsNullOrWhiteSpace(key));
        DiagnoseCommand = new AsyncRelayCommand(DiagnoseAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(BlockHost));
        OpenNetworkSettingsCommand = new RelayCommand(() => ShellHelper.OpenUrl("ms-settings:network-status"));
        FlushDnsCommand = new AsyncRelayCommand(() => RepairAsync("flush_dns"), () => !IsBusy);
        AddBlockedDomainCommand = new RelayCommand(AddBlockedDomain,
            () => HasBlockResult && !string.IsNullOrWhiteSpace(BlockHost));
        AddBlockedIpCommand = new RelayCommand(AddBlockedIp,
            () => HasBlockResult && !string.IsNullOrWhiteSpace(BlockAddress));
        RepairAllCommand = new AsyncRelayCommand(RepairAllAsync, () => !IsBusy);
        DnsProviders = new ObservableCollection<DnsProvider>(dns.Providers);
        _selectedDnsProvider = DnsProviders.FirstOrDefault(p => p.Id == "cloudflare") ?? DnsProviders.FirstOrDefault();
        ApplyDnsCommand = new AsyncRelayCommand(ApplyDnsAsync, () => !IsDnsBusy && SelectedDnsProvider is not null);
        RestoreDnsCommand = new AsyncRelayCommand(RestoreDnsAsync, () => !IsDnsBusy && HasDnsBackup);
    }

    public ObservableCollection<DoctorSectionViewModel> Sections { get; } = new();
    public ObservableCollection<DiagnosticItemViewModel> SystemChecks { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<string> RepairCommand { get; }
    public AsyncRelayCommand DiagnoseCommand { get; }
    public RelayCommand OpenNetworkSettingsCommand { get; }
    public AsyncRelayCommand FlushDnsCommand { get; }
    public RelayCommand AddBlockedDomainCommand { get; }
    public RelayCommand AddBlockedIpCommand { get; }
    public AsyncRelayCommand RepairAllCommand { get; }
    public ObservableCollection<DnsProvider> DnsProviders { get; }
    public AsyncRelayCommand ApplyDnsCommand { get; }
    public AsyncRelayCommand RestoreDnsCommand { get; }

    public DnsProvider? SelectedDnsProvider
    {
        get => _selectedDnsProvider;
        set
        {
            if (SetProperty(ref _selectedDnsProvider, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string DnsStatusText
    {
        get => _dnsStatusText;
        private set => SetProperty(ref _dnsStatusText, value);
    }

    public string DnsBackupText
    {
        get => _dnsBackupText;
        private set => SetProperty(ref _dnsBackupText, value);
    }

    public bool HasDnsBackup => File.Exists(_dns.BackupPath);

    public bool IsDnsBusy
    {
        get => _isDnsBusy;
        private set
        {
            if (SetProperty(ref _isDnsBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsAdmin => DiagnosticsService.IsAdministrator();
    public string AdminHint => IsAdmin
        ? "Права администратора есть — исправления доступны."
        : "Без прав администратора часть исправлений недоступна.";

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set => SetProperty(ref _doctorSummary, value);
    }

    public string SystemSummary
    {
        get => _systemSummary;
        private set => SetProperty(ref _systemSummary, value);
    }

    public string BlockHost
    {
        get => _blockHost;
        set
        {
            if (SetProperty(ref _blockHost, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasBlockResult
    {
        get => _hasBlockResult;
        private set => SetProperty(ref _hasBlockResult, value);
    }

    public string BlockTitle
    {
        get => _blockTitle;
        private set => SetProperty(ref _blockTitle, value);
    }

    public string BlockExplanation
    {
        get => _blockExplanation;
        private set => SetProperty(ref _blockExplanation, value);
    }

    public string BlockRecommendation
    {
        get => _blockRecommendation;
        private set => SetProperty(ref _blockRecommendation, value);
    }

    public string? BlockAddress
    {
        get => _blockAddress;
        private set => SetProperty(ref _blockAddress, value);
    }

    public BlockReason? BlockReason
    {
        get => _blockReason;
        private set => SetProperty(ref _blockReason, value);
    }

    public Task OnActivatedAsync() => RefreshAsync();

    private Task RefreshAsync() => RefreshCoreAsync(false);

    private Task RefreshAfterRepairAsync() => RefreshCoreAsync(true);

    private async Task RefreshCoreAsync(bool force)
    {
        if (IsBusy && !force)
            return;

        IsBusy = true;
        DoctorSummary = "Проверяю сеть и конфликты…";
        try
        {
            var diagnosticsTask = _diagnostics.RunAsync(_scanner.Current);
            var doctorTask = _doctor.CheckAsync(
                _scanner.Current,
                _winws.ProcessId,
                _winws.IsRunning,
                CancellationToken.None);
            var dnsTask = _dns.GetStatusAsync(CancellationToken.None);

            await Task.WhenAll(diagnosticsTask, doctorTask, dnsTask).ConfigureAwait(true);

            SystemChecks.Clear();
            foreach (var item in diagnosticsTask.Result)
                SystemChecks.Add(new DiagnosticItemViewModel(item));

            var ok = diagnosticsTask.Result.Count(i => i.Status == DiagnosticStatus.Ok);
            var warnings = diagnosticsTask.Result.Count(i => i.Status == DiagnosticStatus.Warn);
            var errors = diagnosticsTask.Result.Count(i => i.Status == DiagnosticStatus.Fail);
            SystemSummary = errors > 0
                ? $"{errors} ошибок · {warnings} предупреждений"
                : warnings > 0 ? $"{ok} в порядке · {warnings} предупреждений" : "Всё в порядке";

            Sections.Clear();
            foreach (var section in doctorTask.Result.Sections)
                Sections.Add(new DoctorSectionViewModel(section));

            DoctorSummary = doctorTask.Result.Summary;
            DnsStatusText = dnsTask.Result.CurrentText;
            DnsBackupText = dnsTask.Result.BackupText;
            var currentProvider = DnsProviders.FirstOrDefault(p =>
                p.Id.Equals(dnsTask.Result.ActiveProviderId, StringComparison.OrdinalIgnoreCase));
            if (currentProvider is not null)
                SelectedDnsProvider = currentProvider;
            OnPropertyChanged(nameof(HasDnsBackup));
        }
        catch (Exception ex)
        {
            DoctorSummary = "Не удалось выполнить проверку: " + ex.Message;
            _log.Warn("diagnostics", "Ошибка доктора: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsAdmin), nameof(AdminHint));
        }
    }

    private async Task RepairAsync(string? action)
    {
        if (string.IsNullOrWhiteSpace(action) || IsBusy)
            return;

        if (action is "stop_conflicts" or "stop_stray_winws" or "stop_zapret_service")
        {
            var question = action switch
            {
                "stop_conflicts" => "Остановить GoodbyeDPI / ByeDPI и другие обходчики?\n\nИх работа будет прервана.",
                "stop_stray_winws" => "Остановить лишние процессы winws?\n\nТекущий обход RDG.Zapret не будет затронут.",
                _ => "Остановить службу zapret?\n\nОбход, запущенный этой службой, перестанет работать."
            };

            var answer = System.Windows.MessageBox.Show(
                question, "RDG.Zapret", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }

        IsBusy = true;
        try
        {
            var result = await _doctor.RepairAsync(
                action,
                _winws.ProcessId,
                _winws.IsRunning,
                CancellationToken.None);
            _toast(result.Message, !result.Success);
            if (result.Success)
                await RefreshAfterRepairAsync();
        }
        catch (Exception ex)
        {
            _toast("Не удалось выполнить действие: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DiagnoseAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var result = await _classifier.DiagnoseAsync(BlockHost, CancellationToken.None);
            BlockHost = result.Host;
            BlockTitle = result.Title;
            BlockExplanation = result.Explanation;
            BlockRecommendation = result.Recommendation;
            BlockAddress = result.Address;
            BlockReason = result.Reason;
            HasBlockResult = true;

            _log.Info("classifier", $"{result.Host}: {result.Reason} — {result.Title}");
        }
        catch (Exception ex)
        {
            BlockTitle = "Не удалось проверить сайт";
            BlockExplanation = ex.Message;
            BlockRecommendation = "Попробуйте ещё раз.";
            BlockAddress = null;
            BlockReason = RDG.Zapret.Models.BlockReason.Unknown;
            HasBlockResult = true;
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task RepairAllAsync()
    {
        if (IsBusy)
            return;

        var answer = System.Windows.MessageBox.Show(
            "Выполнить безопасную починку?\n\n" +
            "Будут остановлены другие обходчики (GoodbyeDPI, ByeDPI и подобные), сброшены DNS/ARP и проверен драйвер WinDivert.\n\n" +
            "Текущий обход RDG.Zapret не будет остановлен.",
            "RDG.Zapret",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsBusy = true;
        DoctorSummary = "Выполняю починку…";
        try
        {
            var actions = new List<string>
            {
                "stop_conflicts",
                "flush_dns",
                "clear_arp",
                "enable_timestamps"
            };

            if (!_winws.IsRunning)
                actions.Add("restart_windivert");

            var ok = 0;
            var messages = new List<string>();
            foreach (var action in actions)
            {
                var result = await _doctor.RepairAsync(
                    action,
                    _winws.ProcessId,
                    _winws.IsRunning,
                    CancellationToken.None);

                if (result.Success) ok++;
                messages.Add(result.Message);
                _log.Info("doctor", $"{action}: {result.Message}");
            }

            _toast($"Починка завершена: {ok} из {actions.Count} шагов", ok < actions.Count);
            await RefreshAfterRepairAsync();
        }
        catch (Exception ex)
        {
            _toast("Ошибка починки: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyDnsAsync()
    {
        var provider = SelectedDnsProvider;
        if (provider is null || IsDnsBusy)
            return;

        var answer = System.Windows.MessageBox.Show(
            $"Сменить DNS на «{provider.Name}»?\n\n" +
            (provider.Servers.Count == 0
                ? "DNS будет получать автоматически от роутера / провайдера."
                : "Серверы: " + string.Join(", ", provider.Servers)) +
            "\n\nТекущие настройки будут сохранены для отката.",
            "RDG.Zapret",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsDnsBusy = true;
        try
        {
            var result = await _dns.ApplyAsync(provider, CancellationToken.None);
            _toast(result.Message, !result.Success);
            if (result.Success)
                await RefreshCoreAsync(true);
        }
        finally
        {
            IsDnsBusy = false;
        }
    }

    private async Task RestoreDnsAsync()
    {
        if (IsDnsBusy)
            return;

        var answer = System.Windows.MessageBox.Show(
            "Вернуть DNS к настройкам, которые были до изменения?",
            "RDG.Zapret",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        IsDnsBusy = true;
        try
        {
            var result = await _dns.RestoreAsync(CancellationToken.None);
            _toast(result.Message, !result.Success);
            if (result.Success)
                await RefreshCoreAsync(true);
        }
        finally
        {
            IsDnsBusy = false;
        }
    }

    private void AddBlockedDomain()
    {
        if (!TryGetRoot(out var root))
            return;

        var result = _userLists.Add(root, _scanner.Engine, UserListKind.DomainBypass, BlockHost);
        _toast(result.Message, !result.Success);
        if (result.Success)
        {
            BlockRecommendation = "Домен добавлен в список обхода. Перезапустите обход, чтобы применить.";
            _log.Info("classifier", "В обход добавлен домен: " + BlockHost);
        }
    }

    private void AddBlockedIp()
    {
        if (!TryGetRoot(out var root) || string.IsNullOrWhiteSpace(BlockAddress))
            return;

        var result = _userLists.Add(root, _scanner.Engine, UserListKind.IpBypass, BlockAddress);
        _toast(result.Message, !result.Success);
        if (result.Success)
        {
            BlockRecommendation = "IP добавлен в список обхода. Перезапустите обход, чтобы применить.";
            _log.Info("classifier", "В обход добавлен IP: " + BlockAddress);
        }
    }

    private bool TryGetRoot(out string root)
    {
        root = _scanner.Current?.RootPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(root))
            return true;
        _toast("Сначала укажите папку zapret", true);
        return false;
    }
}

public sealed class DoctorSectionViewModel
{
    public DoctorSectionViewModel(DoctorSection section)
    {
        Id = section.Id;
        Title = section.Title;
        Subtitle = section.Subtitle;
        Glyph = section.Glyph;
        Status = section.Status;
        Steps = new ObservableCollection<DoctorStepViewModel>(section.Steps.Select(s => new DoctorStepViewModel(s)));
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public DoctorStepStatus Status { get; }
    public ObservableCollection<DoctorStepViewModel> Steps { get; }
    public string StatusText => Status switch
    {
        DoctorStepStatus.Ok => "в порядке",
        DoctorStepStatus.Warn => "нужно внимание",
        DoctorStepStatus.Error => "ошибка",
        DoctorStepStatus.Skipped => "пропущено",
        DoctorStepStatus.Fixed => "исправлено",
        _ => "информация"
    };
}

public sealed class DoctorStepViewModel
{
    public DoctorStepViewModel(DoctorStep step)
    {
        Title = step.Title;
        Message = step.Message;
        Details = step.Details;
        Status = step.Status;
        ActionKey = step.ActionKey;
        ActionLabel = step.ActionLabel;
    }

    public string Title { get; }
    public string Message { get; }
    public string? Details { get; }
    public DoctorStepStatus Status { get; }
    public string? ActionKey { get; }
    public string? ActionLabel { get; }
    public bool HasAction => !string.IsNullOrWhiteSpace(ActionKey);
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
}
