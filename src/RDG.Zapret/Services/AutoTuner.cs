using System.Diagnostics;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class AutoTuner
{
    /// <summary>Ниже этого балла стратегия считается бесполезной и отсеивается.</summary>
    public const double MinimumUsefulScore = 50;

    private readonly WinwsController _winws;
    private readonly ServiceProbe _probe;
    private readonly LogService _log;

    public AutoTuner(WinwsController winws, ServiceProbe probe, LogService log)
    {
        _winws = winws;
        _probe = probe;
        _log = log;
    }

    public async Task<TuneReport> RunAsync(
        IReadOnlyList<StrategyPreset> candidates,
        TuneRequest request,
        IProgress<TuneUpdate> progress,
        CancellationToken cancellationToken)
    {
        var report = new TuneReport
        {
            StartedAt = DateTime.Now,
            Mode = request.Mode
        };

        try
        {
            progress.Report(new TuneUpdate(TunePhase.Preparing, Total: candidates.Count, Message: "Подготовка…"));
            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            await Task.Delay(900, cancellationToken).ConfigureAwait(true);

            progress.Report(new TuneUpdate(TunePhase.Baseline, Total: candidates.Count,
                Message: "Проверяю сервисы без обхода…"));

            var baseline = await _probe.CheckManyAsync(
                request.Targets,
                new ProbeOptions { TimeoutSeconds = request.TimeoutSeconds, ClassifyWithTls = false },
                cancellationToken).ConfigureAwait(true);

            report.Baseline = baseline;
            var baselineOk = baseline.Where(b => b.IsSuccess).Select(b => b.ServiceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var baselineOkNames = string.Join(", ", request.Targets.Where(t => baselineOk.Contains(t.Id)).Select(t => t.Name));
            _log.Info("tuner", baselineOkNames.Length == 0
                ? "Baseline: все сервисы недоступны без обхода"
                : $"Baseline: уже доступны без обхода — {baselineOkNames}");

            progress.Report(new TuneUpdate(TunePhase.Baseline, Total: candidates.Count,
                Baseline: baseline, Message: "Baseline готов"));

            if (request.SkipBaselineOk && baselineOk.Count == request.Targets.Count && request.Targets.Count > 0)
            {
                report.FinishedAt = DateTime.Now;
                report.Best = null;
                _log.Success("tuner", "Все проверяемые сервисы уже доступны без zapret — автоподбор не требуется");
                progress.Report(new TuneUpdate(TunePhase.Finished, Total: candidates.Count,
                    Baseline: baseline, Message: "Все сервисы уже доступны без обхода"));
                return report;
            }

            var candidatesToTest = candidates;

            if (request.TwoStage)
            {
                var screenTarget = PickScreenTarget(request, baselineOk);
                progress.Report(new TuneUpdate(TunePhase.Screening, Total: candidates.Count,
                    Baseline: baseline,
                    Screened: 0,
                    Message: $"Этап 1 из 2 · быстрая проверка всех стратегий через {screenTarget.Name}"));

                await ScreenAsync(candidates, screenTarget, request, report, progress, cancellationToken)
                    .ConfigureAwait(true);

                var finalistResults = report.ScreenResults
                    .Where(s => s.Status is ProbeStatus.Available or ProbeStatus.Partial)
                    .OrderByDescending(s => s.Score)
                    .ThenByDescending(s => s.Status == ProbeStatus.Available)
                    .ThenBy(s => s.Youtube.LatencyMs ?? int.MaxValue)
                    .Take(Math.Max(1, request.FinalistLimit))
                    .ToList();

                foreach (var finalist in finalistResults)
                    finalist.IsFinalist = true;

                var finalistPresets = finalistResults.Select(f => f.Preset).ToList();

                // Known-good presets from previous runs always get a full probe,
                // even if the quick screen had a transient timeout.
                foreach (var seedFile in request.SeedPresetFiles)
                {
                    if (finalistPresets.Count >= Math.Max(1, request.FinalistLimit))
                        break;

                    var seed = candidates.FirstOrDefault(p =>
                        p.FileName.Equals(seedFile, StringComparison.OrdinalIgnoreCase));
                    if (seed is null ||
                        finalistPresets.Any(p => p.FileName.Equals(seed.FileName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    finalistPresets.Add(seed);
                }

                // If the quick screen produced no finalists (e.g. all YouTube
                // checks timed out), still fully probe the best candidates by
                // previous score instead of ending with nothing.
                if (finalistPresets.Count == 0)
                {
                    finalistPresets = candidates
                        .OrderByDescending(p => report.ScreenResults
                            .FirstOrDefault(s => s.Preset.FileName.Equals(p.FileName, StringComparison.OrdinalIgnoreCase))
                            ?.Score ?? 0)
                        .Take(Math.Clamp(request.FinalistLimit, 1, 4))
                        .ToList();
                    _log.Warn("tuner",
                        $"Быстрая проверка не дала финалистов — полностью проверяю {finalistPresets.Count} лучших кандидатов");
                }

                report.FinalistLimit = request.FinalistLimit;
                candidatesToTest = finalistPresets;

                _log.Info("tuner",
                    $"Быстрая проверка завершена: {report.ScreenedCount} стратегий, в финал вышли {candidatesToTest.Count}");

                progress.Report(new TuneUpdate(TunePhase.Finalists, Total: candidatesToTest.Count,
                    Baseline: baseline,
                    Screened: report.ScreenedCount,
                    FinalistTotal: candidatesToTest.Count,
                    Message: candidatesToTest.Count == 0
                        ? "Ни одна стратегия не прошла быструю проверку"
                        : $"Этап 2 из 2 · полная проверка финалистов: {candidatesToTest.Count}"));

                if (candidatesToTest.Count == 0)
                {
                    report.FinishedAt = DateTime.Now;
                    await _winws.StopAsync(quiet: true).ConfigureAwait(true);
                    _log.Warn("tuner", "Ни одна стратегия не прошла быструю проверку YouTube");
                    progress.Report(new TuneUpdate(TunePhase.Finished, Total: candidates.Count,
                        Baseline: baseline, Screened: report.ScreenedCount,
                        Message: "Ни одна стратегия не прошла быструю проверку YouTube"));
                    return report;
                }
            }

            var index = 0;
            foreach (var preset in candidatesToTest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var phase = request.TwoStage ? TunePhase.Finalists : TunePhase.Testing;
                var stopwatch = Stopwatch.StartNew();
                progress.Report(new TuneUpdate(phase, index, candidatesToTest.Count, preset,
                    Baseline: baseline, Screened: report.ScreenedCount,
                    FinalistTotal: candidatesToTest.Count,
                    Message: request.TwoStage
                        ? $"Финалист {index} из {candidatesToTest.Count}: «{preset.DisplayName}»"
                        : $"Запуск «{preset.DisplayName}»"));

                var startOutcome = await _winws.StartAsync(preset, cancellationToken).ConfigureAwait(true);
                if (!startOutcome.Success)
                {
                    var failed = new TuneResult
                    {
                        Preset = preset,
                        Score = 0,
                        Note = string.IsNullOrWhiteSpace(startOutcome.Error) ? "не запустилась" : startOutcome.Error!,
                        Duration = stopwatch.Elapsed
                    };
                    report.Results.Add(failed);
                    _log.Warn("tuner", $"{preset.DisplayName}: не запустилась — {failed.Note}");
                    progress.Report(new TuneUpdate(phase, index, candidatesToTest.Count, preset, failed,
                        Baseline: baseline, Screened: report.ScreenedCount,
                        FinalistTotal: candidatesToTest.Count, Message: failed.Note));
                    await _winws.StopAsync(quiet: true).ConfigureAwait(true);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(request.WarmupSeconds), cancellationToken).ConfigureAwait(true);

                if (!_winws.IsRunning)
                {
                    var failed = new TuneResult
                    {
                        Preset = preset,
                        Score = 0,
                        Note = "winws завершился после запуска",
                        Duration = stopwatch.Elapsed
                    };
                    report.Results.Add(failed);
                    _log.Warn("tuner", $"{preset.DisplayName}: winws завершился после запуска");
                    progress.Report(new TuneUpdate(phase, index, candidatesToTest.Count, preset, failed,
                        Baseline: baseline, Screened: report.ScreenedCount,
                        FinalistTotal: candidatesToTest.Count, Message: failed.Note));
                    await _winws.StopAsync(quiet: true).ConfigureAwait(true);
                    continue;
                }

                IReadOnlyList<ProbeResult> results;
                try
                {
                    results = await RunPassesAsync(request, cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn("tuner", $"{preset.DisplayName}: ошибка проверки — {ex.Message}");
                    results = request.Targets
                        .Select(t => new ProbeResult
                        {
                            ServiceId = t.Id,
                            Status = ProbeStatus.Error,
                            Detail = ex.Message
                        })
                        .ToList();
                }

                var score = ComputeScore(request.Targets, results, baselineOk, request.SkipBaselineOk);
                var note = BuildNote(score, results);
                var result = new TuneResult
                {
                    Preset = preset,
                    Score = score,
                    Note = note,
                    Services = results,
                    Duration = stopwatch.Elapsed
                };

                report.Results.Add(result);
                _log.Info("tuner",
                    $"{preset.DisplayName}: {score:0}/100 · {result.AvailableCount}/{result.TotalCount} сервисов · {stopwatch.Elapsed.TotalSeconds:0.0} с");

                progress.Report(new TuneUpdate(phase, index, candidatesToTest.Count, preset, result,
                    Baseline: baseline, Screened: report.ScreenedCount,
                    FinalistTotal: candidatesToTest.Count,
                    Message: $"{score:0}/100 · {result.AvailableCount}/{result.TotalCount}"));

                await _winws.StopAsync(quiet: true).ConfigureAwait(true);

                await Task.Delay(650, cancellationToken).ConfigureAwait(true);
            }


            var best = report.Results
                .Where(r => r.IsStable && r.Score >= MinimumUsefulScore)
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.AvailableCount)
                .FirstOrDefault();

            if (best is not null)
            {
                best.IsBest = true;
                report.Best = best;
            }

            if (best is not null && request.KeepBestRunning)
            {
                progress.Report(new TuneUpdate(TunePhase.Applying, Total: candidates.Count, Best: best,
                    Baseline: baseline, Message: $"Запускаю «{best.Preset.DisplayName}»"));
                var apply = await _winws.StartAsync(best.Preset, cancellationToken).ConfigureAwait(true);
                report.BestApplied = apply.Success;
                if (!apply.Success)
                {
                    _log.Error("tuner", $"Не удалось оставить лучшую стратегию включённой: {apply.Error}");
                }
            }
            else
            {
                await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            }

            report.FinishedAt = DateTime.Now;
            _log.Success("tuner", best is null
                ? "Автоподбор завершён: рабочая стратегия не найдена"
                : $"Автоподбор завершён: «{best.Preset.DisplayName}» — {best.Score:0}/100" +
                  (report.BestApplied ? " (запущена)" : string.Empty));

            progress.Report(new TuneUpdate(TunePhase.Finished, Total: candidates.Count, Best: best,
                Baseline: baseline, Message: report.BestApplied ? "Лучшая стратегия запущена" : "Автоподбор завершён"));

            return report;
        }
        catch (OperationCanceledException)
        {
            report.Canceled = true;
            report.FinishedAt = DateTime.Now;

            // Keep the best completed result so the user can apply it manually
            // after stopping a long full pass.
            var partialBest = report.Results
                .Where(r => r.IsStable && r.Score >= MinimumUsefulScore)
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.AvailableCount)
                .FirstOrDefault();
            if (partialBest is not null)
            {
                partialBest.IsBest = true;
                report.Best = partialBest;
            }

            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            _log.Warn("tuner", "Автоподбор отменён");
            progress.Report(new TuneUpdate(TunePhase.Canceled, Baseline: report.Baseline, Message: "Автоподбор отменён"));
            return report;
        }
        catch (Exception ex)
        {
            report.FinishedAt = DateTime.Now;
            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
            _log.Error("tuner", "Ошибка автоподбора: " + ex.Message);
            progress.Report(new TuneUpdate(TunePhase.Failed, Baseline: report.Baseline, Message: ex.Message));
            return report;
        }
    }

    private async Task ScreenAsync(
        IReadOnlyList<StrategyPreset> candidates,
        ServiceTarget screenTarget,
        TuneRequest request,
        TuneReport report,
        IProgress<TuneUpdate> progress,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var preset in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            progress.Report(new TuneUpdate(TunePhase.Screening, index, candidates.Count, preset,
                Screened: index - 1,
                Message: $"Быстрая проверка: {preset.DisplayName}"));

            var start = await _winws.StartAsync(preset, cancellationToken).ConfigureAwait(true);
            if (!start.Success)
            {
                var failed = new TuneScreenResult
                {
                    Preset = preset,
                    Youtube = new ProbeResult
                    {
                        ServiceId = screenTarget.Id,
                        Status = ProbeStatus.Error,
                        Detail = start.Error ?? "не запустилась"
                    },
                    Score = 0,
                    Note = string.IsNullOrWhiteSpace(start.Error) ? "не запустилась" : start.Error!
                };
                report.ScreenResults.Add(failed);
                report.ScreenedCount = index;
                progress.Report(new TuneUpdate(TunePhase.Screening, index, candidates.Count, preset,
                    Screened: index, ScreenCompleted: failed, Message: failed.Note));
                await _winws.StopAsync(quiet: true).ConfigureAwait(true);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(request.ScreenWarmupSeconds), cancellationToken).ConfigureAwait(true);

            ProbeResult youtube;
            if (!_winws.IsRunning)
            {
                youtube = new ProbeResult
                {
                    ServiceId = screenTarget.Id,
                    Status = ProbeStatus.Error,
                    Detail = "winws завершился после запуска"
                };
            }
            else
            {
                try
                {
                    youtube = await _probe.CheckAsync(
                        screenTarget,
                        new ProbeOptions
                        {
                            TimeoutSeconds = Math.Max(2, request.ScreenTimeoutSeconds),
                            ClassifyWithTls = false,
                            ScreenOnly = true
                        },
                        cancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    youtube = new ProbeResult
                    {
                        ServiceId = screenTarget.Id,
                        Status = ProbeStatus.Error,
                        Detail = ex.Message
                    };
                }
            }

            var score = youtube.Status switch
            {
                ProbeStatus.Available => 100 - Math.Clamp((youtube.LatencyMs ?? 0) / 100.0, 0, 20),
                ProbeStatus.Partial => 45,
                _ => 0
            };

            var note = youtube.Status switch
            {
                ProbeStatus.Available => $"YouTube доступен · {youtube.LatencyMs ?? 0} мс",
                ProbeStatus.Partial => "YouTube доступен частично",
                ProbeStatus.Timeout => "YouTube: тайм-аут",
                ProbeStatus.Blocked => "YouTube: соединение сброшено",
                ProbeStatus.Dns => "YouTube: DNS-ошибка",
                ProbeStatus.Tls => "YouTube: TLS-блокировка",
                _ => "YouTube недоступен"
            };

            var screen = new TuneScreenResult
            {
                Preset = preset,
                Youtube = youtube,
                Score = score,
                Note = note
            };

            report.ScreenResults.Add(screen);
            report.ScreenedCount = index;
            _log.Info("tuner", $"Быстро · {preset.DisplayName}: {note}");

            progress.Report(new TuneUpdate(TunePhase.Screening, index, candidates.Count, preset,
                Screened: index, ScreenCompleted: screen, Message: note));

            await _winws.StopAsync(quiet: true).ConfigureAwait(true);
        }
    }

    private static ServiceTarget PickScreenTarget(TuneRequest request, IReadOnlySet<string> baselineOk)
    {
        var notBaseline = request.Targets
            .Where(t => !baselineOk.Contains(t.Id))
            .ToList();

        return notBaseline.FirstOrDefault(t => t.Id.Equals("youtube", StringComparison.OrdinalIgnoreCase))
               ?? notBaseline.FirstOrDefault()
               ?? request.Targets.FirstOrDefault(t => t.Id.Equals("youtube", StringComparison.OrdinalIgnoreCase))
               ?? request.Targets.First();
    }

    private async Task<IReadOnlyList<ProbeResult>> RunPassesAsync(TuneRequest request, CancellationToken cancellationToken)
    {
        var merged = new List<ProbeResult>();

        for (var pass = 0; pass < Math.Max(1, request.Passes); pass++)
        {
            var current = await _probe.CheckManyAsync(
                request.Targets,
                new ProbeOptions { TimeoutSeconds = request.TimeoutSeconds, ClassifyWithTls = false, LightDiscord = true },
                cancellationToken).ConfigureAwait(true);

            if (pass == 0)
            {
                merged.AddRange(current);
                continue;
            }

            var next = new List<ProbeResult>(merged.Count);
            foreach (var previous in merged)
            {
                var other = current.FirstOrDefault(c => c.ServiceId == previous.ServiceId);
                next.Add(other is null ? previous : MergeWorst(previous, other));
            }

            merged = next;
        }

        return merged;
    }

    private static ProbeResult MergeWorst(ProbeResult first, ProbeResult second)
    {
        var chosen = Rank(second.Status) < Rank(first.Status) ? second : first;
        var latency = first.LatencyMs is null && second.LatencyMs is null
            ? (int?)null
            : Math.Max(first.LatencyMs ?? 0, second.LatencyMs ?? 0);

        return new ProbeResult
        {
            ServiceId = chosen.ServiceId,
            Status = chosen.Status,
            LatencyMs = latency,
            DnsMs = chosen.DnsMs,
            TcpMs = chosen.TcpMs,
            TtfbMs = chosen.TtfbMs,
            Detail = chosen.Detail,
            Timestamp = DateTime.Now
        };
    }

    private static int Rank(ProbeStatus status) => status switch
    {
        ProbeStatus.Available => 5,
        ProbeStatus.Partial => 4,
        ProbeStatus.Timeout => 1,
        ProbeStatus.Blocked => 2,
        ProbeStatus.Dns => 2,
        ProbeStatus.Tls => 2,
        ProbeStatus.Error => 1,
        _ => 0
    };

    private static double ComputeScore(
        IReadOnlyList<ServiceTarget> targets,
        IReadOnlyList<ProbeResult> results,
        IReadOnlySet<string> baselineOk,
        bool skipBaselineOk)
    {
        double earned = 0;
        double max = 0;

        foreach (var target in targets)
        {
            var wasOk = baselineOk.Contains(target.Id);
            if (wasOk && skipBaselineOk)
                continue;

            var result = results.FirstOrDefault(r => r.ServiceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            max += target.Weight;

            if (wasOk)
            {
                if (result?.IsSuccess == true)
                    earned += target.Weight;
                else
                    earned -= target.Weight * 0.75; // do not break what already worked
                continue;
            }

            switch (result?.Status)
            {
                case ProbeStatus.Available:
                    var latency = result.LatencyMs ?? 0;
                    var latencyFactor = Math.Clamp(1 - latency / 3000.0, 0, 1);
                    earned += target.Weight * (0.85 + 0.15 * latencyFactor);
                    break;
                case ProbeStatus.Partial:
                    earned += target.Weight * 0.45;
                    break;
            }
        }

        if (max <= 0)
            return 100;

        return Math.Clamp(earned / max * 100, 0, 100);
    }

    private static string BuildNote(double score, IReadOnlyList<ProbeResult> results)
    {
        var failed = results.Where(r => !r.IsSuccess).Select(r => r.ServiceId).ToList();
        if (score >= 90) return "Отличный результат";
        if (score >= 70) return failed.Count == 0 ? "Работает стабильно" : $"Не работает: {failed.Count}";
        if (score >= 40) return "Частично работает";
        return "Не подходит";
    }
}
