using System.Security.Cryptography;
using System.Text.Json;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using AzureFinOps.Dashboard.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──────────────────────────────────────────────
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

var oauthOptions = new MicrosoftOAuthOptions
{
    ClientId = builder.Configuration["Microsoft:ClientId"] ?? "",
    ClientSecret = builder.Configuration["Microsoft:ClientSecret"] ?? "",
    TenantId = builder.Configuration["Microsoft:TenantId"] ?? "common",
    HomeTenantId = builder.Configuration["Microsoft:HomeTenantId"]
                   ?? builder.Configuration["Microsoft:TenantId"]
                   ?? "common",
};
var azureOpenAIEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
if (string.IsNullOrWhiteSpace(azureOpenAIEndpoint))
    throw new InvalidOperationException(
        "AzureOpenAI:Endpoint is required. " +
        "For local dev: dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://YOUR-RESOURCE.openai.azure.com/\" " +
        "(run from src/Dashboard). " +
        "For production: set the AzureOpenAI__Endpoint environment variable.");
var azureOpenAIDeployment = builder.Configuration["AzureOpenAI:DeploymentName"] ?? "gpt-5.6-sol";
// Default reasoning effort (low|medium|high|xhigh) for reasoning-capable
// models. `medium` is the sweet spot for GPT-5.6 on this workload: it roughly
// halves time-to-first-token vs `high` (the dominant first-response latency)
// while keeping tool-orchestration + format-following quality. Trivial turns
// are still auto-routed to `low` per request. Override with
// AzureOpenAI__ReasoningEffort=high for a max-depth demo, or `xhigh`
// (measured 8+ min per LLM round-trip in production — opt-in only).
var azureOpenAIReasoningEffort = builder.Configuration["AzureOpenAI:ReasoningEffort"] ?? "medium";
var appInsightsCs = builder.Configuration["ApplicationInsights:ConnectionString"];

// ── Services ───────────────────────────────────────────────────
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.IsDevelopment() ? Path.GetTempPath() : "/home", "dataprotection-keys")))
    .SetApplicationName("AzureFinOpsAgent");

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    // Idle timeout: 60 min of inactivity. Absolute cap (8h) is enforced by middleware below.
    options.IdleTimeout = TimeSpan.FromHours(1);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
builder.Services.AddHttpClient();
// Force IPv4 + 5-s connect cap on EVERY factory-created HttpClient (default
// and all named clients). Corporate egress drops IPv6 SYNs and the OS retries
// for ~21 s before falling back, wedging every outbound call. See
// Infrastructure/Ipv4HttpHandler.cs for the rationale.
builder.Services.ConfigureHttpClientDefaults(b =>
    b.ConfigurePrimaryHttpMessageHandler(AzureFinOps.Dashboard.Infrastructure.Ipv4HttpHandler.Create));
// Dedicated client for Microsoft Entra token endpoints. Inherits the IPv4-only
// handler from ConfigureHttpClientDefaults above; only overrides Timeout and
// HTTP version for token-endpoint specifics.
builder.Services.AddHttpClient("entra-token", c =>
{
    // Budget enough headroom for retries above the 5-s per-attempt connect cap.
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestVersion = System.Net.HttpVersion.Version20;
});

if (!builder.Environment.IsDevelopment())
    builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);

if (!string.IsNullOrEmpty(appInsightsCs))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(o =>
        {
            o.ConnectionString = appInsightsCs;
            o.SamplingRatio = 1.0f;   // preserve pre-1.5.0 behavior; default in 1.5.0 is RateLimitedSampler (5 req/sec)
        })
        .WithTracing(t => t
            .AddSource("AzureFinOps.AI")
            // Copilot SDK W3C-propagated tool/LLM spans surface here when the
            // SDK's TelemetryConfig.SourceName is set to "AzureFinOps.AI.CLI".
            .AddSource("AzureFinOps.AI.CLI"))
        .WithMetrics(m => m
            .AddMeter("AzureFinOps.AI")
            .AddMeter("AzureFinOps.AI.CLI"));
}

