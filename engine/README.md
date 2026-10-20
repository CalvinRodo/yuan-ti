# Dark Sun Engine

A new engine for **Dark Sun: Shattered Lands**, built with Godot 4.7 and C#
(.NET 10). It reads the original game files at runtime; no game assets are
stored in this repository. The plan is in
`../.github/instructions/dsun-rewrite-roadmap.md`.

## Layout

| Path | Contents |
|------|----------|
| `src/DarkSun.Data` | Original file formats: game install lookup, GFF and its resources. No Godot dependency. |
| `src/DarkSun.Game` | Game state, rules, GPL virtual machine, saves. No Godot dependency. |
| `game/` | Godot project: scenes, rendering, UI, input, audio. |
| `tests/` | xUnit tests for the two libraries. |
| `tools/DarkSun.Dump` | Hashes every resource, image frame, palette and region render in the game files, and lists every script, for `make engine-compare`. |
| `tools/DarkSun.ScriptRun` | Runs the new-game script, every region script and every trigger they register, for `make engine-scripts`. |
| `tools/DarkSun.MoveCompare` | Compares the engine's A* with the port of DSUN.EXE's pathfinder in every region, and walking speeds with the walk scripts', for `make engine-movement`. |
| `tools/DarkSun.ModTool` | Exports a resource into a mod folder (PNG, text, JSON, GPL source or raw bytes), lists and checks what mods replace, and round-trips every script through the GPL assembler, for `make engine-mod-export` / `engine-mod-list` / `engine-mod-check` / `engine-gpl-check`. |
| `tools/DarkSun.MusicRender` | Renders a piece of the music to a WAV file, for `make engine-music`. |
| `tools/DarkSun.CineDump` | Plays a cinematic without a screen and saves snapshots (PPM), for `make engine-cine`. |
| `DarkSun.sln` | All of the above; the Godot project points to it (`dotnet/project/solution_directory`). |
| `Directory.Build.props` | Settings shared by every project (target framework, nullable, language version). |

Keep code that doesn't need Godot in the libraries, so that it can be tested
with `dotnet test` and reused by tools.

## Requirements

- .NET 10 SDK. If it was installed with `dotnet-install.sh` into `~/.dotnet`,
  add this to your shell profile:
  `export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`
- Godot 4.7.2, **.NET edition**. The Makefile expects it in
  `~/.local/opt/Godot_v4.7.2-stable_mono_linux_x86_64/`; override with
  `GODOT=/path/to/godot`.
- For music, a General MIDI SoundFont (`.sf2`). The engine uses, in order:
  the `DARKSUN_SOUNDFONT` environment variable, `soundfont` under `[audio]`
  in `user://settings.cfg`, any `.sf2` in the Godot user data folder or the
  game folder, then the system's (`/usr/share/sounds/sf2`,
  `/usr/share/soundfonts`, …; FluidR3_GM is preferred over the small
  TimGM6mb that Debian and Ubuntu install by default). Without one the game is
  silent. Synthesis is done by [MeltySynth](https://github.com/sinshu/meltysynth)
  (MIT licence), from NuGet.
- A copy of the game. The engine looks, in order, at the `DARKSUN_DIR`
  environment variable, the folder chosen on the start screen (saved in
  `user://settings.cfg`), and the repository's `dark_sun/` folder.

## Commands

From the repository root:

```
make engine-build   # build everything
make engine-test    # unit tests
make engine-run     # run the game
make engine-edit    # open the Godot editor
make engine-compare # check the C# file-format and script decoding against the Python tools
make engine-scripts # run every game script in the C# interpreter (V=-v lists the world commands)
make engine-mod-export FILE=RESOURCE.GFF TAG=ICON N=2048 MOD=mods/MyMod  # start a mod from a resource
make engine-movement # pathfinding and walking speed against DSUN.EXE's
make engine-mod-list MODS=mods   # what the mods in a folder replace
make engine-mod-check MODS=mods  # build everything they change; list files that can't be used
make engine-gpl-check            # every script → .gpl source → bytes again, must be identical
make engine-music   # render music to out/music.wav (MUSIC=n, FILE=RESOURCE.GFF or CINE.GFF, SECONDS=60)
make engine-cine    # play a cinematic headless, snapshots in out/cine (CINE=0 intro, 1 arrival, 100 ending)
```

`engine-compare` runs the repository's `tools/compare/gff_dump.py` (using `src/gff.py` and
`src/image.py`) and `tools/DarkSun.Dump` over the game files, and fails unless
the two outputs are identical. Run it after changing either side.

