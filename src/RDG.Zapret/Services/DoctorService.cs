using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Safe one-click network doctor: finds competing DPI tools, stray winws
/// processes, DNS/ARP/cache problems and a stale WinDivert driver. Repairs are
/// explicit and never kill the engine owned by RDG.Zapret.
/// </summary>
public sealed class DoctorService
{
    private static readonly string[] ConflictNames = { "goodbyedpi", "byedpi", "ciadpi", "spoofdpi" };
    private static readonly string[] EngineNames = { "winws", "winws2" };

    private readonly LogService _log;

    public DoctorService(LogService log) => _log = log;

    public async Task<DoctorReport> CheckAsync(
        ZapretInstallation? installation,
        int? ownedEnginePid,
        bool engineRunning,
        CancellationToken cancellationToken)
    {
        var conflicts = await CheckConflictsAsync(ownedEnginePid, engineRunning, cancellationToken).ConfigureAwait(false);
        var network = await CheckNetworkAsync(cancellationToken).ConfigureAwait(false);
        var driver = await CheckDriverAsync(installation, engineRunning, cancellationToken).ConfigureAwait(false);

        var sections = new[] { conflicts, network, driver };
        var bad = sections.SelectMany(s => s.Steps)
            .Count(s => s.Status is DoctorStepStatus.Warn or DoctorStepStatus.Error);

        return new DoctorReport
        {
            Sections = sections,
            AllOk = bad == 0,
            Summary = bad == 0
                ? "Всё в порядке: конфликтов и проблем с сетью не найдено."
                : $"Найдено замечаний: {bad}. Посмотрите подсказки ниже."
        };
    }

    private async Task<DoctorSection> CheckConflictsAsync(int? ownedEnginePid, bool engineRunning, CancellationToken cancellationToken)
    {
        var steps = new List<DoctorStep>();

        var conflicts = DescribeProcesses(ConflictNames, null);

        steps.Add(conflicts.Count == 0
            ? new DoctorStep
            {
                Id = "other_dpi",
                Title = "Другие обходчики",
                Status = DoctorStepStatus.Ok,
                Message = "GoodbyeDPI, ByeDPI и подобные программы не запущены."
            }
            : new DoctorStep
            {
                Id = "other_dpi",
                Title = "Другие обходчики",
                Status = DoctorStepStatus.Warn,
                Message = "Мешают: " + string.Join(", ", conflicts),
                Details = "Два обходчика одновременно перехватывают трафик и ломают друг друга.",
                ActionKey = "stop_conflicts",
                ActionLabel = "Остановить мешающие программы"
            });

        var zapretService = await QueryServiceStateAsync("zapret", cancellationToken).ConfigureAwait(false);
        steps.Add(zapretService switch
        {
            ServiceState.Running => new DoctorStep
            {
                Id = "zapret_service",
                Title = "Служба zapret",
                Status = DoctorStepStatus.Warn,
                Message = "Служба zapret запущена и может конфликтовать с приложением.",
                Details = "Остановите службу, если обход запускается из RDG.Zapret.",
                ActionKey = "stop_zapret_service",
                ActionLabel = "Остановить службу"
            },
            ServiceState.Stopped => new DoctorStep
            {
                Id = "zapret_service",
                Title = "Служба zapret",
                Status = DoctorStepStatus.Info,
                Message = "Служба установлена, но остановлена — это нормально."
            },
            _ => new DoctorStep
            {
                Id = "zapret_service",
                Title = "Служба zapret",
                Status = DoctorStepStatus.Ok,
                Message = "Отдельная служба zapret не установлена."
            }
        });

        var strays = engineRunning && ownedEnginePid is null
            ? new List<string>()
            : DescribeProcesses(EngineNames, ownedEnginePid);

        steps.Add(engineRunning && ownedEnginePid is null
            ? new DoctorStep
            {
                Id = "stray_winws",
                Title = "Зависшие winws",
                Status = DoctorStepStatus.Info,
                Message = "Обход уже запущен — процессы winws не затрагиваются.",
                Details = "Если обход запущен другой программой, RDG.Zapret не будет его останавливать."
            }
            : strays.Count == 0
                ? new DoctorStep
                {
                    Id = "stray_winws",
                    Title = "Зависшие winws",
                    Status = DoctorStepStatus.Ok,
                    Message = "Лишних процессов winws / winws2 нет."
                }
                : new DoctorStep
                {
                    Id = "stray_winws",
                    Title = "Зависшие winws",
                    Status = DoctorStepStatus.Warn,
                    Message = "Найдены лишние процессы: " + string.Join(", ", strays),
                    Details = ownedEnginePid is null
                        ? "Приложение не управляет этими процессами."
                        : $"Текущий обход (PID {ownedEnginePid}) не будет затронут.",
                    ActionKey = "stop_stray_winws",
                    ActionLabel = "Остановить лишние процессы"
                });

        return new DoctorSection
        {
            Id = "conflicts",
            Title = "Конфликты и процессы",
            Subtitle = "Другие обходчики и зависшие winws",
            Glyph = "\uE7BA",
            Steps = steps
        };
    }

