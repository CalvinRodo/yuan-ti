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
| IT1R | 1 | Item types — 115 records of 20 bytes: weapon kind bits (1 melee, 2 missile, 4 shield, 8 uses ammunition, 16 thrown), weight, material, slot (0 none, 1 chest, 2 waist, 3 arms, 4 feet, 5 hand, 6 head, 7 neck, 8 cloak, 9 finger, 10 legs, 11 ammunition, 12 missile weapon), range, attacks, damage dice/sides/bonus, flags, class mask, AC. Types have no names; item records do. `DarkSun.Data.Items.ItemType`. |
| MAS | 33 | Compiled MAS files — same bytecode as GPL (all 33 decode with `gpl_parser.py --tag=MAS`) |
| NAME | 1 | Name table — 322 records of 25 bytes (8050 bytes), each a NUL-terminated Latin-1 name (items, spells as items, scenery: "Sling", "Long Sword", "Drainage grate"). DSUN.EXE copies name n from `table + 25·n`; an item's name number is the word at +0x12 of its 0x15-byte record. The bytes after each NUL (path fragments like `\NAMEIX\*.oda`, and the trailing `d8 00 ff`-style bytes earlier read as type flags) are leftovers from the tool that wrote the file. |
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
| APFM | 97 | A window's frame or slot (116 bytes): +0x08 number, +0x28/+0x2A size, +0x58 alignment; filled or drawn by the code (character boxes, item slots, bars). `DarkSun.Data.Ui.UiControl` |
| BMP | 105 | Bitmap images — general-purpose sprites and backgrounds |
| BUTN | 139 | A button (110 bytes; 130/163 with a text at +0x6E): +0x08 number, +0x28/+0x2A size, +0x58 alignment (icon across bits 8–9, down 10–11; text across 4–5, down 6–7: start, inset, centre, end), +0x64 its `ICON` (frames normal, highlighted, disabled, pressed). `UiControl` |
| BVOC | 111 | Background audio samples — `.VOC`-format ambient/music loops |
| CMAT | 2 | Color matrix — color transformation data (fade tables, etc.) |
| CPAL | 2 | Alternate color palette (may be compressed or remapped) |
| CSEQ | 1 | Cinematic sequence block |
| EBOX | 6 | A text box (168 bytes): +0x22/+0x24 size, +0x7E background colour (229, transparent), +0x80 text colour (199). `UiControl` |
| FONT | 1 | The game's font, `FONT` 100 in `RESOURCE.GFF` (the only one): word glyph count (256), word height (9), 4 zero bytes; 256 bytes colour map (pixel value → palette index, identity); 256 words, each character's glyph as an offset; a glyph is a word width (2–7) then height × width pixel bytes: 0 none, 254 the letter, 20 its shadow. Codes 0x80–0xFF are code page 437. The game sets map[254] to the text colour. `DarkSun.Data.Text.GameFont` |
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
| RDFF | 1 046 | The object's game record, numbered like its OJFF. A list of entries with 10-byte headers (load action, block, type, index, from, length): each followed by `length` bytes. Type 1 is an item record (21 bytes), type 2 a combat record (58), type 4 a character record (71: +0x3D the size, i.e. footprint half-width size & 7 and height size / 8), and types 3 and 5 reference a `NAME` or `IT1R` entry by index (length 0). 0xFF ends the list. Items are "record, IT1R ref, NAME ref, end" (755); creatures are "combat record, character record, …" (290; the rest is not decoded). `DarkSun.Data.Gff.Rdff`, `ItemRecord`, `CombatRecord`, `CharacterRecord`. |
| SCMD | 256 | Animation scripts — 0x48-byte header (32 start indexes: facing N/E/S/W × 8 actions) and 8-byte records: frame, delay (96 Hz ticks), flags (jump, stop, mirror, …), dx, dy, hot spot x, y, sound. Format in `dsun-exe-reference.md` §3. |
