namespace NexusMods.Sdk;

/// <summary>
/// Branding for Afterburn, a community fork of the Nexus Mods app.
/// Afterburn is not affiliated with or endorsed by Nexus Mods.
/// </summary>
public static class AfterburnBrand
{
    /// <summary>Display name shown to users.</summary>
    public const string Name = "Afterburn";

    /// <summary>Name sent to the Nexus Mods API (Application-Name header and user agent). Keep it the same across versions.</summary>
    public const string ApiApplicationName = "Afterburn";

    /// <summary>
    /// OAuth client ID issued by Nexus Mods once Afterburn is registered with them (support@nexusmods.com).
    /// Empty until registration is approved. Until then, log in with a personal API key by setting the
    /// NEXUS_API_KEY environment variable, which Nexus allows for testing and personal use only.
    /// </summary>
    public const string NexusOAuthClientId = "";

    /// <summary>GitHub owner of the Afterburn repository. Update after creating the repo.</summary>
    public const string GitHubOwner = "raine423-afk";

    /// <summary>GitHub repository name.</summary>
    public const string GitHubRepository = "afterburn";

    /// <summary>Project page.</summary>
    public static readonly Uri ProjectUri = new($"https://github.com/{GitHubOwner}/{GitHubRepository}");

    /// <summary>Where users report bugs and give feedback.</summary>
    public static readonly Uri IssuesUri = new($"https://github.com/{GitHubOwner}/{GitHubRepository}/issues");
}
