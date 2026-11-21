using System.Diagnostics;
using System.Text;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Builds candidate winws parameter sets and tests them until every selected service works.
/// Intended for the classic zapret core (winws.exe): fake/multisplit/fakedsplit recipes.
/// </summary>
public sealed class StrategyGenerator
{
    private readonly WinwsController _winws;
    private readonly ServiceProbe _probe;
    private readonly LogService _log;

    public StrategyGenerator(WinwsController winws, ServiceProbe probe, LogService log)
    {
        _winws = winws;
        _probe = probe;
        _log = log;
    }

    public async Task<GeneratorReport> RunAsync(
        ZapretInstallation installation,
        GeneratorOptions options,
        IProgress<GeneratorProgress> progress,
        CancellationToken cancellationToken)
    {
        var report = new GeneratorReport { StartedAt = DateTime.Now };
        var context = CreateContext(installation);
        if (context is null)
        {
            report.FinishedAt = DateTime.Now;
            progress.Report(new GeneratorProgress(GeneratorPhase.Failed, Message: "Генератор поддерживает только ядро zapret (winws.exe)"));
            return report;
        }

        try
        {
            progress.Report(new GeneratorProgress(GeneratorPhase.Preparing, Total: options.MaxAttempts,
                Message: "Готовлю генератор…"));

            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            await Task.Delay(650, cancellationToken).ConfigureAwait(true);

            progress.Report(new GeneratorProgress(GeneratorPhase.Baseline, Total: options.MaxAttempts,
                Message: "Проверяю сервисы без обхода…"));

            var baseline = await _probe.CheckManyAsync(
                options.Targets,
                new ProbeOptions { TimeoutSeconds = options.TimeoutSeconds, ClassifyWithTls = false },
                cancellationToken).ConfigureAwait(true);
            report.Baseline = baseline;

            progress.Report(new GeneratorProgress(GeneratorPhase.Baseline, Total: options.MaxAttempts,
                Baseline: baseline, Message: "Baseline готов"));

            if (baseline.Count == options.Targets.Count && baseline.All(b => b.Status == ProbeStatus.Available))
            {
                report.AllAvailable = true;
                report.FinishedAt = DateTime.Now;
                progress.Report(new GeneratorProgress(GeneratorPhase.Finished, Total: options.MaxAttempts,
                    Baseline: baseline, Message: "Все выбранные сервисы уже доступны без обхода"));
                return report;
            }

            var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seeds = BuildSeeds(context);
            var maxAttempts = Math.Clamp(options.MaxAttempts, 1, 300);
            var index = 0;

            foreach (var seed in seeds)
            {
                if (attemptsExceeded(report, maxAttempts))
                    break;

                cancellationToken.ThrowIfCancellationRequested();
                if (!tested.Add(seed.Key))
                    continue;

                index++;
                var attempt = await EvaluateAsync(seed, context, options, index, maxAttempts, progress, cancellationToken)
                    .ConfigureAwait(true);
                report.Attempts.Add(attempt);

                if (attempt.AllAvailable)
                    break;
            }

            while (!cancellationToken.IsCancellationRequested &&
                   !report.Attempts.Any(a => a.AllAvailable) &&
                   report.Attempts.Count < maxAttempts)
            {
                var bases = report.Attempts
                    .OrderByDescending(a => a.Score)
                    .ThenByDescending(a => a.AvailableCount)
                    .Take(2)
                    .ToList();
                if (bases.Count == 0)
                    break;

                var neighbors = bases
                    .SelectMany(b => BuildNeighbors(b.Recipe, context))
                    .Where(n => tested.Add(n.Key))
                    .Take(maxAttempts - report.Attempts.Count)
                    .ToList();

                if (neighbors.Count == 0)
                    break;

                foreach (var neighbor in neighbors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                    var attempt = await EvaluateAsync(neighbor, context, options, index, maxAttempts, progress, cancellationToken)
                        .ConfigureAwait(true);
                    report.Attempts.Add(attempt);

                    if (attempt.AllAvailable)
                        break;
                }
            }

            var best = report.Attempts
                .Where(a => a.AvailableCount > 0)
                .OrderByDescending(a => a.AllAvailable)
                .ThenByDescending(a => a.AvailableCount)
                .ThenByDescending(a => a.Score)
                .FirstOrDefault();

            report.Best = best;
            report.AllAvailable = best?.AllAvailable == true;

            if (best is not null && report.AllAvailable)
            {
                progress.Report(new GeneratorProgress(GeneratorPhase.Saving, Total: maxAttempts,
                    Best: best, Message: "Сохраняю рабочую стратегию…"));

                var saved = SavePreset(installation, best.Recipe);
                report.SavedPresetPath = saved.Path;
                report.SavedPreset = saved.Preset;

                if (options.KeepBestRunning && saved.Preset is not null)
                {
                    var start = await _winws.StartAsync(saved.Preset, cancellationToken).ConfigureAwait(true);
                    report.BestRunning = start.Success;
                    if (!start.Success)
                        _log.Warn("generator", "Не удалось оставить сгенерированную стратегию включённой: " + start.Error);
                }
            }
            else
            {
                await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            }

            report.FinishedAt = DateTime.Now;
            var message = report.AllAvailable
                ? $"Все сервисы работают. Попыток: {report.Attempts.Count}"
                : $"Достигнут лимит попыток ({report.Attempts.Count}). Лучший результат: {best?.AvailableCount ?? 0}/{best?.TotalCount ?? options.Targets.Count}";

            _log.Success("generator", message);
            progress.Report(new GeneratorProgress(GeneratorPhase.Finished, Total: maxAttempts,
                Best: best, Baseline: baseline, Message: message));

            return report;
        }
        catch (OperationCanceledException)
        {
            report.Canceled = true;
            report.FinishedAt = DateTime.Now;
            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            _log.Warn("generator", "Генератор остановлен пользователем");
            progress.Report(new GeneratorProgress(GeneratorPhase.Canceled, Baseline: report.Baseline,
                Message: "Генератор остановлен"));
            return report;
        }
        catch (Exception ex)
        {
            report.FinishedAt = DateTime.Now;
            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            _log.Error("generator", "Ошибка генератора: " + ex);
            progress.Report(new GeneratorProgress(GeneratorPhase.Failed, Baseline: report.Baseline,
                Message: ex.Message));
            return report;
        }

        static bool attemptsExceeded(GeneratorReport r, int max) => r.Attempts.Count >= max;
    }