Headless checks: that the engine starts and finds the game files (exit code
1 if it doesn't), that every region loads in the region viewer, that the
intro plays through, and that the main menu builds:

```
$GODOT --headless --path engine/game -- --check-install
$GODOT --headless --path engine/game -- --check-regions
$GODOT --headless --path engine/game -- --check-intro
$GODOT --headless --path engine/game -- --check-menu
$GODOT --headless --path engine/game -- --check-mods   # mod list and unusable mod files (exit 1 if any)
```

Starting the game plays the intro (`CINE.GFF` scripts 1–8: the SSI and AD&D
logos, the title, the sword falling to Athas, the story scroll) with its
music. Any key or click skips it; `-- --no-intro` leaves it out. Then comes the original main menu:
Start Game (or S) plays the arrival cinematic and starts a new game, and
Exit (E) quits; Mods (M, bottom right; `-- --mods` goes straight there) opens the mod list. The music changes as in the original: the menu's
theme (17), each region's own track (its `RDAT` record), with 5-second fades
between them. Create Characters (C) opens the View Character screen (see Characters below). Arrow keys and Enter work too. Esc in the region viewer returns to the
menu. The start screen only appears when the game files can't be found.

The region viewer starts a new game where DSUN.EXE does: region 42
(`RGN2A.GFF`), the party at pixel (488, 152), after the game-start script
(`MAS 99`). PgUp/PgDn browse the other regions, with the party in the middle
of the map. The party is four placeholder sprites (any object whose `SCMD`
can walk). Entering a region
runs its script (and, once, the game-start script); characters reaching a
cell fire its move trigger; right-click an object for its verb menu: Talk, Get,
Use, Use with…, Attack and Look, as the object offers them (Talk, Get and Use
with the original's icons; click one, or press its number or T/G/U/L; an
object with only one verb, usually just Look, skips the menu). The
selected character walks up to the object, then its trigger for that verb
runs; Use opens or closes a door and toggles a lever; Use with… asks for a
second object to click; Look works from anywhere and names an object with
no look script. Get and Attack say so when there's no script, since items
and combat aren't in yet. Dialogue (text, portraits, Continue, Yes/No,
menus) opens in a window and pauses the script until answered; TELEPORT moves
the party and creatures. Region transitions are the game's own scripts:
walk out through a map edge facing out of the map (`GPL 80` asks whether
to leave), step into a building's doorway, or Use a door, stairs or portal
whose script sends the party on. The screen fades to black and back,
and the party keeps its members, looks, facing and selected character. As in
DSUN.EXE, arriving runs only the new region's script; the move triggers of
the party's cells run once, when a new game starts. A region the party has
left comes back as it was left (DSUN.EXE saves it too): doors open or
closed, cells scripts blocked, objects moved, taken off the map or cloned,
creatures' allegiance and script fields; its script then runs again, as in
the original. Saved games keep it, with everything else.

