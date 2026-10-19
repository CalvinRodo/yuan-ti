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
| Items (`IT1R`, `NAME`, `RDFF` item records) | Decoded | `DarkSun.Data.Items`, `DarkSun.Data.Gff.Rdff`; `src/ds1_parsers.py` |
| Monsters, spells (`RDFF` combat records, `SPIN`) | Partly parsed | `CombatRecord`, `SpellInfo`; `src/ds1_parsers.py` |
| Music (`CSEQ`/`GSEQ`/`LSEQ`/`PSEQ`, `MSEQ`; XMIDI) | Converts to MIDI | `src/xmi_tool.py` |
| Sound samples (`BVOC`) | Extracted as Creative `.VOC` | `src/precompute.py` (audio step) |
| GPL scripts (217 `GPL `, 33 `MAS `) | Bytecode fully documented, disassembler | `src/gpl_parser.py`, `dsun-exe-reference.md` |
| Executable | Overlays unpacked; 2 402 functions decompiled; runtime, GPL interpreter and all handlers named | `make decomp` |
| Cinematics (`ACF `, `BMA `, `CINE.GFF` `BMP`/`PAL`) | Decoded and played | `DarkSun.Data.Cinematics`, `DarkSun.Game.Cinematics`, `dsun-exe-reference.md` §7 |
| UI windows (`WIND`, `BUTN`, `ACCL`) | Layout decoded (the main menu uses them) | `DarkSun.Data.Ui` |
| Not yet decoded | `FONT`, `APFM`, `EBOX`, `DBOX`, `MENU`; what follows combat records in `RDFF` (type 4 references and the data after them: inventories?) | |

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
- [x] **Asset cache** (`DarkSun.Data.Assets.AssetCache`, `Session.Assets`;
      `Textures` in Godot): frames and palettes are decoded once per session,
      keyed by the resource's bytes, and each frame's texture is made once per
      palette. Nothing is converted on disk.
- [x] **Mod overrides** (`DarkSun.Data.Mods.ModSet`): mod folders overlay
      every GFF file (`GffFile.WithOverrides`), by tag and number, for any
      file or one file. Replacements can be `.bin` (raw bytes: scripts,
      palettes, maps, music…), `.png` (true-colour images, whole or frame by
      frame) or `.txt` (text). `load_order.txt` sets priority. The
      `DarkSun.ModTool` exports resources into a mod and lists what mods replace.
- [ ] Mod formats beyond raw bytes for structured data (JSON for items,
      monsters, region info), a GPL assembler, and a mod list in the UI.
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
- [x] Region 255 (`RGNFF.GFF`) is a test map holding every item and effect;
      it has no `PAL ` 255 and uses `GPLDATA.GFF` `PAL ` 1 (also in the Python
      tools).
- [x] Port the text parsers and the `NAME` table (`DarkSun.Data.Text`:
      `GameText` for `TEXT`/`MERR`/`SPIN`, `SpellInfo`, `NameTable`). `NAME`
      is fixed 25-byte records (from the code that reads it), not the
      separator patterns `text_parsers.py` used to guess at; the Python
      parser now reads it the same way, and `make engine-compare` checks the
      text, spells and every name.
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
- [x] Y-sort objects and walls with characters (`RegionView`): objects by
      their `ETAB` y (every `ETAB` is sorted by y, so this is also the game's
      list order), characters by their feet, a wall on cell row r just before
      y = (r + 1) × 16, as `draw_objects_and_walls` (`1DBD:0002`) orders them.
- [x] Camera following the party's active member, easing after it and
      jumping on region changes and teleports; whole-number scaling to any
      window size (`PixelScaler`: the 640 × 400 viewport, twice the original's
      320 × 200, which the menus and cinematics confirm, is shown at the
      largest whole-number scale and grows to fill the window). Zoom 2, the
      default, shows the original's view; zoom levels are ½ and 1–4.
- [x] Movement and collision from the `GMAP` bits (0x40 blocks movement,
      0x80 blocks sight, 0x20 occupied at run time; `Region.IsBlocked`,
      `Region.BlocksSight`); click-to-move with A* pathfinding
      (`DarkSun.Game.Movement`: `Pathfinder`, 8-way without cutting corners,
      and `Walker`, one walk cycle per cell).
