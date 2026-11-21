namespace RDG.Zapret.Models;

public enum BlockReason
{
    Open,
    NoNetwork,
    Dns,
    Sni,
    Ip,
    ServerDown,
    Unknown
}

public sealed class BlockDiagnosis
{
    public required string Host { get; init; }
    public BlockReason Reason { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;
    public string? Address { get; init; }
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public bool IsOpen => Reason == BlockReason.Open;
}

public enum DoctorStepStatus
{
    Ok,
    Fixed,
    Warn,
    Error,
    Info,
    Skipped
}

public sealed class DoctorStep
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Details { get; init; }
    public DoctorStepStatus Status { get; init; }
    public string? ActionKey { get; init; }
    public string? ActionLabel { get; init; }
    public bool HasAction => !string.IsNullOrWhiteSpace(ActionKey);
}

public sealed class DoctorSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Glyph { get; init; }
    public IReadOnlyList<DoctorStep> Steps { get; init; } = Array.Empty<DoctorStep>();

    public DoctorStepStatus Status
    {
        get
        {
            if (Steps.Any(s => s.Status == DoctorStepStatus.Error))
                return DoctorStepStatus.Error;
            if (Steps.Any(s => s.Status == DoctorStepStatus.Warn))
                return DoctorStepStatus.Warn;
            if (Steps.Any(s => s.Status == DoctorStepStatus.Fixed))
                return DoctorStepStatus.Fixed;
            if (Steps.Any(s => s.Status == DoctorStepStatus.Skipped))
                return DoctorStepStatus.Skipped;
            return DoctorStepStatus.Ok;
        }
    }
}

public sealed class DoctorReport
{
    public IReadOnlyList<DoctorSection> Sections { get; init; } = Array.Empty<DoctorSection>();
    public string Summary { get; init; } = string.Empty;
    public bool AllOk { get; init; }

    public IEnumerable<DoctorStep> AllSteps => Sections.SelectMany(s => s.Steps);
}

public sealed record DoctorActionResult(bool Success, string Message);

public enum YoutubeVerdict
{
    Unknown,
    Fast,
    Slow,
    Unreachable
}

public sealed class YoutubeNodeResult
{
    public required string Host { get; init; }
    public double Mbps { get; init; }
    public long Bytes { get; init; }
    public int ElapsedMs { get; init; }
    public bool IsThrottled { get; init; }
    public string Detail { get; init; } = string.Empty;

    public string SpeedText => Mbps <= 0 ? "нет данных" : $"{Mbps:0.0} Мбит/с";
}

public sealed class YoutubeThrottleReport
{
    public YoutubeVerdict Verdict { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<YoutubeNodeResult> Nodes { get; init; } = Array.Empty<YoutubeNodeResult>();
    public IReadOnlyList<string> SlowHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FixableHosts { get; init; } = Array.Empty<string>();
    public DateTime CheckedAt { get; init; } = DateTime.Now;

    public bool HasSlowNodes => SlowHosts.Count > 0;
    public bool HasFixableNodes => FixableHosts.Count > 0;
}

public sealed class HostsFixStatus
{
    public bool Applied { get; init; }
    public int NodeCount { get; init; }
    public string HostsPath { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
    public IReadOnlyList<string> Nodes { get; init; } = Array.Empty<string>();
}

public sealed record HostsFixResult(bool Success, string Message);
