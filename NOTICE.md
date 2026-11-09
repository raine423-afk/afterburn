# Notice

Afterburn is a modified version of the **Nexus Mods app** (https://github.com/Nexus-Mods/NexusMods.App), based on upstream commit `48284f3` (2025-11-19, after release v0.21.1).

Original work copyright (C) Nexus Mods and the NexusMods.App contributors, licensed under the GNU General Public License v3.0.
Modifications copyright (C) 2026 the Afterburn contributors, licensed under the GNU General Public License v3.0.

Afterburn is not affiliated with or endorsed by Nexus Mods. Nexus Mods stated that the community may fork the app if the fork uses a different name ("Discontinuing the Nexus Mods App FAQ", help.nexusmods.com, updated 2026-01-14).

## Changes from upstream

All changes made 2026-11-09 unless noted.

### Rebranding and compliance
- Renamed the application to **Afterburn**: window title, welcome screen, Windows protocol-handler registration names, log header, generated plugins.txt comment, uninstall help text, and bottom-bar label. (`src/NexusMods.Sdk/AfterburnBrand.cs` added.)
- Replaced the Nexus Mods logo used as the app's own icon with original Afterburn artwork: `src/NexusMods.App/icon.ico`, `src/NexusMods.App.UI/Assets/afterburn.ico`, `src/NexusMods.App.UI/Assets/afterburn.svg`, `Nexus-Icon.png`, and the `AfterburnFlame` sidebar icon in `IconValues.cs` / `SpineButtonStyles.axaml`. Nexus Mods logos remain only where they refer to the Nexus Mods service itself (for example its login screen and collections).
- Nexus Mods API requests now identify as `Afterburn` (`Application-Name` header and user agent) instead of the official app (`ApplicationConstants.cs`, `Abstractions.Telemetry/Constants.cs`).
- Removed use of the official app's OAuth client ID (`OAuth.cs`). Afterburn's own ID will be added after Nexus Mods approves its registration.
- Afterburn ignores saved Nexus Mods logins that belong to the official app's client ID, and its Log out button no longer deletes them (`OAuth2MessageFactory.cs`, `LoginManager.cs`).
- Disabled all telemetry and usage tracking, and removed the opt-in prompt and setting (`Program.cs`, `TrackingSettings.cs`, `WelcomeOverlayView.axaml`).
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
- Removed Nexus Mods' CI/CD workflows (signing, NuGet publishing, releases and their docs site), the `docs/Nexus` theme submodule and `mkdocs.yml`. Added a simple Afterburn build workflow (`.github/workflows/build.yaml`), a new issue template and `CONTRIBUTING.md`, and an Afterburn section in `CHANGELOG.md`.

### Features and fixes
- **Cyberpunk 2077 2.5 support:** `steam app index` gained `--branch` and `--gameFolder` options to hash a local install (`Networking.Steam/CLI/Verbs.cs`).
- **Temporary Unpack Location** setting (`Sdk/FileExtractor/IFileExtractorSettings.cs`).
- **Rate Meter:** API rate-limit tracking and bottom-bar display (`Networking.NexusWebApi/NexusApiRateLimitTracker.cs`, `NexusApiClient.cs`, `Services.cs`, `App.UI/Controls/DevelopmentBuildBanner/*`).
- **Windows startup crash fixed:** registers the Windows registry service needed by the Steam, GOG and Epic game locators (`Backend/ServiceExtensions.cs`).
