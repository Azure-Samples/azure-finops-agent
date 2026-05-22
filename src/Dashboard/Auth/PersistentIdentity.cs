using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Persistent per-user identity + OAuth refresh-token store backed by an
/// encrypted JSON file under <c>$COPILOT_HOME/users/{oid}/identity.json</c>.
///
/// Why this exists: the ASP.NET <see cref="ISession"/> store is in-memory
/// (<c>AddDistributedMemoryCache</c>) so OAuth tokens vanish on every container
/// restart, forcing users to re-authenticate. We avoid the dependency cost of
/// Redis (and the <strong>file-locking corruption</strong> that breaks SQLite on
/// Azure Files SMB) by writing the long-lived refresh token + a small identity
/// blob to the same persistent <c>/home</c> Azure Files mount the Copilot SDK
/// already uses for chat history, encrypted with ASP.NET Data Protection.
///
/// On the next request after a restart, a hydration middleware reads the
/// signed <c>finops_id</c> cookie (set after successful Entra login), looks up
/// the identity file, and silently mints fresh access tokens via the cached
/// refresh_token. The user never sees a re-auth prompt.
///
/// Security: the file is encrypted with a key from <see cref="IDataProtector"/>
/// scoped to <c>FinOps.Identity.v1</c>; keys persist to
/// <c>/home/dataprotection-keys/</c> so they survive restarts but never leave
/// the tenant. Only refresh tokens are written to disk &#8212; access tokens
/// stay in-memory. The cookie itself contains only an opaque token; the OID is
/// inside the encrypted payload.
/// </summary>
public sealed class PersistentIdentity
{
    private const string IdentityCookieName = "finops_id";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    private readonly IDataProtector _protector;
    private readonly ILogger<PersistentIdentity> _logger;

    // Per-oid serialization lock so concurrent SaveIdentity / UpdateRefreshToken
    // / UpdateGraphTier calls can't race on the same file. Cheap: one Semaphore
    // per logged-in user, GC'd implicitly when the dict is rebuilt on restart.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private static SemaphoreSlim LockFor(string oid) =>
        _fileLocks.GetOrAdd(oid, _ => new SemaphoreSlim(1, 1));

    // userId → oid lookup so background services (e.g. TenantTokenRefresher)
    // can find an identity record by the userId surfaced in telemetry without
    // an HttpContext. Populated on every Save / Load / Update so once a user has
    // touched the system in this process, lookup is O(1).
    private static readonly ConcurrentDictionary<long, string> _userIdToOid = new();

    public PersistentIdentity(IDataProtectionProvider provider, ILogger<PersistentIdentity> logger)
    {
        _protector = provider.CreateProtector("FinOps.Identity.v1");
        _logger = logger;
    }

