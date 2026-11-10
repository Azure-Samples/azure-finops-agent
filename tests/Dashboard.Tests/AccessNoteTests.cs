using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace Dashboard.Tests;

/// <summary>
/// A denied read gave the user no way forward: Cost Management answers a missing role with 401 RBACAccessDenied, which
/// names no role, and Microsoft Graph answers a missing delegated permission or directory role with 403
/// Authorization_RequestDenied (session evaluation of 107 conversations, November 2026: no answer to either named what
/// grants the access). The host adds an _access note; the read stays a failure and shows no consent button.
/// </summary>
public sealed class AccessNoteTests
{
    private const string CostDenied = "HTTP 401 Unauthorized\nCurrent UTC time: 2026-11-03 18:36:54\n"
        + "{\"error\":{\"code\":\"RBACAccessDenied\",\"message\":\"The client does not have authorization to perform action.\"},\"_finops\":{\"cacheStatus\":\"not_available\"}}";

    private const string GraphDenied = "HTTP 403 Forbidden\nCurrent UTC time: 2026-11-01 15:55:54\n"
        + "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges to complete the operation.\"}}";

    [Fact]
    public void CostManagementRoleDenialNamesTheRolesThatReadCost()
    {
        var annotated = AzureQueryTools.WithCostAccessNote(CostDenied);

        Assert.StartsWith("HTTP 401 Unauthorized\nCurrent UTC time: 2026-11-03 18:36:54\n{\"_access\":", annotated);
        Assert.Contains("Cost Management Reader, Reader, Contributor or Owner", annotated);
        Assert.Contains("signing in again or granting consent adds no role", annotated);
        Assert.Contains("https://learn.microsoft.com/azure/cost-management-billing/costs/understand-work-scopes", annotated);
        Assert.Contains("\"code\":\"RBACAccessDenied\"", annotated);
        Assert.False(EvidenceInspector.Inspect(annotated).Success);
        Assert.Empty(HttpHelper.ConsentActions(annotated));
    }

    [Theory]
    [InlineData("HTTP 401 Unauthorized\n{\"error\":{\"code\":\"consent_required\"}}")]
    [InlineData("HTTP 403 Forbidden\n{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"names the action and scope\"}}")]
    [InlineData("HTTP 403 Forbidden\n{\"error\":{\"code\":\"RBACAccessDenied\"}}")]
    [InlineData("HTTP 200 OK\n{\"properties\":{\"rows\":[]}}")]
    public void OtherCostResponsesAreUnchanged(string response) =>
        Assert.Equal(response, AzureQueryTools.WithCostAccessNote(response));

    [Fact]
    public void GraphDenialListsTheGrantedScopesAndTheTiersStillToGrant()
    {
        var token = Token("User.Read Organization.Read.All Reports.Read.All");
        var annotated = AzureQueryTools.WithGraphAccessNote(GraphDenied, token);
        var access = Access(annotated);

        Assert.Equal("Organization.Read.All Reports.Read.All User.Read", access["grantedScopes"]);
        Assert.Equal("User.Read.All, Group.Read.All (Grant cost allocation access)", access["notGranted"]);
        Assert.Contains("Grant license reporting access grants Organization.Read.All and Reports.Read.All", access["consentButtons"]);
        Assert.Contains("Grant cost allocation access grants User.Read.All and Group.Read.All", access["consentButtons"]);
        Assert.Contains("\"code\":\"Authorization_RequestDenied\"", annotated);
        Assert.DoesNotContain(token, annotated);
        Assert.False(EvidenceInspector.Inspect(annotated).Success);
        Assert.Empty(HttpHelper.ConsentActions(annotated));
    }

    [Fact]
    public void GraphDenialWithEveryScopeGrantedPointsToADirectoryRole()
    {
        var access = Access(AzureQueryTools.WithGraphAccessNote(GraphDenied,
            Token("User.Read Organization.Read.All Reports.Read.All User.Read.All Group.Read.All")));

        Assert.StartsWith("none of this app's Microsoft Graph permissions", access["notGranted"]);
    }

    // Microsoft Graph tokens are opaque to clients, so an unreadable one never yields a guess about what is missing.
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public void AnUnreadableTokenOnlyMapsTheButtonsToTheirScopes(string? token)
    {
        var access = Access(AzureQueryTools.WithGraphAccessNote(GraphDenied, token));

        Assert.False(access.ContainsKey("grantedScopes"));
        Assert.False(access.ContainsKey("notGranted"));
        Assert.Contains("Grant cost allocation access grants User.Read.All and Group.Read.All", access["consentButtons"]);
    }

    [Fact]
    public void OtherGraphResponsesAreUnchanged()
    {
        const string ok = "HTTP 200 OK\n{\"value\":[]}";
        const string other = "HTTP 403 Forbidden\n{\"error\":{\"code\":\"Forbidden\"}}";
        Assert.Equal(ok, AzureQueryTools.WithGraphAccessNote(ok, null));
        Assert.Equal(other, AzureQueryTools.WithGraphAccessNote(other, null));
    }

    private static Dictionary<string, string> Access(string response)
    {
        using var document = JsonDocument.Parse(ResponseShaper.SplitPreamble(response).Body);
        return document.RootElement.GetProperty("_access").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!);
    }

    private static string Token(string scopes)
    {
        static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        return Part("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Part($"{{\"scp\":\"{scopes}\",\"tid\":\"synthetic-tenant\"}}") + ".";
    }
}
