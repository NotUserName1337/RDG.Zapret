namespace RDG.Zapret.Models;

public enum ProbeStatus
{
    Unknown,
    Checking,
    Available,
    Partial,
    Blocked,
    Timeout,
    Dns,
    Tls,
    Error
}

public sealed record ServiceTarget(
    string Id,
    string Name,
    string Subtitle,
    string Url,
    string TlsHost,
    string Badge,
    string ColorFrom,
    string ColorTo,
    int Weight);

public sealed class ProbeResult
{
    public required string ServiceId { get; init; }
    public ProbeStatus Status { get; init; }
    public int? LatencyMs { get; init; }
    public int? DnsMs { get; init; }
    public int? TcpMs { get; init; }
    public int? TtfbMs { get; init; }
    public string Detail { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public bool IsSuccess => Status is ProbeStatus.Available or ProbeStatus.Partial;

    public bool HasMetrics => DnsMs is not null || TcpMs is not null || TtfbMs is not null;

    public string MetricsText
    {
        get
        {
            var parts = new List<string>(3);
            if (DnsMs is int dns) parts.Add($"DNS {dns} мс");
            if (TcpMs is int tcp) parts.Add($"TCP {tcp} мс");
            if (TtfbMs is int ttfb) parts.Add($"TTFB {ttfb} мс");
            return string.Join(" · ", parts);
        }
    }

    public static ProbeResult Unknown(string serviceId) => new()
    {
        ServiceId = serviceId,
        Status = ProbeStatus.Unknown,
        Detail = "Проверка ещё не выполнялась"
    };
}

public sealed class ProbeOptions
{
    public int TimeoutSeconds { get; init; } = 5;
    public bool ClassifyWithTls { get; init; } = true;

    /// <summary>Measure DNS / TCP / TLS-TTFB phases. Used for manual checks, not autotune.</summary>
    public bool MeasurePhases { get; init; }

    /// <summary>Fast reachability check for the autotune screening phase (no video body download).</summary>
    public bool ScreenOnly { get; init; }

    /// <summary>Skip the Discord WebSocket per-strategy probe to avoid gateway rate limits during autotune.</summary>
    public bool LightDiscord { get; init; }
}

public static class ServiceCatalog
{
    public static IReadOnlyList<ServiceTarget> BuiltIn { get; } = new List<ServiceTarget>
    {
        new("youtube", "YouTube", "Видео и музыка", "https://www.youtube.com/generate_204", "www.youtube.com", "YT", "#FF4D5A", "#C81E3A", 3),
        new("discord", "Discord", "Чаты и звонки", "https://discord.com/api/v10/gateway", "discord.com", "DC", "#7C8CFF", "#4B55D9", 3),
        new("google", "Google", "Поиск и сервисы", "https://www.google.com/generate_204", "www.google.com", "G", "#5B8DEF", "#2E5FC7", 1),
        new("cloudflare", "Cloudflare", "CDN и инфраструктура", "https://cloudflare.com/cdn-cgi/trace", "cloudflare.com", "CF", "#F59E0B", "#D97706", 1)
    };

    private static IReadOnlyList<ServiceTarget> _all = BuiltIn.ToList();

    public static IReadOnlyList<ServiceTarget> All => _all;

    public static event EventHandler? Changed;

    /// <summary>Probed as part of the deep Discord check, not shown as a separate card.</summary>
    public static ServiceTarget DiscordVoice { get; } =
        new("discord_voice", "Discord Voice", "Голосовые каналы", "https://voice.discord.gg/", "voice.discord.gg", "DV", "#8B5CF6", "#6D28D9", 0);

    public static ServiceTarget ById(string id) =>
        All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? (DiscordVoice.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ? DiscordVoice : null)
        ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown service id");

    public static ServiceTarget? TryById(string id) =>
        All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? (DiscordVoice.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ? DiscordVoice : null);

    public static void SetCustomServices(IEnumerable<CustomService> custom)
    {
        var list = BuiltIn.ToList();

        foreach (var item in custom)
        {
            var created = CreateCustom(item);
            if (created.Target is not null &&
                list.All(t => !t.Id.Equals(created.Target.Id, StringComparison.OrdinalIgnoreCase)))
                list.Add(created.Target);
        }

        _all = list;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static (ServiceTarget? Target, string? Error) CreateCustom(CustomService item)
    {
        var url = (item.Url ?? string.Empty).Trim();
        if (url.Length == 0)
            return (null, "Укажите адрес сервиса.");

        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return (null, "Некорректный адрес. Пример: https://example.com/");

        var name = (item.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            name = uri.Host;

        var id = (item.Id ?? string.Empty).Trim();
        if (id.Length == 0)
            id = "custom_" + Guid.NewGuid().ToString("N")[..8];

        var target = new ServiceTarget(
            id,
            name,
            "Свой сервис",
            uri.ToString(),
            uri.Host,
            "CS",
            "#38BDF8",
            "#0EA5E9",
            2);

        return (target, null);
    }
}

public sealed class StrategyScore
{
    public string PresetFile { get; set; } = string.Empty;
    public double Score { get; set; }
    public string TestedAt { get; set; } = string.Empty;
    public Dictionary<string, string> Services { get; set; } = new();
    public bool IsManual { get; set; }
}

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warn,
    Error
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Success => "OK",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "—"
    };
    public string LineText => $"{TimeText}  {LevelText}  {Source,-12}  {Message}";
}

public enum DiagnosticStatus
{
    Ok,
    Warn,
    Fail,
    Info
}

public sealed record DiagnosticItem(string Title, string Detail, DiagnosticStatus Status);

public sealed class TuneResult
{
    public required StrategyPreset Preset { get; init; }
    public double Score { get; init; }
    public string Note { get; init; } = string.Empty;
    public IReadOnlyList<ProbeResult> Services { get; init; } = Array.Empty<ProbeResult>();
    public TimeSpan Duration { get; init; }
    public bool IsBest { get; set; }

