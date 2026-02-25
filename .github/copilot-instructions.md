# Copilot Instructions

## Project Overview

**yuan-ti** is a reverse-engineering toolkit and web viewer for DOS-era SSI RPG game files — primarily *Dark Sun: Shattered Lands* (DS1), *Dark Sun: Wake of the Ravager* (DS2), and *Al-Qadim: The Genie's Curse*. All are GOG installations living alongside the Python source in this repo.

## Architecture

```
GFF binary files (dark_sun/, dark_sun_2/data/, al-qadim/data/)
    └── src/gff.py              # Core GFF parser — GffFile, ResourceDescriptor, GffiTable
         ├── src/image.py       # BMP/CBMP/TILE/ICON/PORT/WALL image decoder (RLE, PLAN, PLNR)
         ├── src/gpl_parser.py  # GPL bytecode disassembler (GPL / MAS resources)
         ├── src/ds1_parsers.py # Structured binary parsers: IT1R items, RDFF monsters, SPIN spells
         └── src/region_renderer.py  # Compose RMAP tiles + ETAB objects + WALL sprites → PIL image

src/server.py   ← thin Flask entry point (60 lines); just imports + Blueprint registrations
src/config.py   ← GAMES dict, tag constants (IMAGE_TAGS, MUSIC_TAGS, …), URL helpers
src/gff_cache.py  ← load_gff() + find_palette() (shared by all route blueprints)
src/text_parsers.py  ← decode_text(), parse_name_resource()

src/routes/         ← one Blueprint per feature domain
    gff.py          /api/gffs, /api/gff/<name>/resources, /api/gff/<name>/pals, /api/pals/all, raw
    images.py       /api/gff/<name>/image/…, /api/gff/<name>/pal-png/…, image info
    maps.py         /api/gff/<name>/rmap/…, /api/gff/<name>/map-render
    text.py         /api/gff/<name>/texts, /api/gff/<name>/spells (GFF-level spell listing)
    audio.py        /api/gff/<name>/music, audio, midi; /api/sfx, /api/ogg-files, /api/xmi-files
    cinematics.py   /api/cinematics, /api/cinematic/<filename>
    gamedata.py     /api/games, /api/items, /api/monsters, /api/spells (high-level game data)
    scripts.py      /api/scripts, /api/script/<tag_url>/<number>

src/web/        ← vanilla-JS SPA (no build step); app.js routes between view-*.js modules
```

Multi-game support is driven by the `GAMES` dict in `src/config.py`; adding a new game means adding an entry there.

## Key Concepts

