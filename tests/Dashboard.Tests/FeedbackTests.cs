using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.Feedback;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests;

/// <summary>The feedback form: what it accepts, the issue it writes, its limits, and the GitHub App exchange.</summary>
public sealed class FeedbackTests
{
    private const string Repository = "contoso-samples/feedback-test";

    [Fact]
    public void AnswerFeedbackBecomesALabelledIssueThatQuotesOnlyAnIncludedQuestion()
    {
        Assert.True(FeedbackReport.TryRead(Json(new
        {
            kind = "answer",
            title = "  The total\n left out   storage ",
            details = "It summed compute only.\r\nPing @octocat about it.",
            question = "What did I spend last month?\nBy service, please.",
            rating = "down",
        }), out var request, out var error), error?.Message);

        var issue = FeedbackReport.Compose(request, "main 158 (abc1234)");
        Assert.Equal("Answer quality: The total left out storage", issue.Title);
        Assert.Equal(["feedback"], issue.Labels);
        Assert.Contains("It summed compute only.\nPing @\u200Boctocat about it.", issue.Body);
        Assert.Contains("> What did I spend last month?\n> By service, please.\n", issue.Body);
        Assert.Contains("Type: Answer quality · Rating: Poor answer · Build: main 158 (abc1234)", issue.Body);
        Assert.DoesNotContain("\r", issue.Body);

        Assert.True(FeedbackReport.TryRead(Json(new { kind = "bug", title = "Chart is empty", rating = "sideways" }), out var bug, out _));
        var bugIssue = FeedbackReport.Compose(bug, "main 1 (dev)");
        Assert.Equal("Bug: Chart is empty", bugIssue.Title);
        Assert.Equal(["feedback", "bug"], bugIssue.Labels);
        Assert.StartsWith("_No details given._", bugIssue.Body);
        Assert.DoesNotContain("question it was about", bugIssue.Body);
        Assert.DoesNotContain("Rating", bugIssue.Body);
        Assert.Equal(["feedback", "enhancement"], FeedbackReport.Compose(Read(new { kind = "idea", title = "Dark mode" }), "b").Labels);
    }

    [Theory]
    [InlineData("details", "key -----BEGIN RSA " + "PRIVATE KEY----- abc", "the secret or key")]
    [InlineData("details", "DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc123==", "the secret or key")]
    [InlineData("title", "token gh" + "p_abcdefghijklmnopqrstuvwxyz0123456789", "the secret or key")]
    [InlineData("details", "https://x.blob.core.windows.net/c?sv=2024&sig=abcdefghijklmnop1234", "the secret or key")]
    [InlineData("details", "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sig", "the secret or key")]
    [InlineData("details", "subscription 00000000-1111-2222-3333-444444444444 is wrong", "the ID (such as a tenant or subscription ID)")]
    [InlineData("details", "write to someone@contoso.example", "the email address")]
    [InlineData("question", "why is 10.0.0.4 blocked?", "the IP address")]
    public void RefusesPrivateContentBecauseIssuesArePublic(string field, string text, string found)
    {
        var fields = new Dictionary<string, string> { ["kind"] = "bug", ["title"] = "Something broke", [field] = text };
        Assert.False(FeedbackReport.TryRead(Json(fields), out _, out var error));
        Assert.Equal(field, error!.Field);
        Assert.True(error.Sensitive);
        Assert.Equal($"Remove {found} first: feedback is posted publicly on GitHub.", error.Message);
        // The refusal names the kind of content, never the content itself.
        Assert.DoesNotContain(text, error.Message);
    }

    [Fact]
    public void VersionsAndOrdinaryTextAreNotMistakenForPrivateContent()
    {
        foreach (var text in new[] { "Version 1.2.3 of the app", "Costs rose 12.5% @ noon", "Use Standard_D4s_v5 in westeurope", "a@b" })
            Assert.Null(FeedbackReport.FindSensitive(text));
    }

    [Fact]
    public void RefusesMissingOrOversizedFieldsWithTheFieldToFix()
    {
        Assert.Equal("kind", Refused(new { kind = "rant", title = "Something" }).Field);
        Assert.Equal("title", Refused(new { kind = "bug", title = " a " }).Field);
        Assert.Equal("title", Refused(new { kind = "bug", title = new string('x', FeedbackReport.MaxTitle + 1) }).Field);
        Assert.Equal("details", Refused(new { kind = "bug", title = "Fine", details = new string('x', FeedbackReport.MaxDetails + 1) }).Field);
        Assert.False(Refused(new { kind = "bug" }).Sensitive);

        // An earlier question is the user's own message: a long one is shortened, not refused.
        var request = Read(new { kind = "answer", title = "Long question", question = new string('q', 3000) });
        Assert.Equal(FeedbackReport.MaxQuestion + 1, request.Question!.Length);
        Assert.EndsWith("…", request.Question);
    }

