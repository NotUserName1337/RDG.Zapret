namespace RDG.Zapret.Models;

public sealed record StrategyRecipe
{
    public required string Name { get; init; }
    public required string Desync { get; init; }
    public string? Fooling { get; init; }
    public string? SplitPos { get; init; }
    public int? SeqOvl { get; init; }
    public int? Ttl { get; init; }
    public bool AutoTtl { get; init; }
    public int Repeats { get; init; }
    public string? FakeTlsFile { get; init; }
    public string? FakeTlsMod { get; init; }

    public string Key => string.Join("|",
        Desync,
        Fooling ?? "-",
        SplitPos ?? "-",
        SeqOvl?.ToString() ?? "-",
        Ttl?.ToString() ?? "-",
        AutoTtl ? "auto" : "-",
        Repeats,
        FakeTlsFile ?? "-",
        FakeTlsMod ?? "-");

    public string Summary
    {
        get
        {
            var parts = new List<string> { Desync };
            if (!string.IsNullOrWhiteSpace(Fooling)) parts.Add("fooling=" + Fooling);
            if (!string.IsNullOrWhiteSpace(SplitPos)) parts.Add("pos=" + SplitPos);
            if (SeqOvl is > 0) parts.Add("ovl=" + SeqOvl);
            if (Ttl is > 0) parts.Add("ttl=" + Ttl);
            else if (AutoTtl) parts.Add("autottl");
            if (Repeats > 0) parts.Add("repeats=" + Repeats);
            if (!string.IsNullOrWhiteSpace(FakeTlsFile)) parts.Add("fake=" + FakeTlsFile);
            if (!string.IsNullOrWhiteSpace(FakeTlsMod)) parts.Add("mod=" + FakeTlsMod);
            return string.Join(" · ", parts);
        }
    }
}

public sealed class GeneratorAttempt
{
    public required StrategyRecipe Recipe { get; init; }
    public double Score { get; init; }
    public bool AllAvailable { get; init; }
    public IReadOnlyList<ProbeResult> Services { get; init; } = Array.Empty<ProbeResult>();
    public string Note { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }

    public int AvailableCount => Services.Count(s => s.Status == ProbeStatus.Available);
    public int TotalCount => Services.Count;
}

public sealed class GeneratorOptions
{
    public required IReadOnlyList<ServiceTarget> Targets { get; init; }
    public int WarmupSeconds { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 4;
    public int MaxAttempts { get; init; } = 60;
    public bool KeepBestRunning { get; init; } = true;
    public string Mode { get; init; } = "full";
}

public sealed class GeneratorReport
{
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; set; }
    public IReadOnlyList<ProbeResult> Baseline { get; set; } = Array.Empty<ProbeResult>();
    public List<GeneratorAttempt> Attempts { get; } = new();
    public GeneratorAttempt? Best { get; set; }
    public bool AllAvailable { get; set; }
    public bool BestRunning { get; set; }
    public bool Canceled { get; set; }
    public string? SavedPresetPath { get; set; }
    public StrategyPreset? SavedPreset { get; set; }
}

public enum GeneratorPhase
{
    Preparing,
    Baseline,
    Testing,
    Saving,
    Finished,
    Canceled,
    Failed
}

public sealed record GeneratorProgress(
    GeneratorPhase Phase,
    int Index = 0,
    int Total = 0,
    StrategyRecipe? Current = null,
    GeneratorAttempt? Completed = null,
    GeneratorAttempt? Best = null,
    IReadOnlyList<ProbeResult>? Baseline = null,
    string? Message = null);