- **GFF file**: a container of typed, numbered resources. Each resource has a 4-byte ASCII **tag** (e.g. `BMP `, `GPL `, `RMAP`) and an integer **number**. All known tags are documented in `GFF_CHUNK_TYPES` in `src/gff.py`.
- **GffFile API**: `gff.get_resource_data(tag, number) → bytes`, `gff.describe_resources() → list[ResourceDescriptor]`, `gff.replace_resource(tag, number, data)`.
- **Palette**: images are paletted (256-colour VGA). A `PAL ` resource (or an external `.PAL` file) must be loaded separately and passed to `ImageReading` / `ImageFrame.to_pil()`.
- **GPL scripting**: game logic lives in `GPLDATA.GFF`. `GPL ` resources are local scripts; `MAS ` resources are global. Use `gpl_parser.disassemble_gpl(data)`.
- **RDFF objects** (in `SEGOBJEX.GFF` / DS2's `OBJEX.GFF`): 10-byte header parsed by `_RDFF_HDR_FMT`; `type == 1` → item (`_ITEM1R_FMT`, 20 bytes), `type == 2` → combat/monster (`_COMBAT_FMT`, 58 bytes). Helpers in `ds1_parsers.py`.

## Game-by-Game Differences

### DS1 (`dark_sun/`)
- Region files: `RGNxx.GFF` (2-digit hex suffix, e.g. `RGN02.GFF`)
- Objects in `SEGOBJEX.GFF`; no standalone cinematics or music directory
- `CINE.GFF` holds in-engine cutscene data; `DARKRUN.GFF` is the launcher config

### DS2 (`dark_sun_2/data/`)
- Region files: `RGN001.GFF` – `RGNxxx.GFF` (3-digit zero-padded)
- Objects in `OBJEX.GFF` (not `SEGOBJEX.GFF`)
- Standalone FLIC cinematics (`1.FLI` – `5.FLI`) at the data root; exposed via `cinematic_dir`
- XMIDI music lives in `dark_sun_2/data/MUSIC/`; accessible via `music_dir`
- Save-game state in `CHARSAVE.GFF` / `DARKSAVE.GFF`

### Al-Qadim (`al-qadim/data/`)
- **No GFF files exist** — the game uses a completely different proprietary container format
- World/level data is in `WORLDS/*.LIB` (binary offset-table format, not GFF-compatible)
- Images are in `ENGINE/PIC.LIB` and `ENGINE/SYSFILE.LIB` (same `.LIB` format)
- GPL scripts are standalone `GPLS/GPLxx.COD` files, not embedded in a GFF container
- `gff_tool.py` and `GffFile.create()` **will fail** on `.LIB` files; Al-Qadim support requires a separate `.LIB` parser (not yet implemented)
- `GAMES['al-qadim']['supported']` is `True` but GFF-based routes return 404 — treat it as work-in-progress
- Audio SFX is in `SFX/`; XMI music paths point to `SFX/` (no separate `MUSIC/` dir)

## Developer Workflows

```bash
uv sync                   # install deps (flask, pillow) into .venv
make serve                # start Flask viewer → http://localhost:5000
make list-resource        # list dark_sun/RESOURCE.GFF contents
make list-gff GFF=RGN02.GFF
make extract-gff GFF=GPLDATA.GFF
bash extract.sh           # extract ALL GFF files in dark_sun/ → out/
make install-ffmpeg       # needed for VOC → MP3 audio conversion
```

All Python invocations use `uv run python3`; never call `python` or `python3` directly.

## Conventions & Patterns

- **Tags are 4 bytes, space-padded**: `'BMP '` not `'BMP'`. Always use the padded form when calling `get_resource_data`.
- **Little-endian throughout**: all GFF integers are `struct.unpack_from('<I', ...)` / `struct.unpack_from('<H', ...)`.
- **New parsers follow ds1_parsers.py**: define a `_FMT` / `_SIZE` / `_FIELDS` triple at module level; return `list[dict]` from a function that takes a `GffFile`.
- **Server routes follow the pattern**: accept `?game=dark_sun` query param, call `resolve_game_dir(game_id)` from `config.py`, load GFF via `load_gff()` from `gff_cache.py`, return `jsonify(...)`. Each route domain lives in its own Blueprint under `src/routes/`.
- **Adding a new route**: create or add to a `src/routes/<domain>.py` Blueprint, then register it in `server.py` with `app.register_blueprint(<module>.bp)`.
- **Image decoding pipeline**: `ImageReading.read_frames(data) → list[ImageFrame]`, then `frame.to_pil(palette)` for a PIL Image. Palette colours are **6-bit** by default (DOS VGA); use `pal_bit_depth=8` only when the source is 8-bit.
- **region_renderer.py** needs a list of `GffFile` objects (RESOURCE + the region RGNxx file) because assets can span files; use `_get_resource(gffs, tag, number)` to search them in order.
- **libgff / soloscuro**: reference C implementations in `libgff/` and `soloscuro/`; Python code is a port from Java/C originals by John Glassmyer. When behaviour is unclear, cross-check `libgff/src/`.

## Key Files

| File | Purpose |
|------|---------|
| `src/gff.py` | GFF binary container parser (the core) |
| `src/image.py` | Image frame decoder (RLE/PLAN/PLNR) |
| `src/gpl_parser.py` | GPL bytecode disassembler |
| `src/ds1_parsers.py` | IT1R items, RDFF combat objects, OJFF object headers, SPIN/MONR parsers |
| `src/region_renderer.py` | Region map compositor |
| `src/config.py` | GAMES dict, tag constants, URL helpers — single source of truth for config |
| `src/gff_cache.py` | `load_gff()` + `find_palette()` — shared by all route blueprints |
| `src/text_parsers.py` | `decode_text()`, `parse_name_resource()` |
| `src/server.py` | Flask app + Blueprint registrations (60 lines) |
| `src/routes/` | One Blueprint per feature: `gff`, `images`, `maps`, `text`, `audio`, `cinematics`, `gamedata`, `scripts` |
| `src/web/app.js` | SPA entry point and view routing |
| `libgff/src/` | Reference C implementation |
