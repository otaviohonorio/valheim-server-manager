<p align="center">
  <img src="src/ValheimServerManager.App/Assets/AppIcon.png" width="96" alt="" />
</p>

# Valheim Server Manager

> 🇧🇷 [Leia em português](README-ptBR.md)

A manager for **Valheim 1.0** dedicated servers on Windows. It starts servers, stops them with the
world saved, switches between normal and creative mode, makes verified backups, restores them
safely and edits the password, modifiers and player lists — all from a native Windows 11 app, in
English, Brazilian Portuguese or Spanish.

It exists because of a real incident: a server and the game opened the same save folder, a save
was left half-written and Valheim generated a brand-new world over the base
([full write-up](docs/incident-2026-09-16.md)). Every protection in the app comes from there.

## What it does

| | |
|---|---|
| **Safe start and stop** | The server runs without a window (there is no X to close it without saving). Stopping sends Ctrl+C and waits for the log to confirm the save. |
| **Creative mode that really turns off** | One button turns free building on and off. The preset always goes on the command line, so turning it off removes the key from the world — and the app checks the world file after the first save. |
| **Checks before starting** | Incomplete save, missing chunk, a folder shared with the game (even through a shortcut or junction), port in use, world already running: the server does not start. A save folder in OneDrive/Dropbox needs confirmation. A world that does not exist is only created after you confirm. |
| **Emergency stop** | If the log shows Valheim could not find the world data and started generating another world, the server is killed at once, before it saves over yours. |
| **Verified backups** | Before starting and after stopping (and whenever you want). Each backup holds one complete save, checked with SHA-256, with a manifest and retention. |
| **Safe restore** | Copies the current world first (or quarantines it), moves the old folder to `_substituidos` and never mixes files. |
| **Full configuration** | Name, password (encrypted with DPAPI), port, public, crossplay, folders, save interval, game backups, presets and every world modifier. |
| **Players** | Who is online right now (name, Steam ID and since when), join and leave notices, and the admin, banned and permitted lists with names read from the world itself. |
| **Live log** | Important events filtered out, and every session's log archived. |
| **Shut the PC down without losing anything** | If Windows shuts down with servers running, the app holds the shutdown for a few seconds and saves the worlds first. |
| **System tray** | Closing the window with servers running keeps the app in the tray; player and problem notifications show up there. |
| **Create servers** | A "New server" wizard: a new world with the seed you choose (or a random one), or a copy of a world you already play — only its latest complete save, checked. |
| **Several servers at once** | Each profile has its own world, folder and port, and several can run together. Locks prevent two servers on the same world/folder or port (Valheim uses the port and the next one). Servers started outside the app are detected and can be adopted. |
| **Duplicated-world repair** | Finds objects the game generated twice (items that "come back", ore that breaks twice, double enemies) and zones it is still going to generate again; the Dashboard warns you and repairs with a backup first. |
| **Automatic maintenance** | After every stop (including Restart, before starting again), the manager checks the world and fixes it by itself: duplicates, zones to mark and cheat marks. You can also run it any time from World → "Check and fix now". |
| **Cheat marks** | Finds what the game marked as "made with cheats" (marked items do not stack with their twins) and removes the mark from the whole world, without changing amounts or owners. |
| **Diagnostics** | Save inspection and a count of player-built pieces (workbenches, chests, portals…). |

## Installing

