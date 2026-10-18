# DSUN Engine Roadmap (Godot 4, C#)

Plan for a new, moddable engine for **Dark Sun: Shattered Lands**: Godot 4 with
C#, reading the player's original game files at runtime. The priorities are a
game that works and is easy to modify, with modern quality-of-life features.
Matching the original exactly is a secondary goal: keep the behaviour that
players would notice (rules, RNG-driven outcomes, script semantics), but
don't block progress on byte-for-byte matching.

This follows the approach of ScummVM, GemRB and OpenMW: the data and the GPL
scripts are the game; the engine is new code; the original executable is a
reference for what the data and scripts don't say.

Related docs: `gff-data-reference.md` and `dark_sun-game-data.md` (file
formats), `dsun-exe-reference.md` (GPL bytecode, executable layout),
`dsun-decompilation.md` (Ghidra pipeline). Status is as of 2026-10-18; items
marked *unverified* still need checking.

---

## 0. Starting point

What already exists, and what the engine can reuse:

| Area | State | Source to port |
|------|-------|----------------|
| GFF container | Parsed | `src/gff.py` |
| Images (`BMP `, `TILE`, `ICON`, `PORT`, `WALL`) and palettes (`PAL `, `CPAL`) | Decoded | `src/image.py`, `gff-data-reference.md` §4a–4b |
| Region maps (`RMAP`, `GMAP`, `ETAB`) and object sprites (`OJFF` → `BMP`) | Rendered | `src/region_renderer.py` |
| Text (`TEXT`, `MERR`, `NAME`, `SPIN`) | Parsed | `src/text_parsers.py` |
| Items, monsters, spells (`IT1R`, `RDFF`, `SPIN`) | Partly parsed | `src/ds1_parsers.py` |
| Music (`CSEQ`/`GSEQ`/`LSEQ`/`PSEQ`, `MSEQ`; XMIDI) | Converts to MIDI | `src/xmi_tool.py` |
| Sound samples (`BVOC`) | Extracted as Creative `.VOC` | `src/precompute.py` (audio step) |
| GPL scripts (217 `GPL `, 33 `MAS `) | Bytecode fully documented, disassembler | `src/gpl_parser.py`, `dsun-exe-reference.md` |
| Executable | Overlays unpacked; 2 402 functions decompiled; runtime, GPL interpreter and all handlers named | `make decomp` |
| Not yet decoded | `SCMD` animation scripts, `FONT`, UI resources (`BUTN`, `DBOX`, `WIND`, `MENU`, …), cinematics (`ACF `, `BMA `), most of `RDFF` | |

The GOG install ships DOSBox, so the original game is always available to
compare against by eye.

---

## 1. Architecture

- [x] **Repository layout**: `engine/` beside the Python tools, which stay as
      they are: the Godot project in `engine/game/`, the libraries in
      `engine/src/`, tests in `engine/tests/`, all in `engine/DarkSun.sln`
      (.NET 10, Godot 4.7.2). See `engine/README.md`.
