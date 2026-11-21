using System.Text;
using System.Windows.Media;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public static class AccentService
{
    private static readonly Dictionary<string, (string From, string To)> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["violet"] = ("#7C5CFF", "#22D3EE"),
        ["ocean"] = ("#2F80ED", "#56CCF2"),
        ["emerald"] = ("#10B981", "#34D399"),
        ["sunset"] = ("#F97316", "#F43F5E")
    };

    public static IReadOnlyDictionary<string, (string From, string To)> All => Accents;

    public static (string From, string To) Get(string? key) =>
        key is not null && Accents.TryGetValue(key, out var colors) ? colors : Accents["violet"];

    public static void Apply(string? key)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        var (from, to) = Get(key);
        var c1 = (Color)ColorConverter.ConvertFromString(from);
        var c2 = (Color)ColorConverter.ConvertFromString(to);

        var accent = new SolidColorBrush(c1);
        accent.Freeze();

        var soft = new SolidColorBrush(Color.FromArgb(0x26, c1.R, c1.G, c1.B));
        soft.Freeze();

        var glow = new SolidColorBrush(Color.FromArgb(0x66, c1.R, c1.G, c1.B));
        glow.Freeze();

        var gradient = new LinearGradientBrush(c1, c2, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        gradient.Freeze();

        var gradientSoft = new LinearGradientBrush(
            Color.FromArgb(0x33, c1.R, c1.G, c1.B),
            Color.FromArgb(0x14, c2.R, c2.G, c2.B),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(1, 1));
        gradientSoft.Freeze();

        var resources = app.Resources;
        resources["AccentBrush"] = accent;
        resources["AccentSoftBrush"] = soft;
        resources["AccentGlowBrush"] = glow;
        resources["AccentGradient"] = gradient;
        resources["AccentGradientSoft"] = gradientSoft;
    }
}

public sealed record OperationOutcome(bool Success, string Message)
{
    public static OperationOutcome Ok(string message = "") => new(true, message);
    public static OperationOutcome Fail(string message) => new(false, message);
}

public sealed class AutostartService
{
    private const string TaskName = "RDG.Zapret";
    private readonly SettingsService _settings;
    private readonly LogService _log;

    public AutostartService(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await Infrastructure.ProcessRunner
            .RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"", 8000, cancellationToken)
            .ConfigureAwait(false);
        return result.Success;
    }

    public async Task<OperationOutcome> EnableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return OperationOutcome.Fail("Не удалось определить путь к RDG.Zapret.exe");

            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var userId = identity.User?.Value ?? identity.Name;
            var escapedUser = System.Security.SecurityElement.Escape(userId) ?? userId;
            var escapedExe = System.Security.SecurityElement.Escape(exe) ?? exe;

            var xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>RDG.Zapret — запуск обхода DPI при входе в Windows</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{escapedUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escapedExe}</Command>
      <Arguments>--autostart --minimized</Arguments>
    </Exec>
  </Actions>
</Task>
""";

            Directory.CreateDirectory(_settings.DirectoryPath);
            var xmlPath = Path.Combine(_settings.DirectoryPath, "autostart-task.xml");
            await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);

            var result = await Infrastructure.ProcessRunner
                .RunAsync("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", 15000, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.Delete(xmlPath);
            }
            catch
            {
                // ignored
            }

            if (!result.Success)
            {
                _log.Warn("autostart", "Не удалось создать задачу: " + result.Output);
                return OperationOutcome.Fail(result.Output);
            }

            _log.Success("autostart", "Автозапуск включён (Планировщик задач Windows)");
            return OperationOutcome.Ok();
        }
        catch (Exception ex)
        {
            _log.Error("autostart", "Ошибка включения автозапуска: " + ex.Message);
            return OperationOutcome.Fail(ex.Message);
        }
    }

    public async Task<OperationOutcome> DisableAsync(CancellationToken cancellationToken = default)
    {
        var result = await Infrastructure.ProcessRunner
            .RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", 10000, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            _log.Info("autostart", "Автозапуск отключён");
            return OperationOutcome.Ok();
        }

        // Deleting a non-existent task is not an error for our toggle.
        if (result.Output.Contains("не удалось", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("не найден", StringComparison.OrdinalIgnoreCase))
            return OperationOutcome.Ok();

        return OperationOutcome.Fail(result.Output);
    }
}
