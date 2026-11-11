using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Settings;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using R3;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public class Cyberpunk2077Synchronizer : ALoadoutSynchronizer
{
    private Cyberpunk2077Settings _settings;
    
    /// <summary>
    /// Redmod deploys combined mods to the redmod cache folder
    /// </summary>
    private static GamePath RedModCacheFolder => new(LocationId.Game, "r6/cache/modded");
    
    /// <summary>
    /// Redmod stages the scripts in the redmod/scripts folder
    /// </summary>
    private static GamePath RedModScriptsFolder => new(LocationId.Game, "tools/redmod/scripts");
    
    /// <summary>
    /// Redmod stages the tweaks in the redmod/tweaks folder
    /// </summary>
    private static GamePath RedModTweaksFolder => new(LocationId.Game, "tools/redmod/tweaks");
    
    private static GamePath ArchivePcContentFolder => new(LocationId.Game, "archive/pc/content");
    
    private static GamePath ArchivePcEp1Folder => new(LocationId.Game, "archive/pc/ep1");
    
    
    private readonly RedModDeployTool _redModTool;
    
    protected internal Cyberpunk2077Synchronizer(IServiceProvider provider) : base(provider)
    {
        var settingsManager = provider.GetRequiredService<ISettingsManager>();

        _settings = settingsManager.Get<Cyberpunk2077Settings>();
        settingsManager.GetChanges<Cyberpunk2077Settings>(prependCurrent: false).Subscribe(value => _settings = value);
        _redModTool = provider.GetServices<ITool>().OfType<RedModDeployTool>().First();
    }

    private static readonly GamePath[] IgnoredBackupFolders =
    [
        ArchivePcContentFolder,
        ArchivePcEp1Folder,
    ];

    public override async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job)
    {
        loadout = await base.Synchronize(loadout, job);
        if (!MissingRedModEmitter.HasRedMods(loadout, out _, out var numRedModDirs)) return loadout;
        if (!MissingRedModEmitter.HasRedModToolInstalled(loadout, out _))
        {
            Logger.LogWarning("RedMod tool isn't installed but the loadout contains `{Count}` red mods", numRedModDirs);
            return loadout;
        }

        // Afterburn: redMod.exe deploy -force rebuilds everything and the second sync that follows it takes seconds on
        // large loadouts. Skip both when nothing REDmod depends on has changed since the last deploy in this session.
        var fingerprint = TryGetRedModFingerprint(loadout);
        if (fingerprint is not null
            && LastRedModDeploy.TryGetValue(loadout.Id, out var lastFingerprint)
            && string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal)
            && HasRedModDeployOutput(loadout))
        {
            Logger.LogInformation("REDmod: nothing changed since the last deploy, skipping redMod deploy");
            return loadout;
        }

        await _redModTool.Execute(loadout, CancellationToken.None);
        var synchronized = await base.Synchronize(loadout, job);

        var after = TryGetRedModFingerprint(synchronized);
        if (after is not null) LastRedModDeploy[loadout.Id] = after;
        else LastRedModDeploy.TryRemove(loadout.Id, out _);
        return synchronized;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<NexusMods.MnemonicDB.Abstractions.EntityId, string> LastRedModDeploy = new();

    /// <summary>
    /// Everything redMod.exe deploy depends on: the REDmod load order, every file in the game's "mods" folder (path, size,
    /// time) and the game executable (so a game update triggers a new deploy). Returns null if it can't be worked out,
    /// in which case REDmod always deploys, exactly as before.
    /// </summary>
    private string? TryGetRedModFingerprint(Loadout.ReadOnly loadout)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(_redModTool.GetLoadOrderText(loadout)).Append('\n');

            var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "")).ToString();
            var exe = new FileInfo(Path.Combine(gameRoot, "bin", "x64", "Cyberpunk2077.exe"));
            sb.Append(exe.Exists ? $"{exe.Length}|{exe.LastWriteTimeUtc.Ticks}" : "no-exe").Append('\n');

            var modsDir = new DirectoryInfo(Path.Combine(gameRoot, "mods"));
            if (modsDir.Exists)
            {
                foreach (var file in modsDir.EnumerateFiles("*", SearchOption.AllDirectories)
                             .Select(f => (Rel: Path.GetRelativePath(modsDir.FullName, f.FullName), f.Length, f.LastWriteTimeUtc.Ticks))
                             .OrderBy(x => x.Rel, StringComparer.Ordinal))
                {
                    sb.Append(file.Rel).Append('|').Append(file.Length).Append('|').Append(file.Ticks).Append('\n');
                }
            }

            return sb.ToString();
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "REDmod: couldn't work out whether anything changed; deploying");
            return null;
        }
    }

    private static bool HasRedModDeployOutput(Loadout.ReadOnly loadout)
    {
        try
        {
            var dir = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModCacheFolder).ToString();
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }


    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup)
            return false;
        
        if (path.LocationId != LocationId.Game)
            return false;
        
        return IgnoredBackupFolders.Any(ignore => path.Path.InFolder(ignore.Path));
    }

}
