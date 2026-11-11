namespace NexusMods.App.UI;

/// <summary>
/// Afterburn: links written by mod and collection authors (descriptions, manual-download pages) may only open web
/// pages, never other registered protocol handlers such as steam:// or store launchers.
/// </summary>
public static class UntrustedLinks
{
    /// <summary>True for absolute http(s) links.</summary>
    public static bool IsWebLink(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>True for http(s) links and nxm:// download links from Nexus Mods.</summary>
    public static bool IsWebOrNxmLink(Uri uri) =>
        IsWebLink(uri) || (uri.IsAbsoluteUri && string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase));
}
