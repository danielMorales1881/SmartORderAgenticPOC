namespace SmartOrders.Infrastructure.Configuration;

/// <summary>
/// Azure Front Door OAuth2 settings for acquiring Bearer tokens.
/// Copied from FileComparer-Semantic/FrontDoorSettings.cs — same pattern as Note+_Quipee_solution.
/// Uses MSAL Client Credentials flow to obtain tokens from Azure AD.
/// </summary>
public class FrontDoorSettings
{
    public string Authority { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "";
    public string ScopeDetail { get; set; } = "";
}
