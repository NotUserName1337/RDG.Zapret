using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class DiagnosticsService
{
    private readonly LogService _log;

    public DiagnosticsService(LogService log) => _log = log;

    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(
        ZapretInstallation? installation,
        CancellationToken cancellationToken = default)
    {
        var items = new List<DiagnosticItem>();

        var isAdmin = IsAdministrator();
        items.Add(new DiagnosticItem(
            "Права администратора",
            isAdmin ? "Приложение запущено от имени администратора" : "Нужны права администратора для WinDivert",
            isAdmin ? DiagnosticStatus.Ok : DiagnosticStatus.Fail));

        items.Add(installation is not null
            ? new DiagnosticItem("Установка zapret", installation.RootPath, DiagnosticStatus.Ok)
            : new DiagnosticItem("Установка zapret", "Папка не выбрана или не распознана", DiagnosticStatus.Fail));

        if (installation is not null)
        {
            var engines = new List<string>();
            if (!string.IsNullOrWhiteSpace(installation.WinwsPath))
                engines.Add($"winws.exe — {installation.PresetCountFor(ZapretEngine.Zapret)}");
            if (!string.IsNullOrWhiteSpace(installation.Winws2Path))
                engines.Add($"winws2.exe — {installation.PresetCountFor(ZapretEngine.Zapret2)}");

            items.Add(new DiagnosticItem(
                "Движок обхода",
                $"{string.Join(" · ", engines)} · {installation.Presets.Count} " +
                $"{Plural.Ru(installation.Presets.Count, "стратегия", "стратегии", "стратегий")}",
                DiagnosticStatus.Ok));

            var hasBadPath = installation.RootPath.Any(ch => ch >= 'А' && ch <= 'я' || ch is 'Ё' or 'ё') ||
                             installation.RootPath.Contains(' ');
            var onOneDrive = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OneDrive")) &&
                             installation.RootPath.StartsWith(Environment.GetEnvironmentVariable("OneDrive")!, StringComparison.OrdinalIgnoreCase);

            if (onOneDrive)
                items.Add(new DiagnosticItem("Путь установки", "Папка находится в OneDrive — возможны проблемы", DiagnosticStatus.Warn));
            else if (hasBadPath)
                items.Add(new DiagnosticItem("Путь установки", "В пути есть пробелы или кириллица — рекомендуется C:\\zapret", DiagnosticStatus.Warn));
            else
                items.Add(new DiagnosticItem("Путь установки", "Корректный путь без пробелов и кириллицы", DiagnosticStatus.Ok));
        }

        var bfeTask = QueryServiceAsync("BFE", cancellationToken);
        var winDivertTask = QueryServiceAsync("WinDivert", cancellationToken);
        var winDivert14Task = QueryServiceAsync("WinDivert14", cancellationToken);
        var zapretServiceTask = QueryServiceAsync("zapret", cancellationToken);

        await Task.WhenAll(bfeTask, winDivertTask, winDivert14Task, zapretServiceTask).ConfigureAwait(true);

        var bfe = bfeTask.Result;
        items.Add(bfe.State switch
        {
            ServiceState.Running => new DiagnosticItem("Base Filtering Engine", "Служба BFE работает", DiagnosticStatus.Ok),
            ServiceState.NotFound => new DiagnosticItem("Base Filtering Engine", "Служба BFE не найдена", DiagnosticStatus.Fail),
            _ => new DiagnosticItem("Base Filtering Engine", "Служба BFE остановлена — zapret не сможет фильтровать трафик", DiagnosticStatus.Fail)
        });

        var winDivert = winDivertTask.Result.State == ServiceState.Running || winDivert14Task.Result.State == ServiceState.Running;
        if (winDivert)
        {
            items.Add(new DiagnosticItem("Драйвер WinDivert", "Драйвер загружен и работает", DiagnosticStatus.Ok));
        }
        else if (installation?.HasWinDivertFile == true)
        {
            items.Add(new DiagnosticItem("Драйвер WinDivert", "Драйвер установится автоматически при первом запуске", DiagnosticStatus.Warn));
        }
        else
        {
            items.Add(new DiagnosticItem("Драйвер WinDivert", "Файл WinDivert64.sys не найден в папке zapret", DiagnosticStatus.Fail));
        }

        var zapretService = zapretServiceTask.Result.State;
        if (zapretService == ServiceState.Running)
        {
            items.Add(new DiagnosticItem("Служба zapret",
                "Запущена служба zapret — она может конфликтовать с ручным запуском", DiagnosticStatus.Warn));
        }
        else if (zapretService == ServiceState.Stopped)
        {
            items.Add(new DiagnosticItem("Служба zapret", "Служба установлена, но остановлена", DiagnosticStatus.Info));
        }
        else
        {
            items.Add(new DiagnosticItem("Служба zapret", "Служба автозапуска не установлена — это нормально", DiagnosticStatus.Ok));
        }

        var proxy = ReadProxyState();
        items.Add(proxy.Enabled
            ? new DiagnosticItem("Системный прокси", $"Прокси включён: {proxy.Server}. Убедитесь, что он рабочий", DiagnosticStatus.Warn)
            : new DiagnosticItem("Системный прокси", "Системный прокси выключен", DiagnosticStatus.Ok));

        var hosts = CheckHostsFile();
        items.Add(hosts);

        var summary = items.Count(i => i.Status == DiagnosticStatus.Ok);
        _log.Debug("diagnostics", $"Диагностика: {summary}/{items.Count} проверок без замечаний");

        return items;
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> IsServiceRunningAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync("sc.exe", $"query \"{name}\"", 6000, cancellationToken)
            .ConfigureAwait(false);
        return result.Success &&
               result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) &&
               !result.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ServiceQueryResult> QueryServiceAsync(string name, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("sc.exe", $"query \"{name}\"", 6000, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
            return new ServiceQueryResult(ServiceState.NotFound);

        if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
            return new ServiceQueryResult(ServiceState.Running);

        if (result.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            return new ServiceQueryResult(ServiceState.Stopped);

        return new ServiceQueryResult(ServiceState.Unknown);
    }

    private static (bool Enabled, string Server) ReadProxyState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
                return (false, string.Empty);

            var enabled = key.GetValue("ProxyEnable") is int value && value != 0;
            var server = key.GetValue("ProxyServer") as string ?? string.Empty;
            return (enabled, server);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static DiagnosticItem CheckHostsFile()
    {
        try
        {
            var hosts = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "drivers", "etc", "hosts");

            if (!File.Exists(hosts))
                return new DiagnosticItem("Файл hosts", "Файл hosts не найден", DiagnosticStatus.Warn);

            var hasYouTube = File.ReadLines(hosts)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Any(l => l.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                          l.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));

            return hasYouTube
                ? new DiagnosticItem("Файл hosts", "В hosts есть записи YouTube — они могут мешать", DiagnosticStatus.Warn)
                : new DiagnosticItem("Файл hosts", "Конфликтующих записей нет", DiagnosticStatus.Ok);
        }
        catch (Exception ex)
        {
            return new DiagnosticItem("Файл hosts", "Не удалось проверить: " + ex.Message, DiagnosticStatus.Warn);
        }
    }

    private enum ServiceState
    {
        Unknown,
        Running,
        Stopped,
        NotFound
    }

    private sealed record ServiceQueryResult(ServiceState State);
}
