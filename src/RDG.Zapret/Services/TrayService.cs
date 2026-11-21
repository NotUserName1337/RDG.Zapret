using System.Drawing;
using System.Windows.Forms;
using RDG.Zapret.ViewModels;

namespace RDG.Zapret.Services;

/// <summary>Tray icon, context menu and balloon notifications.</summary>
public sealed class TrayService : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly BackgroundMonitor _monitor;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _cancelRecoveryItem;
    private readonly Icon _icon;
    private readonly bool _ownsIcon;
    private bool _closeHintShown;
    private bool _recoveryActive;
    private string _recoveryText = string.Empty;
    private bool _disposed;

    public TrayService(ShellViewModel shell, BackgroundMonitor monitor, Action showWindow, Action exit)
    {
        _shell = shell;
        _monitor = monitor;
        _showWindow = showWindow;
        _exit = exit;

        (_icon, _ownsIcon) = LoadIcon();

        _menu = new ContextMenuStrip();
        _menu.ShowImageMargin = false;

        var openItem = new ToolStripMenuItem("Открыть RDG.Zapret");
        openItem.Click += (_, _) => _showWindow();

        _startItem = new ToolStripMenuItem("Включить обход");
        _startItem.Click += (_, _) => _shell.StartCommand.Execute(null);

        _stopItem = new ToolStripMenuItem("Выключить обход");
        _stopItem.Click += (_, _) => _shell.StopCommand.Execute(null);

        _cancelRecoveryItem = new ToolStripMenuItem("Отменить фоновый подбор");
        _cancelRecoveryItem.Click += (_, _) => _monitor.CancelRecovery();

        var autotuneItem = new ToolStripMenuItem("Автоподбор стратегии");
        autotuneItem.Click += (_, _) =>
        {
            _shell.Navigate("autotune");
            _showWindow();
        };

        var generatorItem = new ToolStripMenuItem("Генератор параметров");
        generatorItem.Click += (_, _) =>
        {
            _shell.Navigate("generator");
            _showWindow();
        };

        var updatesItem = new ToolStripMenuItem("Проверить обновления");
        updatesItem.Click += (_, _) =>
        {
            _shell.Navigate("settings");
            _showWindow();
            _ = _shell.SettingsPage.CheckForUpdatesAsync(silent: false);
        };

        var telegramItem = new ToolStripMenuItem("Telegram-прокси");
        telegramItem.Click += (_, _) =>
        {
            _shell.Navigate("telegram");
            _showWindow();
        };

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => _exit();

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(_cancelRecoveryItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(autotuneItem);
        _menu.Items.Add(generatorItem);
        _menu.Items.Add(telegramItem);
        _menu.Items.Add(updatesItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "RDG.Zapret",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _showWindow();
        };

        _notifyIcon.DoubleClick += (_, _) => _showWindow();

        _shell.PropertyChanged += OnShellPropertyChanged;
        _monitor.RecoveryChanged += OnRecoveryChanged;

        UpdateState();
    }

    public void ShowBalloon(string title, string message, bool isError)
    {
        if (_disposed)
            return;

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = isError ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(7000);
        }
        catch
        {
            // ignored
        }
    }

    public void ShowCloseHint()
    {
        if (_disposed || _closeHintShown)
            return;

        _closeHintShown = true;
        ShowBalloon("RDG.Zapret", "Приложение свёрнуто в трей. Правый клик по иконке — меню, «Выход» — закрыть.", false);
    }

    public void UpdateRecovery(bool active, string text)
    {
        _recoveryActive = active;
        _recoveryText = text;
        UpdateState();
    }

    private void OnRecoveryChanged(bool active, string text) => UpdateRecovery(active, text);

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsRunning)
            or nameof(ShellViewModel.IsBypassBusy)
            or nameof(ShellViewModel.ActiveStrategyName)
            or nameof(ShellViewModel.StatusTitle))
            UpdateState();
    }

    private void UpdateState()
    {
        if (_disposed)
            return;

        try
        {
            var status = _recoveryActive
                ? "фоновый подбор…"
                : _shell.IsRunning
                    ? "обход включён"
                    : "обход выключен";

            _notifyIcon.Text = $"RDG.Zapret · {status}";
            _startItem.Enabled = !_recoveryActive && _shell.StartCommand.CanExecute(null);
            _stopItem.Enabled = !_recoveryActive && _shell.StopCommand.CanExecute(null);
            _cancelRecoveryItem.Visible = _recoveryActive;
            _cancelRecoveryItem.Enabled = _recoveryActive;
            _cancelRecoveryItem.Text = string.IsNullOrWhiteSpace(_recoveryText)
                ? "Отменить фоновый подбор"
                : "Отменить: " + _recoveryText;
        }
        catch
        {
            // ignored
        }
    }

    private static (Icon Icon, bool Owns) LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                    return (icon, true);
            }
        }
        catch
        {
            // ignored
        }

        return ((Icon)SystemIcons.Application.Clone(), true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
            _monitor.RecoveryChanged -= OnRecoveryChanged;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            if (_ownsIcon)
                _icon.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}