var telemetry = new AiTelemetry();
telemetry.LoadTitles();
builder.Services.AddSingleton(telemetry);
builder.Services.AddSingleton(oauthOptions);
builder.Services.AddSingleton<EntraClientCredentials>();
builder.Services.AddSingleton<IdTokenValidator>();
builder.Services.AddSingleton<SessionTokenStore>();
builder.Services.AddSingleton<PersistentIdentity>();
// Janitor is started manually after CopilotSessionFactory is constructed
// (see below) because it now depends on the factory for the 30-day TTL sweep.

var app = builder.Build();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("AzureFinOps.AI");
logger.LogInformation("Application starting. AppInsights configured: {Configured}", !string.IsNullOrEmpty(appInsightsCs));
// Plumb a logger into HttpHelper so 429/5xx retries surface in stdout +
// Application Insights instead of being only a silent counter.
AzureFinOps.Dashboard.Infrastructure.HttpHelper.Logger =
    loggerFactory.CreateLogger("AzureFinOps.AI.HttpHelper");

await using var copilotFactory = await CopilotSessionFactory.CreateAsync(
    telemetry, oauthOptions, azureOpenAIEndpoint, azureOpenAIDeployment, azureOpenAIReasoningEffort, loggerFactory);

// Start the janitor now that the factory exists; tie its lifecycle to the host.
var janitor = new UserStateJanitor(telemetry, copilotFactory, loggerFactory.CreateLogger<UserStateJanitor>());
await janitor.StartAsync(CancellationToken.None);
app.Lifetime.ApplicationStopping.Register(() =>
{
    // Bound shutdown so a hung dispose can't block the App Service drain.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try { janitor.StopAsync(cts.Token).GetAwaiter().GetResult(); } catch { }
});

// Background tenant-token refresher. Keeps Azure ARM / Graph / Log Analytics /
// Storage tokens fresh on the per-user UserTokens bag so background turns
// (browser closed) and long-running scoring jobs don't hit silent 401s after
// the ~60-min access-token expiry. Uses the persisted MSAL refresh token.
var tokenRefresher = new TenantTokenRefresher(
    telemetry,
    app.Services.GetRequiredService<SessionTokenStore>(),
    app.Services.GetRequiredService<PersistentIdentity>(),
    app.Services.GetRequiredService<IHttpClientFactory>(),
    loggerFactory.CreateLogger<TenantTokenRefresher>());
await tokenRefresher.StartAsync(CancellationToken.None);
app.Lifetime.ApplicationStopping.Register(() =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try { tokenRefresher.StopAsync(cts.Token).GetAwaiter().GetResult(); } catch { }
});

// Scheduled background jobs — user-defined prompt + cadence, executed as agent
// turns in per-job sessions. Auth is delegated-only via the persisted refresh
// token (see JobScheduler docs); tool surface identical to chat (no DELETE).
var jobStore = new AzureFinOps.Dashboard.Jobs.JobStore(loggerFactory.CreateLogger("AzureFinOps.Jobs"));
var jobScheduler = new AzureFinOps.Dashboard.Jobs.JobScheduler(
    jobStore,
    telemetry,
    copilotFactory,
    app.Services.GetRequiredService<SessionTokenStore>(),
    app.Services.GetRequiredService<PersistentIdentity>(),
    app.Services.GetRequiredService<IHttpClientFactory>(),
    loggerFactory.CreateLogger<AzureFinOps.Dashboard.Jobs.JobScheduler>());
await jobScheduler.StartAsync(CancellationToken.None);
app.Lifetime.ApplicationStopping.Register(() =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try { jobScheduler.StopAsync(cts.Token).GetAwaiter().GetResult(); } catch { }
});

// ── Middleware pipeline ────────────────────────────────────────
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

