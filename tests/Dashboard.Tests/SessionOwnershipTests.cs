using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Tests;

public sealed class SessionOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"finops-session-ownership-{Guid.NewGuid():N}");

    [Fact]
    public void SameObjectIdInDifferentTenantsHasDifferentOwnerKeys()
    {
        const string oid = "11111111-1111-1111-1111-111111111111";
        const string tenantA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string tenantB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        Assert.NotEqual(
            PersistentIdentity.DeriveUserId(tenantA, oid),
            PersistentIdentity.DeriveUserId(tenantB, oid));
        Assert.NotEqual(
            PersistentIdentity.PrincipalDirectoryName(tenantA, oid),
            PersistentIdentity.PrincipalDirectoryName(tenantB, oid));
    }

    [Fact]
    public void PrincipalIdentifiersMustBeGuids()
    {
        const string tenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        Assert.Throws<ArgumentException>(() =>
            PersistentIdentity.DeriveUserId(tenantId, ".."));
        Assert.Throws<ArgumentException>(() =>
            PersistentIdentity.PrincipalDirectoryName("common", Guid.NewGuid().ToString()));
    }

    [Fact]
    public void LegacyOidDirectoryIsAdmittedOnlyForAttestedTenant()
    {
        const string oid = "11111111-1111-1111-1111-111111111111";
        const string tenantA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string tenantB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var provider = DataProtectionProvider.Create(Path.Combine(_root, "keys"));
        var legacyDirectory = Path.Combine(_root, "users", oid);
        Directory.CreateDirectory(legacyDirectory);
        var record = new IdentityRecord
        {
            TenantId = tenantA,
            Oid = oid,
            UserId = 1,
            RefreshToken = "synthetic-refresh-token",
        };
        var encrypted = provider.CreateProtector("FinOps.Identity.v1")
            .Protect(JsonSerializer.Serialize(record));
        File.WriteAllText(Path.Combine(legacyDirectory, "identity.json"), encrypted);

        var identities = new PersistentIdentity(
            provider, NullLogger<PersistentIdentity>.Instance, _root);

        Assert.Equal(legacyDirectory, identities.GetOwnedUserDirectory(tenantA, oid));
        Assert.NotNull(identities.LoadByPrincipal(tenantA, oid));

        var tenantBDirectory = identities.GetOwnedUserDirectory(tenantB, oid);
        Assert.NotEqual(legacyDirectory, tenantBDirectory);
        Assert.StartsWith(Path.Combine(_root, "users", "v2"), tenantBDirectory);
        Assert.Null(identities.LoadByPrincipal(tenantB, oid));

        File.Delete(Path.Combine(legacyDirectory, "identity.json"));
        var afterLogout = new PersistentIdentity(
            provider, NullLogger<PersistentIdentity>.Instance, _root);
        Assert.Equal(legacyDirectory, afterLogout.GetOwnedUserDirectory(tenantA, oid));
        Assert.NotEqual(legacyDirectory, afterLogout.GetOwnedUserDirectory(tenantB, oid));
        Assert.Null(afterLogout.LoadByPrincipal(tenantA, oid));
    }

    [Fact]
    public async Task RevocationMarkerBlocksIdentityWhenEncryptedFileCannotBeDeleted()
    {
        const string tenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string oid = "11111111-1111-1111-1111-111111111111";
        var provider = new EphemeralDataProtectionProvider();
        var identity = new PersistentIdentity(
            provider, NullLogger<PersistentIdentity>.Instance, _root);
        var context = new DefaultHttpContext();
        var record = new IdentityRecord
        {
            TenantId = tenantId,
            Oid = oid,
            RefreshToken = "synthetic-refresh-token",
        };
        await identity.SaveIdentityAsync(context, record);
        Assert.NotNull(identity.LoadByPrincipal(tenantId, oid));
        var copiedCookie = context.Response.Headers.SetCookie.ToString().Split(';', 2)[0];

        var identityPath = Path.Combine(identity.GetOwnedUserDirectory(tenantId, oid), "identity.json");
        File.Delete(identityPath);
        Directory.CreateDirectory(identityPath);

        Assert.Throws<UnauthorizedAccessException>(() => identity.Clear(context, tenantId, oid));

        var afterRevoke = new PersistentIdentity(
            provider, NullLogger<PersistentIdentity>.Instance, _root);
        Assert.Null(afterRevoke.LoadByPrincipal(tenantId, oid));
        Assert.Null(afterRevoke.LoadByUserId(record.UserId));
        var copiedCookieContext = new DefaultHttpContext();
        copiedCookieContext.Request.Headers.Cookie = copiedCookie;
        Assert.Null(afterRevoke.Load(copiedCookieContext));
    }

    [Fact]
    public async Task FreshIdentityPersistenceRemovesTheRevocationMarker()
    {
        const string tenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string oid = "11111111-1111-1111-1111-111111111111";
        var provider = new EphemeralDataProtectionProvider();
        var identity = new PersistentIdentity(
            provider, NullLogger<PersistentIdentity>.Instance, _root);
        var context = new DefaultHttpContext();

        await identity.SaveIdentityAsync(context, new IdentityRecord
        {
            TenantId = tenantId,
            Oid = oid,
            RefreshToken = "old-refresh-token",
        });
        identity.Clear(context, tenantId, oid);

        await identity.SaveIdentityAsync(context, new IdentityRecord
        {
            TenantId = tenantId,
            Oid = oid,
            RefreshToken = "new-refresh-token",
        });

        var afterLogin = new PersistentIdentity(
            provider, NullLogger<PersistentIdentity>.Instance, _root);
        Assert.Equal("new-refresh-token", afterLogin.LoadByPrincipal(tenantId, oid)?.RefreshToken);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}