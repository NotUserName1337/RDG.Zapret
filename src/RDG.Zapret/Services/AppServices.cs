using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class AppServices
{
    public AppServices()
    {
        Log = new LogService();
        Settings = new SettingsService(Log);
        Settings.Load();
        if (!Settings.Current.BackgroundIntervalDefaultMigrated)
        {
            Settings.Update(s =>
            {
                if (s.BackgroundCheckIntervalMinutes == 5)
                    s.BackgroundCheckIntervalMinutes = 30;
                s.BackgroundIntervalDefaultMigrated = true;
            }, notify: false);
            Log.Info("settings", $"Интервал фоновой проверки: {Settings.Current.BackgroundCheckIntervalMinutes} мин");
        }
        ServiceCatalog.SetCustomServices(Settings.Current.CustomServices);
        if (!Settings.Current.Zapret2CmdArgumentFixApplied)
        {
            var removed = 0;
            Settings.Update(s =>
            {
                removed = s.TuneExcludedPresets.RemoveAll(
                    x => x.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
                s.Zapret2CmdArgumentFixApplied = true;
            }, notify: false);
            if (removed > 0)
                Log.Info("zapret2", $"Сброшено ложных исключений zapret2: {removed}");
        }
        Scanner = new PresetScanner(Log);
        Winws = new WinwsController(Log);
        Probe = new ServiceProbe();
        Tuner = new AutoTuner(Winws, Probe, Log);
        Generator = new StrategyGenerator(Winws, Probe, Log);
        Results = new ResultsService(Settings, Log);
        Diagnostics = new DiagnosticsService(Log);
        Doctor = new DoctorService(Log);
        Dns = new DnsService(Log);
        Classifier = new BlockClassifier();
        YoutubeThrottle = new YoutubeThrottleService();
        HostsFix = new HostsFixService(Log);
        UserLists = new UserListService();
        Autostart = new AutostartService(Settings, Log);
        Banner = new BannerService(Log);
        Download = new ZapretDownloadService(Log);
        StrategyPack = new StrategyPackService(Log, Settings);
        Zapret2Pack = new Zapret2PackService(Log);
        Updates = new UpdateService(Log);
        TgProxy = new TgWsProxyService(Settings, Log);
        TgProxy.ImportExternalSettings();
        Operations = new OperationCoordinator();
        Monitor = new BackgroundMonitor(Settings, Scanner, Winws, Probe, Tuner, Generator, Results, Operations, Log);
    }

    public LogService Log { get; }
    public SettingsService Settings { get; }
    public PresetScanner Scanner { get; }
    public WinwsController Winws { get; }
    public ServiceProbe Probe { get; }
    public AutoTuner Tuner { get; }
    public StrategyGenerator Generator { get; }
    public ResultsService Results { get; }
    public DiagnosticsService Diagnostics { get; }
    public DoctorService Doctor { get; }
    public DnsService Dns { get; }
    public BlockClassifier Classifier { get; }
    public YoutubeThrottleService YoutubeThrottle { get; }
    public HostsFixService HostsFix { get; }
    public UserListService UserLists { get; }
    public AutostartService Autostart { get; }
    public BannerService Banner { get; }
    public ZapretDownloadService Download { get; }
    public StrategyPackService StrategyPack { get; }
    public Zapret2PackService Zapret2Pack { get; }
    public UpdateService Updates { get; }
    public TgWsProxyService TgProxy { get; }
    public OperationCoordinator Operations { get; }
    public BackgroundMonitor Monitor { get; }
}
