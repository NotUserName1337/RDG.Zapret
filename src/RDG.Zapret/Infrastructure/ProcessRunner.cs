using System.Diagnostics;
using System.Text;

namespace RDG.Zapret.Infrastructure;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        int timeoutMs = 20000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process is null)
                return new ProcessResult(-1, "Не удалось запустить процесс");

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }

                return new ProcessResult(-1, "Тайм-аут выполнения", TimedOut: true);
            }

            var output = string.Join(Environment.NewLine,
                new[] { await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false) }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
                .Trim();

            return new ProcessResult(process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, ex.Message);
        }
    }
}
