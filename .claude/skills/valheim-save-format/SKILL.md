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

## `.chunk` contents (`ZDOMan.SaveChunk` / `ZDO.Save`)

`i16 version (41) | i32 count | ZDO[count]` — no ZDO ids (the game assigns new ones on load).
Reader/writer: `ChunkObjects` (`Worlds/WorldObjects.cs`), confirmed on every chunk of a real
world (file fully consumed). Each ZDO:

```
u16 flags        0x01 connection  0x02 floats  0x04 vec3  0x08 quats  0x10 ints  0x20 longs
                 0x40 strings  0x80 byte arrays  0x100 persistent  0x200 distant
                 0xC00 type (<<10)  0x1000 rotation  0x2000 small position
position         i16 x, i16 z (y = 0) with 0x2000, else f32 x, y, z (world coordinates)
i32 prefab       StableHash of the prefab name
rotation         with 0x1000: u16; if its high bit is set y = (v & 0x7FFF) / 2,
                 else a second u16 follows: x = v & 0x3FF, y = v >> 10 & 0x3FF, z = v >> 20, all / 2
connection       u8 type, i32 hash
maps             in the order above: count (1 byte, or 2 when the first has 0x80: ((b0&0x7F)<<8)|b1),
                 then (i32 key, value); string = 7-bit length + UTF-8; byte array = i32 length + bytes
```

Player-built pieces carry a `creator` long (StableHash "creator"); generated objects never do —
this is how to tell a base wall from a ruin wall of the same prefab.

## Stored items (`Inventory.Save` / `ItemDrop.ItemData.Save`, item version 109)

A container keeps its inventory in the byte array `items`, a dropped item keeps itself in
`itemData`. Same item layout, different header: `i32 version | u16 count | items…` for a container,
`u8 version | item` for a drop (a drop with 0 durability starts with the same four bytes as a
container, so tell them apart by the ZDO key, never by the content).

```
i32 durability*100 | u8 grid x | u8 grid y | u8 world level | u8 flags
flags: 1 picked up  2 equipped  4 quality (u16)  8 stack (u16)  0x10 variant (i32)
       0x20 crafter (i64 id + string name)  0x40 prefab hash (i32)  0x80 custom data
custom data: 1–2 byte pair count, then (string key, string value)…
u8 last: bit 0 = cheated
```

## The "cheated" mark (why identical items stop stacking)

`Inventory.FindFreeStackItem` only merges stacks with the same name, quality, **world level** and
**cheated flag**, so a marked item never joins an unmarked one — the symptom players report.
The game sets the mark on:

- a piece placed with the console's `nocost` (`Player.PlacePiece` → ZDO `cheated`), and on anything
  crafted at a station whose ZDO is marked;
- items spawned with `spawn`, or crafted from marked materials;
- **materials returned when a marked piece is dismantled or destroyed** (`Piece` drop code) — this
  is how a base built with `nocost` keeps producing marked materials long afterwards;
- creatures hit by a player with a marked damaging item, in god/ghost/fly mode, and their drops;
- anything destroyed or mined with a marked tool equipped (`Destructible`, `MineRock`).

The world modifier `nobuildcost` (the app's creative mode) does **not** mark anything: only the
console cheat does. `yesiuseddevcommandsbutiwantmyachievementsanyway 1` sets the player key
`bypasscheatchecks`, which stops new marks **for that player only** and does not clean what exists.
`WorldCheatMarks` clears the mark for everyone: the ZDO int and one bit per stored item, patched in
place so every other byte is preserved (`vsm inspect --cheats`, `vsm clean-cheat-marks`, app
"Limpar marcas").

## `_main.N.db2`

`i32 version | f64 net_time | i32 size | gzip(zone data) | RandEventSystem | PersistentEventSystem`.
Zone data: `i32 n | (i16 x, i16 z)[n]` **generated zones**, `i32 location_version`,
`i32 k | string global_keys[k]`, `bool locations_generated`,
`i32 m | (i32 location_hash, f32 x, y, z, bool placed)[m]`. `WorldDatabase` rewrites only the zone list.

### The second regeneration trap: zones not marked as generated

`ZoneSystem.SpawnZone` places vegetation, ore, pickables, locations and a `_ZoneCtrl` (the
enemy spawner of the zone, 64×64 m, `zone = floor((pos + 32) / 64)`) in every zone that is **not**
in the db2 list, whenever a player comes near — on top of whatever the chunks already hold.
Pairing chunks with the db2 of another save (as the 2026-09-16 recovery did) therefore duplicates
the world bit by bit as people explore: pickables that "come back", ore that breaks twice,
double enemy spawns (two `_ZoneCtrl` in a zone), and locations placed again **with another
rotation** (their contents are the original contents rotated by the angle difference).
`WorldRepair` removes the copies (first occurrence is the original; creator-owned objects are never
touched) and adds every zone that has a `_ZoneCtrl` to the list — CLI `vsm inspect --duplicates`,
`vsm repair-world`, app "Reparar mundo". Validated: the dedicated server loads the repaired save
(its gzip is accepted) and saves it again clean.

## `.chunk` header

First 6 bytes: `u16 version | u32 zdo_count`. The count equals the index entry's count, which is
what makes the index **rebuildable** from surviving chunks (`ChunkIndexRebuilder`, CLI
`vsm rebuild-index`). A rebuilt index was accepted by the game and loaded 38,757 objects.

## `_main.N.fwl2`

.NET `BinaryWriter` strings (7-bit length prefix, UTF-8):

```
i32 payload_length (file length - 4) | i32 version | string name | string seed_name
i32 seed | i64 uid | i32 worldgen_version | bool has_been_saved
i32 key_count | string keys…            e.g. "nobuildcost", "resourcerate 150", "preset combat_default:…"
i32 player_count | (platform_id, name, character, player_id)…   e.g. "Steam_7656…", "Viking", "Viking", "71F8…"
```

Creative mode = key `nobuildcost` present. `preset …` entries are history strings, not keys.

### Creating a world with a chosen seed

The menu's "new world" writes only `_main.0.fwl2` (50 bytes for a 5-char name): version 41,
seed = `StableHash(seed_name)`, uid = random int32 sign-extended, worldgen 2,
`has_been_saved` = 0, no keys, no players. Seed names are up to 10 ASCII letters/digits. A server
started on that folder logs "missing _main.0.db2" (expected here — `WorldInspector` reports
`NEW_WORLD_SEEDED`), generates the world from the seed and writes save 1. `WorldCreator.CreateSeeded`
reproduces the file byte for byte (test fixture in `WorldCreatorTests`).

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
   **Then run `vsm repair-world`** (or copy the zone list): a db2 from another save does not list
   the zones present in the chunks, and the game will generate them again on top.
   `vsm inspect --duplicates` must report 0 zones to mark.
5. Undelete tools only help if the MFT records were not reused; small files (`.chunks`, `.ok`,
   `.fwl2`) are MFT-resident and come back intact when they do.
