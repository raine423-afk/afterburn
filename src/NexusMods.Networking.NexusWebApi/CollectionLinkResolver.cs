using System.Text.RegularExpressions;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Afterburn: turns any collection link a user might copy (the normal nexusmods.com web address, with or without
/// a revision, or an nxm:// link) into the nxm:// link the "Add to mod manager" button would have produced.
/// Only nexusmods.com links are accepted. When the link has no revision, the latest published revision is used.
/// </summary>
public static class CollectionLinkResolver
{
    // games/{domain}/collections/{slug}[/revisions/{n}]  or  {domain}/collections/{slug}[/revisions/{n}]
    private static readonly Regex PathRegex = new(
        @"^/(?:games/)?(?<domain>[a-z0-9]+)/collections/(?<slug>[a-z0-9]+)(?:/revisions/(?<rev>\d+))?(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public sealed record ParsedLink(GameDomain Domain, CollectionSlug Slug, RevisionNumber? Revision);

    /// <summary>Recognises a collection link without contacting Nexus Mods.</summary>
    public static bool TryParse(string? text, out ParsedLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Trim('"', '\'', '<', '>');
        if (text.Length > 2048) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        try
        {
            if (uri.Scheme == "nxm")
            {
                if (NXMUrl.Parse(uri) is not NXMCollectionUrl nxm) return false;
                link = new ParsedLink(GameDomain.From(nxm.Game), nxm.Slug, nxm.Revision);
                return true;
            }

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
            var host = uri.Host.ToLowerInvariant();
            if (host != "nexusmods.com" && !host.EndsWith(".nexusmods.com", StringComparison.Ordinal)) return false;

            var match = PathRegex.Match(uri.AbsolutePath);
            if (!match.Success) return false;

            RevisionNumber? revision = match.Groups["rev"].Success && ulong.TryParse(match.Groups["rev"].Value, out var n) && n > 0
                ? RevisionNumber.From(n)
                : null;

            link = new ParsedLink(
                GameDomain.From(match.Groups["domain"].Value.ToLowerInvariant()),
                CollectionSlug.From(match.Groups["slug"].Value),
                revision);
            return true;
        }
        catch (Exception)
        {
            link = null;
            return false;
        }
    }

    /// <summary>
    /// Builds the nxm:// link for <paramref name="link"/>, looking up the latest published revision when needed.
    /// Returns null if the collection can't be found or has no published revision.
    /// </summary>
    public static async ValueTask<string?> ToNxmUrlAsync(ParsedLink link, IGraphQlClient graphQlClient, CancellationToken cancellationToken)
    {
        var revision = link.Revision;
        if (revision is null)
        {
            var result = await graphQlClient.QueryCollectionRevisionNumbers(link.Slug, link.Domain, cancellationToken);
            if (!result.TryGetData(out var revisions)) return null;

            RevisionNumber? latest = null;
            foreach (var (number, status) in revisions)
            {
                if (status != RevisionStatus.Published) continue;
                if (latest is null || number.Value > latest.Value.Value) latest = number;
            }

            revision = latest;
        }

        return revision is null ? null : $"nxm://{link.Domain.Value}/collections/{link.Slug.Value}/revisions/{revision.Value.Value}";
    }
}
