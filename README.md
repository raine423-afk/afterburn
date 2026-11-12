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

## Quick start (testing and personal use, until Nexus Mods approves Afterburn)

Afterburn's registration with Nexus Mods is still pending, so the normal **Log in with Nexus Mods** button doesn't work yet. (Afterburn doesn't borrow the official Nexus Mods app's login credentials, so the button stays inactive until Afterburn has its own.) Until then, use your own personal API key through the **Log in with API key** button. Nexus Mods' [API Acceptable Use Policy](https://help.nexusmods.com/article/114-api-acceptable-use-policy) allows personal keys for **testing and personal use only**, so please don't build other tools or services on this, and don't share your key. It takes about two minutes:

1. **Install Afterburn.** Download `Afterburn-Setup-<version>.exe` from the [Releases page](https://github.com/raine423-afk/afterburn/releases) and run it. If Windows shows "Windows protected your PC", choose **More info → Run anyway** (the installer isn't code-signed yet).
2. **Get your personal API key.** Log in at nexusmods.com, open [your account's API page](https://www.nexusmods.com/users/myaccount?tab=api) and copy the **Personal API Key**. Keep it private: it is like a password for your account, so never post it in an Issue or send it to anyone.
3. **Click the green "Log in with API key" button** at the top right of Afterburn. It reads the key you just copied, checks it with Nexus Mods and saves it, encrypted so only your Windows account can read it. You'll see a message when it's done.
4. **Close Afterburn completely** (check the tray icon too) and open it again. You're now logged in; there is nothing else to enter.
5. **Add Cyberpunk 2077 and install a collection.** See the next section.

**Once Nexus Mods approves Afterburn**, you won't need a key: the button becomes a normal **Log in** button, your browser opens on nexusmods.com, you approve Afterburn there, and you're returned to the app. You can revoke Afterburn's access at any time on your Nexus Mods API page.

Good to know:

- Afterburn only touches Cyberpunk 2077. Close the game before you click **Apply**.
- If the game won't start after applying, open `r6/logs/afterburn-conflicts.txt` in your game folder, or the **Conflicts** tab of your mod list. It lists broken mods, conflicts and damaged archives, and says what to do.
- Free Nexus Mods accounts must click each mod's download on the website (a Nexus Mods rule). Premium accounts download everything automatically.
- Please report problems on this repository's Issues page, not to Nexus Mods.

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

## Privacy policy

Afterburn does not collect or send usage data, analytics or crash reports. It connects to the internet only when you use a feature that needs it: Nexus Mods (to log in with your API key, browse and download the mods you choose) and GitHub (to check for updates and send a bug report if you press "Report a bug"). Your settings, mods and API key (encrypted for your Windows user) stay on your computer.