    /// <summary>SHA-256 of the Entra OID, folded into a 64-bit id. Stable across
    /// devices and sessions for the same human; collisions are astronomically
    /// unlikely (birthday bound > 2^32 OIDs before any expected collision).</summary>
    public static long DeriveUserId(string oid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(oid));
        return BitConverter.ToInt64(hash, 0);
    }

    /// <summary>Persists identity + the rotating refresh token to disk and
    /// writes the encrypted identity cookie. Call this from the OAuth callback
    /// after a successful id_token validation and from any path that mints a
    /// new refresh_token.</summary>
    public async Task SaveIdentityAsync(HttpContext ctx, IdentityRecord record)
    {
        var sem = LockFor(record.Oid);
        await sem.WaitAsync();
        try
        {
            var dir = GetUserDir(record.Oid);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "identity.json");
            var encrypted = _protector.Protect(JsonSerializer.Serialize(record));
            AtomicWrite(path, encrypted);
            _userIdToOid[record.UserId] = record.Oid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist identity for oid={Oid}", record.Oid);
        }
        finally { sem.Release(); }

        try
        {
            var cookie = _protector.Protect(record.Oid);
            ctx.Response.Cookies.Append(IdentityCookieName, cookie, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.Add(CookieLifetime),
                Path = "/",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set identity cookie");
        }
    }

    /// <summary>Returns the persisted identity for the OID encoded in the
    /// caller's <c>finops_id</c> cookie, or null if absent / tampered / the
    /// file is missing.</summary>
    public IdentityRecord? Load(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(IdentityCookieName, out var cookie) || string.IsNullOrEmpty(cookie))
            return null;

        string oid;
        try { oid = _protector.Unprotect(cookie); }
        catch
        {
            // Tampered or key-rotated cookie &#8212; clear it so the browser stops sending.
            ctx.Response.Cookies.Delete(IdentityCookieName);
            return null;
        }

        var path = Path.Combine(GetUserDir(oid), "identity.json");
        if (!File.Exists(path)) return null;

        try
        {
            var encrypted = File.ReadAllText(path);
            var json = _protector.Unprotect(encrypted);
            var rec = JsonSerializer.Deserialize<IdentityRecord>(json);
            if (rec is not null) _userIdToOid[rec.UserId] = rec.Oid;
            return rec;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load identity from {Path}", path);
            return null;
        }
    }

    /// <summary>Clears the identity cookie and removes the on-disk file. Called
    /// from /auth/logout.</summary>
    public void Clear(HttpContext ctx, string? oid)
    {
        ctx.Response.Cookies.Delete(IdentityCookieName);
        if (!string.IsNullOrEmpty(oid))
        {
            try { File.Delete(Path.Combine(GetUserDir(oid), "identity.json")); }
            catch { }
        }
    }

    /// <summary>Loads an identity by its derived userId, used by background
    /// services that have no HttpContext (e.g. <c>TenantTokenRefresher</c>).
    /// First-call after a process restart falls back to a cheap directory scan
    /// to populate the cache; subsequent calls are O(1).</summary>
    public IdentityRecord? LoadByUserId(long userId)
    {
        if (_userIdToOid.TryGetValue(userId, out var cachedOid))
            return LoadByOid(cachedOid);

        // Cold path after restart: walk users/ until we find a match. Cheap —
        // O(active users) and only on cache misses.
        var root = Path.Combine(CopilotHome, "users");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var oid = Path.GetFileName(dir);
            var rec = LoadByOid(oid);
            if (rec is not null && rec.UserId == userId) return rec;
        }
        return null;
    }

    /// <summary>Loads an identity by Entra OID directly (no cookie / context
    /// required). Returns null if the file is missing or undecryptable.</summary>
    public IdentityRecord? LoadByOid(string oid)
    {
        var path = Path.Combine(GetUserDir(oid), "identity.json");
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllText(path);
            var json = _protector.Unprotect(encrypted);
            var rec = JsonSerializer.Deserialize<IdentityRecord>(json);
            if (rec is not null) _userIdToOid[rec.UserId] = rec.Oid;
            return rec;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Updates only the refresh token + recorded scopes on an existing
    /// identity file. Used by <see cref="SessionTokenStore"/> when a refresh
    /// rotates the token (Entra rotates refresh tokens on use).</summary>
    public Task UpdateRefreshTokenAsync(string oid, string newRefreshToken)
    {
        return UpdateRecordAsync(oid, r => { r.RefreshToken = newRefreshToken; });
    }

    /// <summary>Persists the comma-separated list of consented Graph tiers so a
    /// post-restart hydration restores the user's full add-on set, not just the
    /// base ARM scope.</summary>
    public Task UpdateGraphTierAsync(string oid, string? graphTier)
    {
        return UpdateRecordAsync(oid, r => { r.GraphTier = graphTier; });
    }

    private async Task UpdateRecordAsync(string oid, Action<IdentityRecord> mutate)
    {
        var path = Path.Combine(GetUserDir(oid), "identity.json");
        if (!File.Exists(path)) return;
        var sem = LockFor(oid);
        await sem.WaitAsync();
        try
        {
            var existing = JsonSerializer.Deserialize<IdentityRecord>(_protector.Unprotect(File.ReadAllText(path)));
            if (existing is null) return;
            mutate(existing);
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            AtomicWrite(path, _protector.Protect(JsonSerializer.Serialize(existing)));
            _userIdToOid[existing.UserId] = existing.Oid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update identity record for oid={Oid}", oid);
        }
        finally { sem.Release(); }
    }

    /// <summary>Crash-safe write: stage to a sibling .tmp then atomically replace
    /// the target. A torn write can leave the .tmp behind but never corrupts the
    /// live identity.json &#8212; users keep their refresh token across restarts.</summary>
    private static void AtomicWrite(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    private static string GetUserDir(string oid) => Path.Combine(CopilotHome, "users", oid);
}

/// <summary>Encrypted-on-disk identity record. Contains only the long-lived
/// refresh token; access tokens are NOT persisted (they live ~1 hour anyway and
/// staying in-memory limits exposure).</summary>
public sealed class IdentityRecord
{
    public string Oid { get; set; } = "";
    public string TenantId { get; set; } = "";
    public long UserId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? RefreshToken { get; set; }
    /// <summary>Comma-separated list of consented Graph tiers (e.g. "licenses,chargeback").</summary>
    public string? GraphTier { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
