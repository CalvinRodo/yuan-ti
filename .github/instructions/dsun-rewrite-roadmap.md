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
| Monsters (`RDFF` combat and character records, carried items), spells (`SPIN`) | Monsters decoded; spells partly | `CreatureDefinition`, `CombatRecord`, `CharacterRecord`, `SpellInfo`; `src/ds1_parsers.py` |
| Music (`CSEQ`/`GSEQ`/`LSEQ`/`PSEQ`, `MSEQ`; XMIDI) | Converts to MIDI | `src/xmi_tool.py` |
| Sound samples (`BVOC`) | Extracted as Creative `.VOC` | `src/precompute.py` (audio step) |
| GPL scripts (217 `GPL `, 33 `MAS `) | Bytecode fully documented, disassembler | `src/gpl_parser.py`, `dsun-exe-reference.md` |
| Executable | Overlays unpacked; 2 402 functions decompiled; runtime, GPL interpreter and all handlers named | `make decomp` |
| Cinematics (`ACF `, `BMA `, `CINE.GFF` `BMP`/`PAL`) | Decoded and played | `DarkSun.Data.Cinematics`, `DarkSun.Game.Cinematics`, `dsun-exe-reference.md` §7 |
| UI windows (`WIND`, `BUTN`, `APFM`, `EBOX`, `ACCL`) | Decoded; screens are built from them; JSON for mods | `DarkSun.Data.Ui`, `UiWindowView` |
| Font (`FONT` 100) | Decoded, drawn as the game draws it, moddable as a picture | `DarkSun.Data.Text.GameFont`, `FontSheet`; `GameFonts` |
| Not yet decoded | how `APFM` slots are drawn (bevels, bars), `EBOX` fields beyond size and colours, `MENU` | |

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
      elsewhere, placed out of the player's sight when the fifth argument
      is 1); NAMETONUM / NUMTONAME.
- [x] A creature model (`DarkSun.Game.World`: `GameWorld`, `WorldObject`,
      `WorldGplHost`). Region objects become creatures (those with an `RDFF`
      combat record, indexes 4–47; walkers if their `SCMD` can walk) or
      things (48–319); the party is 0–3. It carries out:
      - the orders HUNT, GO, GOXY, FOLLOW, FLEE, FETCH and WAIT, and READORDERS;
      - the queries GETXY, RANGE and NEXTTO (the exact `0898:000C` distance),
        GET_LOS (Bresenham over `GMAP` 0x80), SET_OTHER, GET_STATUS (the
        game-wide table of named creatures' statuses) and GETPARTY;
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
      16 (music level: a section of the track)
      and 20 (count creatures). Party items and hit points last the whole
      game and are saved; regions remember what their creatures carry and
      items left on the ground. An inventory window (I): wear, give, drop.
      `--check-items`; `--give=RDFF,…` and `--open-items` for testing.
- [x] FIGHT's combat (M6) and every REQUEST code the shipped scripts use
      (see M6, "The rest of the REQUEST codes").
- [x] Traced (#80): SEARCH's per-member work (`0826:0344`: one criterion
      weighs each item by its field, more filter on the first's value and
      weigh by the second's; op 3 counts items, not their quantity; the
      smallest of nothing is 99 999 999), a dead creature (`612E:1AAF` →
      `896E:0C92` → `612E:2659`: a body within 7 pixels at random takes what
      it carried, destroyed or not), a rest's healing (`87B3:073D`: the
      party's best cleric and druid above level 4 cure disease, curse and
      paralysis, a druid above 6 poison; whoever was poisoned when the rest
      began dies), the store (`8A89`: goods in the six right-hand slots at
      their value, sold back at the same, restocked when the merchant has
      two; MORE pages, SELL asks "I'll give you %u$"), and FLEE (`1A0A:2C86`:
      away from object 0, to the farthest free cell in reach). The orders'
      goal is the free cell next to the target (`1A0A:2E99`), FOLLOW's too.
