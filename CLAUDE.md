# Valheim Server Manager

Windows desktop app (WinUI 3, .NET 10) + CLI that manages Valheim 1.0 dedicated servers safely.
Born from a real incident on 2026-09-16 where a server and the game client shared a save folder
and Valheim regenerated the world over the player's base (see `docs/incident-2026-09-16.md`).

## Skills in this repo

- `vsm-development` — build/test/run/publish, architecture, conventions, rules for testing with a
  real server. **Read before changing code.**
- `valheim-server-operations` — launch arguments, `-preset`/`-setkey` persistence, Ctrl+C
  shutdown, logs, admin lists, cheat flag.
- `valheim-save-format` — `_main.N.*` and chunk formats, regeneration trap, recovery.

## Non-negotiables

- Never start, stop or edit a valheim_server you did not launch; the author often has a live
  server on port 2456. Tests use copies, port 2466 and `.test-servers/`.
- Safety rules live in Core with unit tests (`ServerController`, `ProfileValidator`,
  `WorldInspector`, `BackupService`).
- Never commit save files, passwords or real SteamIDs.
- UI text in pt-BR; code and comments in English. Warnings are errors.

## Quick commands

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet" PATH="$LOCALAPPDATA/Microsoft/dotnet:$PATH"   # per-user SDK install
dotnet build ValheimServerManager.slnx
dotnet test --project tests/ValheimServerManager.Core.Tests
powershell -File build/publish.ps1
powershell -File build/install.ps1 -Publish   # installs to %LOCALAPPDATA%\Programs + shortcuts
```