    private async Task<GeneratorAttempt> EvaluateAsync(
        StrategyRecipe recipe,
        GeneratorContext context,
        GeneratorOptions options,
        int index,
        int total,
        IProgress<GeneratorProgress> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        progress.Report(new GeneratorProgress(
            GeneratorPhase.Testing, index, total, recipe,
            Message: $"Попытка {index}: {recipe.Summary}"));

        var preset = CreatePreset(recipe, context);
        var start = await _winws.StartAsync(preset, cancellationToken).ConfigureAwait(true);
        if (!start.Success)
        {
            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            var failed = new GeneratorAttempt
            {
                Recipe = recipe,
                Score = 0,
                Note = Shorten(start.Error ?? "не запустилась"),
                Duration = stopwatch.Elapsed
            };
            _log.Warn("generator", $"Попытка {index}: {recipe.Summary} — не запустилась");
            progress.Report(new GeneratorProgress(GeneratorPhase.Testing, index, total, recipe, failed,
                Message: failed.Note));
            return failed;
        }

        await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), cancellationToken).ConfigureAwait(true);
        if (!_winws.IsRunning)
        {
            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            var failed = new GeneratorAttempt
            {
                Recipe = recipe,
                Score = 0,
                Note = "winws завершился после запуска",
                Duration = stopwatch.Elapsed
            };
            progress.Report(new GeneratorProgress(GeneratorPhase.Testing, index, total, recipe, failed,
                Message: failed.Note));
            return failed;
        }

        var results = await _probe.CheckManyAsync(
            options.Targets,
            new ProbeOptions { TimeoutSeconds = options.TimeoutSeconds, ClassifyWithTls = false },
            cancellationToken).ConfigureAwait(true);

        var score = CalculateScore(options.Targets, results);
        var available = results.Count(s => s.Status == ProbeStatus.Available);
        var allAvailable = results.Count == options.Targets.Count &&
                           results.All(s => s.Status == ProbeStatus.Available);

        var attempt = new GeneratorAttempt
        {
            Recipe = recipe,
            Score = score,
            AllAvailable = allAvailable,
            Services = results,
            Duration = stopwatch.Elapsed,
            Note = allAvailable
                ? "Все сервисы работают"
                : $"{available}/{results.Count} сервисов"
        };

        await _winws.StopAsync(quiet: true).ConfigureAwait(true);
        _log.Info("generator",
            $"Попытка {index}: {recipe.Summary} → {score:0}/100 · {available}/{results.Count} · {stopwatch.Elapsed.TotalSeconds:0.0} с");

        progress.Report(new GeneratorProgress(GeneratorPhase.Testing, index, total, recipe, attempt,
            Message: $"{score:0}/100 · {available}/{results.Count}"));
        return attempt;
    }

    public (string Path, StrategyPreset? Preset) SavePreset(ZapretInstallation installation, StrategyRecipe recipe)
    {
        var context = CreateContext(installation)
                      ?? throw new InvalidOperationException("Не найден winws.exe для сохранения стратегии");

        var fileName = "general (RDG generated).bat";
        var path = Path.Combine(installation.RootPath, fileName);
        var arguments = BuildArguments(recipe, context);
        var content =
            "@echo off\r\n" +
            "chcp 65001 > nul\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            $"start \"RDG.Zapret: generated\" /min \"{context.EnginePath}\" {arguments}\r\n";

        File.WriteAllText(path, content, new UTF8Encoding(false));
        var parsed = PresetParser.Parse(path);
        var preset = parsed.Preset ?? new StrategyPreset
        {
            FilePath = path,
            FileName = fileName,
            DisplayName = "General · RDG generated",
            Family = "General",
            Tag = "RDG generated",
            EnginePath = context.EnginePath,
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory
        };

        _log.Success("generator", "Сгенерированная стратегия сохранена: " + path);
        return (path, preset);
    }

    private static double CalculateScore(IReadOnlyList<ServiceTarget> targets, IReadOnlyList<ProbeResult> results)
    {
        double earned = 0;
        double max = 0;

        foreach (var target in targets)
        {
            var result = results.FirstOrDefault(r => r.ServiceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            max += target.Weight;

            if (result?.Status == ProbeStatus.Available)
                earned += target.Weight;
            else if (result?.Status == ProbeStatus.Partial)
                earned += target.Weight * 0.4;
        }

        return max <= 0 ? 0 : Math.Clamp(earned / max * 100, 0, 100);
    }

    private static string BuildArguments(StrategyRecipe recipe, GeneratorContext context)
    {
        var sb = new StringBuilder();
        sb.Append("--wf-tcp=443,8443,2053,2083,2087,2096 ");
        if (context.QuicFake is not null)
            sb.Append("--wf-udp=443 ");

        sb.Append("--filter-tcp=443,8443,2053,2083,2087,2096 ");
        sb.Append("--dpi-desync=").Append(recipe.Desync);

        if (NeedsSplit(recipe.Desync) && !string.IsNullOrWhiteSpace(recipe.SplitPos))
            sb.Append(" --dpi-desync-split-pos=").Append(recipe.SplitPos);

        if (NeedsSplit(recipe.Desync) && recipe.SeqOvl is > 0)
        {
            sb.Append(" --dpi-desync-split-seqovl=").Append(recipe.SeqOvl.Value);
            if (context.SeqPattern is not null)
                sb.Append(" --dpi-desync-split-seqovl-pattern=\"").Append(context.SeqPattern).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(recipe.Fooling))
            sb.Append(" --dpi-desync-fooling=").Append(recipe.Fooling);

        if (recipe.Ttl is > 0)
            sb.Append(" --dpi-desync-ttl=").Append(recipe.Ttl.Value);
        else if (recipe.AutoTtl)
            sb.Append(" --dpi-desync-autottl=2");

        if (recipe.Repeats > 0)
            sb.Append(" --dpi-desync-repeats=").Append(recipe.Repeats);

        if (!string.IsNullOrWhiteSpace(recipe.FakeTlsFile) &&
            recipe.Desync.Contains("fake", StringComparison.OrdinalIgnoreCase))
        {
            var fakePath = Path.Combine(context.WorkingDirectory, recipe.FakeTlsFile);
            sb.Append(" --dpi-desync-fake-tls=\"").Append(fakePath).Append('"');

            if (!string.IsNullOrWhiteSpace(recipe.FakeTlsMod))
                sb.Append(" --dpi-desync-fake-tls-mod=").Append(recipe.FakeTlsMod);
        }

        if (context.QuicFake is not null)
        {
            sb.Append(" --new --filter-udp=443 --dpi-desync=fake --dpi-desync-repeats=6");
            sb.Append(" --dpi-desync-fake-quic=\"").Append(context.QuicFake).Append('"');
        }

        return sb.ToString();
    }

    private static bool NeedsSplit(string desync) =>
        desync.Contains("split", StringComparison.OrdinalIgnoreCase) ||
        desync.Contains("disorder", StringComparison.OrdinalIgnoreCase);

    private static StrategyPreset CreatePreset(StrategyRecipe recipe, GeneratorContext context) => new()
    {
        FilePath = Path.Combine(context.RootPath, "__rdg_generated_test.bat"),
        FileName = "RDG generated",
        DisplayName = recipe.Name,
        Family = "Generator",
        Tag = "AUTO",
        EnginePath = context.EnginePath,
        Arguments = BuildArguments(recipe, context),
        WorkingDirectory = context.WorkingDirectory,
        IsExperimental = false
    };

    private static List<StrategyRecipe> BuildSeeds(GeneratorContext context)
    {
        var result = new List<StrategyRecipe>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fakes = context.TlsFakes;
        var first = fakes.FirstOrDefault();
        var second = fakes.Skip(1).FirstOrDefault() ?? first;

        void Add(string name, string desync, string? fooling = null, string? pos = null,
            int? ovl = null, int? ttl = null, bool autoTtl = false, int repeats = 0,
            string? fake = null, string? mod = null)
        {
            if (desync.Contains("fake", StringComparison.OrdinalIgnoreCase) && fake is null)
                return;

            var recipe = new StrategyRecipe
            {
                Name = name,
                Desync = desync,
                Fooling = fooling,
                SplitPos = NeedsSplit(desync) ? pos : null,
                SeqOvl = NeedsSplit(desync) ? ovl : null,
                Ttl = ttl,
                AutoTtl = autoTtl,
                Repeats = desync.Contains("fake", StringComparison.OrdinalIgnoreCase) ? repeats : 0,
                FakeTlsFile = fake,
                FakeTlsMod = mod
            };

            if (seen.Add(recipe.Key))
                result.Add(recipe);
        }

        if (first is not null)
        {
            Add("Fake + multisplit / md5", "fake,multisplit", "md5sig", "1,midsld", 568, null, false, 6, first, "rnd,dupsid");
            Add("Fake + multisplit / badseq", "fake,multisplit", "badseq", "1", 681, null, false, 6, second, "rnd,dupsid");
            Add("Fake + multisplit / ts", "fake,multisplit", "ts", "midsld", 100, null, false, 6, first, "rnd,dupsid");
            Add("Fake + multidisorder / md5", "fake,multidisorder", "md5sig", "1,midsld", null, null, false, 6, first, "rnd,dupsid");
            Add("Fake + multidisorder / ttl", "fake,multidisorder", "badseq", "midsld", null, 3, false, 11, second, "rnd,rndsni,dupsid");
            Add("Fake + fakedsplit / ts", "fake,fakedsplit", "ts", "1", null, null, false, 6, first, "rnd,dupsid");
            Add("Fake + fakedsplit / method", "fake,fakedsplit", "badseq", "method+2", null, null, false, 6, second, "rnd,dupsid");
            Add("Fake / md5 / ttl", "fake", "md5sig", null, null, 3, false, 8, first, "rnd,dupsid");
            Add("Fake / ttl 1", "fake", null, null, null, 1, false, 8, second, "rnd,dupsid");
            Add("Fake + split2 / md5", "fake,split2", "md5sig", "1", null, null, false, 6, first, "rnd,dupsid");
            Add("Fake + multisplit / autottl", "fake,multisplit", "md5sig", "1,midsld", 568, null, true, 6, second, "rnd,rndsni,dupsid");
            Add("Fake + multidisorder / google SNI", "fake,multidisorder", "ts", "1,midsld", null, 2, false, 6, first, "rnd,dupsid,sni=www.google.com");
        }

        Add("Multisplit / 568", "multisplit", null, "1,midsld", 568);
        Add("Multisplit / 681 + md5", "multisplit", "md5sig", "1", 681);
        Add("Multisplit / 100 + ts", "multisplit", "ts", "2", 100);
        Add("Multidisorder / ts", "multidisorder", "ts", "1,midsld");
        Add("Multidisorder / md5", "multidisorder", "md5sig", "midsld");
        Add("Fakedsplit / ts", "fakedsplit", "ts", "1");
        Add("Fakedsplit / badseq", "fakedsplit", "badseq", "method+2");
        Add("Split2", "split2", null, "1");

        return result;
    }

    private static IEnumerable<StrategyRecipe> BuildNeighbors(StrategyRecipe recipe, GeneratorContext context)
    {
        var result = new List<StrategyRecipe>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(StrategyRecipe candidate)
        {
            if (candidate.Desync.Contains("fake", StringComparison.OrdinalIgnoreCase) &&
                candidate.FakeTlsFile is null)
                return;
            if (seen.Add(candidate.Key))
                result.Add(candidate);
        }

        foreach (var desync in new[]
                 {
                     "fake,multisplit", "fake,multidisorder", "fake,fakedsplit", "fake,split2",
                     "fake", "multisplit", "multidisorder", "fakedsplit", "split2"
                 })
        {
            if (desync == recipe.Desync)
                continue;
            Add(recipe with
            {
                Desync = desync,
                Repeats = desync.Contains("fake", StringComparison.OrdinalIgnoreCase) ? Math.Max(2, recipe.Repeats) : 0,
                FakeTlsFile = desync.Contains("fake", StringComparison.OrdinalIgnoreCase)
                    ? recipe.FakeTlsFile ?? context.TlsFakes.FirstOrDefault()
                    : recipe.FakeTlsFile
            });
        }

        foreach (var value in new[] { "md5sig", "badseq", "ts", "datanoack", "md5sig,badseq" })
        {
            if (!string.Equals(value, recipe.Fooling, StringComparison.Ordinal))
                Add(recipe with { Fooling = value });
        }

        foreach (var pos in new[] { "1", "2", "midsld", "1,midsld", "method+2", "host+1" })
        {
            if (NeedsSplit(recipe.Desync) && !string.Equals(pos, recipe.SplitPos, StringComparison.Ordinal))
                Add(recipe with { SplitPos = pos });
        }

        foreach (var ovl in new[] { 1, 5, 100, 568, 681, 1000 })
        {
            if (NeedsSplit(recipe.Desync) && recipe.SeqOvl != ovl)
                Add(recipe with { SeqOvl = ovl });
        }

        foreach (var ttl in new int?[] { null, 1, 2, 3, 5, 8 })
        {
            if (recipe.Ttl != ttl || recipe.AutoTtl)
                Add(recipe with { Ttl = ttl, AutoTtl = false });
        }

        if (!recipe.AutoTtl)
            Add(recipe with { Ttl = null, AutoTtl = true });

        foreach (var repeats in new[] { 2, 4, 6, 8, 11 })
        {
            if (recipe.Repeats != repeats)
                Add(recipe with { Repeats = repeats });
        }

        foreach (var fake in context.TlsFakes)
        {
            if (!string.Equals(fake, recipe.FakeTlsFile, StringComparison.OrdinalIgnoreCase))
                Add(recipe with { FakeTlsFile = fake });
        }

        foreach (var mod in new[] { "rnd,dupsid", "rnd,rndsni,dupsid", "rnd,dupsid,sni=www.google.com" })
        {
            if (!string.Equals(mod, recipe.FakeTlsMod, StringComparison.Ordinal))
                Add(recipe with { FakeTlsMod = mod });
        }

        return result;
    }

    private static GeneratorContext? CreateContext(ZapretInstallation installation)
    {
        if (string.IsNullOrWhiteSpace(installation.WinwsPath) || !File.Exists(installation.WinwsPath))
            return null;

        var workingDirectory = Path.GetDirectoryName(installation.WinwsPath) ?? installation.RootPath;
        var tlsFiles = new[]
            {
                "tls_clienthello_www_google_com.bin",
                "tls_clienthello_4pda_to.bin",
                "tls_clienthello_www_sferum_ru.bin"
            }
            .Select(name => Path.Combine(workingDirectory, name))
            .Where(File.Exists)
            .ToList();
        var quicPath = Path.Combine(workingDirectory, "quic_initial_www_google_com.bin");
        var seqPattern = tlsFiles.FirstOrDefault();

        return new GeneratorContext(
            installation.RootPath,
            installation.WinwsPath,
            workingDirectory,
            tlsFiles,
            File.Exists(quicPath) ? quicPath : null,
            seqPattern);
    }

    private static string Shorten(string value)
    {
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length > 160 ? value[..160] + "…" : value;
    }

    private sealed record GeneratorContext(
        string RootPath,
        string EnginePath,
        string WorkingDirectory,
        IReadOnlyList<string> TlsFakes,
        string? QuicFake,
        string? SeqPattern);
}
