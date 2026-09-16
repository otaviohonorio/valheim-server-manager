---
name: vsm-development
description: How to build, test, run, publish and extend the Valheim Server Manager repo (.NET 10, WinUI 3 / Windows App SDK 2.4, MVVM Toolkit, xUnit v3 on Microsoft.Testing.Platform). Use for any code change in this repository, including adding pages, view models, CLI commands or core services.
---

# Valheim Server Manager — development

## Toolchain

- .NET SDK 10.0.401 (pinned in `global.json`). On the author's machine it is installed per-user
  (no admin), so a fresh shell needs:
  ```bash
  export DOTNET_ROOT="$(cygpath -w "$LOCALAPPDATA")/Microsoft/dotnet" PATH="$(cygpath "$LOCALAPPDATA")/Microsoft/dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  ```
  Elsewhere: `winget install Microsoft.DotNet.SDK.10` or `dotnet-install.ps1 -Channel 10.0`.
- No Visual Studio needed: WinUI 3 builds with `dotnet build` (Windows App SDK ≥ 2.1.3 reports
  XAML errors properly). If the XAML compiler says it "cannot resolve DataType" for local types,
  look for a **C# error** first — pass 2 needs the compiled assembly.
- Central package versions: `Directory.Packages.props`. Warnings are errors.

## Commands

```bash
dotnet build ValheimServerManager.slnx
dotnet test --project tests/ValheimServerManager.Core.Tests          # MTP runner (global.json)
dotnet run --project src/ValheimServerManager.Cli -- --help
powershell -File build/publish.ps1                                  # self-contained app + CLI in dist/ (~350 MB)
powershell -File build/install.ps1 -Publish                          # %LOCALAPPDATA%\Programs + shortcuts
```

PowerShell scripts must be saved as UTF-8 **with BOM** (Windows PowerShell 5.1 reads BOM-less files
as ANSI; a typographic quote in a string then breaks parsing). Bash heredocs in the agent shell
collapse `\\` — write Python/PowerShell helpers to files instead of inline heredocs.

Run the app against a throwaway settings folder so the user's real profiles are untouched:
`VSM_DATA_DIR=<repo>/.test-servers/appdata` (ignored by git).

## Layout

```
src/ValheimServerManager.Core    domain + services, no UI (net10.0-windows)
  Worlds/      save format: ChunkIndex, WorldMetadata, WorldInspector, recovery
  Profiles/    ServerProfile, ModifierCatalog, LaunchArguments, ProfileValidator, BatchFileImporter
  Processes/   hidden launch, Ctrl+C helper (ConsoleSignal), WMI locator
  Logs/        ServerLogParser, LogTailer, LogArchiver
  Backups/     BackupService (SHA-256 manifests, retention, restore)
  Servers/     ServerController (state machine, safety rules), ServerManager (profiles, attach)
  Settings/    JsonSettingsStore (DPAPI passwords, atomic writes, schema version)
src/ValheimServerManager.App     WinUI 3, MVVM Toolkit, Generic Host DI, Serilog
  Program.cs   custom Main: helper mode (--vsm-send-ctrl-c) + single instance
  Services/    UiDispatcher, DialogService, PickerService, ProfileContext, AlertCenter
  ViewModels/  ProfilePageViewModel base → Dashboard/ServerSettings/World/Backups/Players/Log
  Views/       pages (XAML) + PageCodeBehind.cs
src/ValheimServerManager.Cli     `vsm` System.CommandLine tool; also its own Ctrl+C helper
tests/ValheimServerManager.Core.Tests  xUnit v3; synthetic worlds only (TestWorlds)
```

## Conventions

- UI text is Brazilian Portuguese; identifiers and comments in English.
- View models never touch XAML types except `InfoBarSeverity`; controller events arrive on
  worker threads — always go through `UiDispatcher`.
- MVVM Toolkit partial properties (`[ObservableProperty] public partial T X { get; set; }`).
  The generator creates `OnXChanged` hooks: do not declare your own methods with those names
  (that is why the base class uses `OnServerStatusChanged`, `OnBusyChanged`).
- `x:Bind` only; no generic types in `x:DataType` (use non-generic records such as `OptionItem`).
- Safety rules belong in Core (`ServerController`, `ProfileValidator`, `WorldInspector`), with a
  unit test, never only in the UI.
- Never commit save data, passwords or real SteamIDs (`.gitignore` blocks save file types).

## Testing against a real server — rules

- **Never stop, start or modify a server you did not launch.** Check first:
  `Get-CimInstance Win32_Process -Filter "Name='valheim_server.exe'"`.
- Use a copy of a world, a separate port (2466), `-public 0`, and a work dir under
  `.test-servers/`:
  ```bash
  vsm e2e --server-dir "<install>" --world-source "<world folder copy>" --port 2466 --work-dir .test-servers/e2e-run --keep
  ```
  It covers start → backup while running → Ctrl+C stop with save → creative on/off verified in the
  world file → refusal of a world missing its .db2 → restore.
- Screenshots of the running app: `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` on the
  process' main window.
- Drive the UI with UI Automation (`System.Windows.Automation`): navigation items expose
  `SelectionItemPattern`, buttons `InvokePattern`. Icon+text buttons need
  `AutomationProperties.Name` — without it they are invisible to automation and screen readers.
- Simulate Windows shutdown by sending `WM_QUERYENDSESSION` (0x11) then `WM_ENDSESSION` (0x16,
  wParam=1) to the `WinUIDesktopWin32WindowClass` window; the app must stop servers before
  `WM_ENDSESSION` returns.

## Platform notes

- App notifications (`AppNotificationManager`) fail in self-contained apps (they need the
  installed Windows App SDK runtime, WindowsAppSDK#6071). Notifications go through the tray icon
  (`TrayIcon`, plain `Shell_NotifyIcon`).
- The main window is subclassed (`SetWindowSubclass`) for tray callbacks, `TaskbarCreated` and
  session end. At logoff/shutdown Windows kills hidden console processes without letting them
  save, so `WM_ENDSESSION` stops every server first (with `ShutdownBlockReasonCreate`).
- Editable `ComboBox.Text` bindings lose their value when items arrive later; use `AutoSuggestBox`.
- Closed `InfoBar`s still take `StackPanel.Spacing`; bind `Visibility` too.
- Never edit a `.bat` that is currently running: cmd reads batch files by byte offset.
