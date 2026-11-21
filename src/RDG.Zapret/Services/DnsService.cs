using System.Text;
using System.Text.Json;
using RDG.Zapret.Infrastructure;

namespace RDG.Zapret.Services;

public sealed record DnsProvider(string Id, string Name, string Description, IReadOnlyList<string> Servers);

public sealed record DnsChangeResult(bool Success, string Message);

public sealed class DnsStatus
{
    public string CurrentText { get; init; } = "Не удалось определить";
    public string ActiveProviderId { get; init; } = string.Empty;
    public IReadOnlyList<string> Adapters { get; init; } = Array.Empty<string>();
    public bool HasBackup { get; init; }
    public string BackupText { get; init; } = "Резервной копии нет";
}

/// <summary>
/// Changes DNS servers on active physical adapters through the standard
/// Windows DnsClient cmdlets. The previous state is always saved first.
/// </summary>
public sealed class DnsService
{
    private readonly LogService _log;

    public DnsService(LogService log) => _log = log;

    public IReadOnlyList<DnsProvider> Providers { get; } = new List<DnsProvider>
    {
        new("dhcp", "Автоматически", "DNS от роутера / провайдера", Array.Empty<string>()),
        new("cloudflare", "Cloudflare", "1.1.1.1 · 1.0.0.1", new[] { "1.1.1.1", "1.0.0.1" }),
        new("google", "Google", "8.8.8.8 · 8.8.4.4", new[] { "8.8.8.8", "8.8.4.4" }),
        new("quad9", "Quad9", "9.9.9.9 · 149.112.112.112", new[] { "9.9.9.9", "149.112.112.112" })
    };

    public string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RDG.Zapret", "dns-backup.json");

    public async Task<DnsStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var script = @"
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
$rows = Get-DnsClientServerAddress -AddressFamily IPv4 |
    Select-Object InterfaceAlias, ServerAddresses
$rows | ConvertTo-Json -Compress
";
        var run = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (!run.Success)
            return new DnsStatus { CurrentText = "Не удалось прочитать DNS" };

        try
        {
            var entries = ParseEntries(run.Output);
            var withServers = entries
                .Where(e => e.Servers.Count > 0)
                .GroupBy(e => e.Adapter, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var provider = Providers.FirstOrDefault(p =>
                p.Servers.Count > 0 &&
                withServers.Any(e => SameServers(p.Servers, e.Servers)));

            var text = withServers.Count == 0
                ? "Автоматически (DHCP)"
                : string.Join(" · ", withServers.Select(e =>
                    $"{e.Adapter}: {string.Join(", ", e.Servers)}"));

            return new DnsStatus
            {
                CurrentText = text,
                ActiveProviderId = provider?.Id ?? (withServers.Count == 0 ? "dhcp" : string.Empty),
                Adapters = withServers.Select(e => e.Adapter).ToList(),
                HasBackup = File.Exists(BackupPath),
                BackupText = File.Exists(BackupPath)
                    ? "Резервная копия есть — можно вернуть как было"
                    : "Резервной копии нет"
            };
        }
        catch (Exception ex)
        {
            _log.Debug("dns", "Не удалось разобрать DNS: " + ex.Message);
            return new DnsStatus { CurrentText = "Не удалось прочитать DNS" };
        }
    }

    public async Task<DnsChangeResult> ApplyAsync(DnsProvider provider, CancellationToken cancellationToken)
    {
        if (!DiagnosticsService.IsAdministrator())
            return new DnsChangeResult(false, "Нужны права администратора для смены DNS.");

        try
        {
            var adapters = await GetActiveAdaptersAsync(cancellationToken).ConfigureAwait(false);
            if (adapters.Count == 0)
                return new DnsChangeResult(false, "Не найден активный сетевой адаптер.");

            if (!File.Exists(BackupPath))
                await SaveBackupAsync(cancellationToken).ConfigureAwait(false);

            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            foreach (var adapter in adapters)
            {
                var quoted = Quote(adapter);
                if (provider.Servers.Count == 0)
                    script.AppendLine($"Set-DnsClientServerAddress -InterfaceAlias {quoted} -ResetServerAddresses");
                else
                    script.AppendLine(
                        $"Set-DnsClientServerAddress -InterfaceAlias {quoted} -ServerAddresses @({string.Join(",", provider.Servers.Select(Quote))})");
            }
            script.AppendLine("ipconfig /flushdns | Out-Null");

            var run = await RunPowerShellAsync(script.ToString(), cancellationToken).ConfigureAwait(false);
            if (!run.Success)
                return new DnsChangeResult(false, "Не удалось сменить DNS: " + Trim(run.Output));

            _log.Info("dns", $"DNS изменён на {provider.Name}: {string.Join(", ", provider.Servers)}");
            return new DnsChangeResult(true,
                provider.Servers.Count == 0
                    ? "DNS переключён на автоматический (DHCP)."
                    : $"DNS переключён на {provider.Name}.");
        }
        catch (Exception ex)
        {
            _log.Warn("dns", "Ошибка смены DNS: " + ex.Message);
            return new DnsChangeResult(false, "Ошибка смены DNS: " + ex.Message);
        }
    }

