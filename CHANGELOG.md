# Changelog

## Valheim Server Manager 1.1.0

The app now speaks **English, Brazilian Portuguese and Spanish**.

- Every screen, message, notification and the `vsm` command line are translated. The language
  follows Windows; change it in **Protections & about → Language** (the CLI also honours
  `VSM_LANG`).
- The installer asks nothing new: it opens in your Windows language too.
- The About page links to Ko-fi (no account needed) and GitHub Sponsors.
- The repository is now in English, with a Portuguese README next to it.
- Fixed: opening the World page marked it as having unsaved changes, so leaving it asked
  to save changes nobody made.
- Player join/leave notifications no longer depend on the wording of the alert.

## Valheim Server Manager 1.0.0

First public release, with a Windows installer.

- **Installer.** Pick the folder and shortcuts; no administrator password. Updating keeps running
  servers running, and neither updating nor uninstalling touches worlds, backups or settings.
  Uninstalling waits until every server is stopped, so none is left running with no way to save.
- **The game and the server never share a world.** The game's save folder is refused even when
  reached through a junction, symbolic link, `subst` drive or 8.3 name. A save folder in OneDrive,
  Dropbox or Google Drive needs confirmation, and the default location leaves a synced Documents
  folder. Saves and backups cannot live inside the program folder.
- **Copying a world you already play** warns when the game is open and explains that the server
  gets its own copy.
- Safe start and stop, verified SHA-256 backups, guarded restores, creative mode that really turns
  off, an emergency stop when Valheim starts generating a new world over yours, duplicated-world
  repair and cheat-mark cleanup — see the [README](README.md) for the full list.
