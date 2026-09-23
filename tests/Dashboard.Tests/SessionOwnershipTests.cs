using System.Text.Json;
using AzureFinOps.Dashboard.Auth;
using Microsoft.AspNetCore.DataProtection;
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}