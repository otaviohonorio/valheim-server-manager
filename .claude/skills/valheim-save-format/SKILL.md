---
name: valheim-save-format
description: Valheim 1.0 world save format (worlds_local, _main.N.db2/.fwl2/.chunks/.ok, chunk files), how the game regenerates or deletes worlds, and how to recover a lost world. Use when inspecting, backing up, restoring or repairing Valheim saves, or when a world "reset".
---

# Valheim 1.0 world save format

Everything here was confirmed byte-for-byte against real Valheim 1.0.12 saves (world version 41).
Code lives in `src/ValheimServerManager.Core/Worlds/`.

## Folder layout

```
<savedir>\worlds_local\
  <World>\                         one folder per world; folder name == -world value (case matters)
    _main.N.fwl2                   metadata: name, seed, world keys, player history
    _main.N.db2                    world-wide data
    _main.N.chunks                 index: which chunk files (and revisions) belong to save N
    _main.N.ok                     4 bytes (29 00 00 00): "save N finished"
    zz_xx__f_r.chunk               ZDOs (terrain edits, buildings, chests…), one file per region revision
    cacheMinimap*                  client-only caches (safe to lose)
  <World>_backup_auto-yyyyMMdd-HHmmss\   Valheim's own backups (same layout)
```

- `N` grows by one on every save. Every save writes **new file names** (new N, new chunk
  revisions). Files of a finished save are never rewritten, so a complete set can be copied
  while the server runs.
- On load, Valheim deletes older `_main.*` sets and unreferenced chunk revisions
  ("Removing orphan save file / CHUNK file" in the log). Deleted with `File.Delete` — they do
  **not** go to the Recycle Bin.
- Pre-1.0 `.db`/`.fwl` pairs are converted on first 1.0 save.

## The catastrophic case

If the highest `N` has `.fwl2` but no `.db2`, the log shows
`ZNet.LoadWorld: X (X), save number N` then `missing …/_main.N.db2`, and Valheim **silently
generates a brand-new world with the same seed**, saves it as N+1, and on the next load deletes
the real world's chunks as orphans. `missing _main.0.db2` on a never-played world is normal.

Rule: before loading, the highest save number present must have all four files, and every chunk
in its index must exist with the right object count (`WorldInspector`).

## `_main.N.chunks` (little endian)

```
header 10 bytes : u16 version | u32 total_zdos | u32 entry_count
entry  11 bytes : i8 x | i8 z | u8 flag | u32 revision | u32 zdo_count
```

- Entries sorted by (x, z). `total_zdos` = sum of entry counts.
- File for an entry: `{(byte)z:x2}_{(byte)x:x2}__{flag}_{revision}.chunk` (lowercase hex).
  Example: x=0x1e z=0x20 flag=1 rev=11 → `20_1e__1_11.chunk`.

## `.chunk` header

First 6 bytes: `u16 version | u32 zdo_count`. The count equals the index entry's count, which is
what makes the index **rebuildable** from surviving chunks (`ChunkIndexRebuilder`, CLI
`vsm rebuild-index`). A rebuilt index was accepted by the game and loaded 38,757 objects.

## `_main.N.fwl2`

.NET `BinaryWriter` strings (7-bit length prefix, UTF-8):

```
i32 payload_length (file length - 4) | i32 version | string name | string seed_name
i32 seed | i64 uid | i32 worldgen_version | bool flag
i32 key_count | string keys…            e.g. "nobuildcost", "resourcerate 150", "preset combat_default:…"
i32 player_count | (platform_id, name, character, player_id)…   e.g. "Steam_7656…", "Viking", "Viking", "71F8…"
```

Creative mode = key `nobuildcost` present. `preset …` entries are history strings, not keys.

## Identifying a player's base

Prefabs are stored as `StableHash` ints (see `StableHash.Compute`), not strings — a text search
finds nothing. Wood walls/floors also exist in generated ruins, so only count player-only pieces
(`piece_workbench`, `piece_chest_wood`, `wood_beam`, `portal_wood`, `smelter`…) —
`PlayerBuildScanner`. A regenerated world has zero of them.

## Recovering a lost world

1. Stop everything that could touch the folder (game and server). Copy `worlds_local` elsewhere
   **first** — every load deletes more "orphans".
2. `Player.log` / `server.log`: find `missing …_main.N.db2` to learn the time and the lost save.
3. Look for surviving real chunks (older revisions often survive, e.g. `20_1e__1_11.chunk`), and
   for `*_backup_auto-*` folders and manager backups.
4. If chunks survived but the index/db2 did not: rebuild the index from the chunk headers, pair it
   with a `.fwl2`/`.db2` of the same world (a regenerated one works; world keys and boss
   progress may be lost), give it a fresh N, validate with `vsm inspect --pieces`, load it in a
   **separate** save dir.
5. Undelete tools only help if the MFT records were not reused; small files (`.chunks`, `.ok`,
   `.fwl2`) are MFT-resident and come back intact when they do.
