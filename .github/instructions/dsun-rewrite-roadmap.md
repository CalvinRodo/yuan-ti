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
| UI windows (`WIND`, `BUTN`, `APFM`, `EBOX`, `ACCL`) | Decoded; screens are built from them; JSON for mods | `DarkSun.Data.Ui`, `UiWindowView` |
| Font (`FONT` 100) | Decoded, drawn as the game draws it, moddable as a picture | `DarkSun.Data.Text.GameFont`, `FontSheet`; `GameFonts` |
| Not yet decoded | how `APFM` slots are drawn (bevels, bars), `EBOX` fields beyond size and colours, `MENU`; what follows combat records in `RDFF` (type 4 references and the data after them: inventories?) | |

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
- [x] **Structured mod formats** (2026-10-19): `.json` field edits of item
      types (`IT1R`), objects' item, combat and character records (`RDFF`),
      region info (`RDAT`) and item names (`NAME`), applied over the
      original bytes (`RecordJson`); `.gpl` script source, assembled at load
      (`GplAssembler`; all 250 shipped scripts round-trip byte for byte,
      `make engine-gpl-check`); `mod.json` metadata; unusable mod files are
      reported, not fatal. The main menu's Mods screen (`ModsView`) lists
      mods, their problems, turns them on and off and reorders them.
- [x] **Verb menu** (2026-10-19): right-click shows the object's verbs
      (`ObjectVerbs`, from DSUN.EXE's verb window `68BD:0040` plus Attack and
      Use with), with the original's icons (`VerbMenu`); the chosen verb's
      trigger runs once the character has walked up. Get and Attack without
      a script wait on the inventory and combat.
- [ ] More JSON forms as formats are decoded (spells `SPIN`, the rest of
      the character record, `RDAT`'s other 43 bytes, `ETAB` placements).
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
- [x] Region transitions (2026-10-19): the game's own scripts do them
      (TELEPORT of the party; 123 such teleports reachable from triggers:
      map-edge move boxes through `GPL 80`, which asks only when the active
      member faces out of the map; doorway tiles and boxes; use triggers on
      doors, stairs and portals). The view fades between regions and keeps
      the party's members, sprites, facing and selection. A region change
      (`7155:1487`) runs only the region script; the party's cells' move
      triggers run once, at a new game (`1587:1F83`). Every trigger now
      starts with the actor's cell in `gname[1..3]` (`1A0A:0256`), which
      scripts rely on. `--check-exits` walks into every move trigger from
      each side and walks up to and uses every object with a use trigger.
- [x] Debug overlay (`DebugOverlay`): F1 passability, line of sight,
      occupancy and move triggers; F3 grid; F4 object index and `OJFF`
      numbers; the hovered cell outlined and described (flags, occupant,
      objects and their verbs, triggers and their scripts).

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
- [x] Items (`DarkSun.Game.World.Item`, `GameWorld.Items.cs`; traced in
      `dsun-exe-reference.md`, "Items in scripts"): creatures, party
      members and containers carry items; GIVE, TAKE, DROP and SEARCH as
      `7372` and `043D:08F8` do them, Get puts an item in the pack and runs
      its pickup trigger, the 14 equipment slots, money (`gname[10]`,
      CHANGE_MONEY), AWARD, DAMAGE / PHYS_DAMAGE (`612E:18DA`'s hit point
      and status rules), SHOP (the script waits for a shop window: prices
      are the item's value, half to sell; the original screen isn't
      traced), and REQUEST 1 / 4 (rest), 9 (turn an object into another),
      16 (music level; the music player doesn't play XMIDI sections yet)
      and 20 (count creatures). Party items and hit points last the whole
      game and are saved; regions remember what their creatures carry and
      items left on the ground. An inventory window (I): wear, give, drop.
      `--check-items`; `--give=RDFF,…` and `--open-items` for testing.
- [ ] Still logged only: FIGHT's combat (M5), and REQUEST 3 (a check), 7
      (trap / blast), 8 (random monster), 14 (ambush), 15 (unidentified).
      Not traced: SEARCH's per-member work (`0826:0344`, op 3 is taken to
      count items), the range bytes, the shop screen, what happens to a
      dead creature (`612E:1AAF`: here it leaves the map), healing details
      of a rest, class restrictions on wearing items. FLEE and the orders'
      arrival distances are approximations; the original order handlers
      (`1A0A`) are not ported line by line.
- [x] SOUND (251 uses): the host queues it and the region view plays it
      through `Sounds`, like `SCMD` sounds (`play_sound_effect`, sound n is
      `BVOC` n). Every script uses a sample number (1–127, none of the MIDI
      note effects 131–180). Sounds 19, 30, 87 and 127 (10 uses) have no
      `BVOC` in the game's files, so they are silent, as in DSUN.EXE with a
      digital card. A mod can supply them, or replace any sound, as a WAV
      (`RESOURCE.GFF/BVOC/19.wav`; `ModTool export` writes sounds as WAV).
- [x] Remember each region's changes between visits (`RegionMemory`,
      `GameWorld.Snapshot` / `Restore`), as DSUN.EXE saves the region it
      leaves (`7155:0C64`): `GMAP` flags, and each object's place, hidden
      state, door flags, second state, allegiance, status, script fields
      and clones. Restored before the region script runs again, and kept in
      saved games.