// Security headers — corporate proxies (Zscaler, Cisco Umbrella, Palo Alto) flag/block
// sites missing these headers as "uncategorized" or "potentially unsafe".
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    if (!app.Environment.IsDevelopment())
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
    // The standalone /slides deck has inline <script> + Google Fonts. Relax CSP for that one route.
    var path = ctx.Request.Path.Value ?? "";
    if (path.Equals("/slides", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/slide", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/slides.html", StringComparison.OrdinalIgnoreCase))
    {
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
            "font-src 'self' https://fonts.gstatic.com; " +
            "img-src 'self' data: https://i.ytimg.com; " +
            "connect-src 'self'; " +
            "frame-src 'self' https://www.youtube.com https://www.youtube-nocookie.com; " +
            "frame-ancestors 'none'";
    }
    else
    {
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'wasm-unsafe-eval' blob:; " +
            "worker-src 'self' blob:; " +
            "style-src 'self' 'unsafe-inline'; " +
            // blob: for local thumbnails of pasted/uploaded screenshots
            // (URL.createObjectURL in the attachment chips).
            "img-src 'self' data: blob:; " +
            "connect-src 'self' blob: data: https://cdn.jsdelivr.net https://js.monitor.azure.com https://*.in.applicationinsights.azure.com https://*.livediagnostics.monitor.azure.com; " +
            "font-src 'self'; " +
            "frame-ancestors 'none'";
    }
    await next();
});

// Redirect www.* to bare domain so OAuth callbacks always use canonical host
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
    {
        var bare = host[4..];
        var port = ctx.Request.Host.Port;
        var newHost = port.HasValue ? $"{bare}:{port}" : bare;
        var url = $"{ctx.Request.Scheme}://{newHost}{ctx.Request.Path}{ctx.Request.QueryString}";
        ctx.Response.Redirect(url, permanent: true);
        return;
    }
    await next();
});