    private async Task<DoctorSection> CheckNetworkAsync(CancellationToken cancellationToken)
    {
        var steps = new List<DoctorStep>();

        var dnsOk = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var addresses = await Dns.GetHostAddressesAsync("youtube.com", cts.Token).ConfigureAwait(false);
            dnsOk = addresses.Length > 0;
        }
        catch
        {
            dnsOk = false;
        }

        steps.Add(dnsOk
            ? new DoctorStep
            {
                Id = "dns",
                Title = "DNS",
                Status = DoctorStepStatus.Ok,
                Message = "Домены разрешаются нормально."
            }
            : new DoctorStep
            {
                Id = "dns",
                Title = "DNS",
                Status = DoctorStepStatus.Warn,
                Message = "Не удалось разрешить youtube.com.",
                Details = "Возможно, провайдер блокирует DNS или кэш повреждён.",
                ActionKey = "flush_dns",
                ActionLabel = "Сбросить DNS-кэш"
            });

        var internet = await TryTcpAsync("1.1.1.1", 53, cancellationToken).ConfigureAwait(false);
        steps.Add(internet
            ? new DoctorStep
            {
                Id = "internet",
                Title = "Интернет",
                Status = DoctorStepStatus.Ok,
                Message = "Базовое соединение с интернетом работает."
            }
            : new DoctorStep
            {
                Id = "internet",
                Title = "Интернет",
                Status = DoctorStepStatus.Error,
                Message = "Нет прямого TCP-ответа от 1.1.1.1.",
                Details = "Проверьте Wi-Fi/кабель, VPN и настройки прокси."
            });

        var arp = await ProcessRunner.RunAsync("arp.exe", "-a", 5000, cancellationToken).ConfigureAwait(false);
        var arpLines = arp.Success
            ? arp.Output.Split('\n').Count(l => l.Contains("dynamic", StringComparison.OrdinalIgnoreCase) ||
                                               l.Contains("динами", StringComparison.OrdinalIgnoreCase))
            : 0;

        steps.Add(new DoctorStep
        {
            Id = "arp",
            Title = "Таблица ARP",
            Status = DoctorStatus(arp.Success),
            Message = arp.Success
                ? $"Записей в кэше: {arpLines}. При странных обрывах кэш можно очистить."
                : "Не удалось прочитать таблицу ARP.",
            ActionKey = arp.Success ? "clear_arp" : null,
            ActionLabel = arp.Success ? "Очистить ARP-кэш" : null
        });

        var tcpGlobal = await ProcessRunner.RunAsync(
            "netsh.exe", "interface tcp show global", 6000, cancellationToken).ConfigureAwait(false);
        var timestampsKnown = false;
        var timestampsEnabled = false;
        if (tcpGlobal.Success)
        {
            var lines = tcpGlobal.Output.Split('\n');
            foreach (var line in lines)
            {
                if (!line.Contains("timestamp", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("метк", StringComparison.OrdinalIgnoreCase))
                    continue;

                timestampsKnown = true;
                timestampsEnabled = line.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                                    line.Contains("включ", StringComparison.OrdinalIgnoreCase);
            }
        }

        steps.Add(!timestampsKnown
            ? new DoctorStep
            {
                Id = "tcp_timestamps",
                Title = "TCP-метки времени",
                Status = DoctorStepStatus.Info,
                Message = "Не удалось определить состояние TCP-меток.",
                ActionKey = "enable_timestamps",
                ActionLabel = "Включить метки TCP"
            }
            : timestampsEnabled
                ? new DoctorStep
                {
                    Id = "tcp_timestamps",
                    Title = "TCP-метки времени",
                    Status = DoctorStepStatus.Ok,
                    Message = "TCP Timestamps включены — это помогает обходу."
                }
                : new DoctorStep
                {
                    Id = "tcp_timestamps",
                    Title = "TCP-метки времени",
                    Status = DoctorStepStatus.Warn,
                    Message = "TCP Timestamps выключены.",
                    Details = "Некоторые стратегии обхода работают стабильнее с включёнными метками.",
                    ActionKey = "enable_timestamps",
                    ActionLabel = "Включить метки TCP"
                });

        return new DoctorSection
        {
            Id = "network",
            Title = "Сеть и DNS",
            Subtitle = "Разрешение имён, интернет, ARP и TCP",
            Glyph = "\uE774",
            Steps = steps
        };
    }

