<p align="center"><img src="Nexus-Icon.png" width="128" alt="Afterburn logo"></p>

# Afterburn

**Afterburn is a community-maintained mod manager for Cyberpunk 2077**, forked from the open-source [Nexus Mods app](https://github.com/Nexus-Mods/NexusMods.App) after Nexus Mods stopped developing it in January 2026.

> **Afterburn is not affiliated with, endorsed by, or supported by Nexus Mods.**
> "Nexus Mods" and the Nexus Mods logo are trademarks of their owner and are used here only to describe where Afterburn comes from and which service it connects to.
> Please don't ask Nexus Mods for help with Afterburn. Use this repository's Issues page instead.

> **AI disclosure:** Afterburn's modifications to the upstream code were developed with AI assistance (Claude, by Anthropic), under human direction and testing. The changes are listed in `NOTICE.md` and the commit history.

## What's different from the Nexus Mods app

- **Cyberpunk 2077 only:** support for all other games was removed to keep Afterburn focused.
- **Cyberpunk 2077 patch 2.5 support:** the game version is detected correctly again. The Steam indexer can hash files from your local install instead of downloading them.
- **Rate Meter:** the bottom bar shows how many Nexus Mods API requests you have left this hour and today, when they reset, and when you've been rate limited.
- **Temporary Unpack Location setting:** unpack mods on any drive, not only the system drive.
- **Startup fix on Windows:** source builds no longer crash on launch (a missing registry service in the last upstream code).
- **No telemetry:** usage data is never sent anywhere.

See [NOTICE.md](NOTICE.md) for the full list of changes.

## Adding a Cyberpunk 2077 collection

1. Log in to Nexus Mods in Afterburn (top-right button).
2. Then either:
   - On the collection's page on nexusmods.com, click the button to add it to your mod manager. Afterburn opens it automatically. If your browser asks which app to use, pick **Afterburn**.
   - Or copy the collection's address from your browser's address bar. In Afterburn, go to **Library → Collections** and click **Paste collection link**.
3. Afterburn opens the collection page. Click **Download**, then **Install**.

Free Nexus Mods accounts must click each mod's download on the website. This is a Nexus Mods rule, and Afterburn walks you through it. Premium accounts download everything automatically.

## Status

Afterburn is in **testing**. Under the Nexus Mods [API Acceptable Use Policy](https://help.nexusmods.com/article/114-api-acceptable-use-policy), apps for the public must be registered with Nexus Mods before they use the API. **Afterburn's registration is pending.** Until it's approved:

- The built-in "Log in with Nexus Mods" button won't work.
- For **testing or personal use only**, log in with your own personal API key. Get it from your Nexus Mods account's API Access page, then set it as the `NEXUS_API_KEY` environment variable before starting Afterburn. Afterburn copies the key into an encrypted file that only your Windows account can read (`%LOCALAPPDATA%\AfterburnData`), hides it from the game and other programs it starts, and you can then delete the environment variable.

## Building

Requires Git and the .NET 9 SDK.

```powershell
git clone https://github.com/raine423-afk/afterburn.git
cd afterburn
dotnet publish src/NexusMods.App/NexusMods.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

Run `publish/Afterburn.exe`. Internal project names still use the `NexusMods.*` prefix inherited from upstream. They're code identifiers, not branding.

## Download and install

Windows installers are attached to each release on the [Releases page](https://github.com/raine423-afk/afterburn/releases) as `Afterburn-Setup-<version>.exe`. The installer bundles everything Afterburn needs, including the .NET runtime, and shows the GPL-3.0 license before installing. It isn't code-signed yet, so Windows may show a "Windows protected your PC" warning: choose **More info → Run anyway**.

## Source code for every release (GPL-3.0 section 6)

The installer and the files it installs are object code built from this repository. The complete Corresponding Source for each release is available here, at no charge, from the same place as the download:

- each release is built from the commit its tag points to (for example tag `v1.0.0`), and the release page links the source code for that exact commit as `.zip` and `.tar.gz`;
- the build steps, including the installer script (`installer/afterburn.iss`) and the build workflow (`.github/workflows/build.yaml`), are part of this repository;
- the game-hashes database bundled with the app (`data/game_hashes_db.zip`) is included in the repository.

Development builds produced by GitHub Actions are for testing; their source is the commit shown on the build page.

## License

Afterburn is free software under the **GNU General Public License v3.0**, the same license as the Nexus Mods app it's based on. See [LICENSE.md](LICENSE.md).
Copyright for the original code belongs to Nexus Mods and its contributors. Afterburn's changes are also licensed under GPL-3.0.
Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
