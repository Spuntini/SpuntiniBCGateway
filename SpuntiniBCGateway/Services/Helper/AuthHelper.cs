using Microsoft.Identity.Client;

namespace SpuntiniBCGateway.Services;

/// <summary>
/// Simple MSAL-based client credentials helper.
/// Reads configuration keys under `Auth`:
/// - TenantId
/// - ClientId
/// - ClientSecret
/// - Scope (optional, defaults to Business Central scope)
/// </summary>
public class AuthHelper
{
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string? _clientSecret;
    private readonly string[] _scopes;
    private readonly string _mode; // "service" or "user"

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresOn = DateTimeOffset.MinValue;

    public AuthHelper(IConfiguration config)
    {
        _tenantId = config["Auth:TenantId"] ?? string.Empty;
        _clientId = config["Auth:ClientId"] ?? string.Empty;
        _clientSecret = config["Auth:ClientSecret"];
        _mode = (config["Auth:Mode"] ?? "service").ToLowerInvariant();

        var scopesConfig = config["Auth:Scopes"] ?? config["Auth:Scope"];
        if (string.IsNullOrWhiteSpace(scopesConfig))
        {
            // Default delegated scope for Business Central (user_impersonation)
            _scopes = ["https://api.businesscentral.dynamics.com/user_impersonation"];
        }
        else
        {
            _scopes = scopesConfig.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private IConfidentialClientApplication BuildConfidentialClient()
    {
        if (string.IsNullOrWhiteSpace(_tenantId)) throw new InvalidOperationException("Auth:TenantId not configured");
        if (string.IsNullOrWhiteSpace(_clientId)) throw new InvalidOperationException("Auth:ClientId not configured");
        if (string.IsNullOrWhiteSpace(_clientSecret)) throw new InvalidOperationException("Auth:ClientSecret not configured");

        string authority = $"https://login.microsoftonline.com/{_tenantId}";
        return ConfidentialClientApplicationBuilder.Create(_clientId)
            .WithClientSecret(_clientSecret)
            .WithAuthority(authority)
            .Build();
    }

    /// <summary>
    /// Acquire an access token. Supports two modes:
    /// - service (client credentials)
    /// - user (device code flow / delegated)
    /// Token is cached until expiry.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Return cached token if valid for at least 60 seconds
        if (!string.IsNullOrEmpty(_cachedToken) && _expiresOn > DateTimeOffset.UtcNow.AddSeconds(60))
            return _cachedToken;

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedToken) && _expiresOn > DateTimeOffset.UtcNow.AddSeconds(60))
                return _cachedToken!;

            if (_mode == "user")
            {
                if (string.IsNullOrWhiteSpace(_clientId)) throw new InvalidOperationException("Auth:ClientId not configured for user flow");
                if (string.IsNullOrWhiteSpace(_tenantId)) throw new InvalidOperationException("Auth:TenantId not configured for user flow");

                string authority = $"https://login.microsoftonline.com/{_tenantId}";
                var publicApp = PublicClientApplicationBuilder.Create(_clientId)
                    .WithAuthority(authority)
                    .Build();

                var result = await publicApp.AcquireTokenWithDeviceCode(_scopes, async dc =>
                {
                    Console.WriteLine(dc.Message);
                    await Task.CompletedTask;
                }).ExecuteAsync(cancellationToken).ConfigureAwait(false);

                _cachedToken = result.AccessToken;
                _expiresOn = result.ExpiresOn;
                return _cachedToken!;
            }
            else
            {
                var app = BuildConfidentialClient();
                var result = await app.AcquireTokenForClient(_scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false);
                _cachedToken = result.AccessToken;
                _expiresOn = result.ExpiresOn;
                return _cachedToken!;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Create an HttpClient whose default request headers include a Bearer token.
    /// Note: token is fetched once at creation time. For long-running programs consider
    /// using a DelegatingHandler that refreshes tokens transparently.
    /// </summary>
    public async Task<HttpClient> CreateHttpClientAsync(CancellationToken cancellationToken = default)
    {
        string token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
