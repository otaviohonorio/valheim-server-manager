# Architecture

```
┌──────────────────────────────┐   ┌─────────────────────────┐
│ ValheimServerManager.App     │   │ ValheimServerManager.Cli│
│ WinUI 3 · MVVM · Generic Host│   │ System.CommandLine      │
│ Views → ViewModels → Services│   │ (vsm.exe)               │
└──────────────┬───────────────┘   └────────────┬────────────┘
               │                                │
               ▼                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ValheimServerManager.Core   (no UI, unit tested)             │
│                                                              │
│  Servers   ServerManager ──► ServerController (per profile)  │
│               │                 │  preflight · backup        │
│               │                 │  launch · log · shutdown   │
│  Settings  JsonSettingsStore    │  emergency stop            │
│  Profiles  ServerProfile · LaunchArguments · ProfileValidator│
│            ModifierCatalog · BatchFileImporter               │
│  Processes HiddenConsoleLauncher · ConsoleSignal (Ctrl+C)    │
│            WmiServerProcessLocator                           │
│  Logs      ServerLogParser · LogTailer · LogArchiver         │
│  Backups   BackupService (SHA-256, manifest, restore)        │
│  Worlds    WorldInspector · ChunkIndex · WorldMetadata       │
│            ChunkIndexRebuilder · PlayerBuildScanner          │
│            WorldCreator (new seed / copy of the latest save) │
│            ChunkObjects · WorldDatabase · WorldRepair        │
│            WorldCheatMarks · WorldSaveWriter                 │
│  Localization AppLanguage + Strings.resx (en, pt-BR, es)     │
└──────────────────────────────────────────────────────────────┘
               │
               ▼
      valheim_server.exe  (hidden console, -savedir, -logFile)
```

## Life cycle of a server

```
Stopped ──Start──► preflight ──blocked──► Stopped (reasons on screen)
                        │ ok / confirmed
                        ▼
                  backup first (if this save has none yet)
                        ▼
                  archive log · launch without a window ──► Starting
                        ▼ "Game server connected"
                     Online ──"missing _main.N.db2"──► killed at once (emergency)
                        │
                     Stop: Ctrl+C from a helper process ──► Stopping
                        ▼ process exits
         save confirmed in the log? ── yes ──► Stopped (clean) ──► backup after ──► inspection
                        │ no
                        ▼
                  Stopped (warning) · exit nobody asked for ──► Crashed
```

## Decisions

- **A helper process sends Ctrl+C.** Attaching to another process' console leaves the caller in a
  strange state, so the executable relaunches itself with `--vsm-send-ctrl-c <pid>` and exits.
- **Hidden console instead of a window.** The server keeps console behaviour (Ctrl+C works)
  without giving the user an X button that kills it without saving.
- **Servers outlive the app.** Closing the manager stops nothing; when it opens again, WMI finds
  the processes by command line and the controller reattaches (reading the log without reacting to
  old events).
- **Backups copy only complete saves.** Valheim never rewrites the files of a finished save, so the
  copy is consistent even while the server runs; restoring into a new folder guarantees nothing is
  mixed.
- **System tray.** With servers running, closing the window hides the app in the tray; the icon
  also delivers notifications (Windows App SDK notifications do not work in self-contained apps).
- **Windows shutdown.** Hidden console processes are killed without saving at logoff; the app
  handles `WM_ENDSESSION`, shows a reason on the shutdown screen and stops every server with Ctrl+C
  before letting go.
- **Passwords with DPAPI** (user scope); settings with atomic writes, a schema version and a
  `.bak` copy.
- **`TimeProvider`** injected into everything that waits or timestamps, for deterministic tests.
- **Localization.** English is the key language. Each area has `Localization\<Name>.resx` with
  `<Name>.pt-BR.resx` and `<Name>.es.resx` next to it; the build turns them into strongly-typed
  classes that XAML reaches with `{x:Bind loc:<Name>.<Key>}`. The language follows Windows unless
  chosen in the app; a test fails when a key is missing in a translation or its placeholders differ.
- **Installer.** Inno Setup, per user, never touches settings, worlds or backups; see
  `build/installer.iss` for the rules it enforces.
