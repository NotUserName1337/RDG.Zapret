namespace RDG.Zapret.Models;

public sealed record StrategyPackProgress(double Percent, string Status);

public sealed class StrategyPackResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int RemovedDuplicates { get; init; }
    public string? Version { get; init; }
    public string? Hash { get; init; }
    public IReadOnlyList<string> NewStrategies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasChanges => Added > 0 || Updated > 0 || RemovedDuplicates > 0;

    public string Summary => HasChanges
        ? $"Добавлено: {Added} · обновлено: {Updated}" +
          (RemovedDuplicates > 0 ? $" · убрано дублей: {RemovedDuplicates}" : string.Empty)
        : "Новых стратегий нет";
}

public sealed class StrategyPackState
{
    public string Version { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string InstalledAt { get; set; } = string.Empty;
    public List<InstalledStrategyFile> Files { get; set; } = new();
}

public sealed class InstalledStrategyFile
{
    public string Engine { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