    private async Task<DoctorSection> CheckDriverAsync(
        ZapretInstallation? installation,
        bool engineRunning,
        CancellationToken cancellationToken)
    {
        var steps = new List<DoctorStep>();

        if (engineRunning)
        {
            steps.Add(new DoctorStep
            {
                Id = "windivert",
                Title = "Драйвер WinDivert",
                Status = DoctorStepStatus.Skipped,
                Message = "Обход запущен — проверка драйвера пропущена.",
                Details = "Остановите обход и повторите проверку, чтобы проверить или перезапустить драйвер."
            });
            return new DoctorSection
            {
                Id = "driver",
                Title = "Драйвер WinDivert",
                Subtitle = "Загрузка драйвера перехвата трафика",
                Glyph = "\uE7BA",
                Steps = steps
            };
        }

        var state = await QueryServiceStateAsync("WinDivert", cancellationToken).ConfigureAwait(false);
        var state14 = await QueryServiceStateAsync("WinDivert14", cancellationToken).ConfigureAwait(false);
        var running = state == ServiceState.Running || state14 == ServiceState.Running;
        var exists = state != ServiceState.NotFound || state14 != ServiceState.NotFound;
        var hasFile = installation?.HasWinDivertFile == true;

        if (running)
        {
            steps.Add(new DoctorStep
            {
                Id = "windivert",
                Title = "Драйвер WinDivert",
                Status = DoctorStepStatus.Ok,
                Message = "Драйвер загружен и работает."
            });
        }
        else if (hasFile)
        {
            steps.Add(new DoctorStep
            {
                Id = "windivert",
                Title = "Драйвер WinDivert",
                Status = DoctorStepStatus.Info,
                Message = "Драйвер загрузится автоматически при запуске обхода.",
                ActionKey = exists ? "restart_windivert" : null,
                ActionLabel = exists ? "Перезапустить драйвер" : null
            });
        }
        else
        {
            steps.Add(new DoctorStep
            {
                Id = "windivert",
                Title = "Драйвер WinDivert",
                Status = DoctorStepStatus.Error,
                Message = "Файл WinDivert64.sys не найден в папке zapret.",
                Details = "Переустановите стратегии или укажите корректную папку zapret в настройках."
            });
        }

        return new DoctorSection
        {
            Id = "driver",
            Title = "Драйвер WinDivert",
            Subtitle = "Загрузка драйвера перехвата трафика",
            Glyph = "\uE7BA",
            Steps = steps
        };
    }

