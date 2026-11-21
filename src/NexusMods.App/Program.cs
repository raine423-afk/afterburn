using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Logging;
using NexusMods.Abstractions.Serialization;
using NexusMods.Sdk.Settings;
using NexusMods.Abstractions.NexusLinks;
using NexusMods.App.Commandline;
using NexusMods.App.UI;
using NexusMods.App.UI.Settings;
using NexusMods.Backend;
using NexusMods.Backend.Games.Locators;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.DataModel.SchemaVersions;
using NexusMods.Paths;
using NexusMods.ProxyConsole;
using NexusMods.Sdk;
using NexusMods.SingleProcess;
using NexusMods.SingleProcess.Exceptions;
using NLog.Extensions.Logging;
using NLog.Targets;
using ReactiveUI;
using Spectre.Console;

namespace NexusMods.App;

public class Program
{
    private static ILogger<Program> _logger = default!;

    /// <summary>
    /// Afterburn keeps its data in %LOCALAPPDATA%\Afterburn. If an older "NexusMods.App" data folder exists and no Afterburn
    /// folder does yet, it is renamed (instant, same drive) so no library or settings are lost.
    /// </summary>
    // Saved settings may contain the old folder's full path; point them at the new folder.
    private static void FixMovedPaths(string newDir, string oldDir)
    {
        try
        {
            var configs = System.IO.Path.Combine(newDir, "Configs");
            if (!System.IO.Directory.Exists(configs)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(configs, "*.json", System.IO.SearchOption.AllDirectories))
            {
                try
                {
                    var text = System.IO.File.ReadAllText(file);
                    var fixedText = text
                        .Replace(oldDir.Replace("\\", "\\\\"), newDir.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
                        .Replace(oldDir, newDir, StringComparison.OrdinalIgnoreCase)
                        .Replace(oldDir.Replace('\\', '/'), newDir.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
                    if (fixedText != text) System.IO.File.WriteAllText(file, fixedText);
                }
                catch { /* leave this file as it is */ }
            }
        }
        catch { /* not critical */ }
    }

    /// <summary>
    /// Afterburn 1.0.x still read and wrote its settings in %LOCALAPPDATA%\NexusMods.App\Configs. From 1.1 they live in
    /// %LOCALAPPDATA%\Afterburn\Configs. On first start, copy each old settings file across (newest wins), once. The old
    /// folder is left alone so nothing else that uses it breaks.
    /// </summary>
    private static void MigrateLegacyConfigs(string oldDir, string newDir)
    {
        try
        {
            var oldConfigs = System.IO.Path.Combine(oldDir, "Configs");
            var newConfigs = System.IO.Path.Combine(newDir, "Configs");
            var marker = System.IO.Path.Combine(newConfigs, ".migrated-from-nexusmods-app");
            if (!System.IO.Directory.Exists(oldConfigs) || System.IO.File.Exists(marker)) return;

            System.IO.Directory.CreateDirectory(newConfigs);
            foreach (var source in System.IO.Directory.EnumerateFiles(oldConfigs, "*", System.IO.SearchOption.AllDirectories))
            {
                try
                {
                    var target = System.IO.Path.Combine(newConfigs, System.IO.Path.GetRelativePath(oldConfigs, source));
                    var targetInfo = new System.IO.FileInfo(target);
                    if (targetInfo.Exists && targetInfo.LastWriteTimeUtc >= System.IO.File.GetLastWriteTimeUtc(source)) continue;
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    System.IO.File.Copy(source, target, overwrite: true);
                }
                catch { /* skip this file */ }
            }

            FixMovedPaths(newDir, oldDir);
            System.IO.File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch { /* not critical: Afterburn starts with default settings */ }
    }

    private static void MigrateLegacyDataFolder()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldDir = System.IO.Path.Combine(root, "NexusMods.App");
            var newDir = System.IO.Path.Combine(root, "Afterburn");
            try { var lf = System.IO.Path.Combine(newDir, "Configs", "NexusMods.CrossPlatform.LoggingSettings.json"); if (System.IO.File.Exists(lf)) { var lt = System.IO.File.ReadAllText(lf); var ln = lt.Replace("NexusMods.App/Logs", "Afterburn/Logs"); if (ln != lt) System.IO.File.WriteAllText(lf, ln); } } catch { /* not critical */ }
            if (System.IO.Directory.Exists(oldDir) && !System.IO.Directory.Exists(newDir))
            {
                System.IO.Directory.Move(oldDir, newDir);
                FixMovedPaths(newDir, oldDir);
            }

            MigrateLegacyConfigs(oldDir, newDir);
        }
        catch
        {
            // Not critical: Afterburn simply starts with a fresh data folder.
        }
    }

    // Afterburn: held for the whole life of the main (UI) process, so only one can run at a time. The old LOCK-file check
    // below raced when two copies started within a second of each other (e.g. a double launch): both passed it, and the
    // second then crashed opening the database.
    private static Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    private enum SingleInstanceResult { Proceed, Exit, AskRunningToShowWindow }

    /// <summary>
    /// Proceed: this process should go on starting as the main process. Exit: another Afterburn is already running and
    /// its window was brought to the front. AskRunningToShowWindow: another Afterburn is running with its window hidden
    /// (closing after an apply), so ask it, over the local command channel, to show its window.
    /// </summary>
    private static SingleInstanceResult AcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, @"Local\Afterburn.MainProcess");
        var deadline = DateTime.UtcNow.AddSeconds(30);