app.UseSession();
app.UseDefaultFiles();
// Cache policy that makes deploys atomic for browsers:
//  • /assets/* files are content-hashed by Vite → cache forever (immutable).
//  • index.html must NEVER be cached — a cached copy references the PREVIOUS
//    deploy's hashed bundles, which 404 after a redeploy and leave the user a
//    blank page until a hard refresh (observed live in Edge after the last
//    deploy: cached index → stale /assets/index-*.js 404 → white screen).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = spCtx =>
    {
        var p = spCtx.Context.Request.Path.Value ?? "";
        if (p.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            spCtx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        else if (p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || p == "/" || p == "")
            spCtx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

// Absolute session lifetime — even if the user is active, force re-auth after 8h.
// Limits the blast radius of a stolen session cookie.
const int AbsoluteSessionMaxHours = 8;
app.Use(async (ctx, next) =>
{
    var startStr = ctx.Session.GetString("session_started_utc");
    if (startStr is null)
    {
        ctx.Session.SetString("session_started_utc", DateTimeOffset.UtcNow.ToString("o"));
    }
    else if (DateTimeOffset.TryParse(startStr, out var started)
             && DateTimeOffset.UtcNow - started > TimeSpan.FromHours(AbsoluteSessionMaxHours))
    {
        ctx.Session.Clear();
        ctx.Session.SetString("session_started_utc", DateTimeOffset.UtcNow.ToString("o"));
    }
    await next();
});

// CSRF defense — for state-changing requests, require Origin/Referer to match this host.
// Combined with SameSite=Lax cookies this defeats the standard CSRF surface.
app.Use(async (ctx, next) =>
{
    var method = ctx.Request.Method;
    if (method == "POST" || method == "PUT" || method == "PATCH" || method == "DELETE")
    {
        // Allow OAuth callback POSTs from Microsoft (none today, but defensive).
        // Allow non-mutating endpoints (none of ours are POST without state change).
        var origin = ctx.Request.Headers.Origin.ToString();
        var referer = ctx.Request.Headers.Referer.ToString();
        var sourceHost = "";
        if (Uri.TryCreate(origin, UriKind.Absolute, out var oUri)) sourceHost = oUri.Authority;
        else if (Uri.TryCreate(referer, UriKind.Absolute, out var rUri)) sourceHost = rUri.Authority;

        var ownHost = ctx.Request.Host.Value ?? "";
        if (string.IsNullOrEmpty(sourceHost) || !string.Equals(sourceHost, ownHost, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("Forbidden: cross-origin write blocked");
            return;
        }
    }
    await next();
});

// Auto-assign anonymous session user on first request (no login required for chat).
// If the user previously authenticated with Entra, the encrypted finops_id cookie
// lets us silently rehydrate identity + refresh token across container restarts.
var persistentIdentity = app.Services.GetRequiredService<PersistentIdentity>();
app.Use(async (ctx, next) =>
{
    var hasUser = ctx.Session.GetString("user") is not null;
    var hasAzureUser = ctx.Session.GetString("azure_user") is not null;
    if (!hasUser || !hasAzureUser)
    {
        var record = persistentIdentity.Load(ctx);
        if (record is not null && !string.IsNullOrEmpty(record.Oid))
        {
            // Returning Entra user: rebuild the session blobs deterministically.
            // We rehydrate `azure_user` even when `user` is already set so the
            // sidebar always shows the signed-in email after a backend restart
            // (the in-memory session middleware loses azure_user across
            // process boundaries; the persistent identity cookie survives).
            if (!hasUser)
            {
                ctx.Session.SetString("user", JsonSerializer.Serialize(new
                {
                    id = record.UserId,
                    login = $"user-{record.UserId & 0xFFFF:X4}",
                    name = record.Name,
                    avatar = (string?)null,
                    email = record.Email,
                }));
            }
            if (!hasAzureUser)
            {
                ctx.Session.SetString("azure_user", JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["tenantId"] = record.TenantId,
                    ["objectId"] = record.Oid,
                    ["name"] = record.Name,
                    ["email"] = record.Email,
                }));
            }
            if (!string.IsNullOrEmpty(record.RefreshToken) && ctx.Session.GetString("azure_refresh_token") is null)
                ctx.Session.SetString("azure_refresh_token", record.RefreshToken);
            if (!string.IsNullOrEmpty(record.GraphTier) && ctx.Session.GetString("graph_tier") is null)
                ctx.Session.SetString("graph_tier", record.GraphTier);
        }
        else if (!hasUser)
        {
            // Brand-new visitor: crypto-random anonymous id keyed only in this session.
            var sessionUserId = (long)(RandomNumberGenerator.GetInt32(1_000_000, int.MaxValue)) << 24
                                 | (long)RandomNumberGenerator.GetInt32(0, 1 << 24);
            ctx.Session.SetString("user", JsonSerializer.Serialize(new
            {
                id = sessionUserId,
                login = $"user-{sessionUserId % 10000:D4}",
                name = (string?)null,
                avatar = (string?)null,
                email = (string?)null
            }));
        }
    }
    await next();
});

// ── Endpoints ──────────────────────────────────────────────────
var tokenStore = app.Services.GetRequiredService<SessionTokenStore>();
var entraCredentials = app.Services.GetRequiredService<EntraClientCredentials>();
var idTokenValidator = app.Services.GetRequiredService<IdTokenValidator>();

app.MapMicrosoftAuthEndpoints(oauthOptions, entraCredentials, idTokenValidator, telemetry, persistentIdentity, logger);
app.MapAzureSessionEndpoints(tokenStore, telemetry, logger);
app.MapChatEndpoints(copilotFactory, tokenStore, telemetry, logger);
app.MapSessionEndpoints(copilotFactory, telemetry, logger);
AzureFinOps.Dashboard.Jobs.JobEndpoints.MapJobEndpoints(app, jobStore, jobScheduler, logger);
app.MapMetaEndpoints(appInsightsCs ?? "", azureOpenAIDeployment);
app.MapDownloadEndpoints();
app.MapUploadEndpoints();
app.MapSeoEndpoints();

// Customer overview deck — clean URL (file is also reachable at /slides.html)
// Both /slides (canonical) and /slide (alias) serve the same deck.
IResult ServeSlides(IWebHostEnvironment env)
{
    var path = Path.Combine(env.WebRootPath, "slides.html");
    return File.Exists(path)
        ? Results.File(path, "text/html; charset=utf-8")
        : Results.NotFound();
}
app.MapGet("/slides", ServeSlides);
app.MapGet("/slide", ServeSlides);

// SPA fallback (deep links like /faq/... handled elsewhere; anything unmatched
// gets index.html). Must carry the same no-cache policy as direct index.html
// hits — MapFallbackToFile uses its OWN StaticFileOptions, not UseStaticFiles'.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = spCtx =>
        spCtx.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();