    public async Task<DoctorActionResult> RepairAsync(
        string action,
        int? ownedEnginePid,
        bool engineRunning,
        CancellationToken cancellationToken)
    {
        if (!DiagnosticsService.IsAdministrator() && action is not ("flush_dns" or "clear_arp" or "enable_timestamps"))
            return new DoctorActionResult(false, "Нужны права администратора. Перезапустите RDG.Zapret от имени администратора.");

        switch (action)
        {
            case "stop_conflicts":
            {
                var killed = KillProcesses(ConflictNames, null);
                return killed > 0
                    ? new DoctorActionResult(true, $"Остановлено программ: {killed}.")
                    : new DoctorActionResult(true, "Мешающие программы уже не запущены.");
            }

            case "stop_zapret_service":
            {
                var result = await ProcessRunner.RunAsync("sc.exe", "stop \"zapret\"", 10000, cancellationToken)
                    .ConfigureAwait(false);
                return result.Success
                    ? new DoctorActionResult(true, "Служба zapret остановлена.")
                    : new DoctorActionResult(false, "Не удалось остановить службу zapret: " + result.Output);
            }

            case "stop_stray_winws":
            {
                if (engineRunning)
                    return new DoctorActionResult(false, "Сначала остановите обход кнопкой «Остановить».");
                var killed = KillProcesses(EngineNames, ownedEnginePid);
                return killed > 0
                    ? new DoctorActionResult(true, $"Остановлено лишних процессов: {killed}.")
                    : new DoctorActionResult(true, "Лишних процессов не найдено.");
            }

            case "flush_dns":
            {
                var result = await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", 8000, cancellationToken)
                    .ConfigureAwait(false);
                return result.Success
                    ? new DoctorActionResult(true, "DNS-кэш очищен.")
                    : new DoctorActionResult(false, "Не удалось очистить DNS-кэш.");
            }

            case "clear_arp":
            {
                var result = await ProcessRunner.RunAsync(
                    "netsh.exe", "interface ip delete arpcache", 10000, cancellationToken).ConfigureAwait(false);
                return result.Success
                    ? new DoctorActionResult(true, "ARP-кэш очищен.")
                    : new DoctorActionResult(false, "Не удалось очистить ARP-кэш: " + result.Output);
            }

            case "enable_timestamps":
            {
                var result = await ProcessRunner.RunAsync(
                    "netsh.exe", "interface tcp set global timestamps=enabled", 10000, cancellationToken).ConfigureAwait(false);
                return result.Success
                    ? new DoctorActionResult(true, "TCP Timestamps включены.")
                    : new DoctorActionResult(false, "Не удалось включить TCP Timestamps.");
            }

            case "restart_windivert":
            {
                if (engineRunning)
                    return new DoctorActionResult(false, "Сначала остановите обход — иначе можно потерять сеть.");

                await ProcessRunner.RunAsync("sc.exe", "stop \"WinDivert\"", 8000, cancellationToken).ConfigureAwait(false);
                await ProcessRunner.RunAsync("sc.exe", "stop \"WinDivert14\"", 8000, cancellationToken).ConfigureAwait(false);
                await ProcessRunner.RunAsync("sc.exe", "config \"WinDivert\" start= demand", 8000, cancellationToken).ConfigureAwait(false);
                var start = await ProcessRunner.RunAsync("sc.exe", "start \"WinDivert\"", 8000, cancellationToken)
                    .ConfigureAwait(false);

                if (start.Success)
                    return new DoctorActionResult(true, "Драйвер WinDivert перезапущен.");

                return new DoctorActionResult(true,
                    "Старый драйвер остановлен. Запустите обход — winws загрузит драйвер заново.");
            }

            default:
                return new DoctorActionResult(false, "Неизвестное действие.");
        }
    }

    private async Task<bool> TryTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ServiceState> QueryServiceStateAsync(string name, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("sc.exe", $"query \"{name}\"", 6000, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            return ServiceState.NotFound;

        if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
            return ServiceState.Running;

        if (result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
            return ServiceState.Stopped;

        return ServiceState.NotFound;
    }

    private static List<string> DescribeProcesses(IEnumerable<string> names, int? excludePid)
    {
        var result = new List<string>();
        foreach (var process in names.SelectMany(SafeProcesses))
        {
            try
            {
                if (process.Id == excludePid)
                    continue;
                result.Add($"{process.ProcessName} (PID {process.Id})");
            }
            catch
            {
                // ignored
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static IEnumerable<Process> SafeProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private int KillProcesses(IEnumerable<string> names, int? excludePid)
    {
        var killed = 0;
        foreach (var process in names.SelectMany(SafeProcesses))
        {
            try
            {
                if (process.Id == excludePid || process.HasExited)
                    continue;

                process.Kill(entireProcessTree: true);
                killed++;
                _log.Info("doctor", $"Остановлен процесс {process.ProcessName} (PID {process.Id})");
            }
            catch
            {
                // process may have exited or be protected
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    private static DoctorStepStatus DoctorStatus(bool ok) => ok ? DoctorStepStatus.Ok : DoctorStepStatus.Warn;

    private enum ServiceState
    {
        NotFound,
        Stopped,
        Running
    }
}