- [x] Creature footprints (`Footprint`: half-width and height cells, as
      `footprint_blocked` tests them) and occupied cells (`Occupancy`, the
      engine's form of `GMAP` 0x20): characters never overlap, wait for each
      other, and detour when that is short.
- [x] Creature sizes from the game data (`CharacterRecord`, `Footprint.Of`):
      byte 0x3D of each creature's 0x47-byte character record, the type-4
      entry of its `RDFF` after the combat record. An object without one is a
      single cell, as in DSUN.EXE. 263 of the 290 creatures are 1 × 1; the rest
      are 3 × 1, 1 × 2, 3 × 2 or 3 × 3. This replaces the estimate from
      sprite width, which made 53 creatures too wide. Checked against the
      Python port by `make engine-compare`.
- [x] Compare with DSUN.EXE's own pathfinding and walking speed
      (`make engine-movement`). The original's pathfinder (`08BB`, a "bug"
      algorithm: straight line, then wall-following round each obstacle) is
      ported as `OriginalPathfinder`. For goals within one screen it reaches
      94.0% of goals to A*'s 95.7%, with paths 4% longer on average; across a
      whole map 43.7% to 82.2% (79-cell paths, 30 obstacle nodes). The engine
      keeps A*; `[game] original_pathfinding=true` in `user://settings.cfg`
      switches. Walking speed matches the walk scripts (28 or 32 ticks a
      cell: 3.43 or 3.0 cells/s), now also for SCMD 239, whose cycle moves only
      half a cell; walks with delay 0 ran as fast as the PC in the original
      and get 6 ticks a record (4 cells/s).
- [x] Decode `SCMD` animation scripts (`DarkSun.Data.Animation.Scmd`,
      `DarkSun.Game.Animation.ScmdAnimator`, 96 Hz ticks); objects in the
      region view animate.
- [x] Character sprites: walking and standing by direction from the `SCMD`
      header slots; the creature hot spot (width / 2, height). Any of the 120
      walking object sprites can be chosen (Tab) until party characters exist.
- [x] Depth-sort objects, walls and characters (Godot y-sort on each
      sprite's `ETAB` point or bottom edge; DSUN.EXE draws row by row,
      `draw_objects_and_walls`, `1DBD:0002`).
