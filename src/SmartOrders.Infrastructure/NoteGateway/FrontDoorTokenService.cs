using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using SmartOrders.Infrastructure.Configuration;

namespace SmartOrders.Infrastructure.NoteGateway;

/// <summary>
/// Acquires and caches Azure Front Door Bearer tokens using MSAL Client Credentials flow.
/// Copied exactly from FileComparer-Semantic/FrontDoorTokenService.cs.
/// Same pattern as AzureFrontDoorTokenManager in Note+_Quipee_solution.
/// </summary>
public class FrontDoorTokenService
{
    private readonly FrontDoorSettings _settings;
    private readonly ILogger<FrontDoorTokenService> _logger;
    private readonly IConfidentialClientApplication _msalClient;
    private readonly string[] _scopes;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public FrontDoorTokenService(
        IOptions<FrontDoorSettings> settings,
        ILogger<FrontDoorTokenService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        var authority = $"{_settings.Authority.TrimEnd('/')}/{_settings.TenantId}";
        _msalClient = ConfidentialClientApplicationBuilder
            .Create(_settings.ClientId)
            .WithClientSecret(_settings.ClientSecret)
            .WithAuthority(authority)
            .Build();

        _scopes = [$"{_settings.Scope}{_settings.ScopeDetail}"];
    }

    /// <summary>
    /// Returns a valid Bearer token, refreshing if expired.
    /// Token is cached and refreshed 1 minute before expiry (same as Quippe).
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
            return _cachedToken;

        try
        {
            var result = await _msalClient
                .AcquireTokenForClient(_scopes)
                .ExecuteAsync(ct);

            _cachedToken = result.AccessToken;
            // Refresh 1 minute before actual expiry (same as Quippe)
            _tokenExpiry = result.ExpiresOn.AddMinutes(-1);

            _logger.LogDebug("Front Door token acquired, expires at {Expiry}", result.ExpiresOn);
            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire Front Door token");
            throw;
        }
    }
}
