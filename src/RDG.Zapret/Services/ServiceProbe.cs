using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using RDG.Zapret.Models;

namespace RDG.Zapret.Services;

public sealed class ServiceProbe
{
    public async Task<IReadOnlyList<ProbeResult>> CheckManyAsync(
        IReadOnlyList<ServiceTarget> targets,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(options);
        var tasks = targets.Select(t => CheckWithClientAsync(client, t, options, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<ProbeResult> CheckAsync(
        ServiceTarget target,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(options);
        return await CheckWithClientAsync(client, target, options, cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateClient(ProbeOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds + 2, 3, 15)),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 RDG.Zapret/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8");
        return client;
    }

    private static async Task<ProbeResult> CheckWithClientAsync(
        HttpClient client,
        ServiceTarget target,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        // YouTube: a plain generate_204 check only proves the homepage is reachable.
        // The watch page lets us verify that video playback data is actually served.
        if (target.Id.Equals("youtube", StringComparison.OrdinalIgnoreCase))
            return options.ScreenOnly
                ? await CheckYoutubeScreenAsync(client, target, options, cancellationToken).ConfigureAwait(false)
                : await CheckYoutubeAsync(client, target, options, cancellationToken).ConfigureAwait(false);

        var quick = await CheckHttpBasicAsync(client, target, options, cancellationToken).ConfigureAwait(false);

        // Discord: HTTP API is not enough for voice. Add the real-time gateway
        // WebSocket and a STUN/UDP probe so "app opens but voice is blocked"
        // is visible in the result.
        if (target.Id.Equals("discord", StringComparison.OrdinalIgnoreCase))
        {
            if (!quick.IsSuccess)
            {
                // A single transient timeout should not mark the whole
                // strategy as unstable during a long autotune pass.
                try
                {
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    quick = await CheckHttpBasicAsync(client, target, options, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // keep the first result
                }
            }

            if (quick.Status == ProbeStatus.Available)
                return await CheckDiscordDeepAsync(client, target, quick, options, cancellationToken).ConfigureAwait(false);
        }

        return quick;
    }

    private static async Task<ProbeResult> CheckHttpBasicAsync(
        HttpClient client,
        ServiceTarget target,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var phases = options.MeasurePhases
            ? await MeasurePhasesAsync(target.TlsHost, 443, "/", options.TimeoutSeconds, cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();
            var code = (int)response.StatusCode;

            if (code == 451)
            {
                return new ProbeResult
                {
                    ServiceId = target.Id,
                    Status = ProbeStatus.Partial,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    DnsMs = phases?.DnsMs,
                    TcpMs = phases?.TcpMs,
                    TtfbMs = phases?.TtfbMs,
                    Detail = "HTTP 451 · ограничено провайдером"
                };
            }

            if (code >= 500)
            {
                return new ProbeResult
                {
                    ServiceId = target.Id,
                    Status = ProbeStatus.Partial,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    DnsMs = phases?.DnsMs,
                    TcpMs = phases?.TcpMs,
                    TtfbMs = phases?.TtfbMs,
                    Detail = $"HTTP {code} · сервер недоступен"
                };
            }

            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = ProbeStatus.Available,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                DnsMs = phases?.DnsMs,
                TcpMs = phases?.TcpMs,
                TtfbMs = phases?.TtfbMs,
                Detail = $"HTTP {code}"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return await BuildFailureAsync(client, target, options, ProbeStatus.Timeout, "Тайм-аут соединения", cancellationToken, phases)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var (status, detail) = MapException(ex);
            return await BuildFailureAsync(client, target, options, status, detail, cancellationToken, phases).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return await BuildFailureAsync(client, target, options, ProbeStatus.Error, Shorten(ex.Message), cancellationToken, phases)
                .ConfigureAwait(false);
        }
    }

    private static async Task<ProbeResult> CheckYoutubeScreenAsync(
        HttpClient client,
        ServiceTarget target,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 3));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://www.youtube.com/watch?v=aqz-KE-bpKQ&hl=en&bpctr=9999999999");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+cb.20210328-17-p0.en+FX+123");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();
            var code = (int)response.StatusCode;
            return code is >= 200 and < 500
                ? new ProbeResult
                {
                    ServiceId = target.Id,
                    Status = ProbeStatus.Available,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    Detail = "YouTube отвечает"
                }
                : new ProbeResult
                {
                    ServiceId = target.Id,
                    Status = ProbeStatus.Partial,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    Detail = $"HTTP {code}"
                };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = ProbeStatus.Timeout,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                Detail = "YouTube: тайм-аут"
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var (status, detail) = MapException(ex);
            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = status,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                Detail = detail
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = ProbeStatus.Error,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                Detail = Shorten(ex.Message)
            };
        }
    }