- [x] **Three layers**, so that most code can be tested and reused without
      Godot:
  - `DarkSun.Data` (plain C# library): GFF reader and format decoders. Has no
    Godot dependency, and returns plain arrays and records.
  - `DarkSun.Game` (plain C# library): game state, rules, the GPL virtual
    machine, saving. Talks to the presentation layer only through
    interfaces (e.g. `IScriptHost` for what GPL commands ask the engine to
    do).
  - `engine/` (Godot): scenes, rendering, UI, input, audio. It turns `Data`
    output into Godot textures and audio, and drives `Game`.
- [x] **Game files**: the player points the engine at their install on first
      run (`GameInstall`; also `DARKSUN_DIR` and the repository's `dark_sun/`).
      No original assets in the repository or in builds.
- [ ] Find GOG and Steam installs automatically.
- [ ] **Asset cache**: decode to Godot `Image`/`ImageTexture` at load time
      instead of converting files on disk, so that the original files stay the
      single source of truth and mods can replace individual resources.
- [ ] **Mod overrides**: a folder of loose files (PNG, JSON, recompiled GPL)
      that takes priority over GFF resources by tag and number. Design the
      lookup now, even if the mod tools come later.
- [x] **Tests**: xUnit tests for `Data` and `Game`, run by `make engine-test`
      without the editor. Borland's RNG (`BorlandRandom`) is the first rule
      ported.
- [ ] **Check web export** support for Godot C# at the version you choose, if
      a browser build matters; otherwise target desktop only.

---

## 2. Milestone 1: data layer

Goal: every resource the first playable region needs, loaded in C#.

- [x] Port `gff.py`: header, index block, resource lookup by tag and number
      (`DarkSun.Data.Gff`: `GffFile`, and `GffSet` for searching several files
      or a mod folder in order).
- [x] Port the image decoder and palettes; produce RGBA for Godot
      (`DarkSun.Data.Images`; `Textures` in the Godot project).
- [x] Port `RMAP`, `GMAP`, `ETAB`, `OJFF` and `WALL` lookups from
      `region_renderer.py` (`DarkSun.Data.Regions.Region`; `RegionRenderer`
      draws a whole region in software).
- [ ] Find the palette the game uses for region 255 (`RGNFF.GFF`): there is no
      `GPLDATA.GFF` `PAL ` 255, so the engine falls back to the first `PAL ` in
      `RESOURCE.GFF`, as `precompute.py` does.
- [ ] Port the text parsers and the `NAME` table.
- [x] **Check against the Python tools**: `make engine-compare` hashes every
      resource, image frame and palette in the game files with both
      (`tools/compare/gff_dump.py`, `engine/tools/DarkSun.Dump`) and requires
      identical output; on 2026-10-18, 15 400 resources, 13 895 frames, 57
      palettes and the renders of all 33 regions matched.
- [x] Render every region from C# and diff it against `region_renderer.py`'s
      output (part of `make engine-compare`).

Done when every region file renders identically in both tool chains.

---

## 3. Milestone 2: walk a region

Goal: a party sprite walking around a region at the original scale.

- [x] Debug overlay of the `GMAP` flags (F1 in the region view).
- [x] Region scene (`scenes/region_view.tscn`): the ground baked into one
      2048 × 1568 texture from `RMAP` and `TILE`; objects and walls as sprites
      in the original draw order. Scroll, zoom, and PgUp/PgDn through all 33
      regions.
- [ ] Y-sort objects and walls with characters, once characters move.
- [ ] Camera following the party, with integer scaling to any window size
      (the original is 320 × 200, *unverified*).
- [x] Movement and collision from the `GMAP` bits (0x40 blocks movement,
      0x80 blocks sight, 0x20 occupied at run time; `Region.IsBlocked`,
      `Region.BlocksSight`); click-to-move with A* pathfinding
      (`DarkSun.Game.Movement`: `Pathfinder`, 8-way without cutting corners,
      and `Walker`, one walk cycle per cell).
- [x] Creature footprints (`Footprint`: half-width and height cells, as
      `footprint_blocked` tests them) and occupied cells (`Occupancy`, the
      engine's form of `GMAP` 0x20): characters never overlap, wait for each
      other, and detour when that is short.
- [ ] Creature sizes from the game data: byte 0x3D of the 0x47-byte creature
      records (table `DS:1661`), whose source file is not yet traced; until
      then sizes are estimated from sprite width.
- [ ] Compare with DSUN.EXE's own pathfinding (not yet located) and walking
      speed in play (walk records move 4 px per 7 ticks: about 3.4 cells/s).
- [x] Decode `SCMD` animation scripts (`DarkSun.Data.Animation.Scmd`,
      `DarkSun.Game.Animation.ScmdAnimator`, 96 Hz ticks); objects in the
      region view animate.
- [x] Character sprites: walking and standing by direction from the `SCMD`
      header slots; the creature hot spot (width / 2, height). Any of the 120
      walking object sprites can be chosen (Tab) until party characters exist.
- [x] Depth-sort objects, walls and characters (Godot y-sort on each
      sprite's `ETAB` point or bottom edge; DSUN.EXE draws row by row,
      `draw_objects_and_walls`, `1DBD:0002`).
- [ ] Play `SCMD` sound effects (record byte 7).
- [ ] Region transitions: map edges and doors to other `RGNxx` files.
- [ ] Debug overlay: grid, passability, object numbers, hovered tile.

---

## 4. Milestone 3: GPL scripts running

Goal: the world reacts. Most quest, dialogue and event logic lives in GPL, so
this milestone brings most of the game's content.

- [ ] Port the GPL virtual machine to `DarkSun.Game`: fetch/dispatch, the
      expression evaluator (left to right, no precedence), operand decoding,
      variables (`lnum`/`gnum` int16, `lbignum`/`gbignum`/`acc` int32,
      strings, flags), call frames and cross-file calls, with the original
      limits.
- [ ] Every opcode that affects the world goes through `IScriptHost`. Start
      with logging stubs, so scripts run end to end before the engine supports
      everything.
- [ ] **Find how scripts are triggered** (reverse engineering, §10): which
      script runs on region entry, on talking to or using an object, on
      stepping on a tile, on timers.
- [ ] **Golden tests**: disassemble every script with `gpl_parser.py`, and check
      the C# VM decodes the same instructions.
- [ ] Script debugger in the engine: current script, program counter,
      variables, breakpoints. Cheap to build here, and very useful for
      modders.

---

## 5. Milestone 4: user interface and dialogue

- [ ] Text windows, dialogue with portraits (`PORT`), and GPL `MENU` choices.
- [ ] Decide the UI approach: rebuild the screens as Godot `Control` scenes
      using the original art (decoded `WIND`, `BUTN`, `DBOX`, `FONT`), so that
      layout is resolution-independent and easy to change. The original
      layouts are a reference, not a constraint.
- [ ] Main menu, options, and a journal or quest log (new; fed by GPL
      variables and dialogue history).

---

## 6. Milestone 5: characters, items and party

- [ ] Character data model: races, classes, stats, levels, psionics
      (reverse engineering, §10).
- [ ] Items from `IT1R` and `NAME`; inventory, equipping, containers, shops.
- [ ] Character sheet and inventory screens.
- [ ] Party creation, plus a pre-made party option.

---

## 7. Milestone 6: combat

The largest reverse-engineering task, because its rules live in the
executable, not in the data.

- [ ] Find and document the combat code (§10): initiative and turn order,
      to-hit and damage, armour class, movement points, spells and psionic
      powers, and saving throws.
- [ ] Monster data from `RDFF` combat objects (`ds1_parsers.py` has a start).
- [ ] Monster AI: target choice and spell use.
- [ ] Tactical combat scene on the region map, with clear feedback: hit chance
      shown, a combat log, damage numbers.
- [ ] Keep the rules in data (JSON tables) wherever possible, so they're easy to
      tune and mod.

---

## 8. Milestone 7: audio

- [ ] Music: XMIDI → MIDI (port `xmi_tool.py`), played through a C# SoundFont
      synthesizer (e.g. MeltySynth) with a bundled freely licensed General MIDI
      SoundFont; an OPL (AdLib) emulation option later for the original sound.
- [ ] Sound effects: decode `BVOC` (Creative `.VOC`, as `precompute.py`
      already extracts) and any other sample resources to Godot
      `AudioStreamWAV`.
- [ ] Which music plays where: find the region, combat and event music
      selection (GPL commands or engine code).

---

## 9. Milestone 8: saving, cinematics, full game

- [ ] Save and load in the engine's own format (JSON plus compressed state):
      GPL variables, party, region states. Save at any time, with autosaves.
- [ ] Optional: import original save games (format needs reverse engineering).
- [ ] Cinematics from `CINE.GFF` (`ACF `, `BMA `, sequences); can be skipped
      until late.
- [ ] Full playthrough with a checklist of every region, quest and ending;
      compare doubtful behaviour against the original in DOSBox.

---

## 10. Reverse-engineering backlog

Reverse engineering is now on demand: pick the item the next milestone needs,
find it in the decompiled code, document it in `dsun-exe-reference.md`, and
name the functions in `tools/ghidra/symbols.tsv`.

| Needed for | What to find | Where to start |
|------------|--------------|----------------|
| ~~M2~~ | ~~`SCMD` animation script format~~ | Done 2026-10-18: `dsun-exe-reference.md` §3 |
| ~~M2~~ | ~~`GMAP` flag bits~~ | Done 2026-10-18: `dsun-exe-reference.md` §3 |
| M2 | Region exits | door GPL scripts; the region change in `main`'s loop |
| M2 | `SCMD` record flags 0x10 and 0x20, header bytes 0x40–0x47, the action numbers 2–6 | `1DF3:2BCF`; `896E:0C92`, `87B3:0AE1`; the combat code's header lookups |
| M3 | Script triggers (region entry, object use, tiles, timers) | callers of `gpl_run` (`043D:0001`); `main`'s loop (`dsun-decompilation.md` §4) |
| M3 | What each world-affecting GPL command does | the 114 named `gpl_op_*` handlers and their callees |
| M4 | `FONT` and UI resource formats | text-drawing and window code; `WIND`/`BUTN` loaders |
| M5 | Character rules and level tables | character sheet and creation code; `GPLX` tables (`DS:4D7A`, `DS:537A`) |
| M6 | Combat rules and AI | functions that call `rand` (18 in 9 segments; 6 in `612E`, 3 in `6466`; *unverified* that these are combat) |
| M7 | Music and sound selection | callers of the sound driver; segment `0980` has the most port I/O (*unverified*: sound or video driver) |
| M9 | Original save format | `303C` file-library callers that write files |

Pipeline improvements, worth doing only when a backlog item needs them:

- [ ] Struct types in `symbols.tsv` (game objects, characters, region state),
      so the decompiled code reads in terms of fields.
- [ ] Jump-table recovery for the 61 functions where the decompiler failed,
      when one of them is on the path of a backlog item.
- [ ] Register-argument prototypes for the Borland helpers, to clean up
      `unaff_` noise.

What the engine does **not** need from the executable: the DOS file library
(`303C`), the EMS/XMS memory manager (`13A7`), the overlay manager (`3653`),
and the hardware drivers beyond what they reveal about formats. Godot replaces
all of them.

---

## 11. Quality-of-life features

These are the point of the project. Plan them into the milestones above, rather
than leaving them all for the end:

| Feature | Milestone |
|---------|-----------|
| Any resolution, integer or smooth scaling, widescreen view of the map | M2 |
| Fast-forward, adjustable game and combat speed | M2, M6 |
| Remappable keys, gamepad support, mouse wheel zoom | M2 |
| Highlight interactive objects, tooltips on hover | M2–M4 |
| Dialogue history, quest journal | M4 |
| Inventory sorting, item comparison, stack handling | M5 |
| Hit chances and combat log, undo of a move before it's confirmed | M6 |
| Save anywhere, autosave, multiple save slots with screenshots | M8 |
| Script debugger and resource viewer, mod folder | M3, §1 |
| Original-behaviour options (original UI scale, original music) | throughout |

---

## First steps

1. ~~Create `engine/` with the Godot project, the libraries and tests (§1).~~
   Done 2026-10-18.
2. ~~Port `gff.py` and the image decoder, compared against the Python tools
   (M1).~~ Done 2026-10-18.
3. ~~Render one region in Godot and scroll around it (start of M2).~~ Done
   2026-10-18: every region, from the start screen's "View regions".
4. In parallel: ~~decode `SCMD`~~ (done 2026-10-18), and find the script
   triggers (§10).
