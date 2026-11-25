using AzureFinOps.Dashboard.Auth;

namespace Dashboard.Tests;

public sealed class MicrosoftOAuthOptionsTests
{
    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData("common")]
    [InlineData("ORGANIZATIONS")]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("login.contoso.com")]
    public void ValidTenantIdentifiersAreAccepted(string tenant) =>
        Assert.True(MicrosoftOAuthOptions.IsValidTenantId(tenant));

    [Theory]
    [InlineData("")]
    [InlineData(" contoso.onmicrosoft.com")]
    [InlineData("contoso.onmicrosoft.com ")]
    [InlineData("contoso")]
    [InlineData("-contoso.onmicrosoft.com")]
    [InlineData("contoso-.onmicrosoft.com")]
    [InlineData("contoso..onmicrosoft.com")]
    [InlineData("https://contoso.onmicrosoft.com")]
    [InlineData("contoso.onmicrosoft.com/path")]
    [InlineData("contoso.onmicrosoft.com:443")]
    [InlineData("127.0.0.1")]
    [InlineData("1.2.3")]
    [InlineData("contoso.123")]
    [InlineData("contoso.onmicrosoft.com?tenant=x")]
    [InlineData("cöntoso.onmicrosoft.com")]
    public void UnsafeTenantIdentifiersAreRejected(string tenant) =>
        Assert.False(MicrosoftOAuthOptions.IsValidTenantId(tenant));

    [Fact]
    public void ConnectedTenantWinsWhenNoTenantWasSelected()
    {
        var tenant = MicrosoftOAuthOptions.ResolveTenantId(
            selectedTenantId: null,
            connectedTenantId: "22222222-2222-2222-2222-222222222222",
            configuredTenantId: "common");

        Assert.Equal("22222222-2222-2222-2222-222222222222", tenant);
    }

    [Fact]
    public void ExplicitTenantSelectionWinsForAnInitialOrSwitchSignIn()
    {
        var tenant = MicrosoftOAuthOptions.ResolveTenantId(
            selectedTenantId: "Contoso.OnMicrosoft.com",
            connectedTenantId: "22222222-2222-2222-2222-222222222222",
            configuredTenantId: "common");

        Assert.Equal("contoso.onmicrosoft.com", tenant);
    }
}
