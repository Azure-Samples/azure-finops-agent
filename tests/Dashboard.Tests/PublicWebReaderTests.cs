using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class PublicWebReaderTests
{
    private static readonly Uri Source = new("https://example.invalid/pricing");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BodyReadingHonorsCancellationAndTimeoutAndDisposesResponse(bool callerCancels)
    {
        using var body = new WaitingStream();
        using var client = new HttpClient(new ResponseHandler(body))
        {
            Timeout = callerCancels ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(1)
        };
        using var cancellation = new CancellationTokenSource();
        var pending = PublicWebReader.FetchPageAsync(client, Source, null, 60_000, cancellation.Token);
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (callerCancels)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("Error: public web request failed (", result);
            Assert.DoesNotContain("HTTP 200", result);
            Assert.DoesNotContain(Source.AbsoluteUri, result);
        }

        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task SuccessfulReadPreservesFilteringAndInvariantRetrievalTime()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(
            "<html><script>remove-me</script><p>License A &amp; B</p>\n<p>Unrelated</p></html>"));
        using var client = new HttpClient(new ResponseHandler(body));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            var before = DateTimeOffset.UtcNow;
            var result = await PublicWebReader.FetchPageAsync(client, Source, "License", 60_000, CancellationToken.None);

            Assert.StartsWith("HTTP 200 OK", result);
            Assert.Contains("License A & B", result);
            Assert.DoesNotContain("Unrelated", result);
            Assert.DoesNotContain("remove-me", result);
            var timestamp = result.Split('\n').Single(line => line.StartsWith("UTC: ", StringComparison.Ordinal));
            Assert.True(DateTimeOffset.TryParseExact(timestamp[5..].Trim(), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var retrieved));
            Assert.InRange(retrieved, before, DateTimeOffset.UtcNow);
            Assert.False(body.CanRead);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task BodyDownloadAndOutputRetainSeparateCaps()
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 700_000)));
        using var client = new HttpClient(new ResponseHandler(body));
        var result = await PublicWebReader.FetchPageAsync(client, Source, null, 1000, CancellationToken.None);

        Assert.Contains("Bytes on wire: 600000 (HARD CAP", result);
        Assert.Contains("[TRUNCATED to 1000 chars", result);
        Assert.EndsWith(new string('x', 1000), result);
        Assert.False(body.CanRead);
    }

    [Fact]
    public async Task StructuredBodiesBecomeRetainableJson()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("{\"paths\":{\"/a\":{}}}"));
        using var jsonClient = new HttpClient(new ResponseHandler(json, "text/plain"));
        var result = await PublicWebReader.FetchPageAsync(jsonClient, Source, null, 60_000, CancellationToken.None);
        var lines = result.Split('\n');
        Assert.Equal("HTTP 200 OK", lines[0]);
        Assert.StartsWith("Current UTC time: ", lines[1]);
        Assert.Equal("{\"paths\":{\"/a\":{}}}", lines[2]);

        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("Name,Cost\nvm1,1.5\nvm2,2\n"));
        using var csvClient = new HttpClient(new ResponseHandler(csv, "text/csv"));
        var table = (await PublicWebReader.FetchPageAsync(csvClient, Source, null, 60_000, CancellationToken.None, timestamp: false)).Split('\n', 2);
        Assert.Equal("HTTP 200 OK", table[0]);
        using var document = JsonDocument.Parse(table[1]);
        Assert.Equal(2, document.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal("number", document.RootElement.GetProperty("columns")[1].GetProperty("type").GetString());
        Assert.Equal(1.5m, document.RootElement.GetProperty("rows")[0][1].GetDecimal());
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("168.63.129.16", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("20.42.0.1", true)]
    [InlineData("2603:1030::1", true)]
    public void OnlyPublicAddressesAreReachable(string address, bool expected) =>
        Assert.Equal(expected, PublicWebReader.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://localhost/x", true)]
    [InlineData("https://metadata.localhost/x", true)]
    [InlineData("https://169.254.169.254/metadata", true)]
    [InlineData("https://[::1]/x", true)]
    [InlineData("https://learn.microsoft.com/x", false)]
    public void LiteralPrivateHostsAreBlockedBeforeResolution(string url, bool blocked) =>
        Assert.Equal(blocked, PublicWebReader.IsBlockedHost(new Uri(url)));

    [Fact]
    public void LearnSearchGainsTheRequiredLocaleOnlyWhenMissing()
    {
        Assert.Equal("https://learn.microsoft.com/api/search?search=spot&locale=en-us",
            PublicWebReader.Canonicalize(new Uri("https://learn.microsoft.com/api/search?search=spot")).AbsoluteUri);
        var explicitLocale = new Uri("https://learn.microsoft.com/api/search?search=spot&locale=fi-fi");
        Assert.Same(explicitLocale, PublicWebReader.Canonicalize(explicitLocale));
        var page = new Uri("https://learn.microsoft.com/azure/virtual-machines/spot-vms");
        Assert.Same(page, PublicWebReader.Canonicalize(page));
    }

    private sealed class ResponseHandler(Stream body, string contentType = "text/html") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(body)
            };
            response.Content.Headers.ContentType = new(contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class WaitingStream : MemoryStream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
