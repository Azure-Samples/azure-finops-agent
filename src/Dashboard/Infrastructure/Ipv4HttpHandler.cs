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

        Exception? lastError = null;
        for (var i = 0; i < addresses.Length; i++)
        {
            // Caller aborted the request (client disconnect, request timeout, or
            // this handler's own 5-s ConnectTimeout elapsing on a prior slow
            // address). Propagate a clean cancellation instead of looping over the
            // remaining addresses: each of those ConnectAsync calls would fail
            // instantly on the already-cancelled token, and — when logged with the
            // exception object — each becomes its own AppExceptions record. One
            // blackholed multi-address host used to emit ~10 exceptions per request,
            // tripping the "exceptions in 15 min" alert on its own.
            ct.ThrowIfCancellationRequested();

            var addr = addresses[i];
            var connectStart = sw.ElapsedMilliseconds;
            // Per-attempt budget so one slow/blackholed address can't consume the
            // whole allowance and force every remaining address to fail instantly.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(5));
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller (not our per-attempt timer) cancelled — clean up and
                // propagate the cancellation. Never logged as an exception:
                // cancellations are expected and would only add telemetry noise.
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                // Per-attempt connect timeout or a genuine socket error for THIS
                // address. Both are expected/transient (corporate egress dropping
                // SYNs is the whole reason this handler exists), so record a
                // structured warning WITHOUT the exception object — passing the
                // exception would emit an AppExceptions row per failed address and
                // turn one flaky request into an exception-rate alert.
                lastError = ex;
                socket.Dispose();
                var timedOut = attemptCts.IsCancellationRequested && !ct.IsCancellationRequested;
                HttpHelper.Logger?.LogWarning(
                    "IPv4 connect FAIL {Host}:{Port} via {Addr} (attempt {N}/{Total}) after {Ms}ms — {Reason}",
                    host, port, addr, i + 1, addresses.Length, sw.ElapsedMilliseconds - connectStart,
                    timedOut ? "connect timed out after 5s" : ex.Message);
            }
        }

        // Every address failed with a real connect error (not a caller cancellation).
        // Log a structured error WITHOUT the exception object, then throw a single
        // HttpRequestException — the caller's retry/telemetry records it once instead
        // of us emitting one AppExceptions row per address here.
        HttpHelper.Logger?.LogError(
            "IPv4 connect EXHAUSTED {Host}:{Port} — all {N} addresses failed after {Ms}ms",
            host, port, addresses.Length, sw.ElapsedMilliseconds);
        throw new HttpRequestException(
            $"All {addresses.Length} IPv4 connects to {host}:{port} failed", lastError);
    }
}
