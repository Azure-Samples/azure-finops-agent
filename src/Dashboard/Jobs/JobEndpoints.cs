using System.Text.Json;
using AzureFinOps.Dashboard.AI;

namespace AzureFinOps.Dashboard.Jobs;

/// <summary>
/// CRUD + run-now API for scheduled jobs. Entra-only (jobs need the persisted
/// refresh token for background auth; anonymous users don't have one). All
/// mutating routes enforce ownership via the job's EntraOid/UserId.
/// </summary>
public static class JobEndpoints
{
    // Cadence bounds — presets (15/60/1440/10080) plus any custom value in
    // between. 1 min floor = the scheduler's tick cadence (the fastest it can
    // fire); 30 day ceiling keeps a job meaningful against its expiry horizon.
    private const int MinIntervalMinutes = 1;
    private const int MaxIntervalMinutes = 43200; // 30 days
    private const int MaxEnabledJobsPerUser = 3;

    public static void MapJobEndpoints(
        this IEndpointRouteBuilder app,
        JobStore store,
        JobScheduler scheduler,
        ILogger logger)
    {
        app.MapGet("/api/jobs", (HttpContext ctx) =>
        {
            if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(entraOid))
                return Results.Ok(new { jobs = Array.Empty<object>(), entraRequired = true });

            var jobs = store.ForUser(userId, entraOid).Select(ToDto);
            return Results.Ok(new { jobs, entraRequired = false });
        });

