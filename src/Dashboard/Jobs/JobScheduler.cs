using AzureFinOps.Dashboard.AI;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Observability;
using GitHub.Copilot;

namespace AzureFinOps.Dashboard.Jobs;

/// <summary>
/// Runs user-defined scheduled jobs (prompt + cadence) as background agent
/// turns in each job's dedicated Copilot session. Ticks every minute, finds
/// due jobs, and executes them sequentially (jobs are rare; sequential keeps
/// AOAI + ARM pressure trivial).
///
/// Auth: delegated-only. Each run self-hydrates the user's ARM (+ optional
/// Graph/LA/Storage) access tokens from the persisted MSAL refresh token via
/// <see cref="SessionTokenStore.ExchangeRefreshTokenForResource"/>. This is
/// deliberate — the in-memory <see cref="UserTokens"/> bag is EMPTY after a
/// container restart and <see cref="TenantTokenRefresher"/> only tops up
/// tokens that already exist, so a scheduler that merely read the bag would
/// silently run unauthenticated after every deploy. If the ARM refresh fails
/// (consent revoked, password change, CA policy) the job pauses with status
/// <c>auth_expired</c> instead of burning runs.
///
/// Safety: shares the one-turn-per-session gate with chat
/// (<see cref="ChatEndpoints.TryBeginTurn"/>) so a run can never race a live
/// chat turn in the same session; preserves the user's CurrentSessionId
/// (session acquisition repoints it as a side effect — without restore, a 3 AM
/// run would hijack which conversation the user sees next morning); tool
/// surface is identical to chat (GET/POST/PUT/PATCH, DELETE blocked).
/// </summary>
public sealed class JobScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    private const int MaxConsecutiveFailures = 5;

    private readonly JobStore _store;
    private readonly AiTelemetry _telemetry;
    private readonly CopilotSessionFactory _factory;
    private readonly SessionTokenStore _tokenStore;
    private readonly PersistentIdentity _identity;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<JobScheduler> _logger;

    public JobScheduler(
        JobStore store,
        AiTelemetry telemetry,
        CopilotSessionFactory factory,
        SessionTokenStore tokenStore,
        PersistentIdentity identity,
        IHttpClientFactory httpFactory,
        ILogger<JobScheduler> logger)
    {
        _store = store;
        _telemetry = telemetry;
        _factory = factory;
        _tokenStore = tokenStore;
        _identity = identity;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish startup before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "JobScheduler sweep failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in _store.DueJobs(now))
        {
            if (ct.IsCancellationRequested) return;

            if (job.ExpiresUtc <= now)
            {
                job.Enabled = false;
                job.LastStatus = "expired";
                _store.Save();
                _logger.LogInformation("Job {JobId} '{Name}' expired; disabled", job.Id, job.Name);
                continue;
            }

            await RunJobAsync(job, ct);
        }
    }

    /// <summary>Executes one job run end-to-end. Public so the "Run now"
    /// endpoint can trigger a run without waiting for the next tick.</summary>
    public async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        // Reentrancy guard: "Run now" can race the minute sweep (or a second
        // click). Without this, two concurrent runs could both create a session
        // for a first-run job (orphaning one) and double-spend LLM turns.
        if (!_runningJobs.TryAdd(job.Id, 0)) return;
        try
        {
            await RunJobCoreAsync(job, ct);
        }
        finally
        {
            _runningJobs.TryRemove(job.Id, out _);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _runningJobs = new();

    private async Task RunJobCoreAsync(ScheduledJob job, CancellationToken ct)
    {
        // Reschedule FIRST so a crash mid-run can't produce a hot retry loop.
        job.NextRunUtc = DateTimeOffset.UtcNow.AddMinutes(job.IntervalMinutes);
        _store.Save();

        // 1) Hydrate delegated tokens from the persisted refresh token.
        var record = _identity.LoadByUserId(job.UserId)
                     ?? (string.IsNullOrEmpty(job.EntraOid) ? null : _identity.LoadByOid(job.EntraOid));
        if (record is null || string.IsNullOrEmpty(record.RefreshToken))
        {
            MarkFailure(job, "auth_expired", "No stored Azure connection — reconnect Azure to resume this job.");
            return;
        }

        var tokens = _telemetry.UserTokens.GetOrAdd(job.UserId, id => new UserTokens { UserId = id });
        await tokens.RefreshLock.WaitAsync(ct);
        try
        {
            var armOk = await HydrateAsync(record, TenantTokenRefresher.ScopeArm,
                v => { tokens.AzureToken = v.Token; tokens.AzureTokenExpiry = v.Expiry; });
            if (!armOk)
            {
                MarkFailure(job, "auth_expired", "Azure token refresh failed — reconnect Azure to resume this job.");
                return;
            }
            // Best-effort extras: only for consented add-on tiers (unconsented
            // scopes are guaranteed HTTP 400s — see SessionTokenStore gating).
            var tiers = record.GraphTier ?? "";
            if (tiers.Contains("licenses") || tiers.Contains("chargeback"))
                await HydrateAsync(record, TenantTokenRefresher.ScopeGraph,
                    v => { tokens.GraphToken = v.Token; tokens.GraphTokenExpiry = v.Expiry; });
            if (tiers.Contains("loganalytics"))
                await HydrateAsync(record, TenantTokenRefresher.ScopeLogAnalytics,
                    v => { tokens.LogAnalyticsToken = v.Token; tokens.LogAnalyticsTokenExpiry = v.Expiry; });
            if (tiers.Contains("storage"))
                await HydrateAsync(record, TenantTokenRefresher.ScopeStorage,
                    v => { tokens.StorageToken = v.Token; tokens.StorageTokenExpiry = v.Expiry; });
        }
        finally
        {
            tokens.RefreshLock.Release();
        }

        // Keep the janitor from evicting this user's state mid-run.
        UserStateJanitor.LastSeenUtc[job.UserId] = DateTimeOffset.UtcNow;

        // 2) Acquire the job's dedicated session — WITHOUT hijacking the user's
        // current conversation (session acquisition repoints CurrentSessionId).
        var hadCurrent = _telemetry.CurrentSessionId.TryGetValue(job.UserId, out var prevCurrent);
        CopilotSession session;
        try
        {
            if (!string.IsNullOrEmpty(job.SessionId))
            {
                try
                {
                    session = await _factory.GetOrResumeAsync(job.UserId, job.SessionId, job.UserLogin, job.EntraOid);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Job {JobId}: resume of session {SessionId} failed; creating fresh", job.Id, job.SessionId);
                    session = await _factory.CreateNewAsync(job.UserId, job.UserLogin, job.EntraOid);
                }
            }
            else
            {
                session = await _factory.CreateNewAsync(job.UserId, job.UserLogin, job.EntraOid);
            }
        }
        finally
        {
            // Restore the user's real current conversation.
            if (hadCurrent && prevCurrent is not null) _telemetry.CurrentSessionId[job.UserId] = prevCurrent;
            else _telemetry.CurrentSessionId.TryRemove(job.UserId, out _);
        }

        if (job.SessionId != session.SessionId)
        {
            job.SessionId = session.SessionId;
            // Label the backing conversation so it's recognizable in the sidebar.
            _telemetry.SaveTitle(session.SessionId, $"⚙ {job.Name}");
            _store.Save();
        }

        // 3) One turn per session — never race a live chat turn.
        if (!ChatEndpoints.TryBeginTurn(session.SessionId))
        {
            job.LastStatus = "busy";
            job.NextRunUtc = DateTimeOffset.UtcNow.AddMinutes(2); // retry shortly
            _store.Save();
            _logger.LogInformation("Job {JobId}: session {SessionId} busy with a live turn; retrying in 2 min", job.Id, session.SessionId);
            return;
        }

        try
        {
            var (ok, summary) = await RunTurnAsync(job, session, ct);
            job.LastRunUtc = DateTimeOffset.UtcNow;
            job.RunCount++;
            if (ok)
            {
                job.LastStatus = "ok";
                job.LastSummary = summary;
                job.ConsecutiveFailures = 0;
            }
            else
            {
                job.ConsecutiveFailures++;
                job.LastStatus = "error";
                job.LastSummary = summary;
                if (job.ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    job.Enabled = false;
                    _logger.LogWarning("Job {JobId} '{Name}' paused after {N} consecutive failures", job.Id, job.Name, job.ConsecutiveFailures);
                }
            }
            _store.Save();
            _logger.LogInformation("Job {JobId} '{Name}' run #{Run} status={Status}", job.Id, job.Name, job.RunCount, job.LastStatus);
        }
        finally
        {
            ChatEndpoints.EndTurn(session.SessionId);
        }
    }

    /// <summary>Sends the job prompt into the session and waits for the turn to
    /// complete (SessionIdleEvent) or fail (SessionErrorEvent), with a hard
    /// timeout. Returns (success, answer-summary).</summary>
    private async Task<(bool Ok, string Summary)> RunTurnAsync(ScheduledJob job, CopilotSession session, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buf = new System.Text.StringBuilder();
        var bufLock = new object();

        using var sub = session.On(async (SessionEvent evt) =>
        {
            if (evt is AssistantMessageDeltaEvent ad && !string.IsNullOrEmpty(ad.Data.DeltaContent))
                lock (bufLock) { buf.Append(ad.Data.DeltaContent); }
            else if (evt is AssistantMessageEvent am && !string.IsNullOrWhiteSpace(am.Data.Content))
                lock (bufLock) { buf.Clear(); buf.Append(am.Data.Content); }
            else if (evt is SessionIdleEvent)
                done.TrySetResult(true);
            else if (evt is SessionErrorEvent err)
            {
                lock (bufLock) { buf.Clear(); buf.Append(err.Data?.Message ?? "session error"); }
                done.TrySetResult(false);
            }
            await Task.CompletedTask;
        });

        var cadence = job.IntervalMinutes switch
        {
            15 => "every 15 minutes",
            60 => "hourly",
            1440 => "daily",
            10080 => "weekly",
            _ => $"every {job.IntervalMinutes} min",
        };
        var prompt =
            $"[SCHEDULED JOB RUN — '{job.Name}' — run #{job.RunCount + 1}, cadence {cadence}, {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC. " +
            "This is an automated background run; no human is watching live. Produce a complete, CONCISE answer — lead with what changed since the last run if prior runs exist in this conversation. " +
            "If the goal is now achieved (e.g. capacity found and acted on) or permanently impossible, say so explicitly on the first line so the user knows to disable this job.]\n" +
            job.Prompt;

        try
        {
            await session.SendAsync(new MessageOptions { Prompt = prompt });
        }
        catch (Exception ex)
        {
            return (false, $"send failed: {ex.Message}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RunTimeout);
        try
        {
            var ok = await done.Task.WaitAsync(timeout.Token);
            string answer;
            lock (bufLock) { answer = buf.ToString(); }
            answer = answer.Trim();
            var summary = answer.Length > 200 ? answer[..200] + "…" : answer;
            return (ok, summary.Length > 0 ? summary : (ok ? "(empty answer)" : "session error"));
        }
        catch (OperationCanceledException)
        {
            // Turn still running server-side; we stop waiting. The answer will
            // land in the session transcript regardless.
            return (false, "run timed out after 10 min (answer may still appear in the conversation)");
        }
    }

    private async Task<bool> HydrateAsync(
        IdentityRecord record,
        string scope,
        Action<(string Token, DateTimeOffset Expiry)> apply)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var result = await _tokenStore.ExchangeRefreshTokenForResource(http, record.RefreshToken!, scope, record.TenantId);
            if (result is null) return false;
            apply((result.Value.Token, result.Value.Expiry));
            if (!string.IsNullOrEmpty(result.Value.RotatedRefreshToken) && result.Value.RotatedRefreshToken != record.RefreshToken)
            {
                await _identity.UpdateRefreshTokenAsync(record.Oid, result.Value.RotatedRefreshToken);
                record.RefreshToken = result.Value.RotatedRefreshToken;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job token hydration failed for scope prefix {Scope}", scope.Split(' ')[0]);
            return false;
        }
    }

    private void MarkFailure(ScheduledJob job, string status, string summary)
    {
        job.LastRunUtc = DateTimeOffset.UtcNow;
        job.LastStatus = status;
        job.LastSummary = summary;
        job.ConsecutiveFailures++;
        if (status == "auth_expired" || job.ConsecutiveFailures >= MaxConsecutiveFailures)
            job.Enabled = false; // paused until the user re-connects / re-enables
        _store.Save();
        _logger.LogWarning("Job {JobId} '{Name}' failed: {Status} — {Summary}", job.Id, job.Name, status, summary);
    }
}
