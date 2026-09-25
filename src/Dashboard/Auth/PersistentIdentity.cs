using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Persistent per-user identity + OAuth refresh-token store backed by an
/// encrypted JSON file under a tenant-and-object scoped directory beneath
/// <c>$COPILOT_HOME/users/</c>.
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
/// stay in-memory. The cookie itself contains only an opaque token; the tenant
/// and object identifiers are inside the encrypted payload.
/// </summary>
public sealed class PersistentIdentity
{
    private const string IdentityCookieName = "finops_id";
    private const string IdentityCookieVersion = "v2";
    private const string LegacyOwnerFileName = "owner.json";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    private readonly IDataProtector _protector;
    private readonly ILogger<PersistentIdentity> _logger;
    private readonly string _copilotHome;

    // Per-principal serialization lock so concurrent SaveIdentity / UpdateRefreshToken
    // / UpdateGraphTier calls can't race on the same file. Cheap: one Semaphore
    // per logged-in user, GC'd implicitly when the dict is rebuilt on restart.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private static SemaphoreSlim LockFor(string principalKey) =>
        _fileLocks.GetOrAdd(principalKey, _ => new SemaphoreSlim(1, 1));

    // userId → tenant/object lookup so background services (e.g. TenantTokenRefresher)
    // can find an identity record by the userId surfaced in telemetry without
    // an HttpContext. Populated on every Save / Load / Update so once a user has
    // touched the system in this process, lookup is O(1).
    private static readonly ConcurrentDictionary<long, (string TenantId, string Oid)> _userIdToPrincipal = new();
    private readonly ConcurrentDictionary<string, string> _principalDirectories = new();

    public PersistentIdentity(IDataProtectionProvider provider, ILogger<PersistentIdentity> logger)
        : this(provider, logger, CopilotHome)
    {
    }

    internal PersistentIdentity(IDataProtectionProvider provider, ILogger<PersistentIdentity> logger, string copilotHome)
    {
        _protector = provider.CreateProtector("FinOps.Identity.v1");
        _logger = logger;
        _copilotHome = copilotHome;
    }

    /// <summary>SHA-256 of the validated Entra tenant and object identifiers,
    /// folded into a 64-bit runtime owner id. An OID alone is not globally unique
    /// in a multi-tenant application.</summary>
    public static long DeriveUserId(string tenantId, string oid)
    {
        var hash = PrincipalHash(tenantId, oid);
        return BitConverter.ToInt64(hash, 0);
    }

    internal static string PrincipalDirectoryName(string tenantId, string oid) =>
        Convert.ToHexString(PrincipalHash(tenantId, oid)).ToLowerInvariant();