- [x] **Saved games** (`DarkSun.Game.Saves.SaveGame`, JSON): party
      (placeholder sprites, cells, facing, selection), globals, engine
      variables, the 200-record trigger table as is, creature scripts, the
      random generator and region memory. F5 / F9 quick save and load, an
      autosave on entering a region, and the main menu's Load Saved Game.
      Loading doesn't run the region script (its triggers are saved).
      DSUN.EXE's own save format isn't read.
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
- [x] Script debugger in the engine (F6, `ScriptDebugger`): the script as
      `.gpl` source (a mod's own file, or as exported), breakpoints kept
      between runs, break on the next script, step over / into / out, the
      call stack, variables (changes since the last pause, set by typing
      `gnum[5] = 3`). The machine pauses through its existing suspend
      (`GplMachine.Step`, `Continue`, `Abandon`), so the game waits as it
      does for a conversation. `--check-debugger`. Not yet: watch
      expressions, conditional breakpoints, breaking on a variable change,
      object fields.

---

## 5. Milestone 4: user interface and dialogue

- [x] Text windows, dialogue with portraits (`PORT`), and GPL `MENU`
      choices (`DialogueWindow`), in the original's windows and art
      (`dsun-exe-reference.md`, "The windows"): `WIND` 3007 over `BMP` 3007
      with the portrait in its frame (the sun when there is none), text in
      the text box's colour, paged with MORE and the up arrow; MENU and
      ASK_YES_NO in `WIND` 3008's five stone bars, scrolled the same way.
      MORE (Space, Enter, a click on the text) reads on, answers CONTINUE,
      opens the choices or puts the window away; 1–9 and Y / N choose.
      Scaled with the screen like the 320 × 200 screens, in the game's font
      (`FONT` 100: `GameFonts`). Not yet: `WIND` 3500 (text without a
      portrait; every window here has the portrait box). `--check-dialogue`,
      and `--talk` to start a conversation for testing.
- [x] The UI approach: screens are Godot `Control`s built from the original
      windows' data and art (`UiWindowView`: a `WIND` with its `BUTN`s,
      `APFM` slots, `EBOX` text boxes, `ACCL` keys and backdrop `BMP`, in the
      game's `FONT`), scaled by whole numbers with the 320 × 200 screen. The
      screen's code finds controls by number and fills them in, so a layout
      is the window's data: a mod's `WIND/n.json` moves controls (`BUTN`,
      `APFM`, `EBOX` `.json` resize them, change icons and texts), and the
      engine's own departures from the original are the same JSON, in
      `game/ui/WIND/` (Character Generation's die). The main menu, View
      Character, Character Generation and the text windows are built this
      way; `--check-windows` builds all 27 windows (400 controls, no art
      missing). There are no `DBOX` resources in this game. Modern screens
      with no original window (items, shop, saves, mods, debugger, key help)
      are plain Godot controls.
- [x] Main menu, from the original's window 3000 (`MainMenu`): Start Game
      (plays cinematic 1, then a new game in region 42) and Exit work; S, C,
      L and E and the arrow keys work. Create Characters and Load Saved Game
      say they aren't ported yet.
- [x] Options, and a journal or quest log (new; fed by GPL variables and
      dialogue history).
      - Options (O, on the main menu and the map; `OptionsWindow`,
        `Settings`, `user://settings.cfg`): music and sound volume, full
        screen, skipping the opening, autosave, the map's information
        lines, the original's pathfinding, and every key (rebound by
        pressing the new one; "Default keys" resets them).
      - Journal (J; `DarkSun.Game.History`, `JournalWindow`): every
        conversation as the dialogue window showed it, with the speaker's
        name and portrait and the player's replies; events (items the party
        is given or gives up, experience, money, regions reached); and a
        quest log whose quests are JSON (`journal/quests.json`, a mod's
        replacing the engine's), each stage a condition on the scripts'
        global variables. The journal is saved with the game.
        `--check-journal`.
      - The engine ships one quest, Semyon's in region 42 (GPL 5:
        `gflag[28]`, `gflag[26]`). Writing the rest means reading each
        quest's scripts; the format is there for it.

