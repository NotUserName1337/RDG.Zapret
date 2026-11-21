using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

/// <summary>
/// Answers "why does this site not open" using separate DNS, TCP, TLS and HTTP
/// signals measured twice. A verdict needs two concordant signals; otherwise
/// the answer is honestly "Unknown".
/// </summary>
public sealed class BlockClassifier
{
    private static readonly string[] ControlHosts = { "1.1.1.1", "8.8.8.8" };

    public async Task<BlockDiagnosis> DiagnoseAsync(string rawHost, CancellationToken cancellationToken)
    {
        var host = NormalizeHost(rawHost);
        var details = new List<string>();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new BlockDiagnosis
            {
                Host = rawHost,
                Reason = BlockReason.Unknown,
                Title = "Не понял адрес",
                Explanation = "Введите адрес сайта, например youtube.com.",
                Recommendation = "Введите домен без https:// и пути."
            };
        }

        var controlTask = CheckControlAsync(cancellationToken);
        var dns1Task = ResolveAsync(host, cancellationToken);
        var dns2Task = ResolveAsync(host, cancellationToken);
        await Task.WhenAll(controlTask, dns1Task, dns2Task).ConfigureAwait(false);

        var controlOk = controlTask.Result;
        var dns1 = dns1Task.Result;
        var dns2 = dns2Task.Result;
        var addresses = dns1.Concat(dns2)
            .Where(a => a is not null)
            .Select(a => a!)
            .Distinct()
            .Take(2)
            .ToList();

        details.Add(controlOk ? "Базовое соединение с интернетом работает" : "Нет прямого TCP-ответа от 1.1.1.1 / 8.8.8.8");
        details.Add(addresses.Count > 0
            ? "DNS вернул адрес: " + string.Join(", ", addresses)
            : "DNS не вернул адрес");

        if (!controlOk && addresses.Count == 0)
        {
            return Build(host, BlockReason.NoNetwork, null, details,
                "Нет интернета",
                "Компьютер не может установить даже базовое соединение с интернетом.",
                "Проверьте Wi-Fi или кабель, затем повторите проверку.");
        }

        if (addresses.Count == 0)
        {
            return Build(host, BlockReason.Dns, null, details,
                "Проблема с DNS",
                "Домен не удалось преобразовать в IP-адрес. Провайдер или настройки DNS возвращают ошибку.",
                "Нажмите «Сбросить DNS-кэш» ниже. Если не поможет — попробуйте сменить DNS на 1.1.1.1 в настройках сети.");
        }

        var tcpOkAny = false;
        var tcpMeasured = 0;
        var tcpDead = 0;
        var tlsOkAny = false;
        var tlsFailAfterTcp = 0;
        var tlsMeasured = 0;
        var httpStatus = 0;
        string? chosenAddress = null;

        foreach (var address in addresses)
        {
            var tcp1 = await TryTcpAsync(address, 443, cancellationToken).ConfigureAwait(false);
            var tcp2 = await TryTcpAsync(address, 443, cancellationToken).ConfigureAwait(false);
            tcpMeasured++;
            if (tcp1 || tcp2)
            {
                tcpOkAny = true;
                chosenAddress ??= address.ToString();
            }
            else
            {
                tcpDead++;
                continue;
            }

            var tls1 = await TryTlsAsync(address, host, cancellationToken).ConfigureAwait(false);
            var tls2 = await TryTlsAsync(address, host, cancellationToken).ConfigureAwait(false);
            tlsMeasured++;
            if (tls1.Success || tls2.Success)
            {
                tlsOkAny = true;
                httpStatus = tls1.Status > 0 ? tls1.Status : tls2.Status;
            }
            else
            {
                tlsFailAfterTcp++;
            }
        }

        details.Add(tcpOkAny ? "TCP-соединение с сайтом устанавливается" : "TCP-соединение с сайтом не устанавливается");
        details.Add(tlsOkAny ? "TLS-рукопожатие проходит" : "TLS-рукопожатие не проходит");
        if (httpStatus > 0)
            details.Add("Сервер ответил HTTP " + httpStatus);

        if (!tcpOkAny)
        {
            return Build(host, BlockReason.Ip, chosenAddress, details,
                "IP-адрес заблокирован",
                "Домен определяется, но соединение с его IP-адресом не устанавливается. Похоже на блокировку по IP.",
                "Включите обход или смените стратегию. Если обход уже включён — попробуйте автоподбор.");
        }

        if (!tlsOkAny)
        {
            return Build(host, BlockReason.Sni, chosenAddress, details,
                "Блокируется TLS / SNI",
                "TCP-соединение есть, но защищённое соединение с сайтом обрывается. Обычно это блокировка по имени сайта (SNI).",
                "Это как раз то, что исправляет zapret. Включите обход или запустите автоподбор.");
        }

        if (httpStatus >= 500)
        {
            return Build(host, BlockReason.ServerDown, chosenAddress, details,
                "Сайт временно недоступен",
                $"Соединение проходит, но сервер отвечает ошибкой HTTP {httpStatus}. Скорее всего, проблема на стороне сайта.",
                "Подождите и попробуйте позже. Обход здесь не поможет.");
        }

        if (httpStatus > 0)
        {
            return Build(host, BlockReason.Open, chosenAddress, details,
                "Сайт открывается",
                $"Соединение и TLS работают, сервер ответил HTTP {httpStatus}.",
                "Блокировки не обнаружено.");
        }

        return Build(host, BlockReason.Unknown, chosenAddress, details,
            "Причина не определена",
            "Сигналы противоречат друг другу, поэтому точный диагноз дать нельзя.",
            "Попробуйте повторить проверку. Если сайт не открывается — включите обход и проверьте снова.");
    }

    private static BlockDiagnosis Build(
        string host,
        BlockReason reason,
        string? address,
        List<string> details,
        string title,
        string explanation,
        string recommendation) => new()
    {
        Host = host,
        Reason = reason,
        Address = address,
        Details = details,
        Title = title,
        Explanation = explanation,
        Recommendation = recommendation
    };

    private static string NormalizeHost(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value.TrimStart('/');

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return string.Empty;

        return uri.Host.Trim('.');
    }

    private static async Task<bool> CheckControlAsync(CancellationToken cancellationToken)
    {
        foreach (var host in ControlHosts)
        {
            if (await TryTcpAsync(IPAddress.Parse(host), 53, cancellationToken, 1800).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private static async Task<IPAddress?[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            var chosen = v4.Count > 0 ? v4 : addresses.ToList();
            return chosen.Take(2).Select(a => (IPAddress?)a).ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress?>();
        }
    }

    private static async Task<bool> TryTcpAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken,
        int timeoutMs = 3500)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<TlsAttempt> TryTlsAsync(
        IPAddress address,
        string host,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(address, 443, cts.Token).ConfigureAwait(false);

            await using var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            }, cts.Token).ConfigureAwait(false);

            var request = Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: RDG.Zapret/{UpdateService.CurrentVersionText}\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(request, cts.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var read = await ssl.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
            if (read <= 0)
                return new TlsAttempt(false, 0);

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var firstLine = text.Split('\n')[0];
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var status = parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
            return new TlsAttempt(true, status);
        }
        catch (AuthenticationException)
        {
            return new TlsAttempt(false, 0);
        }
        catch
        {
            return new TlsAttempt(false, 0);
        }
    }

    private sealed record TlsAttempt(bool Success, int Status);
}