    private static async Task<ProbeResult> CheckYoutubeAsync(
        HttpClient client,
        ServiceTarget target,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        // Big Buck Bunny by Blender: public, long-lived and available in most regions.
        const string videoId = "aqz-KE-bpKQ";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds + 1, 5, 9));
        var stopwatch = Stopwatch.StartNew();
        var phases = options.MeasurePhases
            ? await MeasurePhasesAsync("www.youtube.com", 443, "/", options.TimeoutSeconds, cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://www.youtube.com/watch?v={videoId}&hl=en&bpctr=9999999999");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+cb.20210328-17-p0.en+FX+123");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 RDG.Zapret/1.0");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var code = (int)response.StatusCode;
            if (code >= 500)
            {
                stopwatch.Stop();
                return new ProbeResult
                {
                    ServiceId = target.Id,
                    Status = ProbeStatus.Partial,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    DnsMs = phases?.DnsMs,
                    TcpMs = phases?.TcpMs,
                    TtfbMs = phases?.TtfbMs,
                    Detail = $"HTTP {code} · сервер YouTube недоступен"
                };
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var videoOk = html.Contains("\"status\":\"OK\"", StringComparison.Ordinal) &&
                          html.Contains("\"streamingData\"", StringComparison.Ordinal);

            if (videoOk)
            {
                return new ProbeResult
                {
                    ServiceId = target.Id,
                    Status = ProbeStatus.Available,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    DnsMs = phases?.DnsMs,
                    TcpMs = phases?.TcpMs,
                    TtfbMs = phases?.TtfbMs,
                    Detail = "Видео и поток YouTube доступны"
                };
            }

            var statusMatch = Regex.Match(
                html,
                "\"playabilityStatus\"\\s*:\\s*\\{\\s*\"status\"\\s*:\\s*\"(?<status>[A-Z_]+)\"");
            var reason = statusMatch.Success ? statusMatch.Groups["status"].Value : "видео недоступно";

            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = ProbeStatus.Partial,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                DnsMs = phases?.DnsMs,
                TcpMs = phases?.TcpMs,
                TtfbMs = phases?.TtfbMs,
                Detail = $"Страница открывается, видео недоступно ({reason})"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return await BuildFailureAsync(
                    client, target, options, ProbeStatus.Timeout,
                    "Тайм-аут загрузки страницы видео YouTube", cancellationToken, phases)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var (status, detail) = MapException(ex);
            return await BuildFailureAsync(client, target, options, status, detail, cancellationToken, phases)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return await BuildFailureAsync(
                    client, target, options, ProbeStatus.Error, Shorten(ex.Message), cancellationToken, phases)
                .ConfigureAwait(false);
        }
    }

    private static async Task<ProbeResult> CheckDiscordDeepAsync(
        HttpClient client,
        ServiceTarget target,
        ProbeResult quick,
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var gatewayTask = options.LightDiscord
            ? Task.FromResult(true)
            : ProbeDiscordGatewayAsync(cancellationToken);
        var udpTask = ProbeUdpVoicePathAsync(cancellationToken);
        var voiceTask = options.MeasurePhases
            ? MeasurePhasesAsync("discord.com", 443, "/api/v10/voice/regions", 3, cancellationToken)
            : Task.FromResult(PhaseMetrics.Empty);
        var voiceHostTask = options.MeasurePhases
            ? MeasurePhasesAsync(ServiceCatalog.DiscordVoice.TlsHost, 443, "/", 2, cancellationToken)
            : Task.FromResult(PhaseMetrics.Empty);
        await Task.WhenAll(gatewayTask, udpTask, voiceTask, voiceHostTask).ConfigureAwait(false);

        var gatewayOk = gatewayTask.Result;
        var udpOk = udpTask.Result;
        var voice = voiceTask.Result;
        var voiceHost = voiceHostTask.Result;
        stopwatch.Stop();

        var voiceOk = !options.MeasurePhases || voice.Success;
        var status = gatewayOk && udpOk && voiceOk ? ProbeStatus.Available : ProbeStatus.Partial;
        var detail = options.LightDiscord
            ? (udpOk, voiceOk) switch
            {
                (true, true) => "API и UDP для голоса доступны",
                (false, _) => "API работает, UDP/голос может не работать",
                _ => "API работает, голосовой сервис Discord не отвечает"
            }
            : (gatewayOk, udpOk, voiceOk) switch
            {
                (true, true, true) => options.MeasurePhases
                    ? "API, шлюз, голосовые регионы и UDP доступны"
                    : "API, шлюз и UDP для голоса доступны",
                (true, false, _) => "Приложение и шлюз работают, UDP/голос может не работать",
                (false, true, _) => "UDP доступен, WebSocket шлюза Discord не отвечает",
                (true, true, false) => "Чат работает, голосовой сервис Discord не отвечает",
                _ => "API работает, шлюз, голос и UDP недоступны"
            };

        if (options.MeasurePhases && voiceHost.Success)
            detail += " · voice.discord.gg отвечает";

        return new ProbeResult
        {
            ServiceId = target.Id,
            Status = status,
            LatencyMs = (int)(quick.LatencyMs ?? 0) + (int)stopwatch.ElapsedMilliseconds,
            DnsMs = voice.DnsMs ?? quick.DnsMs,
            TcpMs = voice.TcpMs ?? quick.TcpMs,
            TtfbMs = voice.TtfbMs ?? quick.TtfbMs,
            Detail = detail
        };
    }

    private static async Task<bool> ProbeDiscordGatewayAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 RDG.Zapret/1.0");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            await socket.ConnectAsync(
                new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), cts.Token).ConfigureAwait(false);

            var buffer = new byte[4096];
            var receive = await socket
                .ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)
                .ConfigureAwait(false);

            if (receive.MessageType != WebSocketMessageType.Text)
                return false;

            var text = Encoding.UTF8.GetString(buffer, 0, receive.Count);
            return text.Contains("\"op\":10", StringComparison.Ordinal) ||
                   text.Contains("\"op\": 10", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ProbeUdpVoicePathAsync(CancellationToken cancellationToken)
    {
        // A STUN binding response proves that outbound UDP and the NAT path work.
        // Discord voice uses UDP; the main gateway above only covers TCP/WebSocket.
        foreach (var (host, port) in new[] { ("stun.l.google.com", 19302), ("stun.cloudflare.com", 3478) })
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (address is null)
                    continue;

                using var udp = new UdpClient(address.AddressFamily);
                udp.Connect(address, port);

                var request = new byte[20];
                request[0] = 0x00;
                request[1] = 0x01;
                request[4] = 0x21;
                request[5] = 0x12;
                request[6] = 0xA4;
                request[7] = 0x42;
                RandomNumberGenerator.Fill(request.AsSpan(8, 12));

                await udp.SendAsync(request, request.Length).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var response = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (response.Buffer.Length >= 20)
                    return true;
            }
            catch
            {
                // try the next STUN server
            }
        }

        return false;
    }

    private static async Task<ProbeResult> BuildFailureAsync(
        HttpClient client,
        ServiceTarget target,
        ProbeOptions options,
        ProbeStatus status,
        string detail,
        CancellationToken cancellationToken,
        PhaseMetrics? phases = null)
    {
        if (!options.ClassifyWithTls)
        {
            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = status,
                DnsMs = phases?.DnsMs,
                TcpMs = phases?.TcpMs,
                TtfbMs = phases?.TtfbMs,
                Detail = detail
            };
        }

        var tls = await ProbeTlsAsync(target.TlsHost, Math.Min(4, options.TimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        if (tls.Success)
        {
            return new ProbeResult
            {
                ServiceId = target.Id,
                Status = ProbeStatus.Partial,
                DnsMs = phases?.DnsMs,
                TcpMs = phases?.TcpMs,
                TtfbMs = phases?.TtfbMs,
                Detail = "TLS работает, но HTTP-запрос не прошёл"
            };
        }

        return new ProbeResult
        {
            ServiceId = target.Id,
            Status = tls.Status == ProbeStatus.Timeout || tls.Status == ProbeStatus.Tls || tls.Status == ProbeStatus.Blocked
                ? tls.Status
                : status,
            DnsMs = phases?.DnsMs,
            TcpMs = phases?.TcpMs,
            TtfbMs = phases?.TtfbMs,
            Detail = tls.Detail
        };
    }

    private static async Task<TlsProbeResult> ProbeTlsAsync(string host, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await tcp.ConnectAsync(host, 443, cts.Token).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            var authOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };

            await ssl.AuthenticateAsClientAsync(authOptions, cts.Token).ConfigureAwait(false);
            return new TlsProbeResult(true, ProbeStatus.Available, $"TLS-соединение с {host} установлено");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TlsProbeResult(false, ProbeStatus.Timeout, "TLS: тайм-аут рукопожатия");
        }
        catch (AuthenticationException)
        {
            return new TlsProbeResult(false, ProbeStatus.Tls, "TLS: рукопожатие отклонено");
        }
        catch (SocketException ex)
        {
            return new TlsProbeResult(false, ProbeStatus.Blocked, "TLS: " + DescribeSocket(ex));
        }
        catch (IOException)
        {
            return new TlsProbeResult(false, ProbeStatus.Blocked, "TLS: соединение сброшено");
        }
        catch (Exception ex)
        {
            return new TlsProbeResult(false, ProbeStatus.Error, "TLS: " + Shorten(ex.Message));
        }
    }

    private static (ProbeStatus Status, string Detail) MapException(HttpRequestException ex)
    {
        if (ex.InnerException is SocketException socket)
            return (MapSocket(socket), DescribeSocket(socket));

        if (ex.InnerException is AuthenticationException)
            return (ProbeStatus.Tls, "TLS: рукопожатие отклонено");

        var message = ex.Message;
        if (message.Contains("prematurely", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("reset", StringComparison.OrdinalIgnoreCase))
            return (ProbeStatus.Blocked, "Соединение сброшено (DPI)");

        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return (ProbeStatus.Tls, "TLS: рукопожатие не прошло");

        return (ProbeStatus.Error, Shorten(message));
    }

    private static ProbeStatus MapSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery => ProbeStatus.Dns,
        SocketError.TimedOut => ProbeStatus.Timeout,
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset or SocketError.Shutdown => ProbeStatus.Blocked,
        _ => ProbeStatus.Error
    };

    private static string DescribeSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery => "Домен не резолвится (DNS)",
        SocketError.TimedOut => "Тайм-аут соединения",
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset or SocketError.Shutdown => "Соединение сброшено (DPI)",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "Сеть недоступна",
        SocketError.AccessDenied => "Доступ запрещён",
        _ => "Ошибка сокета: " + socket.SocketErrorCode
    };

    private static string Shorten(string value)
    {
        value = value.Replace(Environment.NewLine, " ").Trim();
        return value.Length > 110 ? value[..110] + "…" : value;
    }

    private static async Task<PhaseMetrics> MeasurePhasesAsync(
        string host,
        int port,
        string path,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        int? dnsMs = null;
        int? tcpMs = null;
        int? ttfbMs = null;
        string? address = null;

        try
        {
            var dnsWatch = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault();
            dnsWatch.Stop();
            dnsMs = (int)dnsWatch.ElapsedMilliseconds;

            if (ip is null)
                return new PhaseMetrics(false, dnsMs, null, null, null, "DNS не вернул адрес");

            address = ip.ToString();

            using var tcp = new TcpClient(ip.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 8)));

            var tcpWatch = Stopwatch.StartNew();
            await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            tcpWatch.Stop();
            tcpMs = (int)tcpWatch.ElapsedMilliseconds;

            await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cts.Token).ConfigureAwait(false);

            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: RDG.Zapret/{UpdateService.CurrentVersionText}\r\nAccept: */*\r\nConnection: close\r\n\r\n");

            var ttfbWatch = Stopwatch.StartNew();
            await ssl.WriteAsync(request, cts.Token).ConfigureAwait(false);
            var buffer = new byte[1];
            var read = await ssl.ReadAsync(buffer.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            ttfbWatch.Stop();
            ttfbMs = (int)ttfbWatch.ElapsedMilliseconds;

            return read > 0
                ? new PhaseMetrics(true, dnsMs, tcpMs, ttfbMs, address, null)
                : new PhaseMetrics(false, dnsMs, tcpMs, ttfbMs, address, "Сервер закрыл соединение без ответа");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PhaseMetrics(false, dnsMs, tcpMs, ttfbMs, address, "Тайм-аут");
        }
        catch (Exception ex)
        {
            return new PhaseMetrics(false, dnsMs, tcpMs, ttfbMs, address, Shorten(ex.Message));
        }
    }

    private sealed record PhaseMetrics(
        bool Success,
        int? DnsMs,
        int? TcpMs,
        int? TtfbMs,
        string? Address,
        string? Error)
    {
        public static PhaseMetrics Empty { get; } = new(false, null, null, null, null, null);
    }

    private sealed record TlsProbeResult(bool Success, ProbeStatus Status, string Detail);
}
