using DynamicData.Kernel;
using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.NexusLinks;

/// Builds links to Nexus Mods pages. Anything that exposes a URL to the user that points to Nexus Mods
/// should use this class. The links carry no tracking parameters.
[PublicAPI]
public static class NexusModsUrlBuilder
{
    private const string BaseUrl = "https://www.nexusmods.com";
    private const string UsersBaseUrl = "https://users.nexusmods.com";
    // Afterburn: links are plain URLs. No source/campaign tracking parameters are added, so opening a link tells
    // the site nothing about the app. The source/campaign arguments below are kept only so existing callers compile.
    private const string ParameterValueSource = "";

    /// <summary>
    /// Campaign value for everything related to updating mods in the app.
    /// </summary>
    public const string CampaignUpdates = "updates";

    /// <summary>
    /// Campaign value for everything related to using collections in the app.
    /// </summary>
    public const string CampaignCollections = "collections";

    /// <summary>
    /// Campaign value for everything related to Nexus Mods Premium.
    /// </summary>
    public const string CampaignPremium = "premium";

    /// <summary>
    /// Campaign values for everything related to diagnostics.
    /// </summary>
    public const string CampaignDiagnostics = "diagnostics";

    /// <summary>
    /// Creates a URI from the given <paramref name="baseUrl"/>. Afterburn never adds tracking parameters.
    /// </summary>
    public static Uri CreateUri(string baseUrl, string? source = null, string? campaign = null, string? medium = null)
    {
        return new Uri(baseUrl);
    }

    /// <summary>
    /// Uri for the user settings page.
    /// </summary>
    /// <example>
    /// <c>https://users.nexusmods.com</c>
    /// </example>
    public static readonly Uri UserSettingsUri = CreateUri(UsersBaseUrl);

    /// <summary>
    /// Returns a URI for a user profile.
    /// </summary>
    public static Uri GetProfileUri(UserId userId, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/users/6672467
        var url = $"{BaseUrl}/users/{userId}";
        return CreateUri(url, source: source, campaign: campaign);
    }

    /// <summary>
    /// Returns a URI for a game page.
    /// </summary>
    public static Uri GetGameUri(GameDomain gameDomain, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/games/stardewvalley
        var url = $"{BaseUrl}/games/{gameDomain}";
        return CreateUri(url, source: source, campaign: campaign);
    }

    /// <summary>
    /// Returns a URI for a mod page.
    /// </summary>
    public static Uri GetModUri(GameDomain gameDomain, ModId modId, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/stardewvalley/mods/2400
        var url = $"{BaseUrl}/{gameDomain}/mods/{modId}";
        return CreateUri(url, source: source, campaign: campaign);
    }

    /// <summary>
    /// Returns a URI for a file download page.
    /// </summary>
    /// <remarks>
    /// <paramref name="useNxmLink"/> changes how the download button on the page behaves. If set to
    /// <c>true</c>, the download button will open an NXM link, if set to <c>false</c> the download
    /// will happen in the browser.
    /// </remarks>
    public static Uri GetFileDownloadUri(GameDomain gameDomain, ModId modId, FileId fileId, bool useNxmLink, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=128328&nmm=0
        // https://www.nexusmods.com/stardewvalley/mods/2400?tab=files&file_id=128328&nmm=1
        var url = $"{BaseUrl}/{gameDomain}/mods/{modId}?tab=files&file_id={fileId}&nmm={Convert.ToInt32(useNxmLink)}";
        return CreateUri(url, source: source, campaign: campaign);
    }

    /// <summary>
    /// Returns a URI for a game's browse collections page.
    /// </summary>
    public static Uri GetBrowseCollectionsUri(GameDomain gameDomain, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections
        // https://www.nexusmods.com/games/cyberpunk2077/collections
        var url = $"{BaseUrl}/games/{gameDomain}/collections";
        return CreateUri(url, source: source, campaign: campaign);
    }
    
    /// <summary>
    /// Returns a URI for a collection page.
    /// </summary>
    public static Uri GetCollectionUri(GameDomain gameDomain, CollectionSlug collectionSlug, Optional<RevisionNumber> revisionNumber, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80
        var url = $"{BaseUrl}/games/{gameDomain}/collections/{collectionSlug}{(revisionNumber.HasValue ? $"/revisions/{revisionNumber.Value}" : string.Empty)}";
        return CreateUri(url, source: source, campaign: campaign);
    }

    /// <summary>
    /// Returns a URI for the bugs page of a collection.
    /// </summary>
    public static Uri GetCollectionBugsUri(GameDomain gameDomain, CollectionSlug collectionSlug, Optional<RevisionNumber> revisionNumber, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80/bugs
        var url = $"{BaseUrl}/games/{gameDomain}/collections/{collectionSlug}{(revisionNumber.HasValue ? $"/revisions/{revisionNumber.Value}" : string.Empty)}/bugs";
        return CreateUri(url, source: source, campaign: campaign);
    }
    
    /// <summary>
    /// Returns a URI for the changelog page of a collection.
    /// </summary>
    public static Uri GetCollectionChangelogUri(GameDomain gameDomain, CollectionSlug collectionSlug, Optional<RevisionNumber> revisionNumber, string? source = ParameterValueSource, string? campaign = null)
    {
        // https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80/changelog
        var url = $"{BaseUrl}/games/{gameDomain}/collections/{collectionSlug}{(revisionNumber.HasValue ? $"/revisions/{revisionNumber.Value}" : string.Empty)}/changelog";
        return CreateUri(url, source: source, campaign: campaign);
    }

    /// <summary>
    /// Uri for the premium benefits page.
    /// </summary>
    /// <example>
    /// <c>https://www.nexusmods.com/premium</c>
    /// </example>
    public static readonly Uri LearnAboutPremiumUri = CreateUri($"{BaseUrl}/premium", campaign: CampaignPremium);

    /// <summary>
    /// Uri for the upgrade to premium page.
    /// </summary>
    /// <example>
    /// <c></c>
    /// </example>
    public static readonly Uri UpgradeToPremiumUri = CreateUri($"{UsersBaseUrl}/account/billing/premium", campaign: CampaignPremium);
}
