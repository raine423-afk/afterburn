# Notice

Afterburn is a modified version of the **Nexus Mods app** (https://github.com/Nexus-Mods/NexusMods.App), based on upstream commit `48284f3` (2025-11-19, after release v0.21.1).

Original work copyright (C) Nexus Mods and the NexusMods.App contributors, licensed under the GNU General Public License v3.0.
Modifications copyright (C) 2026 the Afterburn contributors, licensed under the GNU General Public License v3.0.

Afterburn is not affiliated with or endorsed by Nexus Mods. Nexus Mods stated that the community may fork the app if the fork uses a different name ("Discontinuing the Nexus Mods App FAQ", help.nexusmods.com, updated 2026-01-14).

AI disclosure: the modifications described below were developed with AI assistance (Claude, by Anthropic), under human direction and testing.

## Changes from upstream

All changes made 2026-11-09 unless noted.

### Rebranding and compliance
- Renamed the application to **Afterburn**: window title, welcome screen, Windows protocol-handler registration names, log header, generated plugins.txt comment, uninstall help text, and bottom-bar label. (`src/NexusMods.Sdk/AfterburnBrand.cs` added.)
- Replaced the Nexus Mods logo used as the app's own icon with original Afterburn artwork: `src/NexusMods.App/icon.ico`, `src/NexusMods.App.UI/Assets/afterburn.ico`, `src/NexusMods.App.UI/Assets/afterburn.svg`, `Nexus-Icon.png`, and the `AfterburnFlame` sidebar icon in `IconValues.cs` / `SpineButtonStyles.axaml`. Nexus Mods logos remain only where they refer to the Nexus Mods service itself (for example its login screen and collections).
- Nexus Mods API requests now identify as `Afterburn` (`Application-Name` header and user agent) instead of the official app (`ApplicationConstants.cs`, `Abstractions.Telemetry/Constants.cs`).
- Removed use of the official app's OAuth client ID (`OAuth.cs`). Afterburn's own ID will be added after Nexus Mods approves its registration.
- Afterburn ignores saved Nexus Mods logins that belong to the official app's client ID, and its Log out button no longer deletes them (`OAuth2MessageFactory.cs`, `LoginManager.cs`).
- Removed all telemetry and usage tracking: the OpenTelemetry packages, the event/analytics senders, the opt-in prompt and setting, and the campaign tracking parameters that were appended to links to Nexus Mods (`NexusMods.Telemetry`, `Tracking`, `NexusModsUrlBuilder`, `WelcomeOverlayView.axaml`).
- Replaced the Nexus Mods logos and icons with neutral ones and changed the colour theme.
- Project, feedback and update-check links now point to the Afterburn repository instead of Nexus Mods (`ConstantLinks.cs`, `UpdateChecker.cs`, `DevelopmentBuildBannerViewModel.cs`, `WelcomeOverlayView.axaml`).

### Cyberpunk 2077 only
- Removed support for all games except Cyberpunk 2077: Stardew Valley, Baldur's Gate 3, Creation Engine games (Skyrim, Fallout and others) and Mount & Blade II: Bannerlord. Deleted their projects (`src/NexusMods.Games.StardewValley`, `.StardewValley.SMAPI`, `.Larian`, `.CreationEngine`, `.MountAndBlade2Bannerlord`) and their tests, removed them from `NexusMods.App.sln` and `NexusMods.App.csproj`, and registered only Cyberpunk 2077 in `src/NexusMods.App/Services.cs`.
- Removed the SMAPI submodule (`extern/SMAPI`). Afterburn no longer includes SMAPI code.
- Switched the legacy-database tests (`ALegacyDatabaseTest.cs`, `AArchivedDatabaseTest.cs`) from Stardew Valley to Cyberpunk 2077.

### Afterburn visual identity
- New colour scheme: blue-violet "Midnight" neutrals replace the grey (zinc) palette, and neon cyan replaces orange as the primary colour (`PrimitiveColors.axaml`, `BrandColors.axaml`). The app gradient is now cyan to magenta (`ElementGradients.axaml`), and the last hard-coded orange in `ApplyControlStyles.axaml` now uses the theme colour.
- Headlines use the Chakra Petch font (SIL OFL 1.1) instead of Inter (`PrimitiveFonts.axaml`, `SemanticFonts.axaml`, theme `.csproj`, new font files).
- Sharper corners throughout (`ElementCornerRadius.axaml`).

### Repository
- Startup: the embedded game-hashes database is no longer re-imported on every launch (timestamp comparison now uses whole seconds) (`FileHashesService.cs`).
- Path-escape hardening: game paths with `..` segments or roots are refused before they become disk paths (`GamePath.cs`, `GameLocations.cs`), and the 7-Zip trailing-space clean-up never moves or deletes outside the extraction folder (`SevenZipExtractor.cs`).
- Description images load only from Nexus Mods and GitHub hosts; all image downloads must be HTTPS and are capped at 20 MB (`MarkdownRendererViewModel.cs`, `HttpLoader.cs`). Optional Authenticode signing of `Afterburn.exe` and the installer in CI when a certificate secret is configured (`build.yaml`).
- Security: the local command channel now requires a per-session secret stored in the user's private app folder (`SyncFile.cs`, `CliServer.cs`, `CliClient.cs`); Windows opens links and files via the shell directly instead of `cmd.exe` (`WindowsInterop.cs`); the GitHub owner used for updates, bug reports and links is set to this repository (`AfterburnBrand.cs`); update downloads are only offered from GitHub (`UpdaterViewModel.cs`).
- Easier collection linking: a "Paste collection link" button in Library → Collections accepts normal nexusmods.com collection addresses (latest published revision if none given) or nxm:// links and hands them to the existing nxm handler (`CollectionLinkResolver.cs`, `LibraryView*`, `LibraryViewModel.cs`, `AvaloniaInterop.cs`). Clear messages when not logged in or when the game isn't set up yet (`MainWindowViewModel.cs`).
- One-click bug reporting: a "Report a bug" button (footer and Help menu) replaces the upstream "Give Feedback" button and the Nexus Mods Discord, forums and status-page links (`Controls/BugReport/BugReporter.cs`, `TopBar*`, `DevelopmentBuildBanner*`, `ConstantLinks.cs`). Lighter runtime settings (no concurrent GC, ConserveMemory) in `NexusMods.App.csproj`. Installer hardening (`AllowUNCPath=no`, close only Afterburn.exe) and SHA-256 checksums plus a dependency vulnerability check in CI.
- The Windows launcher is renamed `NexusMods.App.exe` → `Afterburn.exe` after publish (`NexusMods.App.csproj`, `installer/afterburn.iss`, `uninstall.bat`, `uninstall-helper.ps1`).
- Final branding pass: Windows registry keys and protocol handler IDs now use "Afterburn"; Linux desktop/AppStream metadata, NuGet package author and URLs, the collection "Created with" text, the in-app changelog source and Dependabot reviewer no longer name Nexus Mods; removed the upstream docs site, IDE settings, Qodana/Codecov configs and the upstream release notes (kept in git history). Copyright notices, the license, code identifiers/namespaces, and references to the Nexus Mods website and API (login, downloads, collections) are intentionally kept.
- Removed Nexus Mods' CI/CD workflows (signing, NuGet publishing, releases and their docs site), the `docs/Nexus` theme submodule and `mkdocs.yml`. Added a simple Afterburn build workflow (`.github/workflows/build.yaml`), a new issue template and `CONTRIBUTING.md`, and an Afterburn section in `CHANGELOG.md`.
- Security and bug-fix pass (November 2026): the bundled Windows 7-Zip is updated from 20.02 to 26.06 (`src/NexusMods.Backend/runtimes/win-x64/native/7z.exe`, `7z.dll`); the personal API key is stored encrypted for the current Windows user instead of being read from an environment variable on every start, and is hidden from programs Afterburn starts (`Auth/ApiKeyStore.cs`, `ApiKeyMessageFactory.cs`); only the built-in game hashes database is used and its unpacking is confined to its folder (`FileHashesService.cs`); description and manual-download links open only web pages (`UntrustedLinks.cs`, `MarkdownRendererViewModel.cs`, `ManualDownloadRequiredOverlay.cs`); bug reports also remove download keys and match user and PC names as whole words (`BugReporter.cs`); the pre-launch conflict check reports failed mods instead of turning them off and respects mod authors' archive naming (`CyberpunkConflictResolver.cs`, `RunCyberpunk2077Game.cs`); FOMOD installer windows are released after each install and a waiting mod shows a notice (`FomodXmlInstaller.cs`, `NexusMods.Games.FOMOD.csproj`); contradictory collection load-order rules are skipped quickly (`LoadoutManager.cs`); "Paste collection link" reports errors and rejects other games (`LibraryViewModel.cs`); very long archive entry names no longer fail an install (`SevenZipExtractor.cs`); CI publishes releases from a separate job with write access, pins tool versions, uses current Node 24 actions and shows the dependency check in the job summary (`build.yaml`); Dependabot ignores major .NET library updates (`dependabot.yml`); `Microsoft.AspNetCore.WebUtilities` 9.0.23; the installer removes old program files before upgrading (`installer/afterburn.iss`).
- Dependencies: `Tmds.DBus.Protocol` pinned to 0.21.3 (GHSA-xrw6-gwf8-vvr9) in `Directory.Packages.props` and `NexusMods.App.csproj`; the separate `Microsoft.SourceLink.GitHub` 8.0.0 package was removed in favour of the SDK's built-in Source Link (GHSA-23fw-v26w-5fgq) (`src/Directory.Build.props`).

### Features and fixes
- **Cyberpunk 2077 2.5 support:** `steam app index` gained `--branch` and `--gameFolder` options to hash a local install (`Networking.Steam/CLI/Verbs.cs`).
- **Temporary Unpack Location** setting (`Sdk/FileExtractor/IFileExtractorSettings.cs`).
- **Rate Meter:** API rate-limit tracking and bottom-bar display (`Networking.NexusWebApi/NexusApiRateLimitTracker.cs`, `NexusApiClient.cs`, `Services.cs`, `App.UI/Controls/DevelopmentBuildBanner/*`).
- **Windows startup crash fixed:** registers the Windows registry service needed by the Steam, GOG and Epic game locators (`Backend/ServiceExtensions.cs`).
- **Windows installer:** added an Inno Setup installer (`installer/afterburn.iss`) built by the GitHub workflow. Builds are now self-contained (they include the .NET runtime) and precompiled (ReadyToRun) for faster startup (`.github/workflows/build.yaml`).
- **Lower memory and CPU use:** .NET garbage-collector and runtime settings tuned (`src/NexusMods.App/runtimeconfig.template.json`).
- **Cyberpunk 2077 2.5 detection built in:** the game-hashes database with 2.5 is kept in the repository (`data/game_hashes_db.zip`) and embedded at build time instead of downloading the older one (`NexusMods.Games.FileHashes.csproj`). If the embedded database is newer than the one on disk, Afterburn now adds and uses it (`FileHashesService.cs`).
- **Faster library processing:** mods are compressed with Zstandard level 3 instead of 16/9 when added to the library (`NxFileStore.cs`).
- **Release compliance:** README sections on downloads and where to get the source for each release; tagged releases publish the installer next to the source (`build.yaml`); licenses for bundled third-party components are included with the app (`THIRD-PARTY-NOTICES.md`).
- **Security update:** OpenTelemetry packages updated from 1.12.0 to 1.15.3 to fix GHSA-4625-4j76-fww9 (`Directory.Packages.props`). Telemetry stays disabled.
