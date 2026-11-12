using System.Runtime.InteropServices;
using System.Text;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// Afterburn: keeps the personal Nexus Mods API key out of environment variables.
/// <para>
/// A user environment variable can be read by every program the user runs, and every program Afterburn starts
/// (the game, mod tools) inherits it. Instead the key is stored encrypted for the current Windows user (DPAPI) in
/// %LOCALAPPDATA%\AfterburnData\nexus-api-key.bin. A key still found in NEXUS_API_KEY is imported into that file,
/// and is removed from Afterburn's own environment so programs it starts don't receive it.
/// </para>
/// </summary>
public static class ApiKeyStore
{
    /// <summary>The environment variable older setups used for the key.</summary>
    public const string EnvironmentVariable = "NEXUS_API_KEY";

    // Must match the entropy used by the Afterburn helper scripts that migrate an existing key.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Afterburn.NexusApiKey.v1");

    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _cached;

    /// <summary>Folder that holds the encrypted key (outside the install folder, so updates never remove it).</summary>
    public static string StorageFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfterburnData");

    /// <summary>Full path of the encrypted key file.</summary>
    public static string KeyFilePath => Path.Combine(StorageFolder, "nexus-api-key.bin");

    /// <summary>
    /// Returns the API key, or null if none is configured.
    /// </summary>
    public static string? Read()
    {
        lock (Gate)
        {
            if (_loaded) return _cached;
            _loaded = true;

            var fromEnvironment = Clean(Environment.GetEnvironmentVariable(EnvironmentVariable));
            if (fromEnvironment is null && OperatingSystem.IsWindows())
                fromEnvironment = Clean(Environment.GetEnvironmentVariable(EnvironmentVariable, EnvironmentVariableTarget.User));

            var fromFile = OperatingSystem.IsWindows() ? TryReadFile() : null;

            if (fromEnvironment is not null && OperatingSystem.IsWindows() && !string.Equals(fromEnvironment, fromFile, StringComparison.Ordinal))
                TryWriteFile(fromEnvironment);

            _cached = fromEnvironment ?? fromFile;

            // Programs started by Afterburn inherit its environment; make sure they never see the key.
            try { Environment.SetEnvironmentVariable(EnvironmentVariable, null); } catch { /* best effort */ }

            return _cached;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Checks a personal API key with Nexus Mods and, if it is accepted, saves it (encrypted for this Windows user).
    /// Returns whether it worked and a message for the user. The key only takes effect the next time Afterburn starts.
    /// </summary>
    public static async Task<(bool Success, string Message)> ValidateAndSaveAsync(string? candidate, CancellationToken cancellationToken = default)
    {
        var key = Clean(candidate);
        if (key is null || key.Length < 20 || key.Length > 400 || !key.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=' or '+' or '/' or '.'))
            return (false, "Copy your Personal API Key from nexusmods.com/users/myaccount?tab=api first, then click this button again.");

        if (!OperatingSystem.IsWindows())
            return (false, "Saving an API key is only supported on Windows.");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Get, ClientConfig.LegacyApiEndpoint + "/users/validate.json");
            request.Headers.Add("apikey", key);
            request.Headers.Add(BaseHttpMessageFactory.HeaderApplicationName, NexusMods.Sdk.ApplicationConstants.UserAgent.ApplicationName);
            request.Headers.Add(BaseHttpMessageFactory.HeaderApplicationVersion, NexusMods.Sdk.ApplicationConstants.UserAgent.ApplicationVersion);
            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return (false, "Nexus Mods didn't accept that key. Copy it again from your API page and try again.");
            if (!response.IsSuccessStatusCode)
                return (false, $"Nexus Mods couldn't check the key right now (error {(int)response.StatusCode}). Try again in a few minutes.");
        }
        catch (Exception)
        {
            return (false, "Couldn't reach Nexus Mods to check the key. Check your internet connection and try again.");
        }

        if (!TryWriteFile(key))
            return (false, "The key is valid, but Afterburn couldn't save it. Check that your Windows user can write to %LOCALAPPDATA%\\AfterburnData.");

        lock (Gate)
        {
            _loaded = true;
            _cached = key;
        }

        return (true, "API key saved. Close Afterburn completely (check the tray icon too) and open it again to finish logging in.");
    }

    private static string? TryReadFile()
    {
        try
        {
            if (!File.Exists(KeyFilePath)) return null;
            var plain = Unprotect(File.ReadAllBytes(KeyFilePath));
            return plain is null ? null : Clean(Encoding.UTF8.GetString(plain));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryWriteFile(string key)
    {
        try
        {
            var encrypted = Protect(Encoding.UTF8.GetBytes(key));
            if (encrypted is null) return false;
            Directory.CreateDirectory(StorageFolder);
            var tmp = KeyFilePath + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, KeyFilePath, overwrite: true);
            return true;
        }
        catch
        {
            // Not fatal: the key is still used for this session.
            return false;
        }
    }

    // ------------------------------------------------------------------ DPAPI (current user)

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[]? Protect(byte[] data) => Transform(data, protect: true);

    private static byte[]? Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[]? Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var input = new DataBlob();
        var entropy = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.Size = data.Length;
            input.Data = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            Marshal.Copy(data, 0, input.Data, data.Length);

            entropy.Size = Entropy.Length;
            entropy.Data = Marshal.AllocHGlobal(Entropy.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);

            var ok = protect
                ? CryptProtectData(ref input, "Afterburn Nexus Mods API key", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok || output.Data == IntPtr.Zero) return null;

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                // Don't leave the plain key lying around in freed memory.
                Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size);
                Marshal.FreeHGlobal(input.Data);
            }
            if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
            {
                // Don't leave the decrypted key lying around in memory Windows allocated for us.
                Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }
}
