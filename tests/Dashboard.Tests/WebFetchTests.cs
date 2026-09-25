using System.Globalization;
using System.Net;
using System.Text;
using AzureFinOps.Dashboard.AI.Tools;

namespace Dashboard.Tests;

public sealed class WebFetchTests
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
        var pending = WebFetchTools.FetchPageAsync(client, Source, null, 60_000, cancellation.Token);
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
            var result = await WebFetchTools.FetchPageAsync(client, Source, "License", 60_000, CancellationToken.None);

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
        var result = await WebFetchTools.FetchPageAsync(client, Source, null, 1000, CancellationToken.None);

        Assert.Contains("Bytes on wire: 600000 (HARD CAP", result);
        Assert.Contains("[TRUNCATED to 1000 chars", result);
        Assert.EndsWith(new string('x', 1000), result);
        Assert.False(body.CanRead);
    }

    private sealed class ResponseHandler(Stream body) : HttpMessageHandler
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
            response.Content.Headers.ContentType = new("text/html");
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