        // Started by "Restart now": the old copy is closing, so wait for it rather than switching to its window.
        var restarting = Environment.GetEnvironmentVariable("AFTERBURN_RESTARTING") == "1";
        if (restarting)
        {
            try { Environment.SetEnvironmentVariable("AFTERBURN_RESTARTING", null); } catch { /* ignore */ }
            try
            {
                if (_singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(60))) return SingleInstanceResult.Proceed;
            }
            catch (AbandonedMutexException)
            {
                return SingleInstanceResult.Proceed;
            }
        }
        while (true)
        {
            try
            {
                if (_singleInstanceMutex.WaitOne(TimeSpan.Zero)) return SingleInstanceResult.Proceed;
            }
            catch (AbandonedMutexException)
            {
                return SingleInstanceResult.Proceed; // the previous Afterburn died without releasing it; we own it now
            }

            // Another Afterburn holds it. Once its window is up, show that window and step aside.
            if (TryShowOtherAfterburnWindow()) return SingleInstanceResult.Exit;

            // It has been running for a while but has no visible window: it is finishing an apply in the background
            // (its window was closed). Ask it to show the window again. While it is still starting, keep waiting.
            if (OtherAfterburnRunningLongerThan(TimeSpan.FromSeconds(20))) return SingleInstanceResult.AskRunningToShowWindow;

            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(250);
        }

        CrashReporter.ShowMessage("Afterburn is already running, or is still closing. Close the other Afterburn window, wait a few seconds, then start it again.", "Afterburn");
        return SingleInstanceResult.Exit;
    }

    private static bool AnyOtherAfterburnProcess()
    {
        try
        {
            // Give a just-killed process a moment to disappear.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var current = Process.GetCurrentProcess();
                var others = Process.GetProcessesByName(current.ProcessName);
                var any = false;
                foreach (var other in others)
                {
                    using (other)
                    {
                        if (other.Id != current.Id && !other.HasExited) any = true;
                    }
                }
                if (!any) return false;
                Thread.Sleep(250);
            }
            return true;
        }
        catch
        {
            return true; // can't tell; be safe and don't start another
        }
    }

    private static bool OtherAfterburnRunningLongerThan(TimeSpan age)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(current.ProcessName))
            {
                using (other)
                {
                    if (other.Id == current.Id || other.HasExited) continue;
                    if (DateTime.Now - other.StartTime > age) return true;
                }
            }
        }
        catch { /* can't tell */ }
        return false;
    }

    private static bool TryShowOtherAfterburnWindow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName(current.ProcessName))
            {
                using (other)
                {
                    if (other.Id == current.Id || other.HasExited) continue;
                    var handle = other.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    if (IsIconic(handle)) ShowWindow(handle, 9 /* SW_RESTORE */);
                    SetForegroundWindow(handle);
                    return true;
                }
            }
        }
        catch { /* best effort */ }
        return false;
    }

    [STAThread]
    // Afterburn: a second Afterburn started while the first is still open or still closing used to crash on the database
    // lock. Wait for the lock to be released, then show a plain message instead of crashing.
    private static bool WaitForDatabase()
    {
        var lockFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Afterburn", "DataModel", "MnemonicDB.rocksdb", "LOCK");
        if (!System.IO.File.Exists(lockFile)) return true;

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (true)
        {
            try
            {
                using var stream = new System.IO.FileStream(lockFile, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
                return true;
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }

            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(500);
        }

        CrashReporter.ShowMessage("Afterburn is already running, or is still closing. Close the other Afterburn window, wait a few seconds, then start it again.", "Afterburn");
        return false;
    }

    private static readonly Stopwatch StartupClock = Stopwatch.StartNew();

    internal static void LogStartupStep(string step)
    {
        try { _logger?.LogInformation("Startup: {Step} after {Ms} ms", step, StartupClock.ElapsedMilliseconds); } catch { /* ignore */ }
    }

    /// <summary>
    /// Afterburn: while the main thread opens the database and starts services, read the UI's libraries from disk and
    /// set up ReactiveUI on a background thread. On a slow or cold drive this overlaps a lot of waiting that used to
    /// happen one step after another.
    /// </summary>
    private static void StartBackgroundWarmup()
    {
        var thread = new Thread(() =>
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<Assembly>();
                queue.Enqueue(typeof(NexusMods.App.UI.Windows.MainWindow).Assembly);
                queue.Enqueue(typeof(Program).Assembly);
                while (queue.Count > 0)
                {
                    var assembly = queue.Dequeue();
                    foreach (var reference in assembly.GetReferencedAssemblies())
                    {
                        if (reference.Name is null || !seen.Add(reference.Name)) continue;
                        try { queue.Enqueue(Assembly.Load(reference)); }
                        catch { /* optional */ }
                    }
                }

                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RxApp).TypeHandle);
            }
            catch { /* warm-up is optional */ }
        })
        {
            IsBackground = true,
            Name = "Afterburn startup warm-up",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public static int Main(string[] args)
    {
        // This code will not work properly if it comes after anything that uses the console. So we need to do this first.
        if (OperatingSystem.IsWindows())
            ConsoleHelper.EnsureConsole();

        MainThreadData.SetMainThread();

        MigrateLegacyDataFolder();

        LoggingSettings loggingSettings;
        CrashLogSettings crashLogSettings;
        ExperimentalSettings experimentalSettings;
        GameLocatorSettings gameLocatorSettings;
        using (var settingsHost = BuildSettingsHost())
        {
            var settingsManager = settingsHost.Services.GetRequiredService<ISettingsManager>();
            loggingSettings = settingsManager.Get<LoggingSettings>();
            crashLogSettings = settingsManager.Get<CrashLogSettings>();
            experimentalSettings = settingsManager.Get<ExperimentalSettings>();
            gameLocatorSettings = settingsManager.Get<GameLocatorSettings>();
        }

        try
        {
            var crashFs = FileSystem.Shared;
            CrashReporter.Install(
                crashLogSettings.CrashLogFolder.ToPath(crashFs).GetFullPath(),
                loggingSettings.MainProcessLogFilePath.ToPath(crashFs).GetFullPath()
            );
        }
        catch { /* the crash log is optional */ }

        var startupMode = StartupMode.Parse(args);
        if (startupMode.RunAsMain && startupMode.ShowUI)
        {
            var instance = SingleInstanceResult.Proceed;
            try { instance = AcquireSingleInstance(); }
            catch { /* never block startup on this check */ }

            if (instance == SingleInstanceResult.Exit) return 0;
            if (instance == SingleInstanceResult.AskRunningToShowWindow)
            {
                // Continue as a client of the running Afterburn and send it "show-window".
                startupMode = StartupMode.Parse([StatusVerbs.ShowWindowCommand]);
            }
            else
            {
                try { if (!WaitForDatabase()) return 0; }
                catch { /* never block startup on this check */ }
            }
        }
        var exitCode = RunWithHost(startupMode, loggingSettings, experimentalSettings, gameLocatorSettings, args);

        // Afterburn: everything is saved and disposed by now. If a leftover non-background thread would keep the
        // process alive with no window (it showed in Task Manager after closing), end it here.
        try { _logger?.LogInformation("Afterburn closed"); NLog.LogManager.Flush(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static int RunWithHost(StartupMode startupMode, LoggingSettings loggingSettings, ExperimentalSettings experimentalSettings, GameLocatorSettings gameLocatorSettings, string[] args)
    {
        using var host = BuildHost(
            startupMode,
            loggingSettings,
            experimentalSettings,
            gameLocatorSettings
        );

        var services = host.Services;
        _logger = services.GetRequiredService<ILogger<Program>>();
        LogStartupStep("services built");
        if (startupMode.RunAsMain && startupMode.ShowUI) StartBackgroundWarmup();

        // NOTE(erri120): has to come before host startup
        CleanupUnresponsiveProcesses(services).Wait(timeout: TimeSpan.FromSeconds(10));

        // Okay to do wait here, as we are in the main process thread.
        host.StartAsync().Wait(timeout: TimeSpan.FromMinutes(5));
        LogStartupStep("background services started");

        if (startupMode.RunAsMain)
        {
            var dataModelSettings = services.GetRequiredService<ISettingsManager>().Get<DataModelSettings>();
            var fileSystem = services.GetRequiredService<IFileSystem>();
            var osInterop = services.GetRequiredService<IOSInterop>();

            var modelExists = dataModelSettings.MnemonicDBPath.ToPath(fileSystem).DirectoryExists();

            _ = Task.Run(async () =>
            {
                try
                {
                    var archiveLocation = dataModelSettings.ArchiveLocations[0].ToPath(fileSystem);
                    var mount = await osInterop.GetFileSystemMount(archiveLocation);
                    if (mount is not null) _logger.LogInformation("Archives are stored at {Path} on mount {Mount}", archiveLocation, mount);
                    else _logger.LogWarning("Failed to find file system mount for {Path}", archiveLocation);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to get file system mounts");
                }
            });

            // This will startup the MnemonicDb connection
            var migration = services.GetRequiredService<MigrationService>();
            if (modelExists)
            {
                // Run the migrations
                migration.MigrateAll().Wait();
            }
            else
            {
                // Otherwise, perform the initial setup
                migration.InitialSetup().Wait();
            }
            LogStartupStep("database ready");
        }


        // Start the CLI server if we are the main process.
        var cliServer = services.GetService<CliServer>();
        cliServer?.StartCliServerAsync().Wait(timeout: TimeSpan.FromSeconds(5));

        LogMessages.RuntimeInformation(_logger, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, ApplicationConstants.InstallationMethod);
        TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
        {
            LogMessages.UnobservedTaskException(_logger, eventArgs.Exception, sender, sender?.GetType());
            eventArgs.SetObserved();
        };

        RxApp.DefaultExceptionHandler = Observer.Create<Exception>(ex =>
        {
            LogMessages.UnobservedReactiveThrownException(_logger, ex);
        });
        LogStartupStep("ReactiveUI ready");


        try
        {
            if (startupMode.RunAsMain)
            {
                LogMessages.StartingProcess(_logger, Environment.ProcessPath, Environment.ProcessId, args);
                
                if (startupMode.ShowUI)
                {
                    var task = RunCliTaskAsMain(services, startupMode);
                    Startup.Main(services, []);
                    return task.Result;
                }
                else
                {
                    var task = RunCliTaskAsMain(services, startupMode);
                    return task.Result;
                }
            }
            else
            {
                var task = RunCliTaskRemotely(services, startupMode);
                return task.Result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");
            return 1;
        }
        finally
        {
            // Afterburn: closing the window could leave Afterburn running in the background forever: shutdown got
            // stuck disposing a service, or a leftover thread kept the process alive. Give the normal shutdown a fair
            // amount of time to save everything, then make sure the process really ends.
            ArmExitWatchdog(TimeSpan.FromSeconds(25));
            _logger.LogInformation("Shutting down");

            // Wait for 15 seconds for the host to stop, otherwise kill the process
            if (!host.StopAsync().Wait(timeout: TimeSpan.FromSeconds(15)))
            {
                _logger.LogWarning("Background services didn't stop within 15 seconds; ending the process");
                NLog.LogManager.Flush(TimeSpan.FromSeconds(2));
                Environment.Exit(0);
            }
            _logger.LogInformation("Background services stopped");
        }
    }

    private static int _exitWatchdogArmed;

    /// <summary>
    /// Afterburn: ends the process if it is still alive after <paramref name="after"/>, whatever is holding it open.
    /// </summary>
    internal static void ArmExitWatchdog(TimeSpan after)
    {
        if (Interlocked.Exchange(ref _exitWatchdogArmed, 1) == 1) return;
        var thread = new Thread(() =>
        {
            Thread.Sleep(after);
            try
            {
                _logger.LogWarning("Afterburn was still running {Seconds} seconds after it started closing; ending it now", (int)after.TotalSeconds);
                NLog.LogManager.Flush(TimeSpan.FromSeconds(2));
            }
            catch { /* logging is best effort here */ }
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "Afterburn exit watchdog",
        };
        thread.Start();
    }

    private static async Task CleanupUnresponsiveProcesses(IServiceProvider serviceProvider)
    {
        // NOTE(erri120): this is a hack, see https://github.com/Nexus-Mods/NexusMods.App/issues/3633
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var syncFile = serviceProvider.GetRequiredService<SyncFile>();

        var (process, port) = syncFile.GetSyncInfo();
        if (process is null) return;

        var pid = process.Id;
        var canConnect = await CanConnectToProcess(logger, port, timeout: TimeSpan.FromSeconds(6), services: serviceProvider);
        if (canConnect) return;

        logger.LogWarning("Unable to connect to old process with PID `{PID}` on port `{Port}`, force closing process", pid, port);

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Exception killing old process `{PID}`", pid);
        }
    }

    private static async Task<bool> CanConnectToProcess(ILogger logger, int port, TimeSpan timeout, IServiceProvider services)
    {
        var client = services.GetRequiredService<CliClient>();
        var cts = new CancellationTokenSource(delay: timeout);

        try
        {
            // Run the heartbeat command to check if the process is responsive
            var commandTask = client.ExecuteCommand([StatusVerbs.HeartbeatCommand], AnsiConsole.Console);
            
            // Wait for the command to complete or the timeout to expire
            await commandTask.WaitAsync(cts.Token);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Exception connecting to process on `{Port}`", port);
            return false;
        }
    }

    private static async Task<int> RunCliTaskRemotely(IServiceProvider services, StartupMode startupMode)
    {
        var client = services.GetRequiredService<CliClient>();
        var syncFile = services.GetRequiredService<SyncFile>();
        try
        {
            await client.ExecuteCommand(startupMode.Args, AnsiConsole.Console);
            return 0;
        }
        catch (NoMainProcessStarted _) when (startupMode.Args is [StatusVerbs.ShowWindowCommand])
        {
            // Afterburn: the other Afterburn was stuck (no window, not answering) and has just been closed. With no
            // other copy left, start a fresh one, instead of telling the user to try again. It can't loop: this only
            // happens when no other Afterburn process exists any more.
            if (!AnyOtherAfterburnProcess())
            {
                _logger.LogInformation("The stuck Afterburn was closed; starting a new one");
                try
                {
                    var exe = Environment.ProcessPath;
                    if (exe is not null)
                    {
                        var info = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
                        info.Environment["AFTERBURN_RESTARTING"] = "1";
                        Process.Start(info)?.Dispose();
                        return 0;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Couldn't start a new Afterburn");
                }
            }

            // Afterburn: only sent when another Afterburn is running. Never start a new one from here, or two copies
            // that can't reach each other would keep starting each other.
            _logger.LogWarning("Couldn't reach the running Afterburn to show its window");
            CrashReporter.ShowMessage("Afterburn is already running, or is still closing. Close the other Afterburn window, wait a few seconds, then start it again.", "Afterburn");
            return 1;
        }
        catch (NoMainProcessStarted _)
        {
            var interop = services.GetRequiredService<IOSInterop>();
            var ownExe = interop.GetRunningExecutablePath(out var _);
            _logger.LogInformation("No main process started, starting {OwnExe}", ownExe);
            var processInfo = new ProcessStartInfo
            {
                FileName = ownExe.ToString(),
                Arguments = "",
                UseShellExecute = true,
            };
            var process = Process.Start(processInfo);
            if (process is null)
            {
                _logger.LogError("Failed to start main process {OwnExe}", ownExe);
                return 1;
            }
            else
            {
                _logger.LogInformation("Started main process {OwnExe} with id {ProcessId}", ownExe, process.Id);
            }

            var st = Stopwatch.StartNew();
            while (!syncFile.IsMainRunning)
            {
                await Task.Delay(100);
                if (st.Elapsed > TimeSpan.FromSeconds(60))
                {
                    _logger.LogError("Main process {OwnExe} did not start", ownExe);
                    return 1;
                }
            }

            await client.ExecuteCommand(startupMode.Args, AnsiConsole.Console);
            return 0;
        }
    }

    /// <summary>
    /// Runs the CLI task in this process. 
    /// </summary>
    private static Task<int> RunCliTaskAsMain(IServiceProvider provider, StartupMode startupMode)
    {
        if (!startupMode.ExecuteCli)
            return Task.FromResult(0);
        var configurator = provider.GetRequiredService<CommandLineConfigurator>();
        _logger.LogInformation("Starting with Spectre.Cli");
        return configurator.RunAsync(startupMode.Args, new SpectreRenderer(AnsiConsole.Console), CancellationToken.None);
    }

    private static IHost BuildSettingsHost()
    {
        var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddSingleton(FileSystem.Shared)
                .AddSettingsManager()
                .AddSerializationAbstractions()
                .AddStorageBackend<JsonStorageBackend>()
                .AddSettings<LoggingSettings>()
                .AddSettings<ExperimentalSettings>()
                .AddSettings<GameLocatorSettings>()
                .AddSettings<CrashLogSettings>()
            )
            .ConfigureLogging((_, builder) => builder
                .ClearProviders()
                .AddSimpleConsole()
                .SetMinimumLevel(LogLevel.Trace)
            )
            .Build();

        return host;
    }

    /// <summary>
    /// Constructs the host for the application, if <paramref name="slimMode"/> is true, the host will not register
    /// most of the services, and will only register what is required to proxy the app to the main process.
    /// <paramref name="isAvaloniaDesigner"/> should be set to true when constructing the host for the Avalonia Designer
    /// and will use the in memory database 
    /// </summary>
    private static IHost BuildHost(
        StartupMode startupMode,
        LoggingSettings loggingSettings,
        ExperimentalSettings experimentalSettings,
        GameLocatorSettings? gameLocatorSettings = null)
    {
        var observableTarget = new ObservableLoggingTarget();
        var host = new HostBuilder().ConfigureServices(services =>
        {
            var s = services.AddApp(
                startupMode: startupMode,
                experimentalSettings: experimentalSettings,
                gameLocatorSettings: gameLocatorSettings).Validate();

            if (loggingSettings.ShowExceptions)
                s.AddSingleton<IObservableExceptionSource, ObservableLoggingTarget>(_ => observableTarget);

            if (startupMode.IsAvaloniaDesigner)
            {
                s.OverrideSettingsForTests<DataModelSettings>(settings => settings with { UseInMemoryDataModel = true, });
            }
        })
        .ConfigureLogging((_, builder) => AddLogging(observableTarget, builder, loggingSettings, startupMode))
        .Build();

        return ApplicationConstants.IsDebug ? new DebuggingHost(host) : host;
    }

    private static void AddLogging(ObservableLoggingTarget observableLoggingTarget, ILoggingBuilder loggingBuilder, LoggingSettings settings, StartupMode startupMode)
    {
        var fs = FileSystem.Shared;
        var config = new NLog.Config.LoggingConfiguration();

        const string defaultLayout = "${processtime} [${level:uppercase=true}] (${logger}) ${message:withexception=true}";
        const string defaultHeader = "############ Afterburn log file - ${longdate} ############";

        FileTarget fileTarget;
        if (startupMode.RunAsMain)
        {
            fileTarget = new FileTarget("file")
            {
                FileName = settings.MainProcessLogFilePath.ToPath(fs).GetFullPath(),
                ArchiveFileName = settings.MainProcessArchiveFilePath.ToPath(fs).GetFullPath(),
            };
        }
        else
        {
            fileTarget = new FileTarget("file")
            {
                FileName = settings.SlimProcessLogFilePath.ToPath(fs).GetFullPath(),
                ArchiveFileName = settings.SlimProcessArchiveFilePath.ToPath(fs).GetFullPath(),
            };
        }

        fileTarget.ArchiveOldFileOnStartup = true;
        fileTarget.MaxArchiveDays = settings.MaxArchivedFiles;
        fileTarget.Layout = defaultLayout;
        fileTarget.Header = defaultHeader;

        if (settings.LogToConsole)
        {
            var consoleTarget = new ConsoleTarget("console")
            {
                Layout = "${processtime} [${level:uppercase=true}] ${message:withexception=true}",
            };
            config.AddRuleForAllLevels(consoleTarget);
        }

        // Afterburn: write the log file from a background thread. Written directly, every log line was a file write
        // (and flush) on whatever thread logged it, including the UI thread and the database's write thread.
        var asyncFileTarget = new NLog.Targets.Wrappers.AsyncTargetWrapper(fileTarget)
        {
            Name = "file-async",
            OverflowAction = NLog.Targets.Wrappers.AsyncTargetWrapperOverflowAction.Block,
        };
        config.AddRuleForAllLevels(asyncFileTarget);
        // Afterburn: the log is written from a background thread, so write out what is queued if the app crashes.
        AppDomain.CurrentDomain.UnhandledException += static (_, _) => NLog.LogManager.Flush(TimeSpan.FromSeconds(2));
        config.AddRuleForAllLevels(observableLoggingTarget);


        // NOTE(erri120): RemoveLoggerFactoryFilter prevents
        // the global minimum level to take effect.
        // https://github.com/Nexus-Mods/NexusMods.App/issues/250
        var options = new NLogProviderOptions
        {
            RemoveLoggerFactoryFilter = false,
        };

        loggingBuilder.AddFilter("Microsoft", LogLevel.Warning);
        loggingBuilder.AddFilter("System", LogLevel.Warning);
        loggingBuilder.AddFilter("Avalonia.ReactiveUI", LogLevel.Warning);

        loggingBuilder.ClearProviders();
        loggingBuilder.SetMinimumLevel(settings.MinimumLevel);
        loggingBuilder.AddNLog(config, options);
    }

    /// <summary>
    /// Don't Delete this method. It's used by the Avalonia Designer.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    // ReSharper disable once UnusedMember.Global
    public static AppBuilder BuildAvaloniaApp()
    {
        var startupMode = new StartupMode()
        {
            RunAsMain = true,
            ShowUI = false,
            ExecuteCli = false,
            IsAvaloniaDesigner = true,
            Args = [],
            OriginalArgs = [],
        };

        var host = BuildHost(startupMode,
            LoggingSettings.CreateDefault(OSInformation.Shared),
            experimentalSettings: new ExperimentalSettings()
        );

        host.StartAsync().GetAwaiter().GetResult();

        DesignerUtils.Activate(host.Services);

        return Startup.BuildAvaloniaApp(host.Services);
    }
}

file class DebuggingHost : IHost
{
    private readonly IHost _inner;
    private readonly ILogger _logger;

    public DebuggingHost(IHost inner)
    {
        _inner = inner;
        _logger = inner.Services.GetRequiredService<ILogger<DebuggingHost>>();
    }

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    public IServiceProvider Services => _inner.Services;

    [SuppressMessage("ReSharper", "LocalizableElement")]
    public void Dispose()
    {
        // NOTE(erri120): I'm doing reflection and you can't stop me.
        if (_inner.Services is not ServiceProvider services) throw new NotSupportedException();

        var rootPropertyInfo = services.GetType().GetProperty(name: "Root", BindingFlags.Instance | BindingFlags.NonPublic);
        if (rootPropertyInfo is null) throw new NotSupportedException();

        var root = rootPropertyInfo.GetMethod?.Invoke(services, parameters: null);
        if (root is not IServiceScope scope) throw new NotSupportedException();
        if (scope.GetType().Name != "ServiceProviderEngineScope") throw new NotSupportedException();

        var fieldInfo = scope.GetType().GetField("_disposables", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fieldInfo is null) throw new NotSupportedException();

        var fieldValue = fieldInfo.GetValue(scope);
        if (fieldValue is not List<object> tempList) throw new NotSupportedException();

        var disposableServices = tempList.ToArray();
        Log("Disposing `{0}` services", disposableServices.Length);

        Reloaded.Memory.Utilities.Box<bool> didDispose = false;

        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(5);
            await Task.Delay(delay);

            // ReSharper disable once AccessToModifiedClosure
            bool isDisposed = didDispose;
            if (isDisposed) return;

            Log("Failed to dispose `{0}` services withing `{1}` seconds", disposableServices.Length, delay.TotalSeconds);
            foreach (var disposableService in disposableServices)
            {
                var disposableType = disposableService switch
                {
                    IDisposable => "sync",
                    IAsyncDisposable => "async",
                    _ => throw new NotSupportedException(),
                };

                Log("Type={0} HashCode={1} DisposableType={2}", disposableService.GetType(), disposableService.GetHashCode(), disposableType);
            }

            // NOTE(erri120): If you landed here, that means the app is probably stuck shutting down.
            // Use this opportunity to further debug the issue. You can see all the services that need
            // disposing by inspecting the variables above and checking the logs.
            if (Debugger.IsAttached) Debugger.Break();
            // if (ApplicationConstants.IsCI) Environment.Exit(exitCode: 1);
        });

        _inner.Dispose();
        didDispose = true;
    }

    private void Log(string format, params object?[] arguments)
    {
        _logger.LogDebug(format, arguments);
        if (ApplicationConstants.IsCI) Console.WriteLine(format, arguments);
    }
}
