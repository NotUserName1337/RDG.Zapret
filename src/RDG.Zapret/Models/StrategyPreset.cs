namespace RDG.Zapret.Models;

/// <summary>A parsed zapret preset (a .bat/.cmd file turned into a ready-to-run winws command).</summary>
public sealed class StrategyPreset
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public required string Family { get; init; }
    public required string Tag { get; init; }
    public required string EnginePath { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public bool IsExperimental { get; init; }

    public string FileKey => FileName;
    public string EngineName => Path.GetFileName(EnginePath);
    public string BaseName => Path.GetFileNameWithoutExtension(FileName);
    public bool IsWinws2 => EngineName.Equals("winws2.exe", StringComparison.OrdinalIgnoreCase);
    public ZapretEngine EngineKind => IsWinws2 ? ZapretEngine.Zapret2 : ZapretEngine.Zapret;
    public string EngineDisplay => $"{EngineKind.DisplayName()} · {EngineName}";

    public string ArgumentsPreview
    {
        get
        {
            var compact = Arguments.Replace("\r", " ").Replace("\n", " ").Trim();
            while (compact.Contains("  ", StringComparison.Ordinal))
                compact = compact.Replace("  ", " ", StringComparison.Ordinal);
            return compact.Length > 260 ? compact[..260] + "…" : compact;
        }
    }

    /// <summary>One command-line switch per line, for the read-only preview box.</summary>
    public string ArgumentsPretty => Arguments.Replace(" --", Environment.NewLine + "--", StringComparison.Ordinal);

    public override string ToString() => DisplayName;
}

public sealed class ZapretInstallation
{
    public required string RootPath { get; init; }
    public required string EnginePath { get; init; }
    public required string EngineDirectory { get; init; }
    public required IReadOnlyList<StrategyPreset> Presets { get; init; }
    public string? WinwsPath { get; init; }
    public string? Winws2Path { get; init; }
    public bool HasWinDivertFile { get; init; }
    public bool UsesWinws2 { get; init; }

    public string FolderName => new DirectoryInfo(RootPath).Name;

    public string? EnginePathFor(ZapretEngine engine) =>
        engine == ZapretEngine.Zapret2 ? Winws2Path : WinwsPath;

    public int PresetCountFor(ZapretEngine engine) =>
        Presets.Count(p => p.EngineKind == engine);
}

public sealed record ScanResult(ZapretInstallation? Installation, string? Error, IReadOnlyList<string> Warnings);

public sealed record StartOutcome(bool Success, string? Error)
{
    public static StartOutcome Ok() => new(true, null);
    public static StartOutcome Fail(string error) => new(false, error);
}
