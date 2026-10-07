using System.Text.Json;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Microsoft Learn's public MCP server (https://learn.microsoft.com/api/mcp), connected with the official MCP C# SDK.
/// Its documentation tools are passed to the agent unchanged, as Agent Framework's MCP tools are; the host only keeps
/// one connection open, replaces it after a failure and bounds what a page read adds to the model context.
/// </summary>
internal sealed class MicrosoftLearnMcp : IAsyncDisposable
{
    internal static readonly Uri Endpoint = new("https://learn.microsoft.com/api/mcp");

    // The server also offers microsoft_code_sample_search; scripts are written without documentation lookups.
    internal static readonly string[] ToolNames = ["microsoft_docs_search", "microsoft_docs_fetch"];

    /// <summary>The most a Learn result adds to the model context; a longer page is cut with a note.</summary>
    internal const int MaxResultCharacters = 48 * 1024;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(Ipv4HttpHandler.Create()) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private McpClient? _client;
    private IReadOnlyList<AITool> _tools = [];
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    internal MicrosoftLearnMcp(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("AzureFinOps.AI.MicrosoftLearn");
        _ = ConnectAsync();
    }

    internal static bool IsLearnTool(string? name) => name is not null && ToolNames.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// The server's documentation tools, waiting briefly (3 s unless <paramref name="wait"/> says otherwise) for a
    /// connection that is still opening.
    /// </summary>
    internal async Task<IReadOnlyList<AITool>> ToolsAsync(CancellationToken cancellationToken, TimeSpan? wait = null)
    {
        if (Volatile.Read(ref _tools) is { Count: > 0 } ready) return ready;
        try { await ConnectAsync().WaitAsync(wait ?? TimeSpan.FromSeconds(3), cancellationToken); }
        catch (TimeoutException) { }
        return Volatile.Read(ref _tools);
    }

    /// <summary>
    /// The current tool with the failed tool's name. The connection the failed tool came from is replaced once; calls that
    /// failed on it at the same time use the new one instead of replacing it again. Null when Learn is unavailable.
    /// </summary>
    internal async Task<AIFunction?> ReconnectAsync(AIFunction failed, CancellationToken cancellationToken)
    {
        // The SDK's own tool, also when a delegating wrapper carries it.
        var tool = failed.GetService<McpClientTool>() ?? (AITool)failed;
        McpClient? stale = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _tools).Contains(tool))
            {
                stale = _client;
                _client = null;
                _nextAttempt = DateTimeOffset.MinValue;
                Volatile.Write(ref _tools, []);
            }
        }
        finally { _gate.Release(); }
        if (stale is not null)
            try { await stale.DisposeAsync(); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
        await ConnectAsync();
        return Volatile.Read(ref _tools).OfType<AIFunction>().FirstOrDefault(tool => tool.Name == failed.Name);
    }

    private async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null || DateTimeOffset.UtcNow < _nextAttempt) return;
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = Endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    Name = "Microsoft Learn",
                }, _http, _loggerFactory, ownsHttpClient: false),
                new McpClientOptions { ClientInfo = new Implementation { Name = "azure-finops-agent", Version = "1.0" } },
                _loggerFactory, timeout.Token);
            var tools = (await client.ListToolsAsync(cancellationToken: timeout.Token)).Where(tool => IsLearnTool(tool.Name)).ToArray();
            _client = client;
            Volatile.Write(ref _tools, tools);
            _logger.LogInformation("Microsoft Learn MCP connected with {ToolCount} tools", tools.Length);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _nextAttempt = DateTimeOffset.UtcNow + RetryInterval;
            _logger.LogWarning("Microsoft Learn MCP is unavailable ({ErrorType}); documentation tools are left out until it reconnects", exception.GetType().Name);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A Learn result as the model sees it, its text cut at <see cref="MaxResultCharacters"/>. The SDK's McpClientTool
    /// returns text content as <see cref="AIContent"/>, and a result carrying structured data or an error as the whole
    /// CallToolResult JSON. A structured result's text is the same data (the copy MCP servers send for compatibility),
    /// so the model gets that text once instead of both; an error keeps its JSON so its isError flag still counts.
    /// </summary>
    internal static object? Bound(object? result)
    {
        string? text;
        switch (result)
        {
            case TextContent single:
                text = single.Text;
                break;
            case IEnumerable<AIContent> contents:
                text = string.Join("\n", contents.OfType<TextContent>().Select(content => content.Text));
                break;
            case JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                text = string.Join("\n", content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    .Select(block => block.GetProperty("text").GetString()));
                if (text.Length > 0 && !(element.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True))
                    result = text;
                break;
            default:
                text = null;
                break;
        }
        return text is null || text.Length <= MaxResultCharacters ? result
            : text[..MaxResultCharacters] + $"\n\n[Cut at {MaxResultCharacters / 1024} KB of {text.Length / 1024} KB. Search for the passage you need with microsoft_docs_search instead of reading the rest of the page.]";
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
            try { await client.DisposeAsync(); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
        _http.Dispose();
    }
}
