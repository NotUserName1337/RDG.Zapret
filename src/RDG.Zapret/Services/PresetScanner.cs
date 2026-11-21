using RDG.Zapret.Infrastructure;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class PresetScanner
{
    private readonly LogService _log;
    private ZapretEngine _engine = ZapretEngine.Zapret;

    public PresetScanner(LogService log) => _log = log;

    public ZapretInstallation? Current { get; private set; }

    public ZapretEngine Engine => _engine;

    public IReadOnlyList<StrategyPreset> Presets =>
        Current is null
            ? Array.Empty<StrategyPreset>()
            : Current.Presets.Where(p => p.EngineKind == _engine).ToList();

    public bool HasEngine(ZapretEngine engine) => CountFor(engine) > 0;

    public int CountFor(ZapretEngine engine) => Current?.PresetCountFor(engine) ?? 0;

    public string? EnginePathFor(ZapretEngine engine) => Current?.EnginePathFor(engine);

    public string? ActiveEnginePath => EnginePathFor(_engine);

    public string ActiveEngineFile => Path.GetFileName(ActiveEnginePath ?? string.Empty);

    public string ActiveEngineLabel => $"{_engine.DisplayName()} · {ActiveEngineFile}";

    public event EventHandler? Changed;

    public bool SetEngine(ZapretEngine engine, bool notify = true)
    {
        if (engine == _engine)
            return true;

        if (Current is not null && !HasEngine(engine))
        {
            _log.Warn("scan", $"Ядро {engine.DisplayName()}: не найдено ни одной стратегии");
            return false;
        }

        _engine = engine;
        _log.Info("scan", $"Активное ядро: {engine.DisplayName()} ({engine.EngineFile()})");

        if (notify)
            Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public StrategyPreset? Find(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        return Presets.FirstOrDefault(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        Current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ScanResult> ScanAsync(string folder, CancellationToken ct = default)
    {
        var result = await Task.Run(() => ScanCore(folder), ct).ConfigureAwait(true);

        if (result.Installation is not null)
        {
            Current = result.Installation;

            var zapretCount = result.Installation.PresetCountFor(ZapretEngine.Zapret);
            var zapret2Count = result.Installation.PresetCountFor(ZapretEngine.Zapret2);
            _log.Success("scan",
                $"Найдено стратегий: {result.Installation.Presets.Count} · zapret: {zapretCount} · zapret2: {zapret2Count}");

            if (!HasEngine(_engine))
            {
                var fallback = HasEngine(ZapretEngine.Zapret)
                    ? ZapretEngine.Zapret
                    : HasEngine(ZapretEngine.Zapret2)
                        ? ZapretEngine.Zapret2
                        : _engine;

                if (fallback != _engine)
                {
                    _engine = fallback;
                    _log.Info("scan", $"Автоматически выбрано ядро: {_engine.DisplayName()} ({_engine.EngineFile()})");
                }
            }

            foreach (var warning in result.Warnings.Take(10))
                _log.Warn("scan", warning);
        }
        else
        {
            Current = null;
            _log.Warn("scan", result.Error ?? "Папка zapret не распознана");
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private static ScanResult ScanCore(string folder)
    {
        try
        {
            folder = (folder ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return new ScanResult(null, "Папка не существует", Array.Empty<string>());

            var root = Path.GetFullPath(folder);
            var winws = FindEngine(root, "winws.exe");
            var winws2 = FindEngine(root, "winws2.exe");
            var engine = winws ?? winws2;
            if (engine is null)
                return new ScanResult(null,
                    "В папке не найден winws.exe / winws2.exe. Укажите папку, куда распакован zapret (рядом должна быть папка bin).",
                    Array.Empty<string>());

            EnsureUserLists(root);

            var warnings = new List<string>();
            var presets = new List<StrategyPreset>();
            var files = EnumeratePresetFiles(root);
            if (files.Count == 0)
                return new ScanResult(null, "В папке нет файлов стратегий (*.bat / *.cmd)", Array.Empty<string>());

            foreach (var file in files)
            {
                if (Path.GetFileName(file).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                    continue;

                var outcome = PresetParser.Parse(file);
                if (outcome.Preset is not null)
                {
                    presets.Add(outcome.Preset);
                }
                else
                {
                    warnings.Add($"{Path.GetFileName(file)}: {outcome.Error}");
                }
            }

            if (presets.Count == 0)
                return new ScanResult(null, "Не удалось разобрать ни одну стратегию. Проверьте целостность архива zapret.", warnings);

            presets = presets
                .OrderBy(p => p.Tag == "Базовый" ? 0 : 1)
                .ThenBy(p => p.FileName, NaturalStringComparer.Instance)
                .ToList();

            var hasSys = SafeEnumerate(root, "WinDivert64.sys").Any() || SafeEnumerate(root, "WinDivert32.sys").Any();
            var usesWinws2 = presets.Any(p => p.IsWinws2) || winws2 is not null;

            var installation = new ZapretInstallation
            {
                RootPath = root,
                EnginePath = engine,
                EngineDirectory = Path.GetDirectoryName(engine) ?? root,
                Presets = presets,
                WinwsPath = winws,
                Winws2Path = winws2,
                HasWinDivertFile = hasSys,
                UsesWinws2 = usesWinws2
            };

            return new ScanResult(installation, null, warnings);
        }
        catch (Exception ex)
        {
            return new ScanResult(null, "Ошибка сканирования: " + ex.Message, Array.Empty<string>());
        }
    }

    private static string? FindEngine(string root, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(root, "bin", fileName),
            Path.Combine(root, fileName),
            Path.Combine(root, "zapret-winws", fileName),
            Path.Combine(root, "binaries", fileName),
            Path.Combine(root, "binaries", "windows-x86_64", fileName),
            Path.Combine(root, "zapret2", "bin", fileName),
            Path.Combine(root, "zapret2", fileName),
            Path.Combine(root, "zapret2", "binaries", "windows-x86_64", fileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            return Directory.EnumerateFiles(root, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> EnumeratePresetFiles(string root)
    {
        var files = new List<string>();
        foreach (var pattern in new[] { "*.bat", "*.cmd" })
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly));
            }
            catch
            {
                // ignored
            }
        }

        // Optional second engine installed by RDG.Zapret: <root>\zapret2.
        var zapret2 = Path.Combine(root, "zapret2");
        if (Directory.Exists(zapret2))
        {
            foreach (var pattern in new[] { "*.cmd", "*.bat" })
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(zapret2, pattern, SearchOption.TopDirectoryOnly));
                }
                catch
                {
                    // ignored
                }
            }
        }

        if (files.Count > 0)
            return files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Some bundles keep presets in a subfolder.
        try
        {
            files.AddRange(Directory.EnumerateFiles(root, "*.cmd", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 2,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            // ignored
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void EnsureUserLists(string root)
    {
        var lists = Path.Combine(root, "lists");
        if (!Directory.Exists(lists))
            return;

        var samples = new Dictionary<string, string>
        {
            ["ipset-exclude-user.txt"] = "203.0.113.113/32" + Environment.NewLine,
            ["list-general-user.txt"] = "# Never leave this file empty" + Environment.NewLine + "domain.example.abc" + Environment.NewLine,
            ["list-exclude-user.txt"] = "domain.example.abc" + Environment.NewLine
        };

        foreach (var (name, content) in samples)
        {
            try
            {
                var path = Path.Combine(lists, name);
                if (!File.Exists(path))
                    File.WriteAllText(path, content);
            }
            catch
            {
                // read-only folder is not fatal
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Take(8).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
