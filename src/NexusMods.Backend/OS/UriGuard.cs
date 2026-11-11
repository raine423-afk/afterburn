namespace NexusMods.Backend.OS;

/// <summary>
/// Afterburn: links can come from untrusted places (mod and collection descriptions, collection manifests).
/// Handing an arbitrary URI to the operating system would let a mod author launch any registered protocol handler
/// (for example <c>ms-msdt:</c>, <c>search-ms:</c> or a <c>file:</c> share), so only a short list of schemes is allowed.
/// </summary>
internal static class UriGuard
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https",
        // internal: sign-in callback and launching games through their stores
        "nxm", "steam", "heroic", "com.epicgames.launcher",
    };

    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri && AllowedSchemes.Contains(uri.Scheme);
}
