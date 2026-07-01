using System.Net;
using System.Net.Sockets;

namespace AzureFinOps.Dashboard.Infrastructure;

/// <summary>
/// Single source of truth for outbound HTTP transport in this app.
///
/// Corporate egress here drops IPv6 SYNs and the OS only gives up after
/// 3 TCP retries (~21 s), wedging every outbound call. .NET's default
/// connect path tries each DNS-returned address sequentially, so a single
/// AAAA record kills the request even though A records are reachable.
///
/// Fix: resolve DNS ourselves, filter to IPv4 (AddressFamily.InterNetwork),
/// and connect on an explicit IPv4 socket with a hard 5-s cap. Never even
/// open an IPv6 socket. Use this for every HttpClient — factory default,
/// named clients, and the static helper in HttpHelper.
/// </summary>
public static class Ipv4HttpHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true,
        ConnectCallback = Ipv4ConnectAsync,
    };

    public static async ValueTask<Stream> Ipv4ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IPAddress[] addresses;
        // Skip DNS for literal IPs (avoids AddressFamily mismatch on IPv6 literals).
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException($"IPv6 literal {host} blocked by Ipv4HttpHandler");
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            if (addresses.Length == 0)
                throw new InvalidOperationException($"No IPv4 address resolved for {host}");
        }
        var dnsMs = sw.ElapsedMilliseconds;

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(5));

        Exception? lastError = null;
        for (var i = 0; i < addresses.Length; i++)
        {
            var addr = addresses[i];
            var connectStart = sw.ElapsedMilliseconds;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addr, port, attemptCts.Token);
                var totalMs = sw.ElapsedMilliseconds;
                HttpHelper.Logger?.LogInformation(
                    "IPv4 connect OK {Host}:{Port} via {Addr} (attempt {N}/{Total}) — dns={DnsMs}ms connect={ConnectMs}ms total={TotalMs}ms",
                    host, port, addr, i + 1, addresses.Length, dnsMs, totalMs - connectStart, totalMs);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
                HttpHelper.Logger?.LogWarning(ex,
                    "IPv4 connect FAIL {Host}:{Port} via {Addr} (attempt {N}/{Total}) after {Ms}ms",
                    host, port, addr, i + 1, addresses.Length, sw.ElapsedMilliseconds - connectStart);
            }
        }

        HttpHelper.Logger?.LogError(lastError,
            "IPv4 connect EXHAUSTED {Host}:{Port} — all {N} addresses failed after {Ms}ms",
            host, port, addresses.Length, sw.ElapsedMilliseconds);
        throw new HttpRequestException(
            $"All {addresses.Length} IPv4 connects to {host}:{port} failed", lastError);
    }
}