- [ ] Not traced: class restrictions on wearing items; the original never
      ends GO, FETCH or FOLLOW on arrival (`1A0A:0B0F` only stops the mover),
      which the engine still does, as its scripted scenes expect; the store's
      deal box (a purchase goes to the pack here).
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
      (`GplMachine.RunFor`). The original's timing is not traced. Any
      object given one takes part, not only creatures (`1A0A:0554` moves it
      into the creature slots, `1A0A:273E`): region 42's gate is how the
      arena opening starts (2026-10-20).
- [x] Scripting checked against the OpenDS and Obsidian Edition notes
      (2026-10-20). Object fields follow `GPLX` 1 (`GplFieldMap`: field →
      offset in the item, combat, name, character and item-type records,
      with types and pointers): 73 is the status, 74 the allegiance and 75
      what the AI does (we had 75 as the allegiance, and 73, 74 read 0, so
      no creature looked dead or hostile to a script); `obj.71.f` reaches the
      character record, a party member's from its sheet (the magic pool's
      new abilities and most hit points reach it). GETPARTY makes the member
      "other" (the arena gate's check read the gate itself). The game clock
      runs (`gname[9]`: 1.5 s a real second on the map, a minute a combat
      round, PASS_TIME); it was 0, so timed scripts never moved on. Done
      from the EXE: STAT_ROLL; the thief skills (`REQUEST(3)`, tables in
      `characters.json`; the party check's slot-0 bug as
      `PartySkillCheckBest`); traps (`REQUEST(7)`); SETTHING and the
      "thing"; STRING_COMPARE; INPUT_STRING (a text box: the manual-word
      check, any case); item charges. OpenDS's script repairs (Darkhold's
      dead triggers, the Lava Rifts ranger, Linara) apply as the scripts
      load (`GplRepairs`, `script_repairs`, on); the final battle's gate
      repair is there but off (with the clock running the gate waits its
      window out). Conditions outside fights are the spells cast on the map
      or still running from a fight (`Character.Lasting`): the skill checks
      and REQUEST 7's traps see them, Detect Traps among them (#73). Not yet:
      SKILLROLL (no script uses it), JOINPARTY and
      LEAVEPARTY, the LOG text, where the clock stops (`DS:11B8`,
      `DS:0BC9`: the Look pointer, by Obsidian Edition's notes).
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
      (`FONT` 100: `GameFonts`). INPUT_STRING types into `WIND` 3500's
      edit box (`EBOX` 4500), opened at (0, 140) under the text window, as
      `84C9:0253` mode 4 does (#81). `--check-dialogue`, `--talk` to start a
      conversation, and `--gpl=N@addr` to run a script, for testing.
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
      L and E and the arrow keys work, and so do Create Characters and Load
      Saved Game.
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
      what it carried; containers on the map hold what their `RDFF` links
      to them, and Open shows them in the original's six-slot window
      (`WIND` 13501, `6527:002C`); coins (`RDFF` 1384–1389, 1399) taken
      become the party's money; right-click shows the item window (`WIND`
      15502, `91F7:0C29`) with INFO for who may use it (`91F7:12D8`).
- [x] Casting from items (`ItemMagic`; the item window's USE, `91F7:03D0`):
      scrolls teach; wands, necklaces and fruit cast at level 6, a charge a
      cast (`78A9:2324`; gone at 0); in a fight aimed like CAST, outside one
      only healing (as memorized spells). USE is offered as `91F7:0000` does,
      with the spell's purpose (record −7, `SpellEffect.Purpose`).
- [x] Breakage in combat (`80F5:1F4B`, #56): as in DSUN.EXE only Detonate's
      shrapnel breaks things (`Battle.BreakItem`; the original leaves a
      "Broken Weapon", here the item is gone).
- [x] Quest items' USE runs their use trigger (`039D:0319`, #71): the
      inventory item is the target by its id, as for USE WITH.
- [x] Item spells (#72): out of a fight a spell to aim is aimed on the map
      (`8B7D:04D1`; `RegionView.ItemAim`): a help on a party member takes
      effect as members' own spells do out of a fight, anything else at a
      creature starts the fight and is cast first. A spell on the user that
      isn't a heal takes effect too. A weapon's spell reaches whom it hits
      (`612E:1B7A`). Worn items' ordinary spells do nothing, and the type
      flags 4 and 0x10 are read nowhere. `--check-map-cast`.
- [x] The ability items 0xFA–0xFF (#87; `AbilityScores`): `80F5:2BB8`
      works out a combat record's abilities from the character's, the
      Strength and Feeblemind effects and every item carried (pack and
      containers too): +0x0F − 0xFA picks the ability, the charges (+0x0E)
      add (below 16), set a floor (16–31: Belt of Might's 24) or take away
      (32 and up). Fights, the character screen and STAT_ROLL use them.
- [x] Character sheet and inventory screens (`CharacterScreen`): View
      Character (`WIND` 11500 over `BMP` 11000) with the member's sheet, and
      the inventory (`WIND` 13500 over `BMP` 13001: the member's figure,
      `BMP` 20000 + their look (the map icon's, `OJFF` 300 + n), centred at
      (120, 96) on the parchment 13005 (`74F1:175A`, `74F1:17AF`),
      equipment slot n at `APFM` 11216 + n with its silhouette `BMP` 13007
      frame 9 + n, the pack at 11230–11241, a carried container's six
      things at 13200 over `BMP` 13002/13003, a bag's in 11248–11253, a
      box's in 11254–11259, after the shut art 20104/20105; a store's goods
      in all twelve). Click-to-move
      stands in for the original's dragging. Armour class and THAC0 follow
      AD&D 2nd edition (DSUN.EXE's formulas aren't traced).
- [x] The spell and psionic pages (View Character's 35 spell slots; the
      spells' levels and spheres from DSUN.EXE's table at `DS:019C`;
      `DarkSun.Game.Magic.Spellcasting`: casts per spell level, learn, cast the healing
      spells outside combat, rest; pick and use powers), the leader and
      computer-control buttons, the GAME MENU (`WIND` 10500) with
      preferences (16500) and its questions (14000), and the store on the
      inventory (SHOP: the merchant's goods, the six selling slots
      11242–11247, SELL / TAKE IT). See `dsun-exe-reference.md`, "Spells"
      and "Game menu and preferences".
- [x] Spells and powers in combat, and buffs outside it (#53);
      DSUN.EXE's own spell-slot tables (`DS:4B08`, `DS:4B11`) and psionic
      costs; level-up (#51) and the original's learn and pick windows
      (17500, 17501); computer control; END TURN; the ABOUT text.
- [x] DSUN.EXE's character rules (2026-10-30): psionic strength
      (`8E77:0162`, #77); the classes each race may take and in which
      combinations (`38F0`), the alignments each class allows
      (`38F0:0252`), the key abilities and the dual-class rules
      (`8CCC:1934`: from level 2, not within a group) (#76); the gladiator's
      −1 armour class per 5 levels (`612E:2113`, #78). `--check-chargen`
      makes every combination of every race. The Tabletop mod keeps the
      manual's third level for dual-classing.
- [x] Party creation: Create Characters (View Character, `WIND` 11500) and
      Character Generation (`WIND` 3011) screens; the party starts new games
      and is kept in saves; a roster for ADD and DROP.
- [x] A pre-made party (`game/party/premade.json`: a human gladiator, an
      elf fighter/preserver, a dwarf cleric/fighter, a thri-kreen
      psionicist/ranger, all legal by the rules; START GAME without a party
      uses it, and PRE-MADE PARTY on an empty box adds it). Dual-classing for
      humans (DUAL, `WIND` 17502; `CharacterCreation.DualChoices`: third
      level, prime requisites 15 and 17; the former class at its level comes
      back once the new one passes it). The leader and computer-control
      buttons on Create Characters' screen too; the leader is kept and
      selected when a game starts. `APFM` decoded (`dsun-exe-reference.md`,
      "Character screens"): View Character's bars and hover texts, the
      inventory's bars, hit points and status words, as DSUN.EXE draws them
      (`PartyFrames`).
- [x] The remaining `APFM`s (#81): the Dark Sun icon on the map (`APFM`
      1001, `BMP` 5012; a left click the character screens at the page last
      shown, a right click quick cast); quick cast (`WIND` 14002,
      `5F6D:08EC`: each member's six last spells and powers, crossed out
      when they can't be used, `Character.QuickCasts`, `--check-quick-cast`);
      the status word's strongest condition for Okay members (`78A9:20A0`,
      `Conditions`); only the leader on the map until COLLAPSE PARTY spreads
      the party out, the others gathered again after a fight (`DS:1168`,
      `[game] leader_alone`). The readied weapons (`9106:0B64`) were drawn
      already.

---

## 7. Milestone 6: combat

The largest reverse-engineering task, because its rules live in the
executable, not in the data.

- [x] Find and document the combat code (§10): initiative and turn order,
      to-hit and damage, armour class, movement points, spells and psionic
      powers, and saving throws. `dsun-exe-reference.md` §8: the round loop
      (`612E`), initiative (20 + d10 + Dex, highest first), movement (Move ×
      10; 10 a step, 14 diagonal), attacks (THAC0 − bonuses − AC, rear and
      backstab, half-attacks, non-metal weapons), AC, the ability tables,
      class groups with THAC0, hit dice and saves (`40B1`), the save roll, the
      spell records (`3CB4`), the psionic power table (`3FB9`, with the
      manual's scores and costs). Functions named in `symbols.tsv`.
- [x] Trace the combat AI (2026-10-19, `dsun-exe-reference.md` §8 "The
      AI"): `612E:0BAD` runs for combatants under computer control.
      `1A0A:273E`, once listed as the AI, only gives an object one of the 48
      active slots. Each turn the AI keeps its target or picks a random
      enemy within 24, scores its weapons, then takes the first that applies:
      a spell (once, Int > 9), the +0x16 ability, a random psionic power
      (monsters), an attack, an adjacent enemy, a move next to the target,
      the missile weapon, or ends the turn. A mind state (`8053:000E`) can
      make it skip the turn or only move.
- [x] Trace the rest of combat (2026-10-19, `dsun-exe-reference.md` §8):
      the condition list and its names (Afraid makes the AI flee, Berserk
      re-rolls its side, Confused rolls d10); order mode 1 is flee; spell
      choice by a weight table (`37E5:0000`) and area targeting by hit
      points; monster special abilities (+0x16: breath cones, paralysis,
      webs); the speed state (Paralyzed and Stuck 0 movement, Slowed half,
      Hasted double); spell damage and duration formulas; experience
      (per kill, split over the party slots and classes) and the XP tables.
- [x] Combat details (2026-10-20): `612E:2514` is GUARD (a guard attacks
      the first enemy that moves next to it; the AI guards when it has
      nothing else to do) and WAIT; the −1 duration unit is a charge count
      (Armor's hits, Mirror Image's images, Invisibility until it attacks,
      Poison's 10 rounds); the defiler's XP row is never read, since only
      creatures have class codes 18 and 19.
- [x] Monster data from `RDFF` (2026-10-19): `CreatureDefinition` reads a
      creature's whole `RDFF` the way DSUN.EXE's loader does (`7155:0000`;
      load actions 2 and 4 are the items it carries, 479 in 98 creatures),
      with the combat and character record fields the code uses (mind,
      special abilities, experience value, natural attacks, saves, spell
      and psionic sets). Creatures now get the character record's hit
      points (85 had the combat record's, e.g. Karas 26 instead of 44) and
      their items, which their dead bodies keep; killing an enemy gives its
      experience to the party (`612E:25F2`). The `RDFF` JSON mod format edits
      the new fields; `make engine-compare` checks the carried items against
      `ds1_parsers.py`.
- [x] The creature tables that are only in DSUN.EXE ship as rules
      (2026-10-20, `DarkSun.Game.Rules.CreatureRules`): special-ability masks
      by type (`3E45:0000`, 42 types) and the 11 abilities the AI tries in
      order (`DS:05D0`/`05DB`), spell sets (`3FB9:0110`), psionic sets
      (`37F0:001C`) and the AI's spell weights (`37E5:0000`), numbered as
      `SPIN`. A mod replaces them with `rules/creatures.json`;
      `make engine-mod-rules` writes it (and `characters.json`) with the
      original's values; `engine-mod-check` validates rule files.
- [x] The ability bits are named (2026-10-20, `CreatureAbilityBit`;
      `dsun-exe-reference.md` §8 "Ability bits"): regeneration, poison,
      acid, cold, paralysis, disease and the babau's corroding of armour
      and weapons on hit, immunity to charm and hold, to webs and (burrowing
      or flying) to lingering area spells, seeing invisible. Segment `3E45`
      held only 42 masks; the rest is resistances by element (AD&D's "hit
      only by +1 weapons"), now in `CreatureRules.Resistances` with the
      `DamageElement` bits. Nothing tests bits 7, 10, 11 and 28: from the
      manual and clue book 10 and 11 are "+1/+2 weapon to hit", 7 and 28
      likely seeing in the dark and not being surprised.
- [x] The fire elementals' swapped resistance rows (types 32, 33: in the
      original the lesser one needs +2 weapons, the greater +1): a toggle,
      `Fixes.FireElementalResistances` in `rules/combat.json`, off by
      default; on, the fire elemental needs +2 and the lesser +1, both
      untouched by fire (`Battle.FireElementalRows`).
- [x] Combat rules and effects ported (2026-10-20, `DarkSun.Game.Combat`,
      headless): `Battle` runs rounds (initiative, movement points, speed
      conditions), turns, GUARD / WAIT / END TURN, attacks (THAC0, armour
      class with Dexterity, shields and Armor's cap, from behind, backstab,
      natural attacks, Strength), spells, powers and abilities with their
      areas, immunities, magic resistance, resistances, saves, conditions
      with durations or charges, regeneration, poison and acid each round,
      the on-hit effects by ability bit, corrosion and breakage. DSUN.EXE's
      196 spell records, 45 area templates and 34 power records ship as
      `SpellRules` (`rules/spells.json`). Checked over 600 random fights
      with every creature in `SEGOBJEX.GFF`.
- [x] Monster AI (2026-10-20, `Battle.RunAi`): `612E:0BAD`'s minds (flee,
      stand by, Berserk, Confused), target keeping, its steps (spells by
      weight with stepping back, abilities one turn in 4, psionics, attack,
      an adjacent enemy, move, missile weapon, guard), area targeting by hit
      points and the weapon score with re-equipping. Their order, the
      targeting (Random as in the original; Nearest, Weakest, Casters), the
      weapon score and the round's numbers are `CombatRules`
      (`rules/combat.json`).
- [x] Combat details traced and ported (2026-10-20): the area shapes
      (`8BEB:0000`: circle, cone and line in sixteenths of a cell, in sight
      of the centre); lingering areas as fields (`7D80:0C6E`: entering acts,
      staying acts on each step on the ground and each turn in a cloud,
      leaving ends it; walls block, fog hides, fire and dispelling clear
      them); psionics (the power check with Synaptic Static's contest,
      `6698:0CED`; the defence a psionic attack meets, `6698:0E68`; kept-up
      powers' upkeep, `6698:04C7`); a creature's casts from its class slots
      and health (`6798:0059`, `CombatRules.SpellSlots`); Charmed taking
      the caster's side and back (`8053:03C0`, `066F`); Negative Plane
      Protection (which only spends a charge: its save is Vampiric
      Touch's, which allows none); every handler of `80F5:05E6`; the save
      bonuses of `80F5:119C`; the defences of `80F5:14E5`, `1999` and
      `161F`; to-hit and armour class from effects (`80F5:3849`, `39B7`);
      footprints for reach and standing (`1A0A:2BA5`, `11DB`).
- [x] Combat's last details (2026-10-20): Blink (out of reach once its
      holder has acted this round, `80F5:14E5`); the conjured weapons (items
      30024–30026 in the free hand, gone with the condition; `Battle.NewItem`
      makes them); Turn Undead's failure (nothing: `1A0A:0251` is empty);
      whom some spells take (`80F5:1CF7`: persons, undead, sides);
      power ranks (`Combatant.PowerRanks`, `6698:0137`); the halfling's
      bonus (never at combat range); a blow without a weapon bludgeons, and
      a creature's counts as magic by level (`612E:1359`); the AI's walls are
      a point. The oddities are options in `rules/combat.json`'s `Fixes`,
      all off as the original has them: mirror images taking blows, Negative
      Plane Protection's save, Brave against fear, Dexterity against areas,
      no Strength from Absorb Disease, Berserk's side ending, walls from one
      cell, Bless and Aid's filter measured from the right cell. (A gaze
      flag, 0x80, is on no record; a mod can set it in `rules/spells.json`.)
- [x] Power ranks in the character data (2026-10-20): `Character.PowerRanks`
      (saved with the character), and USE on an item that teaches (`RDFF`
      1400–1499, `91F7:03D0`) as `GameWorld.LearnFrom` /
      `Spellcasting.LearnFromItem`: a spell into the spellbook, a power
      learned at rank 1 or raised a rank (to 30), the item used up.
      `Combatant.PowerRanksOf` gives a party combatant its ranks.
- [x] An inventory USE action that calls `GameWorld.LearnFrom` (the item
      window's USE, `91F7:03D0`).
- [x] Tactical combat scene on the region map (2026-10-20,
      `RegionView.Combat.cs`): FIGHT or an attack without a trigger starts a
      `Battle` (`Encounter.Start`) from the party, built from their sheets
      (`PartyCombatant`: THAC0, saves, attacks, armour, weapons, spells,
      powers and ranks; `CharacterSheet.Saves`, `AttackHalves`), and the
      creatures of a fighting allegiance within 24 cells. Events play back
      one at a time: steps walked, rolls with the number needed, damage and
      healing numbers rising over the target, a combat log. The player's
      turn: click to attack (hit chance on hover) or move, CAST with area
      preview, GUARD, WAIT, END TURN, AUTO. Fallen creatures leave bodies and
      experience (`GameWorld.Fell`); spells, PSP and ammunition used go back
      to the sheets. `--fight`, `--check-combat`, `--screenshot`.
- [x] Combat scene, the rest (2026-10-20): the original's combat menu
      (`WIND` 14000, "END name's MOVE" with GUARD / WAIT / END TURN, by the
      member once it can't move, `1587:2EAF`); attack animations (`SCMD`
      action 4, 5 to shoot; `Walker.PlayAction`) and sounds (the attacker's
      +0x44, the hurt target's +0x42 or 4, 5 for a harmless hit); missiles
      (`OJFF` 8101/8117/8500) and spell projectiles flown to the target;
      spell cast and hit graphics and sounds (`SpellRules.Looks`,
      `Projectiles`); the damage splash (`BMP` 5014); field graphics
      (template +3); getting up at the end (`Battle.Finish`) and the game
      over (`WIND` 3004, `BMP` 10004); move triggers mid-fight; the
      thri-kreen's claws and bite and Move 15, everyone else's 1d1 blows.
- [x] Combat looks, the rest (2026-10-20): a burst's animation (`7D80:200D`:
      the template's graphic on every cell of the area where the projectile
      lands, shrinking away over six steps, `Battle.AreaCells`); damage to
      a party member while a window covers the map (`896E:0ED3`: the splash
      on its portrait, no number). The burst uses the area's cells for
      `69CB:0000`'s layout points.
- [x] Keep the rules in data (JSON tables) wherever possible, so they're easy to
      tune and mod (2026-10-20). `characters.json`: the ability tables
      (`AbilityTables`, `DS:07A8`), Constitution's hit points, each class's
      THAC0 factor (`8E77:0416`: 20 − (level − 1) × factor ÷ 12, so priests
      now go 20, 20, 19, 18 as DSUN.EXE has them) and saves (`40B1:006C`),
      attacks a round by warrior level, the unarmed blow, each race's Move,
      natural attacks and Con saves. `combat.json`: the conditions' plain
      to-hit, armour class, cap, save and damage modifiers and the class save
      bonuses. `spells.json`: the cures, Dispel Magic and what it can't end,
      effect 195's afflictions, the target filters (`80F5:1CF7`) and the
      saves set by level or class (Scare, Cloudkill, Chaos). `creatures.json`:
      what rides on a hit (`80F5:1999`). A rule file from before a field
      existed gets the original's (per race and class for `characters.json`).
      Still in code: what depends on a level, alignment or caster (Barkskin's,
      Magical Vestments' and Flesh Armor's caps, Prayer, Protection from Evil,
      Dispelling Evil, Dismissal), the effects' own handlers (`80F5:05E6`),
      the AI's steps' logic, and `Spellcasting.Table` (spell levels and
      casters, a static table the UI reads before any rules are loaded).
- [x] The arena opening (2026-10-20): the gate's no-orders script (GPL 2)
      has the announcer speak, then `REQUEST(14)` (`87B3:0FE9`) stages the
      bout the party watches: Celgor (`OJFF` 296) on the party's side, the
      rampager (171) and two sligs (269) against him, the party's allegiance
      4 so it stays out; the fight's range is from the party's whole side
      (`612E:281F`). The fight starts once the announcement is read, the
      view follows Celgor, and the script goes on when it ends ("Gladiators:
      Step forward"). A fight the party only watched isn't a defeat. Celgor
      gets Dexterity 25 and base armour class −3 (combat record +0x23,
      character record +0x1C and +0x27, as `GPLX` names them), so he lasts
      and casts. The party can't walk or use verbs while a script waits or a
      bout is pending (as in DSUN.EXE, which stays inside the script), and a
      watched fight shows no combat buttons. In any fight each combatant has
      a faint silhouette above the scenery (QOL): a frightened Celgor flees
      north behind the pens' facade (the EXE's flee search, `1A0A:2C86`,
      allows those cells too). A creature that falls leaves the map when
      its fall is played back, not when the battle (which runs a turn ahead
      of the screen) decides it: before, Celgor vanished mid-walk up to two
      seconds before the blow that felled him was shown. After the bout
      `87B3:11AC` (its caller isn't traced; here: when the fight ends), if
      the active member's allegiance is 4, sets the party's back to 1 and
      removes every object of `OJFF` 296, 171 and 269, whoever won. Stage 1
      of GPL 2 waits for every party member to be south of the gate
      (y ≥ 23); a walk ordered before the gate opens replans through it.
      The monster release after it (2026-10-20): CLONE makes each copy
      "other" (`7372:0A44` stores it in `3781:0363`), which GPL 2's release
      (`@2455`) then sets hostile and HUNTs through `obj_special(0x28).72`.
      Without it the orders went to a party member (GETPARTY's "other") and
      the monsters stood at the arena's side while the fight started.
- [x] The rest of the REQUEST codes (2026-10-21): 6 (a cinematic over the
      map; the script resumes after it), 8 (`MONR` random monsters, with the
      EXE's count from the next slot's strength), 15 (the move pointer) and 19
      (game over), and the pictures 200 and 300 REQUEST 6 shows in GPL 177 and
      62 (`PictureView`, #74: `CMAT` n is a `BMP`-format image in `CPAL` n,
      held 1 000 retraces, `60B9:060D`, with the original's palette fades
      out of the map and in and out of the picture, `PaletteFades`;
      `--check-pictures`, `-- --picture=N` over the map). Open: 7 and 17
      for an object that isn't a name (no shipped script asks for either).

---

## 8. Milestone 7: audio

- [x] Music: an XMIDI sequencer (`DarkSun.Data.Audio.XmidiSequencer`: 120 Hz
      ticks, note durations, live FOR/NEXT loops, callbacks) drives MeltySynth
      (`DarkSun.Game.Audio.MusicPlayer`) with a SoundFont found on the system
      or configured; the Godot `Music` autoload streams it. The General MIDI
      versions (`GSEQ`) are played. Fades and track changes follow
      `35D1:0265`/`012D`.
- [x] The music device (#82, `[audio] music_device`, the Options window's
      MUSIC DEVICE): a SoundFont, or the original's FM sound, the Miles AdLib
      or Sound Blaster Pro (OPL3) driver (AIL2.NET) on Nuked-OPL3 with the
      game's `STDPATCH.AD` timbres (`FmSynth`; FM cards play `GSEQ` in DSUN.EXE
      too, `3256:00D3`). AUTO, the default, falls back to AdLib without a
      SoundFont, so no system is silent; a music volume setting. No SoundFont
      is bundled or downloaded: GeneralUser GS asks not to be linked to
      directly, and bundling one is a licensing choice for the project.
- [ ] MT-32 (`LSEQ`): AIL2.NET gives the MT-32 driver's messages, but
      synthesizing them needs Munt and the MT-32 ROMs, which aren't free.
- [x] Sound effects: `BVOC` (Creative `.VOC`) decoded by `Voc` to Godot
      `AudioStreamWAV` (`Sounds`). Sounds 131–180 (MIDI notes for the music
      driver) never play in DSUN.EXE either: `3172:0573` requires the device
      type to be 0 and 3 at once, and nothing asks for them (#82).
- [x] Which music plays where: the menu plays 17; a region plays the track
      in its `RDAT` record (`+0x2F`); cinematics play 26–29 (`CINE.GFF`).
      GPL's `MUSIC` command exists but no script uses it.
- [x] The music level (#70): REQUEST 16 (382 uses), a fight's start (the
      level its creature has in `RDAT`) and its end (level 1) pick a section
      of the track (`MusicSections`: branch markers, the indirect controller
      table, `361A:0000` / `3164:0033`), up to the track's highest level
      (`MusicTracks`, `DS:1675`). There is no other combat or event music:
      `870B:0584` is the preferences' music toggle, and `35D1:01D3` has no
      callers. `--check-music` plays every level of every region track.
- [ ] The per-track flags at `DS:1676` (bit 4 lets a track start, bit 2
      hands it to a hook); all of DS1's tracks have 4.

---

## 9. Milestone 8: saving, cinematics, full game

- [x] Save and load in the engine's own format (JSON: GPL variables, party,
      region states; `SaveGame`, the original's LOAD and SAVE screen `WIND`
      3009). F5 / F9 quick save and load, an autosave on entering a region.
- [ ] Optional: import original save games (format needs reverse
      engineering; #84).
- [x] Cinematics from `CINE.GFF` (`CinematicPlayer`): the intro (ACF 1–8)
      before the main menu, the arrival (9–10) on a new game; the ending
      (11–13) plays through REQUEST 6 from the final scripts. Any key skips. Timed by
      the music's XMIDI callbacks, as in DSUN.EXE.
- [x] Cinematic op 0x7F (`9944:1AD7`, the ending's first picture, #75): a
      pan, the battlefield scrolling across the sky (`CinematicPlayer.Pan`).
      `-- --cinematic=100 --check-intro` plays the ending through.
- [x] Cinematic music (GSEQ 26–29; ops 0x64–0x66).
- [ ] Full playthrough with a checklist of every region, quest and ending;
      compare doubtful behaviour against the original in DOSBox (#83).

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
| ~~M6~~ | ~~Combat rules~~ | Done 2026-10-19: `dsun-exe-reference.md` §8 |
| ~~M6~~ | ~~Combat AI~~ | Done 2026-10-19: `dsun-exe-reference.md` §8 "The AI" |
| ~~M6~~ | ~~AI mind states, conditions, special abilities, spell damage and duration, experience~~ | Done 2026-10-19: `dsun-exe-reference.md` §8 |
| ~~M6~~ | ~~Guard, ability bit names, charges, the defiler XP row~~ | Done 2026-10-20: `dsun-exe-reference.md` §8 |
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
| Shadows, dust and enemy rings with Tab / Enter to choose and attack, after the Obsidian Edition's (done 2026-10-22: `FloorEffects`) | M2, M6 |

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