    public int AvailableCount => Services.Count(s => s.Status == ProbeStatus.Available);
    public int PartialCount => Services.Count(s => s.Status == ProbeStatus.Partial);
    public int TotalCount => Services.Count;

    /// <summary>
    /// True when every selected service works. Discord is allowed to be
    /// Partial: its WebSocket/voice probe is stricter than the rest and is
    /// easily rate-limited during a full autotune pass.
    /// </summary>
    public bool IsStable => Services.Count > 0 && Services.All(s =>
        s.Status == ProbeStatus.Available ||
        (s.ServiceId.Equals("discord", StringComparison.OrdinalIgnoreCase) &&
         s.Status == ProbeStatus.Partial));
}

public sealed class TuneScreenResult
{
    public required StrategyPreset Preset { get; init; }
    public required ProbeResult Youtube { get; init; }
    public double Score { get; init; }
    public string Note { get; init; } = string.Empty;
    public bool IsFinalist { get; set; }

    public ProbeStatus Status => Youtube.Status;
}

public sealed class TuneReport
{
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; set; }
    public string Mode { get; init; } = "standard";
    public IReadOnlyList<ProbeResult> Baseline { get; set; } = Array.Empty<ProbeResult>();
    public List<TuneResult> Results { get; init; } = new();
    public List<TuneScreenResult> ScreenResults { get; init; } = new();
    public int ScreenedCount { get; set; }
    public int FinalistLimit { get; set; }
    public TuneResult? Best { get; set; }
    public bool BestApplied { get; set; }
    public bool Canceled { get; set; }

    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# RDG.Zapret — отчёт автоподбора");
        sb.AppendLine();
        sb.AppendLine($"- Начало: **{StartedAt:dd.MM.yyyy HH:mm}**");
        sb.AppendLine($"- Режим: **{Mode}**");
        if (ScreenedCount > 0)
            sb.AppendLine($"- Быстро проверено: **{ScreenedCount}** · в финал: **{Results.Count}**");
        sb.AppendLine($"- Полностью проверено стратегий: **{Results.Count}**");
        if (Best is not null)
            sb.AppendLine($"- Победитель: **{Best.Preset.DisplayName}** — **{Best.Score:0} / 100**");
        sb.AppendLine();
        sb.AppendLine("| # | Стратегия | Балл | Доступно | Примечание |");
        sb.AppendLine("|---|-----------|------|----------|------------|");
        var i = 1;
        foreach (var r in Results.OrderByDescending(r => r.Score))
        {
            sb.AppendLine($"| {i++} | {r.Preset.DisplayName} | {r.Score:0} | {r.AvailableCount}/{r.TotalCount} | {r.Note} |");
        }
        return sb.ToString();
    }
}

public enum TunePhase
{
    Preparing,
    Baseline,
    Screening,
    Finalists,
    Testing,
    Applying,
    Finished,
    Canceled,
    Failed
}

public sealed record TuneUpdate(
    TunePhase Phase,
    int Index = 0,
    int Total = 0,
    StrategyPreset? Current = null,
    TuneResult? Completed = null,
    TuneResult? Best = null,
    IReadOnlyList<ProbeResult>? Baseline = null,
    string? Message = null,
    int Screened = 0,
    TuneScreenResult? ScreenCompleted = null,
    int FinalistTotal = 0);

public sealed class TuneRequest
{
    public required IReadOnlyList<ServiceTarget> Targets { get; init; }
    public int WarmupSeconds { get; init; } = 4;
    public int TimeoutSeconds { get; init; } = 5;
    public int Passes { get; init; } = 1;
    public bool SkipBaselineOk { get; init; } = true;
    public bool KeepBestRunning { get; init; } = true;
    public string Mode { get; init; } = "standard";
    public int EarlyExitScore { get; init; } = 98;
    public bool AllowEarlyExit { get; init; } = true;

    /// <summary>Fast YouTube screen of every candidate, then a full probe of the best finalists.</summary>
    public bool TwoStage { get; init; }
    public int FinalistLimit { get; init; } = 8;
    public int ScreenWarmupSeconds { get; init; }
    public int ScreenTimeoutSeconds { get; init; } = 4;

    /// <summary>Previously strong presets that should always reach the full probe.</summary>
    public IReadOnlyList<string> SeedPresetFiles { get; init; } = Array.Empty<string>();
}
