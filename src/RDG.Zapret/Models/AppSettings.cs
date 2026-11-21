namespace RDG.Zapret.Models;

/// <summary>User settings persisted to %LOCALAPPDATA%\RDG.Zapret\settings.json.</summary>
public sealed class AppSettings
{
    public string ZapretPath { get; set; } = string.Empty;
    public string LastPresetFile { get; set; } = string.Empty;
    public string BestPresetFile { get; set; } = string.Empty;
    public double BestScore { get; set; }
    public string BestScoreDate { get; set; } = string.Empty;
    public string LastTuneDate { get; set; } = string.Empty;

    public bool StartWithWindows { get; set; }
    public bool StartLastStrategyOnLaunch { get; set; } = true;
    public bool MinimizeOnAutostart { get; set; } = true;
    public bool LeaveBypassOnExit { get; set; } = true;
    public bool AutoDownloadStrategies { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public bool BackgroundMonitorEnabled { get; set; } = true;
    public int BackgroundCheckIntervalMinutes { get; set; } = 30;
    public bool BackgroundRecoveryEnabled { get; set; } = true;
    public int BackgroundRetryAttempts { get; set; } = 3;
    public int BackgroundRetryDelaySeconds { get; set; } = 8;
    public bool BackgroundIntervalDefaultMigrated { get; set; }

    public bool AutoCheckUpdates { get; set; } = true;
    public string LastUpdateCheckDate { get; set; } = string.Empty;

    public bool AutoCheckStrategyPack { get; set; } = true;
    public string StrategyPackUrl { get; set; } = string.Empty;
    public string StrategyPackHash { get; set; } = string.Empty;
    public string StrategyPackVersion { get; set; } = string.Empty;
    public string LastStrategyPackCheckDate { get; set; } = string.Empty;

    public bool TgProxyAutoStart { get; set; }
    public bool TgProxyStopOnExit { get; set; } = true;
    public int TgProxyPort { get; set; } = 1443;
    public string TgProxySecret { get; set; } = string.Empty;

    public string Accent { get; set; } = "violet";
    public string Engine { get; set; } = "zapret";
    public int ProbeTimeoutSeconds { get; set; } = 5;
    public bool DeepClassification { get; set; } = true;

    public string TuneMode { get; set; } = "standard";
    public bool TuneBestOnly { get; set; } = true;
    public int TuneBestLimit { get; set; } = 10;
    public string GeneratorMode { get; set; } = "full";
    public int TuneWarmupSeconds { get; set; } = 4;
    public bool TuneSkipBaselineOk { get; set; } = true;
    public bool TuneKeepBestRunning { get; set; } = true;
    public int TuneFinalistLimit { get; set; } = 8;
    public List<string> TuneDisabledServices { get; set; } = new();
    public List<string> TuneExcludedPresets { get; set; } = new();
    public List<CustomService> CustomServices { get; set; } = new();
    public bool Zapret2CmdArgumentFixApplied { get; set; }

    public string YoutubeHostsFixAppliedAt { get; set; } = string.Empty;

}