        app.MapPost("/api/jobs", async (HttpContext ctx) =>
        {
            if (!TryResolveUser(ctx, out var userId, out var userLogin, out var entraOid))
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(entraOid))
                return Results.BadRequest(new { error = "Connect Azure first — scheduled jobs run in the background on your behalf and need a signed-in Azure connection." });

            using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = body.RootElement;
            var prompt = root.TryGetProperty("prompt", out var p) ? p.GetString()?.Trim() : null;
            var name = root.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            var interval = root.TryGetProperty("intervalMinutes", out var i) && i.TryGetInt32(out var iv) ? iv : 1440;
            // Default TRUE: most users want a first result right away, then the
            // cadence. Unchecked = first run only after one full interval.
            var runNow = !root.TryGetProperty("runImmediately", out var r) || r.ValueKind != JsonValueKind.False;

            if (string.IsNullOrWhiteSpace(prompt))
                return Results.BadRequest(new { error = "prompt is required" });
            if (prompt.Length > 2000)
                return Results.BadRequest(new { error = "prompt too long (max 2000 chars)" });
            if (interval < MinIntervalMinutes || interval > MaxIntervalMinutes)
                return Results.BadRequest(new { error = $"intervalMinutes must be between {MinIntervalMinutes} and {MaxIntervalMinutes}" });
            if (store.EnabledCountForUser(userId, entraOid) >= MaxEnabledJobsPerUser)
                return Results.BadRequest(new { error = $"Limit reached — max {MaxEnabledJobsPerUser} active jobs. Pause or delete one first." });

            var job = new ScheduledJob
            {
                UserId = userId,
                EntraOid = entraOid,
                UserLogin = userLogin,
                Name = string.IsNullOrWhiteSpace(name) ? Autoname(prompt) : name[..Math.Min(name.Length, 60)],
                Prompt = prompt,
                IntervalMinutes = interval,
                // Sub-daily cadences are for short-lived hunts (capacity, quota);
                // daily/weekly are standing reports. Different expiry horizons.
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(interval < 1440 ? 7 : 90),
                // runNow: fire directly below AND keep NextRunUtc=now as a
                // crash-safe fallback (the run itself reschedules to now+interval).
                // Otherwise the first run waits one full interval.
                NextRunUtc = runNow ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMinutes(interval),
            };
            store.Add(job);
            logger.LogInformation("Job created {JobId} '{Name}' every {Interval}min runNow={RunNow} by {User}", job.Id, job.Name, interval, runNow, userLogin);
            if (runNow)
                _ = Task.Run(() => scheduler.RunJobAsync(job, CancellationToken.None));
            return Results.Ok(ToDto(job));
        });

        app.MapPatch("/api/jobs/{id}", async (HttpContext ctx, string id) =>
        {
            var (job, err) = ResolveOwnedJob(ctx, store, id);
            if (err is not null) return err;

            using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = body.RootElement;

            // Each field is optional — apply only what's present, validate first.
            string? newPrompt = null, newName = null;
            int? newInterval = null;
            if (root.TryGetProperty("prompt", out var p))
            {
                newPrompt = p.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(newPrompt))
                    return Results.BadRequest(new { error = "prompt cannot be empty" });
                if (newPrompt.Length > 2000)
                    return Results.BadRequest(new { error = "prompt too long (max 2000 chars)" });
            }
            if (root.TryGetProperty("name", out var n))
                newName = n.GetString()?.Trim();
            if (root.TryGetProperty("intervalMinutes", out var i) && i.TryGetInt32(out var iv))
            {
                if (iv < MinIntervalMinutes || iv > MaxIntervalMinutes)
                    return Results.BadRequest(new { error = $"intervalMinutes must be between {MinIntervalMinutes} and {MaxIntervalMinutes}" });
                newInterval = iv;
            }

            if (newPrompt is not null) job!.Prompt = newPrompt;
            if (newName is not null)
                job!.Name = string.IsNullOrWhiteSpace(newName)
                    ? Autoname(job.Prompt)
                    : newName[..Math.Min(newName.Length, 60)];
            if (newInterval is not null && newInterval.Value != job!.IntervalMinutes)
            {
                job.IntervalMinutes = newInterval.Value;
                // New cadence horizon + reschedule the next run onto it (only if
                // the job is armed; paused jobs keep their frozen NextRunUtc).
                job.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(newInterval.Value < 1440 ? 7 : 90);
                if (job.Enabled)
                    job.NextRunUtc = DateTimeOffset.UtcNow.AddMinutes(newInterval.Value);
            }
            store.Save();
            logger.LogInformation("Job edited {JobId} '{Name}'", job!.Id, job.Name);
            return Results.Ok(ToDto(job));
        });

        app.MapPost("/api/jobs/{id}/toggle", (HttpContext ctx, string id) =>
        {
            var (job, err) = ResolveOwnedJob(ctx, store, id);
            if (err is not null) return err;
            // Re-enabling a paused job counts against the same active cap as
            // creating one — otherwise pause+create×3+resume yields 4 active,
            // contradicting the "max N active" guarantee the UI shows.
            if (!job!.Enabled && store.EnabledCountForUser(job.UserId, job.EntraOid) >= MaxEnabledJobsPerUser)
                return Results.BadRequest(new { error = $"Limit reached — max {MaxEnabledJobsPerUser} active jobs. Pause or delete one first." });
            job.Enabled = !job.Enabled;
            if (job.Enabled)
            {
                job.ConsecutiveFailures = 0;
                if (job.ExpiresUtc <= DateTimeOffset.UtcNow)
                    job.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(job.IntervalMinutes < 1440 ? 7 : 90);
                job.NextRunUtc = DateTimeOffset.UtcNow;
            }
            store.Save();
            return Results.Ok(ToDto(job));
        });

        app.MapPost("/api/jobs/{id}/run", (HttpContext ctx, string id) =>
        {
            var (job, err) = ResolveOwnedJob(ctx, store, id);
            if (err is not null) return err;
            // Fire-and-forget — the run shows up in the job's conversation; the
            // sidebar polls status. Serialized against chat turns by the shared
            // per-session turn gate inside RunJobAsync.
            _ = Task.Run(() => scheduler.RunJobAsync(job!, CancellationToken.None));
            return Results.Ok(new { started = true });
        });

        app.MapDelete("/api/jobs/{id}", (HttpContext ctx, string id) =>
        {
            var (job, err) = ResolveOwnedJob(ctx, store, id);
            if (err is not null) return err;
            store.Remove(id);
            logger.LogInformation("Job deleted {JobId} '{Name}'", job!.Id, job.Name);
            // The backing conversation is intentionally kept — it holds the run
            // history and the user can delete it from the Conversations list.
            return Results.NoContent();
        });
    }

    private static (ScheduledJob? Job, IResult? Error) ResolveOwnedJob(HttpContext ctx, JobStore store, string id)
    {
        if (!TryResolveUser(ctx, out var userId, out _, out var entraOid))
            return (null, Results.Unauthorized());
        var job = store.Get(id);
        if (job is null) return (null, Results.NotFound());
        // Jobs are Entra-only, so the OID is the identity — exact match required.
        // (No userId-hash fallback: OIDs are the tenant-scoped source of truth,
        // and these jobs can perform ARM writes on the owner's subscriptions.)
        var owned = !string.IsNullOrEmpty(entraOid) && job.EntraOid == entraOid;
        if (!owned) return (null, Results.NotFound()); // no ownership oracle
        return (job, null);
    }

    private static object ToDto(ScheduledJob j) => new
    {
        id = j.Id,
        name = j.Name,
        prompt = j.Prompt,
        intervalMinutes = j.IntervalMinutes,
        sessionId = j.SessionId,
        enabled = j.Enabled,
        nextRunUtc = j.NextRunUtc,
        lastRunUtc = j.LastRunUtc,
        lastStatus = j.LastStatus,
        lastSummary = j.LastSummary,
        runCount = j.RunCount,
        expiresUtc = j.ExpiresUtc,
        running = j.SessionId is not null && ChatEndpoints.IsTurnActive(j.SessionId),
    };

    private static string Autoname(string prompt)
    {
        var s = prompt.Replace('\n', ' ').Trim();
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    private static bool TryResolveUser(HttpContext ctx, out long userId, out string userLogin, out string? entraOid)
    {
        userId = 0;
        userLogin = "";
        entraOid = null;
        var userJson = ctx.Session.GetString("user");
        if (userJson is null) return false;
        try
        {
            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            userId = user.GetProperty("id").GetInt64();
            userLogin = user.TryGetProperty("login", out var loginProp) ? (loginProp.GetString() ?? userId.ToString()) : userId.ToString();
        }
        catch { return false; }
        var azureUserJson = ctx.Session.GetString("azure_user");
        if (azureUserJson is not null)
        {
            try
            {
                var au = JsonSerializer.Deserialize<JsonElement>(azureUserJson);
                if (au.TryGetProperty("objectId", out var oidProp)) entraOid = oidProp.GetString();
            }
            catch { /* ignore malformed */ }
        }
        return true;
    }
}