    public async Task<DnsChangeResult> RestoreAsync(CancellationToken cancellationToken)
    {
        if (!DiagnosticsService.IsAdministrator())
            return new DnsChangeResult(false, "Нужны права администратора для смены DNS.");

        if (!File.Exists(BackupPath))
            return new DnsChangeResult(false, "Резервной копии DNS нет.");

        try
        {
            var json = File.ReadAllText(BackupPath, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize<List<DnsBackupEntry>>(json) ?? new List<DnsBackupEntry>();
            if (entries.Count == 0)
                return new DnsChangeResult(false, "Резервная копия пуста.");

            var script = new StringBuilder();
            script.AppendLine("$ErrorActionPreference = 'Stop'");
            foreach (var entry in entries)
            {
                var quoted = Quote(entry.Adapter);
                if (entry.Servers.Count == 0)
                    script.AppendLine($"Set-DnsClientServerAddress -InterfaceAlias {quoted} -ResetServerAddresses");
                else
                    script.AppendLine(
                        $"Set-DnsClientServerAddress -InterfaceAlias {quoted} -ServerAddresses @({string.Join(",", entry.Servers.Select(Quote))})");
            }
            script.AppendLine("ipconfig /flushdns | Out-Null");

            var run = await RunPowerShellAsync(script.ToString(), cancellationToken).ConfigureAwait(false);
            if (!run.Success)
                return new DnsChangeResult(false, "Не удалось вернуть DNS: " + Trim(run.Output));

            _log.Info("dns", "DNS восстановлен из резервной копии");
            return new DnsChangeResult(true, "DNS возвращён к прежним настройкам.");
        }
        catch (Exception ex)
        {
            return new DnsChangeResult(false, "Ошибка восстановления DNS: " + ex.Message);
        }
    }

    private async Task SaveBackupAsync(CancellationToken cancellationToken)
    {
        var script = @"
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
Get-DnsClientServerAddress -AddressFamily IPv4 |
    Select-Object InterfaceAlias, ServerAddresses |
    ConvertTo-Json -Compress
";
        var run = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (!run.Success)
            throw new InvalidOperationException("не удалось сохранить текущие DNS");

        var entries = ParseEntries(run.Output)
            .GroupBy(e => e.Adapter, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var directory = Path.GetDirectoryName(BackupPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(BackupPath,
            JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private async Task<List<string>> GetActiveAdaptersAsync(CancellationToken cancellationToken)
    {
        const string script = @"
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
Get-NetAdapter -Physical | Where-Object Status -eq 'Up' |
    Select-Object -ExpandProperty Name
";
        var run = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (!run.Success)
            return new List<string>();

        return run.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DnsBackupEntry> ParseEntries(string json)
    {
        var result = new List<DnsBackupEntry>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            AddEntry(root, result);
            return result;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in root.EnumerateArray())
            AddEntry(item, result);

        return result;
    }

    private static void AddEntry(JsonElement item, List<DnsBackupEntry> result)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        var adapter = item.TryGetProperty("InterfaceAlias", out var alias) ? alias.GetString() : null;
        if (string.IsNullOrWhiteSpace(adapter))
            return;

        var servers = new List<string>();
        if (item.TryGetProperty("ServerAddresses", out var values))
        {
            if (values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                    if (value.GetString() is { Length: > 0 } text)
                        servers.Add(text);
            }
            else if (values.ValueKind == JsonValueKind.String && values.GetString() is { Length: > 0 } single)
            {
                servers.Add(single);
            }
        }

        result.Add(new DnsBackupEntry { Adapter = adapter, Servers = servers });
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "RDG.Zapret-dns-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
            return await ProcessRunner.RunAsync(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
                20000,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignored */ }
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool SameServers(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count &&
        a.Select(v => v.Trim()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(b.Select(v => v.Trim()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    private static string Trim(string value)
    {
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length > 160 ? value[..160] + "…" : value;
    }

    private sealed class DnsBackupEntry
    {
        public string Adapter { get; set; } = string.Empty;
        public List<string> Servers { get; set; } = new();
    }
}