    private static byte[] PrincipalHash(string tenantId, string oid) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(PrincipalKey(tenantId, oid)));

    private static string PrincipalKey(string tenantId, string oid)
    {
        if (!Guid.TryParse(tenantId, out var tenantGuid) || !Guid.TryParse(oid, out var objectGuid))
            throw new ArgumentException("The tenant and object identifiers must be GUIDs.");
        return $"{tenantGuid:D}\n{objectGuid:D}";
    }

    /// <summary>Persists identity + the rotating refresh token to disk and
    /// writes the encrypted identity cookie. Call this from the OAuth callback
    /// after a successful id_token validation and from any path that mints a
    /// new refresh_token.</summary>
    public async Task SaveIdentityAsync(HttpContext ctx, IdentityRecord record)
    {
        if (!HasPrincipal(record))
            throw new ArgumentException("A tenant and object identifier are required.", nameof(record));

        record.UserId = DeriveUserId(record.TenantId, record.Oid);
        var principalKey = PrincipalKey(record.TenantId, record.Oid);
        var sem = LockFor(principalKey);
        await sem.WaitAsync();
        try
        {
            var dir = GetOwnedUserDirectory(record.TenantId, record.Oid);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "identity.json");
            var encrypted = _protector.Protect(JsonSerializer.Serialize(record));
            AtomicWrite(path, encrypted);
            WriteLegacyOwnerMarker(dir, record.TenantId, record.Oid);
            _userIdToPrincipal[record.UserId] = (record.TenantId, record.Oid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist identity for the Entra principal");
        }
        finally { sem.Release(); }

        SetIdentityCookie(ctx, record.TenantId, record.Oid);
    }

    /// <summary>Writes (or rewrites) the encrypted <c>finops_id</c> cookie for the
    /// given principal. Also used on Entra account switch to repoint the cookie at the
    /// NEW account when no fresh refresh token came back (the SaveIdentityAsync
    /// path didn't run) — otherwise the stale cookie would resurrect the previous
    /// account's identity on the next hydration.</summary>
    public void SetIdentityCookie(HttpContext ctx, string tenantId, string oid)
    {
        try
        {
            var pointer = JsonSerializer.Serialize(new IdentityPointer(IdentityCookieVersion, tenantId, oid));
            var cookie = _protector.Protect(pointer);
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

    /// <summary>Returns the persisted identity for the principal encoded in the
    /// caller's <c>finops_id</c> cookie, or null if absent / tampered / the
    /// file is missing.</summary>
    public IdentityRecord? Load(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(IdentityCookieName, out var cookie) || string.IsNullOrEmpty(cookie))
            return null;

        string pointer;
        try { pointer = _protector.Unprotect(cookie); }
        catch
        {
            // Tampered or key-rotated cookie &#8212; clear it so the browser stops sending.
            ctx.Response.Cookies.Delete(IdentityCookieName);
            return null;
        }

        if (TryReadPointer(pointer, out var tenantId, out var oid))
        {
            var record = LoadByPrincipal(tenantId, oid);
            if (record is not null) return record;
            ctx.Response.Cookies.Delete(IdentityCookieName);
            return null;
        }

        // Legacy cookies contained only the OID. Admit the old directory only
        // when its encrypted identity record supplies and matches the tenant;
        // then rotate the browser pointer to the pair-bound v2 format.
        var legacyPath = LegacyIdentityPath(pointer);
        var legacy = LoadRecord(legacyPath);
        if (legacy is not null && HasPrincipal(legacy)
            && string.Equals(legacy.Oid, pointer, StringComparison.OrdinalIgnoreCase))
        {
            var normalized = Normalize(legacy);
            var legacyDirectory = Path.GetDirectoryName(legacyPath)!;
            _principalDirectories[PrincipalKey(normalized.TenantId, normalized.Oid)] = legacyDirectory;
            WriteLegacyOwnerMarker(legacyDirectory, normalized.TenantId, normalized.Oid);
            SetIdentityCookie(ctx, normalized.TenantId, normalized.Oid);
            return normalized;
        }

        ctx.Response.Cookies.Delete(IdentityCookieName);
        return null;
    }

    /// <summary>Clears the identity cookie and removes the on-disk file. Called
    /// from /auth/logout.</summary>
    public void Clear(HttpContext ctx, string? tenantId, string? oid)
    {
        ctx.Response.Cookies.Delete(IdentityCookieName);
        if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(oid))
        {
            var principalKey = PrincipalKey(tenantId, oid);
            try { File.Delete(Path.Combine(GetOwnedUserDirectory(tenantId, oid), "identity.json")); }
            catch { }
            _principalDirectories.TryRemove(principalKey, out _);
            _userIdToPrincipal.TryRemove(DeriveUserId(tenantId, oid), out _);
        }
    }

    /// <summary>Loads an identity by its derived userId, used by background
    /// services that have no HttpContext (e.g. <c>TenantTokenRefresher</c>).
    /// First-call after a process restart falls back to a cheap directory scan
    /// to populate the cache; subsequent calls are O(1).</summary>
    public IdentityRecord? LoadByUserId(long userId)
    {
        if (_userIdToPrincipal.TryGetValue(userId, out var cached))
            return LoadByPrincipal(cached.TenantId, cached.Oid);

        // Cold path after restart: walk users/ until we find a match. Cheap —
        // O(active users) and only on cache misses.
        var root = Path.Combine(_copilotHome, "users");
        if (!Directory.Exists(root)) return null;
        foreach (var path in Directory.EnumerateFiles(root, "identity.json", SearchOption.AllDirectories))
        {
            var record = LoadRecord(path);
            if (record is null || !HasPrincipal(record)) continue;
            var normalized = Normalize(record);
            if (normalized.UserId != userId) continue;
            _principalDirectories[PrincipalKey(normalized.TenantId, normalized.Oid)] =
                Path.GetDirectoryName(path)!;
            return normalized;
        }
        return null;
    }

    /// <summary>Loads an identity by the validated Entra tenant and object pair.
    /// Returns null if the record is missing, undecryptable, or belongs to a
    /// different principal.</summary>
    public IdentityRecord? LoadByPrincipal(string tenantId, string oid)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(oid)) return null;
        var path = Path.Combine(GetOwnedUserDirectory(tenantId, oid), "identity.json");
        var record = LoadRecord(path);
        if (record is null || !Matches(record, tenantId, oid)) return null;
        return Normalize(record);
    }

    /// <summary>Updates only the refresh token + recorded scopes on an existing
    /// identity file. Used by <see cref="SessionTokenStore"/> when a refresh
    /// rotates the token (Entra rotates refresh tokens on use).</summary>
    public Task UpdateRefreshTokenAsync(string tenantId, string oid, string newRefreshToken)
    {
        return UpdateRecordAsync(tenantId, oid, r => { r.RefreshToken = newRefreshToken; });
    }

    /// <summary>Persists the comma-separated list of consented Graph tiers so a
    /// post-restart hydration restores the user's full add-on set, not just the
    /// base ARM scope.</summary>
    public Task UpdateGraphTierAsync(string tenantId, string oid, string? graphTier)
    {
        return UpdateRecordAsync(tenantId, oid, r => { r.GraphTier = graphTier; });
    }

    private async Task UpdateRecordAsync(string tenantId, string oid, Action<IdentityRecord> mutate)
    {
        var path = Path.Combine(GetOwnedUserDirectory(tenantId, oid), "identity.json");
        if (!File.Exists(path)) return;
        var sem = LockFor(PrincipalKey(tenantId, oid));
        await sem.WaitAsync();
        try
        {
            var existing = JsonSerializer.Deserialize<IdentityRecord>(_protector.Unprotect(File.ReadAllText(path)));
            if (existing is null || !Matches(existing, tenantId, oid)) return;
            mutate(existing);
            existing.UserId = DeriveUserId(existing.TenantId, existing.Oid);
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            AtomicWrite(path, _protector.Protect(JsonSerializer.Serialize(existing)));
            _userIdToPrincipal[existing.UserId] = (existing.TenantId, existing.Oid);
        }
        catch (CryptographicException ex)
        {
            _logger.LogInformation(
                "Identity record unreadable (key rotated); skipping update: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update identity record");
        }
        finally { sem.Release(); }
    }

    /// <summary>Returns the sole working directory authorized for this Entra
    /// principal. A legacy OID-only directory is accepted only when its encrypted
    /// identity record attests the same tenant and object pair.</summary>
    public string GetOwnedUserDirectory(string tenantId, string oid)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(oid))
            throw new ArgumentException("A tenant and object identifier are required.");

        var principalKey = PrincipalKey(tenantId, oid);
        return _principalDirectories.GetOrAdd(principalKey, _ =>
        {
            var canonical = Path.Combine(
                _copilotHome, "users", "v2", PrincipalDirectoryName(tenantId, oid));
            var canonicalRecord = LoadRecord(Path.Combine(canonical, "identity.json"));
            if (canonicalRecord is not null)
            {
                if (!Matches(canonicalRecord, tenantId, oid))
                    throw new InvalidOperationException("The principal directory owner does not match.");
                return canonical;
            }

            var legacy = Path.Combine(_copilotHome, "users", oid);
            var legacyRecord = LoadRecord(Path.Combine(legacy, "identity.json"));
            if (legacyRecord is not null && Matches(legacyRecord, tenantId, oid))
            {
                WriteLegacyOwnerMarker(legacy, tenantId, oid);
                return legacy;
            }
            var legacyOwner = LoadLegacyOwnerMarker(legacy);
            if (legacyOwner is not null
                && string.Equals(legacyOwner.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(legacyOwner.Oid, oid, StringComparison.OrdinalIgnoreCase))
                return legacy;

            Directory.CreateDirectory(canonical);
            return canonical;
        });
    }

    private IdentityRecord? LoadRecord(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IdentityRecord>(_protector.Unprotect(encrypted));
        }
        catch (CryptographicException ex)
        {
            _logger.LogInformation(
                "Identity record unreadable, re-auth required: {Reason}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load an identity record");
            return null;
        }
    }

    private void WriteLegacyOwnerMarker(string directory, string tenantId, string oid)
    {
        var expectedLegacyDirectory = Path.Combine(_copilotHome, "users", oid);
        if (!string.Equals(directory, expectedLegacyDirectory, StringComparison.Ordinal)) return;
        try
        {
            var marker = _protector.Protect(JsonSerializer.Serialize(
                new LegacyOwner(tenantId, oid)));
            AtomicWrite(Path.Combine(directory, LegacyOwnerFileName), marker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist a legacy conversation owner marker");
        }
    }

    private LegacyOwner? LoadLegacyOwnerMarker(string directory)
    {
        var path = Path.Combine(directory, LegacyOwnerFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LegacyOwner>(
                _protector.Unprotect(File.ReadAllText(path)));
        }
        catch
        {
            return null;
        }
    }

    private IdentityRecord Normalize(IdentityRecord record)
    {
        record.UserId = DeriveUserId(record.TenantId, record.Oid);
        _userIdToPrincipal[record.UserId] = (record.TenantId, record.Oid);
        return record;
    }

    private static bool HasPrincipal(IdentityRecord record) =>
        !string.IsNullOrWhiteSpace(record.TenantId) && !string.IsNullOrWhiteSpace(record.Oid);

    private static bool Matches(IdentityRecord record, string tenantId, string oid) =>
        HasPrincipal(record)
        && string.Equals(record.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(record.Oid, oid, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadPointer(string value, out string tenantId, out string oid)
    {
        tenantId = "";
        oid = "";
        try
        {
            var pointer = JsonSerializer.Deserialize<IdentityPointer>(value);
            if (pointer is null || pointer.Version != IdentityCookieVersion
                || string.IsNullOrWhiteSpace(pointer.TenantId)
                || string.IsNullOrWhiteSpace(pointer.Oid)) return false;
            tenantId = pointer.TenantId;
            oid = pointer.Oid;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string LegacyIdentityPath(string oid)
    {
        if (!Guid.TryParse(oid, out _) || Path.GetFileName(oid) != oid)
            return Path.Combine(_copilotHome, "invalid-legacy-identity");
        return Path.Combine(_copilotHome, "users", oid, "identity.json");
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

    private sealed record IdentityPointer(string Version, string TenantId, string Oid);
    private sealed record LegacyOwner(string TenantId, string Oid);
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
