# Dark Sun: Shattered Lands

**GOG folder:** `dark_sun/`  
1993 SSI/Strategic Simulations isometric RPG based on the AD&D Dark Sun setting.
Uses the SSI GFF (Generic File Format) container for all game data.
All GFF files live directly in the `dark_sun/` directory; there are no subdirectories.

---

## CINE.GFF
Cutscene / cinematic resources. Drives the intro, ending, and in-game FMV sequences.

### Resource Types
| Tag | Count | Description |
|-----|------:|-------------|
| ACF | 13 | Cinematic scripts (16-bit word bytecode; `dsun-exe-reference.md` §7). 1–8 intro, 9–10 arrival, 11–13 ending |
| ADV | 13 | AIL audio driver — Miles Sound System `.ADV` driver files |
| BMA | 11 | Cinematic animation: BMP-format images back to back, split by the matching ACF's op-1 table; frames draw over the previous one |
| BMP | 19 | Bitmap images — cinematic backgrounds and frame graphics |
| CSEQ | 1 | Cinematic sequence — playback control block |
| GSEQ | 4 | Global sequence — shared timing/sync sequence used across cutscenes |
| LSEQ | 4 | Local sequence — per-scene sequencing data |
| MERR | 31 | Error/message text strings (same format as in RESOURCE.GFF) |
| PAL | 8 | VGA 256-color palettes for the cinematic scenes |
| PSEQ | 4 | Playback sequence — audio/visual sync block |

---

## DARKRUN.GFF
Single-entry launcher configuration file. Loaded by `DARKRUN.EXE`.

### Resource Types
| Tag | Count | Description |
|-----|------:|-------------|
| ETME | 1 | Event timer entry — timing/launch configuration record |

---

## GPLDATA.GFF
Gameplay data: compiled game scripts, item/entity name table, UI portraits, wall graphics. 
This file is loaded alongside region GFFs when rendering a region (supplies WALL images and GPL scripts).

### Resource Types
| Tag | Count | Description |
|-----|------:|-------------|
| GFFI | 9 | GFF index blocks — internal secondary-table index entries |
| GPL | 217 | Compiled GPL files — the game's bytecode scripting language (items, containers, NPCs, world events, dialogue). Fully decoded: see `dsun-exe-reference.md` / `src/gpl_parser.py` |
| GPLI | 1 | GPL index — lookup table mapping GPL resource numbers to their scripts |
| GPLX | 1 | GPL tables, 1269 bytes, loaded by overlay 7372: `0x000–0x3DD` → DS:4D7A, `0x3DE–0x440` → DS:537A (stat field types), 10 bytes → DS:53F7, `0xAA` bytes after |
| IT1R | 1 | Item type 1 record — item constants / configuration table |
| MAS | 33 | Compiled MAS files — same bytecode as GPL (all 33 decode with `gpl_parser.py --tag=MAS`) |
| NAME | 1 | Name table — 256 null-terminated entries, each with: display name `\x00` path-fragment `\x00` 2–3 type/flags bytes ending in `\xff`. Used to look up canonical names for items and entities. Many entries carry a leading `=` byte (0x3D) that may act as a category flag. |
| PAL | 45 | VGA 256-color palettes shared across gameplay scenes |
| PORT | 112 | Portrait bitmaps — character and NPC portrait images used in dialogue and character sheets |
| WALL | 664 | Wall tile graphics — isometric wall-edge sprites. Resource numbers are encoded as `regionNumber × 100 + wallIndex − 1` (wallIndex = lower 5 bits of the GMAP byte). |

---

## RESOURCE.GFF
The main global resource file. Contains UI chrome, audio drivers, spell data,
icon sprites, text strings, and palettes. Almost every other system in the game
references resources here — palettes are looked up here when not found in the
current GFF, and spell ICON images are numbered from 21 000 upward.