- [x] Play `SCMD` sound effects (record byte 7), and GPL SOUND (`Sounds`
      autoload): sound n plays `BVOC` n (`DarkSun.Data.Audio.SoundEffects`,
      from the game's sound table), decoded from Creative VOC
      (`Voc`). Objects, creatures and party members sound when on screen;
      `[audio] sound_volume` in `user://settings.cfg`. Most `SCMD` sounds are
      in state changes (doors opening and closing, levers: `SCMD` slots 2 and
      3, or the `REQUEST(5)` start), so they are heard when those run.
- [x] Open and close doors (`GameWorld.UseObject`, `SetDoor`): right-click
      uses a door (`6BB8:0B56`): it opens or closes with its other leaf
      (`OJFF` ± 1), plays `SCMD` slot 2 (opening) or 3 (closing) with their
      sounds, and frees or blocks its cells (`6BB8:0FF8`); then its use trigger
      runs, and a door that didn't move says why ("THE DOOR WON'T OPEN", …).
      Scripts move doors with `REQUEST(2)`. Lever-like objects (item type
      flag 1) toggle on use. Door triggers (`CREATE_DOOR_*`) turned out to be
      unused by the game and its scripts.
- [x] Walk to an object before using it (`Approach`): right-click sends the
      selected character to the nearest cell next to the object (a door's
      whole width, a creature's footprint), then uses it. Where it can't get
      next to it, it stops up to 3 cells away; beyond that it uses it from
      where it stands, so no script is out of reach. DSUN.EXE's own rule is
      not traced.
- [ ] The game's other verbs (look, take, talk as separate actions).
- [ ] Region transitions: map edges and doors to other `RGNxx` files.
- [ ] Debug overlay: grid, passability, object numbers, hovered tile.

---

## 4. Milestone 3: GPL scripts running

Goal: the world reacts. Most quest, dialogue and event logic lives in GPL, so
this milestone brings most of the game's content.

- [x] Port the GPL virtual machine to `DarkSun.Game.Scripting`
      (`GplDecoder`, `GplMachine`, `GplMemory`): operand decoding, the
      expression evaluator (left to right, no precedence), variables as the
      original byte areas, call frames and cross-file calls, IF / COMPARE /
      WHILE with their limits and quirks, RAND.
- [x] Every opcode that affects the world goes through `IGplHost`;
      `LoggingGplHost` records them, so scripts run end to end
      (`make engine-scripts`: every region script and every trigger it
      registers, 0 interpreter errors).
- [x] Dialogue: PRINT_STRING / PRINT_NUMBER / PRINT_NEWLINE, SHOW_PIC /
      CLEAR_PIC, CONTINUE, ASK_YES_NO, MENU (`DialogueGplHost`,
      `GplDialogue`; the Godot `DialogueWindow`). Scripts pause for the
      player and resume (`GplMachine.Suspend` / `Resume`, which replays the
      asking command, so questions inside expressions work).
- [x] TELEPORT: the party (region exits: 31 found by `--check-exits`, which
      tries every cell of a box) and named objects (hidden when sent
      elsewhere); NAMETONUM / NUMTONAME.
- [x] A creature model (`DarkSun.Game.World`: `GameWorld`, `WorldObject`,
      `WorldGplHost`). Region objects become creatures (those with an `RDFF`
      combat record, indexes 4–47; walkers if their `SCMD` can walk) or
      things (48–319); the party is 0–3. It carries out:
      - the orders HUNT, GO, GOXY, FOLLOW, FLEE, FETCH and WAIT, and READORDERS;
      - the queries GETXY, RANGE and NEXTTO (the exact `0898:000C` distance),
        GET_LOS (Bresenham over `GMAP` 0x80), SET_OTHER, GET_STATUS and
        GETPARTY;
      - CLONE, TELEPORT, BITSNOOP and CLEAR_LOS, and FIGHT, which ends the
        script;
      - REQUEST 5 (toggle state and animation), 10–13 (`GMAP` bits), 17
        (place) and 18 (allegiance).

      It also keeps object fields (72 id, 75 allegiance, 95 facing, the
      rest stored), the special objects (`obj_special(n)` = the object whose
      index is in `gname` n) and the engine variables (actor, target and
      other set per run).
- [ ] Still logged only: GIVE / TAKE / SEARCH / AWARD / DROP (need items),
      FIGHT's combat (M5), REQUEST 1–4, 6–9, 14–16 and 19–20, SHOP,
      DAMAGE. FLEE and the orders' arrival distances are approximations;
      the original order handlers (`1A0A`) are not ported line by line.
- [ ] SOUND (251 uses): queued by the host; needs the audio layer (M7).
- [ ] Remember each region's changes (moved and removed objects) between
      visits.
- [x] **Find how scripts are triggered** (`dsun-exe-reference.md` §4):
      `MAS 99` at a new game, `MAS` *n* on entering region *n*, and the
      triggers those scripts register (move tile/box, look, talk, use, use
      with, pick up, attack; per-creature no-orders and line of sight).
- [x] Port the trigger lists and checks (`GplTriggers`, byte for byte, and
      `GplCreatureScripts`); the region view runs `MAS 99`, the region's
      script, move triggers as characters reach cells, and object triggers on
      right-click (F2 shows the log).
- [x] Run creature no-orders and line-of-sight scripts: `GameWorld.Update`
      queues them (idle creatures; kind 1 when the active member is in
      sight within range, kind 2 when not), checked every half second; the
      view runs a few per frame with the creature as actor
      (`GplMachine.RunFor`). The original's timing is not traced.
- [x] **Golden tests**: `make engine-compare` checks that the C# decoder lists
      every script exactly as `gpl_parser.py` does (69 741 lines).
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
- [x] Main menu, from the original's window 3000 (`MainMenu`): Start Game
      (plays cinematic 1, then a new game in region 42) and Exit work; S, C,
      L and E and the arrow keys work. Create Characters and Load Saved Game
      say they aren't ported yet.
- [ ] Options, and a journal or quest log (new; fed by GPL variables and
      dialogue history).

---

## 6. Milestone 5: characters, items and party

- [ ] Character data model: races, classes, stats, levels, psionics
      (reverse engineering, §10).
- [x] Items from `IT1R` and `NAME` (`DarkSun.Data.Items`): `ItemType`
      (115 types: weapon kind, slot, damage, AC, weight, classes),
      `ItemRecord` (the 21-byte record of each item `RDFF`, 755 in
      `SEGOBJEX.GFF`), `ItemCatalog.NameOf` (the game's `"%+d name"`).
      Region items get their names in the world. Checked against the Python
      port by `make engine-compare`.
- [ ] Inventory, equipping, containers, shops. Still to decode: item-record
      `special`, `charges`, `flags` bits, `IT1R` `material` and the bits of the
      class mask; containers' contents.
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

- [x] Music: an XMIDI sequencer (`DarkSun.Data.Audio.XmidiSequencer`: 120 Hz
      ticks, note durations, live FOR/NEXT loops, callbacks) drives MeltySynth
      (`DarkSun.Game.Audio.MusicPlayer`) with a SoundFont found on the system
      or configured; the Godot `Music` autoload streams it. The General MIDI
      versions (`GSEQ`) are played. Fades and track changes follow
      `35D1:0265`/`012D`.
- [ ] Bundle (or offer to download) a good freely licensed SoundFont, for
      systems without one; a volume setting.
- [ ] An OPL (AdLib) emulation option for the original sound (`ADV` drivers,
      `TIMB` timbres), and MT-32 (`LSEQ`).
- [x] Sound effects: `BVOC` (Creative `.VOC`) decoded by `Voc` to Godot
      `AudioStreamWAV` (`Sounds`). Sounds 131–180 (MIDI notes for the music
      driver) are not played yet.
- [x] Which music plays where: the menu plays 17; a region plays the track
      in its `RDAT` record (`+0x2F`); cinematics play 26–29 (`CINE.GFF`).
      GPL's `MUSIC` command exists but no script uses it.
- [ ] Combat and event music (`870B:0584` plays `4931:028C` again; other
      callers of `35D1:01D3`), and the per-track flags at `DS:1676`.

---

## 9. Milestone 8: saving, cinematics, full game

- [ ] Save and load in the engine's own format (JSON plus compressed state):
      GPL variables, party, region states. Save at any time, with autosaves.
- [ ] Optional: import original save games (format needs reverse engineering).
- [x] Cinematics from `CINE.GFF` (`CinematicPlayer`): the intro (ACF 1–8)
      before the main menu, the arrival (9–10) on a new game; the ending
      (11–13) plays too, but nothing triggers it yet. Any key skips. Timed by
      the music's XMIDI callbacks, as in DSUN.EXE.
- [ ] Cinematic op 0x7F (`9944:1AD7`, 12 arguments; only in the ending's
      first script): not decoded, so that picture is shown still.
- [x] Cinematic music (GSEQ 26–29; ops 0x64–0x66).
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
| M2 | Region exits | probably move-box and move-tile triggers whose scripts change region; find the GPL command that sets `DS:117C` and the region change (`1587:1F83`) |
| M2 | `SCMD` record flags 0x10 and 0x20, header bytes 0x40–0x47, the action numbers 2–6 | `1DF3:2BCF`; `896E:0C92`, `87B3:0AE1`; the combat code's header lookups |
| ~~M3~~ | ~~Script triggers~~ | Done 2026-10-18: `dsun-exe-reference.md` §4 |
| M3 | Which player actions call each trigger check (look, talk, use, pickup, attack menus); the per-step movement check | the callers listed in `dsun-exe-reference.md` §4 |
| M3 | What each world-affecting GPL command does | the 114 named `gpl_op_*` handlers and their callees |
| M4 | `FONT`, `APFM`, `EBOX` formats; what each `WIND` is | text-drawing and window code (`2531`, `25EC`); `WIND`/`BUTN`/`ACCL` done (§7) |
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
4. ~~In parallel: decode `SCMD`, and find the script triggers.~~ Done
   2026-10-18.