Saved games: F5 saves (a quick save), F9 loads it, and entering a region
autosaves; Load Saved Game (L) on the main menu lists every save, newest
first, to load or delete. A save is a JSON file in `saves` in the Godot user
data folder holding the party (sprite, cell, facing, the selected member),
the script globals and engine variables, the trigger table, the creatures'
scripts, the random generator, and every region visited as it was left (the
current one as it is). Loading enters the region without running its script
again, since its triggers are saved. Saving waits until a conversation is
over. A save that can't be read, or comes from another version, says why and
leaves the game in progress alone. `-- --load=quick` starts straight into a
save, `-- --saves` opens the list. Creatures (objects with an `RDFF` combat record) walk where their
scripts send them (HUNT, GO, GOXY, FOLLOW, FLEE, FETCH, WAIT), and run their
no-orders and line-of-sight scripts; RANGE, GETXY, GET_LOS and the other
queries read the world (`DarkSun.Game.World`). Combat and a few
REQUEST codes are still only logged: F2 shows the script log, F6 the script debugger (see Mods).
Items: party members, creatures and containers carry items that scripts give, take, drop and search
for; Get picks an item up; a script's
SHOP opens a store (below) and waits for it to close; the party's money
is `gname[10]`. AWARD gives experience to the party's characters, DAMAGE and PHYS_DAMAGE hurt and kill,
REQUEST 1 and 4 rest the party. Party items and hit points are saved; regions remember what their
creatures carry and items left on the ground. `-- --new-game --give=1051,1095` hands out items by
`RDFF` number; `--check-items` checks it all with the game's items.
Items can only be worn by the classes their type allows. A creature that dies leaves a dead body
holding what it carried.
The character screens (`CharacterScreen`) are the original's windows and art: C shows the
character sheet (View Character, `WIND` 11500: abilities, hit points, armour class, THAC0,
experience, psionics), I the items (`WIND` 13500: the body with its 14 equipment slots, the pack, DROP
and SPLIT), and the bottom icons switch between them. Click a thing, then where it goes (a slot to
wear it, the pack, another member's box to give it); double-click wears or takes off; right-click
describes it. Open on a chest, a bag or a body shows its contents in place of the body, to take or
put in. The bar's other icons are spells and psionics, on View Character's panel: memorize a spell
by clicking it, cast one by clicking it below and then a member's box (outside combat only the
healing ones work); a preserver LEARNs spells into the spellbook, a psionicist picks powers; a rest
(REQUEST 1 or 4) makes every spell ready again. The small buttons by each box make that member leader
and turn computer control on or off. A script's SHOP opens the items as a store: click the goods then
the pack to buy (at the item's value), put things in the six slots by SELL to sell (at half).
Esc on the map, or the wide button, opens the original GAME MENU: the character screens, exit, save
and load, preferences (music and sound, GAME DIFFICULTY scaling creatures' hit points, animations),
the overhead map, centre on the leader, the move, look and attack cursors, and collapse party.
START GAME without a party made plays the pre-made party (`game/party/premade.json`, editable:
the roster's JSON); Create Characters' right-click menu has PRE-MADE PARTY on an empty box and DUAL for
humans of third level or more (a new class; the old one returns once the new passes its level). The
leader button picks who leads (selected when the game starts), computer control is kept for combat.
View Character and the inventory draw what DSUN.EXE draws in their frames: the hit point, psionic and
status bars, hit points and status under the inventory's boxes, and the status line's texts.
`--open-sheet`, `--open-items`, `--open-spells`, `--open-psionics`, `--open-store`, `--open-menu`,
`--open-preferences` and `--open-body` (kills the first creature and opens its body) open them for
testing; `--check-character` clicks through them.
The original screens (main menu, View Character, Character Generation, the text windows) are built
from their windows' data (`UiWindowView`): each control where its `WIND` puts it, with its own art,
so a mod's `WIND/n.json` moves them. The engine's own changes to the original layouts are the same
JSON, in `game/ui/WIND/` (an export has to include `ui/*.json` and `journal/*.json`). `--check-windows` builds every window.
Text on the 320 × 200 screens and in conversations is in the game's font (`FONT` 100, `GameFonts`),
letters in the text colour over a dark shadow as the original draws them.
Conversations use the original's text windows (`WIND` 3007 and 3008): the portrait, the text paged
with MORE (Space, Enter or a click) and the up arrow, choices on five stone bars (1–9, Y and N);
`-- --new-game --talk` starts one, `--check-dialogue` reads a conversation through and pages a long text.
Options (O on the main menu and the map) set the music and sound volume, full screen, skipping the
opening, autosave, the map's information lines and the original pathfinding, and rebind every key;
they are kept in `user://settings.cfg`. The journal (J) keeps every conversation (speaker, portrait,
your replies), events (items, experience, money, new regions) and a quest log. Quests are JSON, in
`journal/quests.json` (a mod's replaces the engine's, `game/journal/quests.json`):

```
{ "quests": [ { "title": "Semyon, the bound prisoner", "region": 42,
    "stages": [ { "when": "gflag[28] == 1", "text": "A bound prisoner, Semyon, begs for water." } ],
    "done": "gflag[26] == 1" } ] }
```

A stage shows once its condition holds: global variables (`gflag[n]`, `gnum[n]`, `gbignum[n]`,
`gname[n]`) compared with numbers, joined by `&&` and `||`; the script debugger (F6) shows which
variables a script sets. `--check-journal` checks it all with a real conversation.
On any screen, `?` lists every key and click that screen takes, with the keys as they are bound now
(`KeyHelp`; `--check-help` fails if a `Controls` action is listed nowhere, `--show-keys` opens it at start). Click: the selected character (green ring) walks there;
Shift+click: the whole party; Tab: select
the next character; N: add a character; C: change the selected character's
sprite. Characters cover a footprint of cells (estimated from the sprite's
width until the game's size data is traced) and never overlap: one blocked by
another waits, goes round if that's short, or gives up after about 4 s.
Arrows, WASD or middle-drag scroll (the camera eases after the selected
character, and follows it again after the next click); mouse wheel or +/-
zoom (½, 1–4; 2, the default, shows what the original showed); PgUp/PgDn change region;
F1 shows blocked cells (red), cells that block line of sight (blue outline),
occupied cells (yellow) and move triggers (magenta); F3 a cell grid (coordinates every 8 cells); F4 each object's index
and `OJFF` number (party green, creatures yellow, doors cyan); with any of
these on, the cell under the pointer is outlined and a panel shows its
coordinates, `GMAP` flags, occupant, objects (with their verbs) and move
triggers (with the script they run); `-- --debug-overlay` starts with all on; Esc goes back. Objects with an `SCMD` script
animate. `--check-regions` also walks the party 20 cells in every region and
fails if two characters ever overlap; `--check-exits` walks the active member into
every move trigger from each side and uses every object with a use trigger,
answering Yes, and lists the region exits it finds and the regions none
lead to (`--only=RGN0E` for one region, `-v` to see each try); `--check-save` saves a changed game, changes it again, loads
it and checks that it came back; `--check-saves` lists the saved games;
`--check-memory` changes every region (doors,
a creature gone, one moved, a clone), leaves and comes back, and checks it
is as it was left; `--check-start` starts a new game and
prints where the party stands and what the scripts did. `--check-verbs` opens every region's verb menus and does every verb
(but Use with) to every object that offers it.

## Mods

Mods are folders of loose files that replace the game's resources; the
original files are never changed. The engine reads mods from the
`DARKSUN_MODS` folder, `mods` in the Godot user data folder
(`~/.local/share/godot/app_userdata/Dark Sun Engine/mods` on Linux), and
`mods` in the game folder, and prints the mods it loaded. In a mods folder:

```
mods/
  load_order.txt                    optional: mod names, highest priority first
  MyMod/
    mod.json                        optional: {"title", "version", "author", "description"}
    ICON/2048/0.png                 frame 0 of ICON 2048 (the other frames stay)
    BMP/20029.png                   a whole image, in its own colours
    TEXT/12.txt                     text, UTF-8
    RDFF/87.json                    a monster's or item's fields: {"combat": {"hp": 99, "name": "Scar"}}
    IT1R/1.json                     item types: {"2": {"damageDice": 2, "damageSides": 6}}
    NAME/1.json                     item names: {"2": "Longbow"}
    RDAT/42.json                    region 42's music and colour cycling: {"music": 5}
    GPL/12.gpl                      a script, as source; assembled when the game loads it
    GPL/80.bin                      raw resource bytes: any tag (scripts, PAL, GMAP, GSEQ, …)
    WIND/3000.json                  a window's layout: {"items": {"BUTN 2048": {"x": 10, "y": 150}}} moves
                                    a control (the screens are built from their windows' data)
    BUTN/2048.json                  a control's size, alignment, icon and text (also APFM, EBOX)
    FONT/100.png                    the game's font as a picture: 16 × 16 cells of 8 × 10, white the
                                    letters, dark their shadow, a row under each marking its width
    BVOC/4.wav                      a sound effect: PCM WAV, any rate, 8 or 16 bits, mono or stereo;
                                    a smpl loop repeats (stored as VOC; .voc files are taken as they are)
    RGN2A.GFF/ETAB/42.bin           only in that file; a file folder may also add resources
    RESOURCE.GFF/BVOC/19.wav        so a new sound goes here: sounds 19, 30, 87 and 127, which
                                    scripts play, have no BVOC in the game
  _Disabled/                        names starting with _ or . are skipped
```

`make engine-mod-export` writes a resource in the right form to edit:
images are drawn in a palette (`--palette FILE:N` for the tool) and come back
in true colour; in cinematics they are matched to the colours on screen.
Items, monsters, regions and names come out as JSON with every known field
(`--bin` for raw bytes instead); delete the fields you don't change, since a
JSON file is applied as edits to the original, leaving undecoded bytes alone.
Fields are listed in `DarkSun.Data.Mods.RecordJson`; `"bytes": {"0x1c": 5}`
sets bytes no field names yet, and keys starting with `_` (the exporter's
name hints) are ignored.

Scripts come out as `.gpl` source in the listing's syntax, with labels for
jumps and branches (`IF(@L0042)`, `L0042:`), `.org` before each address
other scripts enter the file at (triggers, CALL_GLOBAL), and strings with
`\"` and `\xNN` escapes; every shipped script assembles back to the same
bytes (`make engine-gpl-check`). The assembler (`GplAssembler`) also takes
`;` comments, `.bytes`, menus as `MENU` / `TITLE(…)` / `ENTRY(…)` / `MENU_END`,
and `:w` `:b` `:s` `:l` suffixes to choose how a number is stored. If an edit
makes a function too long for the next `.org`, move the extra code to the
end of the file and `JUMP` to it; the error says by how much.

### Script debugger

F6 in the region view opens the script debugger. It shows a script as source:
the mod's own `.gpl` file if a mod replaces the script (and the file still
assembles to what the game loaded), otherwise the source `ModTool export`
would write, ready to copy into a mod. Type `GPL 12` or `MAS 42` to open a
script; the region's `MAS` script shows first.

- **Breakpoints:** click left of a line number, or Ctrl+B / Break on the
  caret's line. The game pauses before running that command, and the
  debugger opens. Breakpoints are kept between runs in the user data folder's
  `breakpoints.txt`, one per line (`GPL 12 @0040`); the Breakpoints tab lists
  them.
- **Break on next:** pauses at the first command of the next script the game
  starts (a region, a trigger, a verb), but not creature scripts, which run
  every half second. `-- --new-game --break-on-start` pauses the game-start
  script `MAS 99` at its first command.
- **Paused:** the command about to run is marked, and the game waits as it
  does for a conversation. F8 continues; F10 steps over (calls, menus and
  cross-file calls run through); F11 steps into; Shift+F11 steps out of the
  function; Stop drops the script. The Stack tab lists the call frames, and
  clicking one shows where that caller goes on. Scripts the game would have
  started meanwhile (the region's, after the game-start script) run once the
  paused one ends. Saving waits until then.
- **Variables:** lists the accumulator, actor, target and other, each local
  and global variable that isn't 0, and `gname[0–14]`. While paused, values
  changed since the last pause are yellow. Type `gnum[5] = 3`, `gflag[83] = 1`
  or `gstr[1] = "Ral"` to set one.

`--check-debugger` pauses the game-start script, steps it, stops the region
script at a breakpoint set by a gutter click, sets a variable and runs on. It
then shows every script as source and checks that each command maps to a line.

A mod file that can't be read, parsed or assembled doesn't stop the game:
the original resource is used, and the problem is logged and shown in the
mod list. The mod list (M on the main menu) shows every mod with its
`mod.json` details, how many resources it changes and its problems; it
turns mods on and off (renaming the folder with a leading `_`) and moves
them up and down (`load_order.txt`), applying changes at once.
`make engine-mod-check` reports problems from the command line.

Decoded images and palettes are cached for the session
(`Session.Assets`), with one texture per frame and palette, so returning to a
region or screen doesn't decode again.

## Characters

Create Characters (C) opens the View Character screen (`WIND` 11500 over
`BMP` 11000): four character boxes and the selected character's details.
An empty box offers NEW and ADD, a full one EDIT (NAME once the character
has adventured) and DROP, from the bottom buttons or a right-click on the
box, as in the manual. NEW and EDIT open the Character Generation screen,
laid out as `WIND` 3011 with the original art: body portraits (`BMP`
20000–20013; left-click the portrait for the next race and gender,
right-click for the previous), the class list (`ICON` 2002–2009), the die
(`BMP` 20048–20058, new abilities and hit points), the psionic disciplines
and clerical spheres (`ICON` 2038–2047), EXIT and DONE. Left-click an
ability, the hit points or the alignment to raise it, right-click to lower
it; the map icon is the original's for the race and gender (`OJFF` 300–313),
and clicking it picks another; new characters get a random name from `TEXT` 0–32 (male), 100–118
(female) or 200–207 (thri-kreen). The screen's backdrop is drawn by the
engine: the original's frames (`APFM`) aren't decoded.

The rules (`DarkSun.Game.Characters.CharacterRules`) are DSUN.EXE's own where
traced (its tables in segment `38D4`; `dsun-exe-reference.md`, "Character
creation"): racial adjustments (the half-giant's differ from the manual's: Dex
and Int −5, Wis and Cha −3), each ability the best of four rolls of 4d4+4 plus
the adjustment, up to 20 plus the adjustment, and at least 17 in each class's
key ability and the class's floor (9–14) in the others; hit points a die per
level of each class (half-giants double), averaged over the classes, with
Constitution's bonus; 3000 experience in each class to start (a fighter starts
at level 2); and the map icon. The rest is the manual's: the classes each race
may take (humans one; the others up to three, never cleric with druid), good
or neutral alignments, one psionic discipline (psionicists all three) and a
sphere for clerics, druids and rangers. Where the manual contradicts itself,
half-giants can't be rangers and muls and thri-kreen can't be druids. A mod can replace the rules with
`rules/characters.json` in its folder (the same JSON `CharacterRules` reads).

What creatures can do in combat beyond their `RDFF` records is in DSUN.EXE, not the data files, so
the engine ships it as `DarkSun.Game.Rules.CreatureRules` (`dsun-exe-reference.md` §8, "The AI"): the
special abilities each type has (combat record +0x16; `3E45:0000`, named by `CreatureAbilityBit`:
regeneration, poison on hit, immunity to charm, …) and the 11 the AI tries, in order, one turn in 4
(a fire cone, Fireball, paralysis, a web, Fear …); what each type resists, by `DamageElement`
(shadows are hit only by magic or +1 weapons, elementals by +2, spiders ignore poison); the spell sets (character record
+0x3E; `3FB9:0110`) and psionic sets (+0x46; `37F0:001C`); and how much the AI wants each spell
(`37E5:0000`: Fireball 63, Magic Missile 40, …). Spells and powers are numbered as `SPIN`. A mod can
replace them with `rules/creatures.json`.

Combat itself is `DarkSun.Game.Combat.Battle`, a port of DSUN.EXE's rules that knows nothing of the
screen (`dsun-exe-reference.md` §8): rounds with initiative and movement points, turns in initiative
order, GUARD, WAIT and END TURN, attacks (THAC0, armour class, from behind, backstabs, natural
attacks), spells, psionic powers and monster abilities with the original's circles, cones and
lines, saves with their bonuses, magic resistance, psionic defence and resistances, and effects that
last rounds or a number of charges (Armor's hits, Mirror Image's images, Poison's rounds). Areas that
linger (fog, webs, clouds, walls) stay on the map as `Battle.Fields`, acting on whoever enters or
stays in them; walls block and fog hides. A creature's casts come from its classes and health, a
charm takes it to the caster's side, and larger creatures stand and reach by their footprint. Where the
original is odd (mirror images ignore blows, Negative Plane Protection never saves, …) `combat.json`'s
`Fixes` turn on a fix; all are off by default. A creature's abilities ride on its hits by ability bit (a spider's poison,
a babau corroding armour and weapons), and its immunities keep effects off it. `Battle.RunAi` is the
original AI (`612E:0BAD`): its steps (spells, ability, psionics, attack, move, missile, guard), their
order, how it picks a target (at random as in the original, or the nearest, the weakest, casters
first), its weapon score and the round's numbers are `CombatRules` (`rules/combat.json`); what each
spell, power and monster effect does is `SpellRules` (`rules/spells.json`, DSUN.EXE's 196 records
and 45 area templates, one a line). `Battle.Log` lists what happened for the combat log. The tactical scene that drives it
is still to come.

`make engine-mod-rules MOD=mods/MyMod` writes all four rule files (`characters.json`,
`creatures.json`, `combat.json`, `spells.json`) with the original's values to start from, keeping any
that are there, and `make engine-mod-check` reports rule files that can't be read.

The party is kept in `party.json` in the Godot user data folder and starts
the next new game (no party: four placeholder characters); characters made
or dropped are kept in `characters/` for ADD. Party members walk as their
chosen sprite and dialogue calls them by name; saved games keep them.
`-- --party` and `-- --chargen` open the screens; `-- --check-chargen` makes
a character of every race, gender and class and checks the art is there.

## Sound

Music: see the SoundFont notes above. Sound effects come from the game
(`BVOC` in `RESOURCE.GFF`): what animations play (`SCMD`) when the object is
on screen, and what scripts play (SOUND). `[audio] sound_volume` (0–1, 0
for none) in `user://settings.cfg` sets their loudness.
`-- --check-sounds` decodes every effect.

## Display

The game is drawn for a 640 × 400 viewport (twice the original's 320 × 200)
and shown at the largest whole-number scale that fits the window; the
viewport grows to fill the rest (`PixelScaler`), so pixels stay square and
sharp at any window size. `-- --check-display` prints the window, viewport
and scale; `-- --new-game` goes straight into a new game.

## Notes

- Every project targets `net10.0`. `game/DarkSunEngine.csproj` repeats it,
  because Godot adds `net8.0` to a project that doesn't name a framework.
- Commit the `*.uid` files Godot creates next to scripts and resources; they
  keep references stable when files move.
