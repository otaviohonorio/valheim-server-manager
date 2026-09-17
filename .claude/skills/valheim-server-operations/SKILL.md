---
name: valheim-server-operations
description: Operating Valheim 1.0 dedicated servers on Windows safely - launch arguments, world modifiers and -setkey persistence, graceful Ctrl+C shutdown from another process, save directories, logs, admin lists, crossplay, and the achievement cheat flag. Use when starting/stopping/configuring valheim_server.exe or debugging why a setting did not apply.
---

# Operating valheim_server.exe (Valheim 1.0.12)

## Launch line the manager always builds

```
valheim_server -nographics -batchmode -name "<name>" -port <p> -world "<World>" -password "<pw>"
  -public 0|1 [-crossplay] -savedir "<dir>" -logFile "<dir>\server.log"
  -saveinterval <s> -backups <n> -backupshort <s> -backuplong <s>
  -preset <normal|casual|easy|hard|hardcore|immersive|hammer>
  [-modifier combat|deathpenalty|resources|raids|portals <value>]…
  [-setkey nobuildcost|playerevents|passivemobs|nomap]…
```

- Environment: `SteamAppId=892970`; working directory = server install. Dedicated Server Steam
  app id is 896660 (`steamapps\appmanifest_896660.acf`).
- `-port p` uses UDP p and p+1 (not three ports in 1.0).
- Password: ≥ 5 chars, must not be contained in the server name.
- Who is online, from `server.log` (1.0.12):
  - connect: `PlayFab socket with remote ID playfab/… received local Platform ID Steam_<id>` (crossplay)
    or `Got connection SteamID <id>`; the character spawns a few seconds later, in the same order;
  - spawn: `Got character ZDOID from <name> : <owner>:<n>` — `0:0` means the character **died**
    (not a new join); a respawn repeats the same owner;
  - leave: `Destroying abandoned non persistent zdo … owner <owner>` (crossplay) or
    `Closing socket <steamid>`; `now 0 player(s)` clears everyone.
  `ServerController` keeps `ServerStatus.OnlinePlayers` from these lines.
- `-crossplay` adds the PlayFab relay and a join code (log: `registered with join code N`);
  no port forwarding, but no LAN IP join.

## Non-obvious rules (all verified)

- **Always pass `-savedir`.** Without it the server uses
  `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim` — the game client's folder. Server and game
  opening the same world corrupted a world on 2026-09-16.
- **Always pass `-logFile`.** Otherwise the Unity log only goes to the console and is lost.
  The server recreates the log on each start, so archive the previous one first.
- **`-setkey` is written into the world file permanently.** Removing it from the line does not
  remove it. With `-preset` on the line, keys are rebuilt from the line on every boot, so
  dropping `-setkey nobuildcost` really turns creative mode off (verified in `.fwl2`: 714 → 702
  bytes, the key gone). Order: `-preset`, then `-modifier`, then `-setkey`; a preset placed after
  modifiers wipes them. The boot log prints `Setting world modifier…` lines but no line for setkey.
- `nobuildcost` = "Hammer Mode": free building **and** workbench crafting for everyone; smelting
  and cooking still cost. Achievements are blocked while it is active (temporary).
- Console cheats (`devcommands`, `god`, `fly`, `debugmode`) do not work on a dedicated server
  without the BepInEx mod *Server Devcommands*. There is no per-player free-build in vanilla.
- Cheat flag: using most devcommands flags the character **and** the world permanently.
  Since 1.0.12, `yesiuseddevcommandsbutiwantmyachievementsanyway 1` (after `devcommands`, in
  single player) clears the character flag.
- Admin/ban/allow lists (`adminlist.txt`, `bannedlist.txt`, `permittedlist.txt`) live in the
  save dir, one SteamID64 per line, read at start. A non-empty permitted list blocks everyone else.

## Graceful shutdown from another process

Only Ctrl+C makes the server save (`World save (1/5)…(5/5) done`, then `Net scene destroyed`).
Closing the console window or Alt+F4 kills it without saving.

Recipe (`ConsoleSignal`), always in a short-lived helper process:
`FreeConsole()` → `AttachConsole(pid)` → `SetConsoleCtrlHandler(NULL, TRUE)` →
`GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)` → short sleep → `FreeConsole()`.

Launch with `UseShellExecute=false, CreateNoWindow=true` (hidden console that still receives
Ctrl+C; no window to close by accident). **Before launching, call
`SetConsoleCtrlHandler(NULL, FALSE)`**: the "ignore Ctrl+C" attribute is inherited, and hosts
that start processes in a new process group (terminals, IDEs, Claude Code's shell) set it —
the server then silently ignores Ctrl+C. Do not use `CREATE_NEW_PROCESS_GROUP` for the server
(it disables Ctrl+C too).

## Useful log lines (`server.log`)

| Line | Meaning |
|---|---|
| `ZNet.LoadWorld: W (W), save number N` | loading save N |
| `missing …/_main.N.db2` (N>0) | **world about to be regenerated — kill immediately, do not save** |
| `ZDOMan.LoadChunks - Starting to load 38.757 zdos from 4 Chunks` | objects loaded (locale thousands separator) |
| `Game server connected` | online |
| `… now N player(s)` / `is active with N player(s)` | player count |
| `Got character ZDOID from Name :` | a player spawned |
| `World save (1/5) … => Save number N` … `World save (5/5) done. Total time [52ms]` | save |
| `Removing orphan …` | old files deleted on load |
| `Net scene destroyed` | clean shutdown |

## Finding running servers

WMI `Win32_Process` where `Name='valheim_server.exe'` gives the full command line, from which
`-savedir`, `-world`, `-port`, `-logFile` and creative mode can be read (`WmiServerProcessLocator`).
