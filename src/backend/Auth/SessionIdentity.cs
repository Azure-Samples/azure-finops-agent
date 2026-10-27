using System.Text.Json;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Reads the caller's identity from the ASP.NET session: the app user (<c>user</c>,
/// anonymous or Entra-derived) and, once connected, the validated Entra principal
/// (<c>azure_user</c>). Every endpoint resolves ownership through this one parser, so
/// all of them use the same tenant and object id pair.
/// </summary>
internal static class SessionIdentity
{
    /// <summary>
    /// Resolves the session user. Returns false when the session has no user record or
    /// it cannot be read. The Entra ids are null unless the user is connected to Azure.
    /// </summary>
    public static bool TryResolve(
        HttpContext ctx, out long userId, out string login,
        out string? entraTenantId, out string? entraOid)
    {
        userId = 0;
        login = "";
        (entraTenantId, entraOid) = (null, null);

        var userJson = ctx.Session.GetString("user");
        if (userJson is null) return false;
        try
        {
            var user = JsonSerializer.Deserialize<JsonElement>(userJson);
            userId = user.GetProperty("id").GetInt64();
            login = user.TryGetProperty("login", out var loginProp)
                ? loginProp.GetString() ?? userId.ToString()
                : userId.ToString();
        }
        catch
        {
            userId = 0;
            return false;
        }

        (entraTenantId, entraOid) = EntraPrincipal(ctx.Session);
        return true;
    }

    /// <summary>
    /// The connected Entra tenant and object id, or nulls when the session is not
    /// connected or its record cannot be read.
    /// </summary>
    public static (string? TenantId, string? Oid) EntraPrincipal(ISession session)
    {
        var json = session.GetString("azure_user");
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            var user = JsonSerializer.Deserialize<JsonElement>(json);
            return (
                user.TryGetProperty("tenantId", out var tenant) ? tenant.GetString() : null,
                user.TryGetProperty("objectId", out var oid) ? oid.GetString() : null);
        }
        catch
        {
            return (null, null);
        }
    }
}