1. Download **`ValheimServerManager-Setup-x.y.z.exe`** from the
   [Releases](https://github.com/otaviohonorio/valheim-server-manager/releases/latest) page.
2. Run it and follow the wizard: you pick the install folder and whether you want a desktop
   shortcut. It does not ask for an administrator password.
3. On first use, click **Create my first server**. If a server is already running from a `.bat`,
   the app detects it and offers to adopt it.

Requires Windows 10 2004+ or Windows 11 (x64) and the **Valheim Dedicated Server**, installed from
Steam (Library → Tools). There is no need to install .NET or the Windows App SDK.

The app follows the Windows display language (English, Portuguese or Spanish; anything else falls
back to English). You can change it in **Protections & about → Language**.

> **"Windows protected your PC"?** The installer is not digitally signed yet, so SmartScreen warns
> on the first downloads. Click **More info → Run anyway**. The `.sha256` file next to the
> installer on the Releases page lets you check it is the original.

**Updating** is just running the new installer: it uses the same folder, keeps everything, and
running servers keep running (close the app from the tray with "Exit and keep running" first).
**Uninstalling** is in Settings → Apps. Neither touches worlds, backups or settings
(`%LOCALAPPDATA%\ValheimServerManager`), and uninstalling only happens with the servers stopped, so
none is left running with no way to save.

### The game and the server never share a world

Every server created by the app has **its own save folder**, separate from the game's
(`AppData\LocalLow\IronGate\Valheim`). The app refuses the game's folder, including through
shortcuts, links or junctions that point to it, and warns when the folder is in OneDrive, Dropbox
or Google Drive, which lock files in the middle of a save.

If you create the server from a world you already play, it gets a **copy**. The world in your
in-game list keeps existing but does not receive what happens on the server. To play on the
server's world, even alone, use **Join Game** (IP or join code), like your friends do.

### Building from source

`build/make-installer.ps1` builds the installer (needs the .NET 10 SDK and
[Inno Setup 6](https://jrsoftware.org/isinfo.php)); `build/install.ps1 -Publish` installs directly,
without an installer.

## Command line

`cli\vsm.exe` does what the app does, handy for scheduled tasks. It speaks the same language as the
app (override with the `VSM_LANG` environment variable: `en`, `pt-BR`, `es`).

```
vsm profiles                      list profiles and running servers
vsm create  --name X --password Y [--seed S | --copy-world <folder>] [--port N] [--private]
vsm status  -p "My server"        server and world status
vsm start   -p "My server"        start with every check
vsm stop    -p "My server"        Ctrl+C and wait for the save
vsm restart -p "My server"        stop, fix the world and start again
vsm backup  -p "My server"        verified backup (fine while the server runs)
vsm backups / vsm restore --backup <name>
vsm inspect <world-folder> --pieces --duplicates --cheats
vsm clean-cheat-marks -p "My server"        remove the cheat mark from the world
vsm clean-character <file.fch> --apply      remove the mark from a character's inventory
vsm repair-world -p "My server"   remove duplicated objects (server stopped; backup first)
vsm rebuild-index <world-folder> --save-number N    world recovery
vsm e2e ...                       end-to-end test against the real server
```

## Development

- .NET 10 · WinUI 3 (Windows App SDK 2.4) · CommunityToolkit.Mvvm · Generic Host · Serilog
- `Core` has no UI dependency and is covered by xUnit v3 tests
- `dotnet build ValheimServerManager.slnx` · `dotnet test --project tests/ValheimServerManager.Core.Tests`
- `build/publish.ps1` builds the self-contained app into `dist/`; `build/make-installer.ps1` builds the installer into `artifacts/installer/`
- Pushing a `vX.Y.Z` tag runs `release.yml`, which tests, builds and publishes the installer on Releases

**Translations.** English is the key language. Each area has `Localization/<Name>.resx` with
`<Name>.pt-BR.resx` and `<Name>.es.resx` next to it; a test fails when a key is missing in any
language or its placeholders differ. A new language is one more file per area plus an entry in
`AppLanguage.Supported`.

See [docs/architecture.md](docs/architecture.md), the skills in `.claude/skills/` and the
[CHANGELOG](CHANGELOG.md).

## Support

The app is free and always will be. If it saved your world (or your patience), there are two ways
to help, and both pay for the same thing — the hours that go into keeping it current with each
Valheim update:

- **[Ko-fi](https://ko-fi.com/ottorocket)** — a one-off tip, any amount, **no account needed**.
- **[GitHub Sponsors](https://github.com/sponsors/otaviohonorio)** — recurring, if you'd rather.

Doing neither costs you nothing here. A good bug report is worth just as much.

## Disclaimer

An independent project, not affiliated with Iron Gate or Coffee Stain. Valheim is a trademark of
its respective owners.

## License

[MIT](LICENSE). Valheim is a trademark of Iron Gate AB; this project is not affiliated with or
endorsed by it.
