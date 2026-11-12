using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Diagnostics.Values;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;

/// <summary>
/// Afterburn: warns when the installed redscript compiler is too old for the game's script file.
/// <para>
/// Game patch 2.5 changed the layout of r6/cache/final.redscripts (header flag 0x2). redscript builds made before
/// 2026-10-22 (release 0.5.32) can't read it and fail with "Failed to load the original script cache", which turns
/// every script mod off. The version label of a download can be wrong, so this looks at the files themselves.
/// </para>
/// </summary>
public class OutdatedRedscriptEmitter : ILoadoutDiagnosticEmitter
{
    private static readonly GamePath CompilerPath = new(LocationId.Game, "engine/tools/scc_lib.dll");
    private static readonly GamePath ScriptCachePath = new(LocationId.Game, "r6/cache/final.redscripts");

    // Build day of redscript 0.5.32, the first release that reads the patch 2.5 script file.
    private static readonly DateTimeOffset FirstSupportedBuild = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    public static readonly NamedLink RedscriptLink = new("redscript on Nexus Mods", new Uri("https://www.nexusmods.com/cyberpunk2077/mods/1511"));

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var compiler = loadout.InstallationInstance.Locations.ToAbsolutePath(CompilerPath);
        var scriptCache = loadout.InstallationInstance.Locations.ToAbsolutePath(ScriptCachePath);
        if (!compiler.FileExists || !scriptCache.FileExists) yield break;

        if (!UsesPatch25Layout(scriptCache.ToString())) yield break;

        var buildDate = ReadBuildDate(compiler.ToString());
        if (buildDate is null || buildDate.Value >= FirstSupportedBuild) yield break;

        yield return Diagnostics.CreateOutdatedRedscript(
            RedscriptLink: RedscriptLink,
            BuildDate: buildDate.Value.ToString("yyyy-MM-dd"),
            CompilerFile: compiler.ToString()
        );
    }

    /// <summary>True if the script file starts with "REDS" and has the patch 2.5 flag (0x2) set.</summary>
    private static bool UsesPatch25Layout(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return false;
            if (header[0] != (byte)'R' || header[1] != (byte)'E' || header[2] != (byte)'D' || header[3] != (byte)'S') return false;
            var flags = BitConverter.ToUInt32(header.Slice(8, 4));
            return (flags & 0x2) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the link date from a Windows program file. Null if it can't be read or isn't a plausible date.</summary>
    private static DateTimeOffset? ReadBuildDate(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[4];

            stream.Position = 0x3C;
            if (stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) < 4) return null;
            var peOffset = BitConverter.ToUInt32(buffer);
            if (peOffset > 0x10000) return null;

            stream.Position = peOffset;
            if (stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) < 4) return null;
            if (buffer[0] != (byte)'P' || buffer[1] != (byte)'E' || buffer[2] != 0 || buffer[3] != 0) return null;

            stream.Position = peOffset + 8;
            if (stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) < 4) return null;
            var date = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToUInt32(buffer));

            // Some builds store a hash here instead of a date; ignore anything implausible.
            if (date.Year < 2020 || date > DateTimeOffset.UtcNow.AddYears(1)) return null;
            return date;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
