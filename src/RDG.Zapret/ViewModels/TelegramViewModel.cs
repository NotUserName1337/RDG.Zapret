using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Services;

namespace RDG.Zapret.ViewModels;

/// <summary>
/// Page for the bundled TgWsProxy component: local MTProto proxy that carries
/// Telegram traffic over WebSocket. The upstream program owns its own tray icon;
/// this page starts/stops it and exposes the tg:// link.
/// </summary>
public sealed class TelegramViewModel : ObservableObject, IPageActivation, IDisposable
{
    private readonly SettingsService _settings;
    private readonly TgWsProxyService _proxy;
    private readonly LogService _log;
    private readonly Action<string, bool> _toast;
    private readonly DispatcherTimer _timer;

    private TgProxyStatus _status;
    private bool _isBusy;
    private double _progress;
    private string _progressText = string.Empty;
    private string _portText;
    private bool _isLinkVisible;
    private bool _isSecretVisible;
    private CancellationTokenSource? _cts;

    public TelegramViewModel(
        SettingsService settings,
        TgWsProxyService proxy,
        LogService log,
        Action<string, bool> toast)
    {
        _settings = settings;
        _proxy = proxy;
        _log = log;
        _toast = toast;
        _status = proxy.GetStatus();
        _portText = proxy.Port.ToString();

        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => !IsBusy && !IsExternal);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy);
        OpenTelegramCommand = new AsyncRelayCommand(OpenTelegramAsync, () => !IsBusy);
        CopyLinkCommand = new RelayCommand(CopyLink);
        CopySecretCommand = new RelayCommand(CopySecret);
        SavePortCommand = new AsyncRelayCommand(SavePortAsync, () => !IsBusy && !IsExternal);
        RotateSecretCommand = new AsyncRelayCommand(RotateSecretAsync, () => !IsBusy && !IsExternal);
        ToggleLinkCommand = new RelayCommand(() => IsLinkVisible = !IsLinkVisible);
        ToggleSecretCommand = new RelayCommand(() => IsSecretVisible = !IsSecretVisible);
        OpenFolderCommand = new RelayCommand(() => ShellHelper.OpenFolder(_proxy.InstallDirectory));
        OpenLogCommand = new RelayCommand(OpenLog);
        OpenGitHubCommand = new RelayCommand(() => ShellHelper.OpenUrl("https://github.com/Flowseal/tg-ws-proxy"));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    public AsyncRelayCommand ToggleCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand OpenTelegramCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public RelayCommand CopySecretCommand { get; }
    public AsyncRelayCommand SavePortCommand { get; }
    public AsyncRelayCommand RotateSecretCommand { get; }
    public RelayCommand ToggleLinkCommand { get; }
    public RelayCommand ToggleSecretCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand OpenGitHubCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(IsIdle), nameof(IsProgressVisible));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !IsBusy;

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
                OnPropertyChanged(nameof(IsProgressVisible));
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool IsProgressVisible => IsBusy && Progress > 0;

    public bool IsRunning => _status.IsRunning;
    public bool IsManaged => _status.IsManaged;
    public bool IsExternal => _status.IsExternal;

    public bool IsInstalled => _proxy.IsInstalled;

    public string StatusTitle => _status.State switch
    {
        TgProxyState.RunningManaged => "Работает",
        TgProxyState.RunningExternal => "Внешний процесс",
        TgProxyState.NotInstalled => "Не установлен",
        _ => "Выключен"
    };

    public string StatusPill => _status.State switch
    {
        TgProxyState.RunningManaged => "ONLINE",
        TgProxyState.RunningExternal => "EXTERNAL",
        TgProxyState.NotInstalled => "SETUP",
        _ => "OFFLINE"
    };

    public string StatusColor => _status.State switch
    {
        TgProxyState.RunningManaged => "#34D399",
        TgProxyState.RunningExternal => "#60A5FA",
        TgProxyState.NotInstalled => "#FBBF24",
        _ => "#93A4BA"
    };

    public string StatusDetail => _status.Message;

    public string PortLabel => "127.0.0.1:" + _proxy.Port;

    public string ToggleText => IsManaged
        ? "Остановить прокси"
        : IsExternal
            ? "Прокси уже запущен"
            : "Включить прокси";

    public string InstalledVersionText => _proxy.IsInstalled
        ? _proxy.GetInstalledVersion()
        : "не установлен";

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value);
    }

    public bool IsLinkVisible
    {
        get => _isLinkVisible;
        set
        {
            if (!SetProperty(ref _isLinkVisible, value))
                return;
            OnPropertyChanged(nameof(LinkDisplay), nameof(LinkToggleText));
        }
    }

    public bool IsSecretVisible
    {
        get => _isSecretVisible;
        set
        {
            if (!SetProperty(ref _isSecretVisible, value))
                return;
            OnPropertyChanged(nameof(SecretDisplay), nameof(SecretToggleText));
        }
    }

    public string Link => _proxy.BuildLink();

    public string LinkDisplay => IsLinkVisible
        ? Link
        : $"tg://proxy?server=127.0.0.1&port={_proxy.Port}&secret=dd••••••••••••••••";

    public string LinkToggleText => IsLinkVisible ? "Скрыть ссылку" : "Показать ссылку";

    public string SecretDisplay => IsSecretVisible
        ? _proxy.Secret
        : new string('•', 24) + "  " + _proxy.Secret[..4] + "…";

    public string SecretToggleText => IsSecretVisible ? "Скрыть секрет" : "Показать секрет";

    public bool AutoStart
    {
        get => _settings.Current.TgProxyAutoStart;
        set
        {
            if (value == _settings.Current.TgProxyAutoStart)
                return;
            _settings.Update(s => s.TgProxyAutoStart = value);
            OnPropertyChanged();
        }
    }

    public bool StopOnExit
    {
        get => _settings.Current.TgProxyStopOnExit;
        set
        {
            if (value == _settings.Current.TgProxyStopOnExit)
                return;
            _settings.Update(s => s.TgProxyStopOnExit = value);
            OnPropertyChanged();
        }
    }

    public Task OnActivatedAsync()
    {
        RefreshStatus();
        return Task.CompletedTask;
    }

    public async Task TryAutoStartAsync()
    {
        if (!_settings.Current.TgProxyAutoStart)
            return;

        if (!_proxy.IsInstalled)
            return;

        if (_proxy.GetStatus().IsRunning)
        {
            RefreshStatus();
            return;
        }

        var result = await _proxy.StartAsync(CancellationToken.None);
        if (result.Success)
            _log.Info("tgproxy", "Telegram-прокси автоматически запущен");
        else
            _log.Warn("tgproxy", "Автозапуск Telegram-прокси не удался: " + result.Error);

        RefreshStatus();
    }

    private async Task ToggleAsync()
    {
        if (IsBusy)
            return;

        var status = _proxy.GetStatus();
        if (status.State == TgProxyState.RunningExternal)
        {
            _toast("Прокси уже запущен отдельным TgWsProxy. Закройте его через иконку в трее.", true);
            RefreshStatus();
            return;
        }

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            if (status.State == TgProxyState.RunningManaged)
            {
                Progress = 0;
                ProgressText = "Останавливаю прокси…";
                await _proxy.StopAsync();
                _toast("Telegram-прокси остановлен", false);
                return;
            }

            Progress = 0;
            ProgressText = _proxy.IsInstalled
                ? "Запускаю TgWsProxy…"
                : "Устанавливаю TgWsProxy…";

            if (!_proxy.IsInstalled)
            {
                var progress = new Progress<DownloadProgress>(update =>
                {
                    Progress = update.Percent;
                    ProgressText = update.Status;
                });

                var install = await _proxy.EnsureInstalledAsync(progress, _cts.Token);
                if (!install.Success)
                {
                    _toast("Не удалось установить TgWsProxy: " + (install.Error ?? "ошибка"), true);
                    return;
                }
            }

            ProgressText = "Открываю порт…";
            var result = await _proxy.StartAsync(_cts.Token);
            if (result.Success)
            {
                _toast("Telegram-прокси включён. Откройте ссылку в Telegram.", false);
            }
            else
            {
                _toast(result.Error ?? "Не удалось запустить Telegram-прокси", true);
            }
        }
        catch (OperationCanceledException)
        {
            _toast("Операция отменена", false);
        }
        finally
        {
            ProgressText = string.Empty;
            Progress = 0;
            IsBusy = false;
            RefreshStatus();
        }
    }

    private async Task DownloadAsync()
    {
        if (IsBusy)
            return;

        var wasRunning = _proxy.GetStatus().IsManaged;
        if (wasRunning)
        {
            var answer = MessageBox.Show(
                "Для обновления TgWsProxy текущий прокси будет остановлен, затем запущен снова.\n\nПродолжить?",
                "RDG.Zapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Progress = 0;

        try
        {
            var progress = new Progress<DownloadProgress>(update =>
            {
                Progress = update.Percent;
                ProgressText = update.Status;
            });

            var result = await _proxy.UpdateAsync(progress, _cts.Token);
            if (!result.Success)
            {
                _toast("Не удалось скачать TgWsProxy: " + (result.Error ?? "ошибка"), true);
                return;
            }

            _toast("TgWsProxy установлен: " + (result.Version ?? "готово"), false);

            if (wasRunning)
            {
                ProgressText = "Запускаю прокси…";
                var start = await _proxy.StartAsync(_cts.Token);
                if (!start.Success)
                    _toast(start.Error ?? "Прокси не запустился", true);
            }
        }
        catch (OperationCanceledException)
        {
            _toast("Загрузка отменена", false);
        }
        finally
        {
            ProgressText = string.Empty;
            Progress = 0;
            IsBusy = false;
            RefreshStatus();
        }
    }

    private async Task SavePortAsync()
    {
        if (_proxy.GetStatus().IsExternal)
        {
            _toast("Порт задаётся внешним TgWsProxy. Закройте его, чтобы изменить настройку.", true);
            return;
        }

        if (!int.TryParse(PortText.Trim(), out var port) || port < 1024 || port > 65535)
        {
            _toast("Порт должен быть числом от 1024 до 65535", true);
            PortText = _proxy.Port.ToString();
            return;
        }

        if (port == _proxy.Port)
            return;

        var wasRunning = _proxy.GetStatus().IsManaged;
        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            if (wasRunning)
            {
                ProgressText = "Перезапускаю прокси на новом порту…";
                await _proxy.StopAsync();
            }

            _settings.Update(s => s.TgProxyPort = port);

            if (wasRunning)
            {
                var start = await _proxy.StartAsync(_cts.Token);
                if (!start.Success)
                {
                    _toast(start.Error ?? "Не удалось запустить прокси на новом порту", true);
                    return;
                }
            }

            _toast($"Порт изменён на {port}. Обновите прокси в Telegram.", false);
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
            RefreshStatus();
        }
    }

    private async Task RotateSecretAsync()
    {
        if (IsBusy)
            return;

        if (_proxy.GetStatus().IsExternal)
        {
            _toast("Секрет задаётся внешним TgWsProxy. Закройте его, чтобы изменить секрет.", true);
            return;
        }

        var answer = MessageBox.Show(
            "Сгенерировать новый секрет?\n\n" +
            "После этого старую настройку прокси в Telegram нужно заменить новой ссылкой.",
            "RDG.Zapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        var wasRunning = _proxy.GetStatus().IsManaged;
        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            if (wasRunning)
                await _proxy.StopAsync();

            _proxy.RegenerateSecret();

            if (wasRunning)
            {
                ProgressText = "Перезапускаю прокси с новым секретом…";
                var start = await _proxy.StartAsync(_cts.Token);
                if (!start.Success)
                    _toast(start.Error ?? "Прокси не запустился", true);
            }

            RefreshLinkProperties();
            _toast("Секрет обновлён. Подключите прокси в Telegram заново.", false);
        }
        finally
        {
            ProgressText = string.Empty;
            IsBusy = false;
            RefreshStatus();
        }
    }

    private async Task OpenTelegramAsync()
    {
        if (!_proxy.GetStatus().IsRunning)
        {
            _toast("Сначала включите Telegram-прокси", true);
            return;
        }

        RefreshLinkProperties();

        if (_proxy.OpenInTelegram())
        {
            _toast("Открываю Telegram…", false);
            return;
        }

        try
        {
            Clipboard.SetText(Link);
            _toast("Telegram не открылся. Ссылка скопирована в буфер обмена.", false);
        }
        catch
        {
            _toast("Не удалось открыть Telegram. Скопируйте ссылку вручную.", true);
        }

        await Task.CompletedTask;
    }

    private void CopyLink()
    {
        try
        {
            RefreshLinkProperties();
            Clipboard.SetText(Link);
            _toast("Ссылка для Telegram скопирована", false);
        }
        catch
        {
            _toast("Не удалось скопировать ссылку", true);
        }
    }

    private void CopySecret()
    {
        try
        {
            Clipboard.SetText(_proxy.Secret);
            _toast("Secret скопирован", false);
        }
        catch
        {
            _toast("Не удалось скопировать secret", true);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_proxy.LogPath))
            {
                _toast("Журнал TgWsProxy ещё не создан", true);
                return;
            }

            Process.Start(new ProcessStartInfo(_proxy.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast("Не удалось открыть журнал: " + ex.Message, true);
        }
    }

    private void RefreshStatus()
    {
        var status = _proxy.GetStatus();
        var changed = status.State != _status.State ||
                      status.Port != _status.Port ||
                      !string.Equals(status.ProcessName, _status.ProcessName, StringComparison.Ordinal);

        _status = status;

        OnPropertyChanged(
            nameof(IsRunning),
            nameof(IsManaged),
            nameof(IsExternal),
            nameof(IsInstalled),
            nameof(StatusTitle),
            nameof(StatusPill),
            nameof(StatusColor),
            nameof(StatusDetail),
            nameof(PortLabel),
            nameof(ToggleText),
            nameof(InstalledVersionText));

        if (changed)
            RefreshLinkProperties();

        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshLinkProperties()
    {
        if (int.TryParse(PortText.Trim(), out var typed) && typed != _proxy.Port)
            PortText = _proxy.Port.ToString();

        OnPropertyChanged(nameof(Link), nameof(LinkDisplay), nameof(SecretDisplay));
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