    [Fact]
    public void ThePrefilledGitHubLinkFillsTheFeedbackIssueFormAndStaysShort()
    {
        var issue = FeedbackReport.Compose(Read(new { kind = "idea", title = "Export to CSV & PDF", details = "Please." }), "main 2 (abc)");
        var url = FeedbackReport.NewIssueUrl(Repository, issue);
        Assert.StartsWith($"https://github.com/{Repository}/issues/new?template=feedback.yml&title=Idea%3A%20Export%20to%20CSV%20%26%20PDF&feedback=Please.", url);
        // GitHub answers 404 when the user may not set labels, so the form's own label applies instead.
        Assert.DoesNotContain("labels=", url);
        Assert.DoesNotContain("&body=", url);

        var long_ = FeedbackReport.Compose(Read(new { kind = "bug", title = "Long", details = new string('ü', FeedbackReport.MaxDetails) }), "b");
        var shortened = FeedbackReport.NewIssueUrl(Repository, long_);
        Assert.True(shortened.Length <= 7000, $"{shortened.Length} characters");
        Assert.Contains(Uri.EscapeDataString("Shortened to fit in a link"), shortened);
    }

    [Fact]
    public void TheLinkedIssueFormExistsWithTheFeedbackLabelAndTheFilledField()
    {
        var form = File.ReadAllText(Path.Join(RepositoryRoot(), ".github", "ISSUE_TEMPLATE", FeedbackReport.IssueForm));
        Assert.Contains("labels: [\"" + FeedbackReport.FeedbackLabel + "\"]", form);
        Assert.Contains("type: textarea\n    id: " + FeedbackReport.IssueFormField + "\n", form.Replace("\r\n", "\n"));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var directory = Path.GetDirectoryName(source); directory is not null; directory = Path.GetDirectoryName(directory))
            if (Directory.Exists(Path.Join(directory, ".github", "ISSUE_TEMPLATE"))) return directory;
        throw new DirectoryNotFoundException("The repository root was not found above the test source.");
    }

    [Fact]
    public void ThePrivateKeyIsAcceptedAsPemEscapedPemOrBase64()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        Assert.Equal(pem, FeedbackOptions.NormalizePrivateKey(pem));
        Assert.Equal(pem, FeedbackOptions.NormalizePrivateKey(pem.Replace("\n", "\\n")));
        Assert.Equal(pem, FeedbackOptions.NormalizePrivateKey(Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))));
        Assert.Null(FeedbackOptions.NormalizePrivateKey("not a key"));
        Assert.Null(FeedbackOptions.NormalizePrivateKey(Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text"))));

        // Without a usable key, or with an invalid repository, the form only links to GitHub.
        Assert.Null(Options(new() { ["AppId"] = "123", ["PrivateKey"] = "not a key" }).AppId);
        var invalid = Options(new() { ["Repository"] = "not a repository", ["AppId"] = "123", ["PrivateKey"] = pem });
        Assert.Equal(FeedbackOptions.DefaultRepository, invalid.Repository);
        Assert.Null(invalid.PrivateKeyPem);
        Assert.Equal(Repository, Options(new() { ["Repository"] = Repository }).Repository);
    }

    [Fact]
    public void EachUserGetsThreeIssuesInTenMinutesAndTenADay()
    {
        var time = new ManualTime();
        var limiter = new FeedbackRateLimiter(time);
        for (var i = 0; i < 3; i++) Assert.True(limiter.TryAcquire(1, out _));
        Assert.False(limiter.TryAcquire(1, out var wait));
        Assert.Equal(TimeSpan.FromMinutes(10), wait);
        // Another user is not affected.
        Assert.True(limiter.TryAcquire(2, out _));

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(limiter.TryAcquire(1, out wait));
        Assert.Equal(TimeSpan.FromMinutes(6), wait);

        for (var round = 0; round < 3; round++)
        {
            time.Advance(TimeSpan.FromMinutes(11));
            for (var i = 0; i < 3 && round * 3 + i < 7; i++) Assert.True(limiter.TryAcquire(1, out _));
        }
        // Ten in a day: the eleventh waits for the first of the day to age out.
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(limiter.TryAcquire(1, out wait));
        Assert.True(wait > TimeSpan.FromHours(20));
    }

    [Fact]
    public async Task TheAppSignsAJwtAndCreatesIssuesWithATokenLimitedToTheRepositorysIssues()
    {
        using var rsa = RSA.Create(2048);
        var time = new ManualTime();
        var github = new FakeGitHub(time);
        using var client = Client(rsa, github, time);

        Assert.True(await client.IsAvailableAsync(CancellationToken.None));
        var created = await client.CreateIssueAsync(new FeedbackIssue("Bug: x", "Body", ["feedback", "bug"]), CancellationToken.None);
        Assert.Equal(new CreatedIssue(5, $"https://github.com/{Repository}/issues/5"), created);

        var lookup = github.Requests[0];
        Assert.Equal($"GET /repos/{Repository}/installation", lookup.Line);
        var jwt = lookup.Bearer.Split('.');
        Assert.Equal(3, jwt.Length);
        using (var header = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt[0])))
            Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        using (var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt[1])))
        {
            Assert.Equal(424242, payload.RootElement.GetProperty("iss").GetInt64());
            var issued = payload.RootElement.GetProperty("iat").GetInt64();
            var expires = payload.RootElement.GetProperty("exp").GetInt64();
            Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds() - 60, issued);
            Assert.True(expires - issued <= 600);
        }
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64Url.DecodeFromChars(jwt[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.All(github.Requests, request => Assert.Equal("2022-11-28", request.ApiVersion));

        var mint = github.Requests[1];
        Assert.Equal("POST /app/installations/77/access_tokens", mint.Line);
        Assert.Equal("""{"repositories":["feedback-test"],"permissions":{"issues":"write"}}""", mint.Body);

        var issue = github.Requests[2];
        Assert.Equal($"POST /repos/{Repository}/issues", issue.Line);
        Assert.Equal("ghs_synthetic_1", issue.Bearer);
        Assert.Equal("""{"title":"Bug: x","body":"Body","labels":["feedback","bug"]}""", issue.Body);

        // The token is reused until five minutes before it expires, then replaced.
        await client.CreateIssueAsync(new FeedbackIssue("Idea: y", "Body", ["feedback"]), CancellationToken.None);
        Assert.Equal(1, github.Requests.Count(request => request.Line.EndsWith("/access_tokens")));
        time.Advance(TimeSpan.FromMinutes(56));
        await client.CreateIssueAsync(new FeedbackIssue("Idea: z", "Body", ["feedback"]), CancellationToken.None);
        Assert.Equal(2, github.Requests.Count(request => request.Line.EndsWith("/access_tokens")));
        Assert.Equal("ghs_synthetic_2", github.Requests[^1].Bearer);
        // The installation was looked up once.
        Assert.Equal(1, github.Requests.Count(request => request.Line.EndsWith("/installation")));
    }

    [Fact]
    public async Task ARefusedLabelDoesNotLoseTheFeedback()
    {
        using var rsa = RSA.Create(2048);
        var time = new ManualTime();
        var github = new FakeGitHub(time) { RefuseLabels = true };
        using var client = Client(rsa, github, time);
        var created = await client.CreateIssueAsync(new FeedbackIssue("Bug: x", "Body", ["feedback"]), CancellationToken.None);
        Assert.Equal(5, created.Number);
        Assert.Equal("""{"title":"Bug: x","body":"Body","labels":[]}""", github.Requests[^1].Body);
    }

    [Fact]
    public async Task AnAppThatIsNotInstalledOrConfiguredIsUnavailable()
    {
        using var rsa = RSA.Create(2048);
        var time = new ManualTime();
        var github = new FakeGitHub(time) { Installed = false };
        using var client = Client(rsa, github, time);
        Assert.False(await client.IsAvailableAsync(CancellationToken.None));
        // The answer is remembered rather than asked on every form.
        Assert.False(await client.IsAvailableAsync(CancellationToken.None));
        Assert.Single(github.Requests);
        await Assert.ThrowsAsync<GitHubFeedbackUnavailableException>(() =>
            client.CreateIssueAsync(new FeedbackIssue("Bug: x", "Body", []), CancellationToken.None));

        // Installed later: noticed once the remembered answer expires.
        github.Installed = true;
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await client.IsAvailableAsync(CancellationToken.None));

        using var unconfigured = new GitHubAppClient(Options(new()), new Factory(github), time, NullLogger.Instance);
        Assert.False(await unconfigured.IsAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheEndpointsLinkToGitHubWithoutTheAppAndCreateTheIssueWithIt()
    {
        using var rsa = RSA.Create(2048);
        var time = new ManualTime();
        var github = new FakeGitHub(time) { Installed = false };
        using var client = Client(rsa, github, time);
        await using var app = await StartAsync(client, new FeedbackRateLimiter(time));
        using var http = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(http, null, new { kind = "bug", title = "Broken" })).StatusCode);
        Assert.Equal("link", (await Read(await http.GetAsync("/api/feedback/config"))).GetProperty("mode").GetString());

        // Not installed: the user gets GitHub's page with the issue filled in.
        using (var response = await Post(http, 7, new { kind = "idea", title = "Dark mode" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await Read(response);
            Assert.Equal("link", json.GetProperty("mode").GetString());
            Assert.StartsWith($"https://github.com/{Repository}/issues/new?template=feedback.yml&title=Idea%3A%20Dark%20mode", json.GetProperty("url").GetString());
        }

        // Refusals name the field and never reach GitHub.
        using (var response = await Post(http, 7, new { kind = "bug", title = "Hi" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("title", (await Read(response)).GetProperty("field").GetString());
        }
        using (var response = await Post(http, 7, new { kind = "bug", title = "Mail me", details = "at someone@contoso.example" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("details", (await Read(response)).GetProperty("field").GetString());
        }
        Assert.DoesNotContain(github.Requests, request => request.Line.EndsWith("/issues"));

        // Installed: the issue is created and linked; a fourth in ten minutes waits.
        github.Installed = true;
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("direct", (await Read(await http.GetAsync("/api/feedback/config"))).GetProperty("mode").GetString());
        for (var i = 0; i < 3; i++)
        {
            using var response = await Post(http, 7, new { kind = "bug", title = $"Broken {i}" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var json = await Read(response);
            Assert.Equal("direct", json.GetProperty("mode").GetString());
            Assert.Equal(5, json.GetProperty("number").GetInt32());
        }
        using (var response = await Post(http, 7, new { kind = "bug", title = "Broken again" }))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("600", response.Headers.GetValues("Retry-After").Single());
            Assert.StartsWith($"https://github.com/{Repository}/issues/new?", (await Read(response)).GetProperty("fallbackUrl").GetString());
        }

        // GitHub failing outright is reported without its own error text.
        github.FailIssues = true;
        using (var response = await Post(http, 8, new { kind = "bug", title = "Broken" }))
        {
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic", text);
            Assert.Contains("fallbackUrl", text);
        }
    }

    private static FeedbackRequest Read(object body)
    {
        Assert.True(FeedbackReport.TryRead(Json(body), out var request, out var error), error?.Message);
        return request;
    }

    private static FeedbackError Refused(object body)
    {
        Assert.False(FeedbackReport.TryRead(Json(body), out _, out var error));
        return error!;
    }

    private static JsonElement Json(object body) => JsonSerializer.SerializeToElement(body);

    private static FeedbackOptions Options(Dictionary<string, string?> values) =>
        FeedbackOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => "GitHubFeedback:" + pair.Key, pair => pair.Value))
            .Build(), NullLogger.Instance);

    private static GitHubAppClient Client(RSA rsa, FakeGitHub github, TimeProvider time) =>
        new(Options(new() { ["Repository"] = Repository, ["AppId"] = "424242", ["PrivateKey"] = rsa.ExportRSAPrivateKeyPem() }),
            new Factory(github), time, NullLogger.Instance);

    private static async Task<WebApplication> StartAsync(GitHubAppClient client, FeedbackRateLimiter limiter)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        var app = builder.Build();
        app.UseSession();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var id))
                context.Session.SetString("user", JsonSerializer.Serialize(new { id = long.Parse(id.ToString()), login = "signed-out" }));
            await next();
        });
        app.MapFeedbackEndpoints(client, limiter, NullLogger.Instance);
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, long? user, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feedback")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (user is { } id) request.Headers.Add("X-Test-User", id.ToString());
        return http.SendAsync(request);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed record Recorded(string Line, string Bearer, string ApiVersion, string Body);

    /// <summary>Answers the three GitHub calls the app makes, and records them.</summary>
    private sealed class FakeGitHub(ManualTime time) : HttpMessageHandler
    {
        private int _tokens;
        public bool Installed { get; set; } = true;
        public bool RefuseLabels { get; set; }
        public bool FailIssues { get; set; }
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri.AbsolutePath;
            Requests.Add(new($"{request.Method} {path}", request.Headers.Authorization?.Parameter ?? "",
                request.Headers.GetValues("X-GitHub-Api-Version").Single(), body));
            return path switch
            {
                _ when path.EndsWith("/installation") => Installed
                    ? Respond(HttpStatusCode.OK, new { id = 77 })
                    : Respond(HttpStatusCode.NotFound, new { message = "synthetic not found" }),
                _ when path.EndsWith("/access_tokens") => Respond(HttpStatusCode.Created, new
                {
                    token = $"ghs_synthetic_{++_tokens}",
                    expires_at = time.GetUtcNow().AddHours(1),
                }),
                _ when FailIssues => Respond(HttpStatusCode.InternalServerError, new { message = "synthetic failure" }),
                _ when RefuseLabels && body.Contains("\"feedback\"") => Respond(HttpStatusCode.UnprocessableEntity, new { message = "synthetic label refused" }),
                _ => Respond(HttpStatusCode.Created, new { number = 5, html_url = $"https://github.com/{Repository}/issues/5" }),
            };
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