### Resource Types
| Tag | Count | Description |
|-----|------:|-------------|
| ACCL | 1 | Accelerator table — keyboard shortcut definitions |
| ADV | 17 | AIL audio drivers — Miles Sound System `.ADV` driver blobs |
| APFM | 97 | Application frames — top-level UI window/layout containers |
| BMP | 105 | Bitmap images — general-purpose sprites and backgrounds |
| BUTN | 139 | Button resources — clickable UI button definitions |
| BVOC | 111 | Background audio samples — `.VOC`-format ambient/music loops |
| CMAT | 2 | Color matrix — color transformation data (fade tables, etc.) |
| CPAL | 2 | Alternate color palette (may be compressed or remapped) |
| CSEQ | 1 | Cinematic sequence block |
| EBOX | 6 | Edit-box UI resources |
| FONT | 1 | Font resource |
| GFFI | 15 | GFF index blocks |
| GSEQ | 23 | Global audio/sync sequences |
| ICON | 292 | Icon images — spell and item icons. Spell icons are at number `21 000 + SPIN_number`. |
| LSEQ | 23 | Local audio sequences |
| MERR | 31 | Error / message text strings (null-terminated latin-1) |
| MONR | 1 | Monster/monologue record |
| PAL | 2 | VGA 256-color palettes (primary gameplay palettes) |
| PSEQ | 23 | Playback sequences |
| RDAT | 45 | Runtime data blocks — purpose unclear; may be pre-computed lookup tables |
| SPIN | 180 | Spell information — null-terminated latin-1 text, format `"Spell Name: Description"`. Numbered 0–179. Paired ICON is at `21 000 + number`. |
| TEXT | 60 | General text strings — null-terminated latin-1 (item descriptions, area names, etc.) |
| VECT | 1 | Vector resource — likely a UI vector path or polygon table |
| WIND | 27 | Window resources — UI window layout definitions |

---

## RGN02.GFF … RGN2D.GFF, RGNFF.GFF  *(33 region files)*
One file per explorable region of the game world. Region filenames use two-digit
hexadecimal identifiers (`02` … `2D` plus the special `FF`).
Each file is self-contained: the RMAP provides a 128 × 98 tile index that the
renderer looks up against the TILE sprites, and the ETAB/GMAP layers add 3-D
wall edges and placed objects (resolved via OJFF/WALL in SEGOBJEX.GFF and GPLDATA.GFF).

### Resource Types (all region files share this identical structure)
| Tag | Count | Description |
|-----|------:|-------------|
| ETAB | 1 | Object entry table — array of 8-byte records: `int16 x, int16 y, int8 elevation, uint8 flags (bit7=mirrored), int16 −ojffNumber`. Each record places one game-world object sprite at a pixel position. OJFF numbers reference SEGOBJEX.GFF. Flags and run-time use: `dsun-exe-reference.md` §3. |
| GFFI | 1 | GFF index block |
| GMAP | 1 | Region map flags — 128 × 98 byte array (one byte per tile). Lower 5 bits = wall index (0 = no wall); bit 5 (0x20) occupied by a creature (run time only); bit 6 (0x40) blocks movement; bit 7 (0x80) blocks line of sight (see `dsun-exe-reference.md` §3). Wall index encodes `wallNumber = regionNumber × 100 + wallIndex − 1` into GPLDATA.GFF's WALL resources. |
| RMAP | 1 | Region tile map — 128 × 98 byte array (one byte per tile). Each byte is the TILE resource number for the ground-layer graphic. |
| TILE | 152–254 | Tile graphics — 16 × 16 pixel indexed bitmaps for the ground layer. Typically ~190–254 unique tiles per region. Require a PAL to render. |

**Region tile dimensions:** 128 columns × 98 rows = 12 544 tiles, 2 048 × 1 568 pixels fully rendered.

**Palette:** `GPLDATA.GFF` `PAL ` numbered like the region's `GMAP`. Region 255 (`RGNFF.GFF`) is a test map holding every item and effect; it has no such palette (`GPLDATA.GFF` has `PAL ` 1–45 only) and uses `PAL ` 1.

---

## SEGOBJEX.GFF
Segment / object / extra sprites — the large sprite sheet for all game-world objects:
characters, monsters, items, doors, containers, and environmental props.
Region rendering requires SEGOBJEX.GFF + GPLDATA.GFF alongside the region GFF.

### Resource Types
| Tag | Count | Description |
|-----|------:|-------------|
| BMP | 2 419 | Bitmap sprite frames — the actual pixel data for all in-world objects. Referenced by OJFF entries via their `bmpNumber` field. |
| GFFI | 4 | GFF index blocks |
| OJFF | 2 775 | Object sprite definition — 16-byte record: `+0 flags, int16 anchor x at 2, int16 anchor y at 4, int8 y adjustment at 0x0A, uint16 bmpNumber at 0x0C, int16 scmdNumber at 0x0E` (0 = not animated). ETAB entries in region files reference these by number. |
| RDFF | 1 046 | Resource definition — paired with OJFF entries at the same resource numbers. Size varies (61, 159, 312, or 455 bytes). Likely stores collision boxes, pathfinding hints, or extra metadata for each object. |
| SCMD | 256 | Animation scripts — 0x48-byte header (32 start indexes: facing N/E/S/W × 8 actions) and 8-byte records: frame, delay (96 Hz ticks), flags (jump, stop, mirror, …), dx, dy, hot spot x, y, sound. Format in `dsun-exe-reference.md` §3. |