---

## 6. Milestone 5: characters, items and party

- [x] Character data model and creation rules (`DarkSun.Game.Characters`,
      2026-10-19), from the manual: races, genders, classes, multi-class
      limits, abilities and racial adjustments, class requirements, hit
      dice, levels by experience, alignments, disciplines and spheres.
      Moddable as JSON.
- [x] DSUN.EXE's own creation tables, checked against the manual's rules
      (`dsun-exe-reference.md`, "Character creation"). They are in segment
      `38D4`, read by `6CCA` and `6ECD` (not `9106`, `8F23`, `91F7`: those
      are the View Character screen, the character sheet and the item
      window). Now the engine's: the best of four 4d4+4 rolls, the limits
      (20 + adjustment; 17 in each class's key ability, the class's floor
      in the others), the racial adjustments (the half-giant's differ),
      hit points per level (half-giants double), 3000 experience in each
      class, and the map icon per race and gender (`OJFF` 300–313). A
      character becomes its records by copying the templates at
      `38D4:0048` (character, 0x47 bytes) and `008F` (combat, 0x3A) into
      the tables (`6ECD:1644`). Not traced: which classes each race may
      take (still the manual's), the psionic point tables (`38D4:011C`…)
      and the attack counts (`8E77:038F`).
- [x] Items from `IT1R` and `NAME` (`DarkSun.Data.Items`): `ItemType`
      (115 types: weapon kind, slot, damage, AC, weight, classes),
      `ItemRecord` (the 21-byte record of each item `RDFF`, 755 in
      `SEGOBJEX.GFF`), `ItemCatalog.NameOf` (the game's `"%+d name"`).
      Region items get their names in the world. Checked against the Python
      port by `make engine-compare`.
- [x] Inventory, equipping, containers, shops. The item fields are
      decoded (`dsun-exe-reference.md`, "Items"): material, the type flags,
      the class mask, magic items' power and charges, and `special`
      (breakage). Equipping checks the member's classes
      (`Character.ItemClasses`); a dead creature leaves a "Dead Body" with
      what it carried; Open on a container (chest, bag, body) shows a
      take/put window; the items window shows material, the spell an item
      casts, its charges and who may use it.
- [ ] Items still to do: casting from items (charges used up; needs
      spells), breakage in combat (`80F5:1F4B`), bags' contents in a pack,
      and the type flags 4 and 0x10.
- [x] Character sheet and inventory screens (`CharacterScreen`): View
      Character (`WIND` 11500 over `BMP` 11000) with the member's sheet, and
      the inventory (`WIND` 13500 over `BMP` 13001: the body `BMP` 13004,
      equipment slot n at `APFM` 11216 + n with its silhouette `BMP` 13007
      frame 9 + n, the pack at 11230–11241, a container's contents at
      13200 and 11248–11259 over `BMP` 13002/13003/13005). Click-to-move
      stands in for the original's dragging. Armour class and THAC0 follow
      AD&D 2nd edition (DSUN.EXE's formulas aren't traced).
- [x] The spell and psionic pages (View Character's 35 spell slots; the
      spells' levels and spheres from DSUN.EXE's table at `DS:019C`;
      `DarkSun.Game.Magic.Spellcasting`: memorize, learn, cast the healing
      spells outside combat, rest; pick and use powers), the leader and
      computer-control buttons, the GAME MENU (`WIND` 10500) with
      preferences (16500) and its questions (14000), and the store on the
      inventory (SHOP: the merchant's goods, the six selling slots
      11242–11247, SELL / TAKE IT). See `dsun-exe-reference.md`, "Spells"
      and "Game menu and preferences".
- [ ] Spells and powers in combat (all but healing say ONLY IN COMBAT);
      DSUN.EXE's own spell-slot tables (`DS:4B08`, `DS:4B11`), psionic
      costs and strength; level-up (`WIND` 17502, probably) and the
      original's learn and pick windows (17500, 17501); computer control's
      effect; END TURN; the ABOUT text.
- [x] Party creation: Create Characters (View Character, `WIND` 11500) and
      Character Generation (`WIND` 3011) screens; the party starts new games
      and is kept in saves; a roster for ADD and DROP.
- [ ] A pre-made party option; dual-classing for humans; party leader and
      computer-control buttons; decode `APFM` to draw the screens exactly as
      the original (the font is done).

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
| M4 | `APFM`, `EBOX` formats; what each `WIND` is (`FONT` done) | text-drawing and window code (`2531`, `25EC`); `WIND`/`BUTN`/`ACCL` done (§7) |
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
