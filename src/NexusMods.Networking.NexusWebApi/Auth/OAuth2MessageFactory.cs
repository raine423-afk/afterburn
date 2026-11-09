using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.DTOs.OAuth;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// OAuth2 based authentication
/// </summary>
public class OAuth2MessageFactory : BaseHttpMessageFactory, IAuthenticatingMessageFactory
{
    private readonly ILogger<OAuth2MessageFactory> _logger;
    private readonly OAuth _auth;

    /// <summary>
    /// Constructor.
    /// </summary>
    public OAuth2MessageFactory(
        IConnection conn,
        OAuth auth,
        ILogger<OAuth2MessageFactory> logger)
    {
        _conn = conn;
        _auth = auth;
        _logger = logger;
    }

    private readonly IConnection _conn;

    /// <summary>
    /// Afterburn: saved OAuth logins in the shared app data belong to the official Nexus Mods app's client ID.
    /// Afterburn must not use them. Until Afterburn has its own client ID (see AfterburnBrand.NexusOAuthClientId),
    /// every stored OAuth token is ignored; log in with a personal API key (NEXUS_API_KEY) instead.
    /// TODO: once Afterburn is registered, also check that a stored token was issued to Afterburn's client.
    /// </summary>
    private static bool CanUseStoredTokens => !string.IsNullOrEmpty(NexusMods.Sdk.AfterburnBrand.NexusOAuthClientId);

    /// <inheritdoc/>
    public override AuthenticationHeaderValue? GetAuthenticationHeaderValue()
    {
        var token = GetToken();
        if (token is null) return null;

        return new AuthenticationHeaderValue("Bearer", token);
    }

    private string? GetToken()
    {
        if (!CanUseStoredTokens) return null;
        if (!JWTToken.TryFind(_conn.Db, out var token)) return null;
        if (!token.HasExpired) return token.AccessToken;

        return null;
    }

    private async ValueTask<string?> GetOrRefreshToken(CancellationToken cancellationToken)
    {
        if (!CanUseStoredTokens) return null;
        if (!JWTToken.TryFind(_conn.Db, out var token)) return null;
        if (!token.HasExpired) return token.AccessToken;

        _logger.LogDebug("Refreshing expired OAuth token");

        var newToken = await _auth.RefreshToken(token.RefreshToken, cancellationToken);
        var db = _conn.Db;
        using var tx = _conn.BeginTransaction();

        var newTokenEntity = JWTToken.Create(db, tx, newToken!);
        if (!newTokenEntity.HasValue)
        {
            _logger.LogError("Invalid new token in OAuth2MessageFactory");
            return null;
        }

        var result = await tx.Commit();

        token = JWTToken.Load(result.Db, result[newTokenEntity.Value]);
        return token.AccessToken;
    }

    /// <inheritdoc/>
    public override async ValueTask<HttpRequestMessage> Create(HttpMethod method, Uri uri)
    {
        var token = await GetOrRefreshToken(CancellationToken.None);
        if (token is null) throw new Exception("Unauthorized!");

        var requestMessage = await base.Create(method, uri);
        requestMessage.Headers.Add("Authorization", $"Bearer {token}");
        return requestMessage;
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> IsAuthenticated()
    {
        var token = await GetOrRefreshToken(CancellationToken.None);
        return token is not null;
    }

    /// <inheritdoc/>
    public async ValueTask<UserInfo?> Verify(INexusApiClient nexusApiNexusApiClient, CancellationToken cancel)
    {
        OAuthUserInfo oAuthUserInfo;
        try
        {
            var res = await nexusApiNexusApiClient.GetOAuthUserInfo(cancellationToken: cancel);
            oAuthUserInfo = res.Data;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while fetching OAuth user info");
            return null;
        }

        return new UserInfo
        {
            UserId = UserId.From(ulong.Parse(oAuthUserInfo.Sub)),
            Name = oAuthUserInfo.Name,
            UserRole =  oAuthUserInfo.MembershipRoles.Contains(MembershipRole.Premium) ? UserRole.Premium : oAuthUserInfo.MembershipRoles.Contains(MembershipRole.Supporter) ? UserRole.Supporter : UserRole.Free,
            AvatarUrl = oAuthUserInfo.Avatar,
        };
    }
}
