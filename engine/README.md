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
- For music, nothing more: without a SoundFont the engine plays the
  original's AdLib music. For General MIDI music, a SoundFont (`.sf2`): the
  engine uses, in order, the `DARKSUN_SOUNDFONT` environment variable,
  `soundfont` under `[audio]` in `user://settings.cfg`, any `.sf2` in the
  Godot user data folder or the game folder, then the system's
  (`/usr/share/sounds/sf2`, `/usr/share/soundfonts`, …; FluidR3_GM is
  preferred over the small TimGM6mb that Debian and Ubuntu install by
  default). SoundFonts are synthesized by [MeltySynth](https://github.com/sinshu/meltysynth)
  (MIT licence); the FM music by the Miles AIL drivers of
  [AIL2.NET](https://github.com/codengine/AIL2.NET) (MIT) on
  [NukedOPL3Sharp](https://github.com/codengine/NukedOPL3Sharp) (LGPL-2.1),
  all from NuGet.
- A copy of the game. The engine looks, in order, at the `DARKSUN_DIR`
  environment variable, the folder chosen on the start screen (saved in
  `user://settings.cfg`), and the repository's `dark_sun/` folder.

## Commands

From the repository root:

```
make engine-build   # build everything
make engine-test    # unit tests
make engine-run     # run the game (ARGS="--new-game --fight" passes flags to it)
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
the two outputs are identical. Run it after changing either side. The script
listings compare the engine's names for REQUEST codes (`REQUEST(MUSIC_LEVEL, …)`):
`tools/compare/gpl_listing.py` reads them from `GplRequests.cs`, so a code
renamed or added there needs no change on the Python side. A GFF file that
isn't valid (an empty `DARKRUN.GFF`) is skipped by both.

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
(`MAS 99`). There the arena's gate runs the opening: the announcer speaks,
then the party watches the mage Celgor fight a rampager and two sligs
(`REQUEST(14)`), and once it is over the gladiators are called into the arena.
The opening combatants are removed afterward, whoever won, as in DSUN.EXE.
The next announcement ("Citizens of Draj!") waits for all four party members
to leave the starting pen through the gate. Then the gate closes behind them and the
announcer releases the monsters: GPL 2 clones them at the arena's far side,
(13, 32) or (51, 32), and has them HUNT the party; they walk in and attack
when one comes within 5 cells (its line-of-sight script, `GPL 74` @0059, FIGHTs).
`-- --new-game --check-arena` plays the whole opening; `--check-arena-release`
starts at the release. A walk ordered before the gate
opens continues through it if it opens while the party approaches.
The arena's bound prisoners (GPL 5) become an empty frame and either a freed
man or a body (`OJFF` 1258); a creature that dies leaves a body only if it
carried something (`612E:2659`), as in DSUN.EXE. `--check-prisoners` unties
them, then leaves the arena and comes back to check what they left.
The game clock (`gname[9]`) runs as DSUN.EXE's does, so timed scripts move
on; thief skills, ability rolls and traps are rolled as the EXE rolls them
(the skill tables are in `characters.json`). The shipped scripts' known bugs
found by the OpenDS project (Darkhold's dead triggers, the Lava Rifts ranger,
Linara's conversation) and here (the slave pens' halfling greeting a
halfling, region 8's ranger tracking, the sewer grate's "hack the lock off", a Strength roll when it is attacked, and the Rebel Mindhome's reward given at every talk)
are repaired as they load; the Options window's
"Repair scripts" (`script_repairs`) turns that off.
PgUp/PgDn browse the other regions, with the party in the middle
of the map. The party is four placeholder sprites (any object whose `SCMD`
can walk). Entering a region
runs its script (and, once, the game-start script); characters reaching a
cell fire its move trigger; right-click an object for the original's verb
window (`68BD:0040`): Get, Talk and Use, greyed where the object doesn't offer
them, and INFO (Look, or Open for a container); an object offering only Talk,
only Use or only Open has it done at once (a fire pit rests the party). Attack is
the attack pointer's; to use an item with something, pick it up in the
inventory and leave it: it rides on the pointer (pointer mode 3) until clicked
on the second thing; clicked on the bare ground, it is put down on that cell,
and clicked on a party member, it goes in their pack. Get on an item on the
ground doesn't put it away: it rides on the pointer the same way (its pickup
trigger has run). Put it on a character to take it: click one on the map, or
open the inventory, where it is still picked up, and click a slot. Esc puts
it back where it lay. The selected character walks up to the object, then its
trigger for that verb runs; Use opens or closes a door and toggles a lever;
Look works from anywhere and names an object with no look script. Open on a
chest, a bag or a body on the map (a chest showing open, as Use leaves it, its opening playing even while
the pointer holds the map) shows the original's container window
(`WIND` 13501, `6527:002C`): six slots holding what the container's `RDFF`
links to it; click a thing to take it into the opener's pack. Coins (`RDFF`
1384–1389, 1399) are never carried: taken from the map (at once, no verb
window), a container or a bag, they become the party's money. In the
inventory, double-click a carried bag or box to open it in the body's place
(`6527:12BD`: shut for a moment, then open; a bag's six slots above a box's)
and again to close it (or right-click an empty slot of it); it holds six
things, and what it holds stays in it when it is dropped, picked up or saved.
Right-click an item, in the inventory or a container, for the item window
(`WIND` 15502, `91F7:0C29`): its picture, value and name, a weapon's damage;
INFO turns it to who in the party may use it. Its USE (in the inventory, `91F7:03D0`) uses
the item: a quest item runs its use trigger (Nagi's journal is read); a scroll teaches its spell or power;
a wand, a necklace or fruit casts its spell at level 6 and uses a charge (the item is gone at 0): in a
fight, on the member's turn, a spell on oneself at once and any other aimed like CAST's; outside one, a
spell on oneself heals or takes effect (and a lemon makes its eater whole), and any other is aimed on the
map with the spell's pointer (right-click puts it away): a help on a party member, anything else on a
creature, which starts the fight with the spell cast first. A weapon's spell reaches whom it hits. Worn
things whose spell helps have no USE (their spell does nothing worn, as in the original).
`-- --new-game --check-map-cast` aims a wand out of a fight. Get and Attack say so when there's no script, since items
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

The game menu, the LOAD and SAVE screen and the preferences stop the game while they are up, a fight too.

Saved games: F5 saves (a quick save, QUICKSAVE in the lists), F9 loads it,
and entering a region autosaves (AUTOSAVE). The game menu's LOAD/SAVE GAME
and Load Saved Game (L) on the main menu open the original's LOAD and SAVE
screen (`WIND` 3009): every save, newest first, ten at a time, by name, with
the selected one's date, time, region and party beside the list. Saving
names the game (the first line is a new one; another is replaced after
asking), and DELETE removes one after asking. A save is a JSON file in `saves` in the Godot user
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
queries read the world (`DarkSun.Game.World`). Every REQUEST code the
shipped scripts use is carried out (`WorldGplHost.Request`): REQUEST 6 plays
the ending's cinematic over the map, the script waiting until it is over (the
pictures 200 and 300 aren't decoded, so those go straight on); 8 picks a random
monster for the region from `MONR` and how many to make; 15 goes back to the
move pointer; 19 is a game over (the party-has-fallen window, where DSUN.EXE
goes straight back to the start). Each code has a name in `GplRequests`
(`SET_DOOR`, `RANDOM_MONSTER`, …), the only place the numbers are written:
the host dispatches by name, and the script log (`REQUEST(SET_DOOR=2, …)`)
and the debugger show it. F2 shows the script log, F6 the script debugger (see Mods).
Items: party members, creatures and containers carry items that scripts give, take, drop and search
for; Get picks an item up; Open shows a container's contents in its own window, and so does Use
on a chest or other container it swaps to its open picture (DSUN.EXE's Use only swaps the picture); a script's
SHOP opens a store (below) and waits for it to close; the party's money
is `gname[10]`. AWARD gives experience to the party's characters, DAMAGE and PHYS_DAMAGE hurt and kill,
REQUEST 1 and 4 rest the party. Party items and hit points are saved; regions remember what their
creatures carry and items left on the ground. `-- --new-game --give=1051,1095` hands out items by
`RDFF` number; `--check-items` checks it all with the game's items.
Items can only be worn by the classes their type allows. A creature that dies leaves a dead body
holding what it carried.
The character screens (`CharacterScreen`) are the original's windows and art: C shows the
character sheet (View Character, `WIND` 11500 at (0, 9) over the menu's fire, its banner over it:
VIEW•CHARACTER, USE on the spell pages, EFFECTS; the panel as Create Characters writes it, the
abilities, three readied weapons, race, alignment, the classes and levels with a cleric's, druid's or
ranger's in its element's colour, experience, hit and psionic points, armour class and damage; the
selected box in its yellow frame and the page's icon pressed), I the items (`WIND` 13500: the body with its 14 equipment slots, the pack, DROP
and SPLIT; on the right `BMP` 13004 with the abilities, PSI, AC and the attacks, `74F1:37B3`; the money
under the window, `74F1:1C92`; what happens in the description box), and the bottom icons switch between them. Click a thing, then where it goes (a slot to
wear it, the pack, another member's box to give it); double-click wears or takes off; right-click
opens the item window (and describes it in the panel). A carried bag or box opens in place of the
body, to take or put in. The bar's other icons are CAST SPELL/USE PSIONIC and CURRENT SPELL EFFECTS (the spells
and powers on the member: a fight's, or the spells cast on the map still running), on View Character's panel. As in DSUN.EXE
nothing is memorized: a caster has a number of casts at each spell level (`DS:4B08` wizard, `DS:4B11`
priest, a byte a level; `78A9:5344` takes one a cast), and every spell it knows at a level can be cast
while that level has some left ("2 LEVEL 1 SPELLS TO CAST"; the spells of a level with none left, or
of a member not Okay, are crossed out, and the pointer on one says YOU NEED TO REST). The spells fill the
panel's slots in their numbers' order, as DSUN.EXE does (`8F23:19B6`); MAGE / CLERIC / PSIONIC goes round
the kinds and LEVEL n round the levels, and the points and status show under the boxes. Click a spell, then a member's box, to
cast it (in a fight CAST lists them all). Out of a fight the healing ones heal, and those that put a condition on
the party (Bless, Haste, Enlarge, Strength, Protection from Evil, Biofeedback …) take effect as they would in a fight on
the members shown (collapsed, the leader alone), and last on the game clock into the next fight; a spell still
running when a fight ends goes on after it, a power ends with it; damage and summoning wait for a fight. A combatant
hit in a round can't cast or use a power until the next (YOU WERE HIT; an item's spell is still cast). A new preserver
knows the original's starting spells for its preserver level (`6ECD:1977`: Grease and Magic Missile; at
level 2, where the starting experience puts it, Shield and Wall of Fog too; at 3 Fog Cloud and Stinking
Cloud; `CharacterRules.PreserverStartingSpells`) and, on gaining a preserver level, chooses one more in DSUN.EXE's
study window (`WIND` 17500, CHOOSE A SPELL: the spells it doesn't know up to level (preserver level + 1) ÷ 2;
EXIT before choosing asks YOU HAVEN'T CHOSEN and gives the choice up). Levels come with the level check, after an
award and at the end of a fight (`8E77:08A6`): each one says "%s is %d%s level %s" in the message box, rolls the
class's hit die (d8 priests, d10 warriors, d4 preservers, d6 psionicists and thieves; at least 1, more at Con 20+;
doubled for a half-giant; `8E77:0000`; a human's new class only past its old classes' levels) into maximum and
current hit points, and a psionicist's opens PICK A PSIONIC POWER (`WIND` 17501): the discipline's powers under
rank 30, a new one learned or a known one enhanced a rank, 11320 going round the disciplines; EXIT with choices left
asks YOU HAVE MORE. A gladiator's armour class improves by 1 from level 5 (the guide's; not traced). As DSUN.EXE
(`6ECD:1871`) does when a character is made and again for each member when a game starts, every power is
forgotten and a psionicist learns those of its psionicist level, anyone else four of its discipline's, each
with power 169 (`CharacterRules.DisciplineStartingPowers` and `PsionicistStartingPowers`); PSIONIC shows the
powers known in a discipline (LEVEL's button goes round the disciplines), crossed out without the points for
one, and scrolls teach more. A rest (REQUEST 1 or 4) fills the casts again. The small buttons by each box make that member leader
and turn computer control on or off. A walk click where no way leads says NO PATH FROM HERE, and a verb on
something out of the leader's sight NO LINE OF SIGHT, in the original's message box (`WIND` 10501 over `BMP`
10001), until a click or a key. A script's SHOP opens the items as the original's store: the
goods in the six slots on the right, priced (a stack at its value times its quantity), MORE turning their
pages; a click buys one, and the merchant makes another of what it has two of. SELL, with a thing held (or
the thing put on the goods), asks "I'll give you N$" for the same value: TAKE IT or LEAVE IT. Quest items
(worth 9999) aren't traded.
Esc on the map, or the wide button, opens the original GAME MENU: the character screens, exit, save
and load, preferences (music and sound, GAME DIFFICULTY scaling creatures' hit points, animations),
the overhead map, centre on the leader, the move, look and attack cursors, and collapse party (its icon
the party while it is spread out, one figure while it is gathered).
On the map the original's colours turn (`ColourCycling`): ranges 1–5, 6–10, 11–15 and 240–248 from the
start of the game and the region's two `RDAT` ranges, at 12 steps a second, so fire, silt, the
highlighted icons (the spell button's flames, the menu button's lava) and enchanted items flicker;
`--check-colours` checks it (with a window, on the screen too).
A right-click on the map turns the pointer the same way (MOVE, ATTACK, LOOK, a held item). As in
DSUN.EXE (the clock runs only under the move pointer, `1DF3:29E8`), out of a fight the map holds still
under any pointer but MOVE (creatures, animations, the clock) until a chosen verb sends the character on.
As in the original (the manual: "by default, only the leader of your party appears on the map"), a new
game shows the leader alone, the others gathered into it, and they gather again after each fight; COLLAPSE
PARTY spreads them out and they stay so until it is pressed again (Options → Leader alone off starts new
games with everyone shown). The Dark Sun icon in the top left corner (`BMP` 5012) opens the character
screens at the page last shown; a right click opens quick cast: the six spells and powers the member chose
last, an orange X over those it can't use now; one clicked is cast as from the Cast Spells page (in a fight
at once, else that page opens with it picked). `--check-quick-cast` tries it.
COLLAPSE PARTY makes the other members walk into the selected leader and disappear
(as in the original, quickly: three times their walking pace; Options → Quick collapse off walks them in);
they travel with that character, still present for scripts, dialogue and character screens.
COLLAPSE PARTY again, Shift+click, selecting a different map character or starting combat spreads the party
onto separate free cells again. Saved games and region changes keep a collapsed party.
OVERHEAD MAP (the game menu) shows the whole region, the game stopped, the party's members as steady dots and
the enemies left on it as flashing red ones; a click, Esc or O goes back to the view as it was
(`--open-overhead` opens it at once).
The original's other keys are there too, on keys the engine left free: M the overhead map (the original's O,
the options here), V as well as C View Character, K cast a spell (the original's C), 1–4 make that member
the leader, 5 shows the whole party (spread out) and 6 the leader alone (collapsed), H back to the party,
Alt+A animations on or off, Ctrl+S save, Ctrl+L load, Alt+X exit (asking to save). The key help (?) lists them.
START GAME without a party made plays the pre-made party (`game/party/premade.json`, editable:
the roster's JSON); Create Characters' right-click menu has PRE-MADE PARTY on an empty box and DUAL for
humans of third level or more (a new class; the old one returns once the new passes its level, and until then
its gear can't be used: what it alone could wear goes to the pack). A second dual-class needs second level in
each class so far. In the game a right-click on a member's box, on View Character or the inventory, opens the
same DUAL, fights included. Raise Dead doesn't bring back an elf or a half-elf (the manual; the guide for
half-elves); the game's own raisings do. The
leader button picks who leads (selected when the game starts), computer control is kept for combat.
View Character and the inventory draw what DSUN.EXE draws in their frames: the hit point, psionic and
status bars, hit points and status under the inventory's boxes, and the status line's texts.
`--open-sheet`, `--open-items`, `--open-spells`, `--open-psionics`, `--open-store`, `--open-menu`,
`--open-preferences`, `--open-save`, `--open-load` and `--open-body` (kills the first creature and opens its body) open them for
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
opening, autosave, the original pathfinding and the rest below (GAME),
and rebind every key (KEYS); they are kept in `user://settings.cfg`. The original has no options
screen: it is drawn in the stone of its LOAD and SAVE window (`WIND` 3009, as the mods screen), one
option to a slot bar with its value on the right, ten to a page; a click changes it, a right-click
steps back, the line under the window says what the bar under the pointer does
(`-- --show-options` opens it at start). The journal (J) keeps every conversation (speaker, portrait,
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
Shift+click: the whole party; with "Box selection" on in Options
(`[game] box_select`, off by default: DSUN.EXE has no such thing), dragging a box out of a fight picks
the characters it touches (faint green rings; the leader becomes one of them) and a click walks only
them while the rest stay; a box round no one, choosing another leader, collapsing, a fight or a new
region drops the picks. Tab: select
the next character; N: add a character; C: change the selected character's
sprite. Characters cover a footprint of cells (estimated from the sprite's
width until the game's size data is traced) and, except when gathering into the leader, never overlap: one blocked by
another waits, goes round if that's short, or gives up after about 4 s.
As in the original, a click or the walking keys walk the selected character and the others
follow it, each keeping to the free cell next to it nearest to them (DSUN.EXE's chase target
chooser); Options → Party follows off makes each walk alone, as box selection and Shift+click
(the whole party to the click) do anyway.
Hold W, A, S, D to walk the selected character (two of them walk diagonally; keys can be changed in Options, and
the ones saved there, `[keys]` in `settings.cfg`, are kept);
releasing them stops after the current step. They move the active combatant one
cell per key press in combat, using its movement allowance. Menus and dialogue
block keyboard walking. The arrow keys or middle-drag scroll (the camera eases after the selected
character, and follows it again after the next click); mouse wheel or +/-
zoom (½, 1–4; 2, the default, shows what the original showed); PgUp/PgDn change region;
F1 shows blocked cells (red), cells that block line of sight (blue outline),
occupied cells (yellow) and move triggers (magenta); F3 a cell grid (coordinates every 8 cells); F4 each object's index
and `OJFF` number (party green, creatures yellow, doors cyan); with any of
these on, the cell under the pointer is outlined and a panel shows its
coordinates, `GMAP` flags, occupant, objects (with their verbs) and move
triggers (with the script they run); F7 shows the map information lines (region file, object and trigger
counts), hidden at first; `-- --debug-overlay` starts with all on; Esc goes back. Objects with an `SCMD` script
animate. `--check-regions` also walks the party 20 cells in every region and
fails if two characters ever overlap; `--check-exits` walks the active member into
every move trigger from each side and uses every object with a use trigger,
answering Yes, and lists the region exits it finds and the regions none
lead to (`--only=RGN0E` for one region, `-v` to see each try); `--check-save` saves a changed game, changes it again, loads
it and checks that it came back; `--check-saves` lists the saved games;
`--check-memory` changes every region (doors,
a creature gone, one moved, a clone), leaves and comes back, and checks it
is as it was left; `--check-start` starts a new game and
prints where the party stands and what the scripts did: with `--region=N
--at=X,Y` somewhere else, `--set=gnum[12]=1;gflag[11]=1` with script
variables as earlier scripts would have left them, and `--idle=N` after N
ticks of answering for the player, counting how often each script ran (a
script that keeps firing stands out). `--check-verbs` opens every region's verb menus and does every verb
(but Use with) to every object that offers it.

## Builds and CI

`make engine-export` exports the engine for Linux, Windows and macOS into
`build/` from the presets in `game/export_presets.cfg` (it needs Godot's .NET
export templates for 4.7.2). The builds hold only the engine: players point
them at their own copy of Dark Sun. The macOS build is ad-hoc signed, not
notarized, so it is opened with right-click > Open the first time.

GitHub Actions (`.github/workflows/`):

- `ci.yml`, on every pull request and push to `main`: builds the solution
  (libraries, tests, tools and the Godot project), runs the unit tests
  (their `.trx` results are a run artifact), compiles the Python toolkit and
  imports its web server, and exports the Linux build to catch a broken export.
- `release.yml`, on every push to `main` (a merged pull request): runs the
  unit tests, exports Linux, Windows and macOS builds and publishes them as a
  GitHub release tagged `v0.1.<run number>`, with notes listing the pull
  requests merged since the last one: `DarkSunEngine-linux-x86_64.tar.gz`,
  `DarkSunEngine-windows-x86_64.zip` and `DarkSunEngine-macos-universal.zip`.

Godot and its templates come from the official release
(`.github/actions/setup-godot`, cached between runs); change `GODOT_VERSION`
in both workflows to move to another version. The headless checks
(`-- --check-*`) need the game files, so they aren't run in CI.

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
    RDAT/42.json                    region 42's music, fights' music levels and colour cycling: {"music": 5, "fightLevel": 2}
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
`:w` `:b` `:s` `:l` suffixes to choose how a number is stored, and REQUEST
names for their codes (`REQUEST(SET_DOOR, -98, 0, 0)`; exported source uses them). If an edit
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
mod list. The mod list (M on the main menu), in the stone of the original's
LOAD and SAVE window, shows every mod by its `mod.json` title (else its
folder's name as words: `better_shops` is Better Shops), on or off; the
selected one's version, author, description (as many lines as it takes),
folder, how many resources it changes and its problems. It turns mods on
and off (renaming the folder with a leading `_`) and raises and lowers
them (`load_order.txt`), applying changes at once.
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
at level 2); the map icon; the classes each race may take and in which
combinations (humans one; a gladiator never with another; half-giants two at most; DSUN.EXE's table,
`RaceRules.Combinations`, where the manual has no half-giant rangers or mul and thri-kreen druids); the
alignments each class allows (none evil, a druid True Neutral, a ranger good, a thief not Lawful Good: the
alignment button steps past the others, and taking a class moves the alignment to one it allows); dual-classing
for humans from the class's second level into a class of another group; and psionic strength. The rest is the
manual's: one psionic discipline (psionicists all three; it gives the starting powers) and a sphere for clerics,
druids and rangers, which start unchosen, as in the original: DONE asks for them. A click on an ability or the
hit points goes round past its top to its bottom and back; a race that keeps no class gets the fighter again.
`-- --check-chargen` makes every combination of every race. A mod can replace the rules with
`rules/characters.json` in its folder (the same JSON `CharacterRules` reads; a file need only hold what
it changes). The same file holds what
the character sheet and combat work out: the ability tables (Strength's damage and to-hit,
Dexterity's reaction and defence, Constitution's poison save, Wisdom's magical defence), Constitution's
hit points, each class's THAC0 factor and saving throws, attacks a round by warrior level, the
unarmed blow, and each race's Move, natural attacks (the thri-kreen's claws and bite: with a weapon in
hand its limbs swing the weapon and it still bites), base armour class (10, a thri-kreen 5) and whether it
wears only weapons (a thri-kreen: no armour, shield or other worn gear, only weapons and ammunition). A race or
class that leaves a field out gets the original's.

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
first), its weapon score, the round's numbers and what each condition adds to hitting, armour
class, saves and damage (Bless +1, Shield caps armour class at 4 from the front, the protections
halve their element) are `CombatRules` (`rules/combat.json`); what each spell, power and monster
effect does is `SpellRules` (`rules/spells.json`, DSUN.EXE's 196 records and 45 area templates, one
a line, with the cures, what Dispel Magic can't end, whom effects take and the saves set by level).
What rides on a creature's hits is `CreatureRules.OnHit`. `Battle.Log` lists what happened for the combat log.

Fights happen on the region map. FIGHT, or attacking a creature that has no attack trigger, starts one
(`Encounter.Start`): the party members standing, each built from the character sheet
(`PartyCombatant`: THAC0, saving throws and attacks of the classes in use, armour from what is worn,
the weapons in hand and missile slot, the spells known with their levels' casts left, powers and their ranks), and the creatures of a
fighting allegiance within 24 cells. The battle plays back step by step as DSUN.EXE shows it:
attackers play their attack animation with its sound, missiles and spell projectiles fly to their
targets, spells show their graphics over caster and target with their sounds, blows that hurt sound
the target's cry, damage shows as a number on the original's splash (its look by what hit), centred on
the figure for 40 ticks (`896E:0A0E`; a party member's on its portrait while the character screen shows it), and
lingering fields show their graphic on every cell. The summonings (Monster Summoning I–III, the Conjure Elemental
spells, Summon Shadow, Summon Insects, Dust Devil, Evard's Black Tentacles: `80F5:2687`) are aimed at a point and bring
their creature (`Battle.SummonedBy`, rolled where there are several) onto the free cell nearest it, on the caster's side;
Shillelagh, Flame Blade and Spiritual Hammer put their weapon in an empty hand ("Failed: weapon in hand" otherwise, as
in the original); a combat log (lower right, see [Combat log](#combat-log)) says it all. On a party member's turn, click an enemy to attack it (it walks up first if needed;
hovering shows the chance to hit and the number needed), click the ground to move, or use CAST (Q,
then click a target; hovering shows who the area takes), GUARD (G), WAIT (T), END TURN (E) and AUTO
(U: the computer fights for that member). Once a member can't move any more, the original's menu
pops up by it: "END name's MOVE" with GUARD, WAIT and END TURN. Stepping on a move trigger runs its
script mid-fight, as in the original. A creature that falls leaves its body and gives the party its
experience; what members used (spells cast, psionic points, ammunition) goes back to their sheets.
At the end those down get up with a hit point; if no one was left standing, the original's "your
party has been defeated" screen leads back to the main menu. No saving during a fight; leaving the
region drops it. `-- --new-game --fight` starts a fight with the nearest creature (`--auto`: the
party fights by itself), `--check-combat` plays one through headless, and `--screenshot=FILE` saves
the screen after five seconds (`--screenshot-after=N` seconds).
`-- --new-game --check-arena` plays the opening bout, checks that its combatants
leave, then walks the whole party through the gate and checks for the
"Citizens of Draj!" announcement.
`-- --new-game --check-party-movement` checks the collapse menu, follower
visibility and occupancy, arrow-key movement, modal input blocking, loading a
collapsed party, spreading it out again (also with COLLAPSE PARTY a second time),
and the others following the leader on a click and the arrows (and not with Party follows off).

`make engine-mod-rules MOD=mods/MyMod` writes all four rule files (`characters.json`,
`creatures.json`, `combat.json`, `spells.json`) with the original's values to start from, keeping any
that are there, and `make engine-mod-check` reports rule files that can't be read.

### The original's rules and the tabletop rules

The rules are DSUN.EXE's, not the tabletop game's, where they have been traced:

| Rules | Where they come from |
|---|---|
| Experience for each level | `38F0:027C` (a row per class by `38F0:00CA`; gladiators use the fighter's) |
| Spells per day and the Wisdom bonus | `6798:0B32`'s formula (profiles `DS:0664` / `DS:0678`); every priest class's slots added; the Wisdom bonus for clerics and druids, even at spell levels their level doesn't give yet |
| Spellbook size | none: DSUN.EXE doesn't limit it |
| Constitution's hit points | `40B1:0052`; levels of non-warrior classes held to +2 (`8E77:032C`) |
| Saves, THAC0, attacks a round, ability tables, thief skills, racial adjustments, hit dice, starting experience | `40B1`, `8E77`, `DS:07A8`, `3FAA`, `38D4` (see `CharacterRules`) |
| Combat, spells, creatures | `CombatRules`, `SpellRules`, `CreatureRules` (see `dsun-exe-reference.md` §8) |

Still from the manual or AD&D 2nd edition, not yet traced: the classes each race may take, the
alignments, prime requisites, the dual-class requirements, and psionic strength (DSUN.EXE works it
out from the abilities and the psionicist level, `6698:0B8A` and `8E77:0162`; the engine's table is a
stand-in).

The tabletop rules come with the game as a mod, **Tabletop rules** (`engine/game/mods/Tabletop`), off
until it is turned on in the mod list: AD&D 2nd edition's and the manual's experience tables, spells
per day, Wisdom bonus (only for spell levels the priest can already cast), spellbook size by
Intelligence, and Constitution's hit points. Rules take effect from a new game; characters keep their
experience, so their levels follow the rules in play.

Mods in `engine/game/mods` are packed with the game, copied to `bundled-mods` in the Godot user data
folder each time it starts, and loaded after the player's own mods (theirs win). Their on/off
state is a setting (`[mods] bundled_on` in `settings.cfg`), not a renamed folder. To change one, copy
its folder into a mods folder and edit the copy.

The party is kept in `party.json` in the Godot user data folder and starts
the next new game (no party: four placeholder characters); characters made
or dropped are kept in `characters/` for ADD. Party members walk as their
chosen sprite and dialogue calls them by name; saved games keep them.
`-- --party` and `-- --chargen` open the screens; `-- --check-chargen` makes
a character of every race, gender and class and checks the art is there.

## Sound

Music: `[audio] music_device` (Options → Music device) picks what plays it:
`soundfont` (General MIDI, see the SoundFont notes above), `adlib` or `opl3`
(the original's FM sound: the Miles AdLib or Sound Blaster Pro driver on an
emulated OPL chip, with the game's `STDPATCH.AD` timbres; DSUN.EXE plays FM
cards the General MIDI tracks too), or `auto` (the default: a SoundFont if
there is one, else AdLib). MT-32 isn't offered (it needs Munt and the
MT-32's ROMs). The music level, which scripts set
(REQUEST 16) and fights change (the level the region's `RDAT` gives the
creature fought, then 1 when it's over), plays a section of the track as in
the original (`MusicSections`). `-- --check-music` plays every level of
every region track and checks that its section comes, and that every
track's timbres are in `STDPATCH.AD` and sound on both FM cards (it prints
their loudness against the SoundFont's). Sounds 131–180, the original's MIDI
note effects, aren't played: DSUN.EXE never plays them either, and no script
asks for them.
Sound effects come from the game
(`BVOC` in `RESOURCE.GFF`): what animations play (`SCMD`) when the object is
on screen, and what scripts play (SOUND). `[audio] sound_volume` (0–1, 0
for none) in `user://settings.cfg` sets their loudness.
`-- --check-sounds` decodes every effect. `-- --check-pictures` decodes the two pictures
scripts show (`CMAT` 200 and 300, `PictureView`) and times their fades as the original does
(`-- --new-game --picture=300` shows one over the map); `-- --cinematic=100` plays the ending
instead of the intro (with `--check-intro`, through, headless).

## Display

The game is drawn for a 640 × 400 viewport (twice the original's 320 × 200)
and shown at the largest whole-number scale that fits the window; the
viewport grows to fill the rest (`PixelScaler`), so pixels stay square and
sharp at any window size. `-- --check-display` prints the window, viewport
and scale; `-- --new-game` goes straight into a new game.

## Floor effects

Not in DSUN.EXE, and each with a switch in the Options window (O); the
ideas come from the [Obsidian Edition](https://github.com/daaki85/darksun-obsidian-edition)
(its FEATURES.md, Graphics and Controls), drawn here with Godot instead of
into the original's floor. `FloorEffects` is a layer between the ground and
the sorted sprites, so every wall and figure stands on top of them. Objects
that lie flat (paving, rugs, items put down: hung from their picture's top
edge, which they are sorted by, and not animated) are drawn with the ground,
under the layer; sorted, they would cover the shadows, dust and rings of
whoever stands on or beside them.

- **Shadows** (`[display] shadows`): every living figure on the map (the
  party, people and monsters, walking or standing; not the dead, nor
  items) casts its picture, flattened and laid toward the lower right (the
  maps are lit from the upper left), darkened and see-through. They are
  drawn as one group, so overlapping shadows darken the floor once.
- **Dust** (`[display] dust`): walking raises a puff behind the feet, the
  feet in turn, every 6 pixels; it spreads, rises and fades in a second.
  Only warm, middling floor colours (sand, dirt) raise dust, in a lighter
  colour of the floor under the feet; stone, water and lava don't. A puff
  is single pixels thinned toward its edge and as it fades, by a pattern
  fixed to the map, so it doesn't shimmer.
  Maps marked **Indoors** in the GFF viewer raise no dust from the floor's
  colour; outdoors still uses the floor-colour test and the Dust option.
  Cells painted in the GFF viewer (see [Map terrain](#map-terrain)) decide
  over the colour, indoors or out: **Sand** raises dust, and **Water**
  splashes instead, a ring that widens on the floor where the foot fell and
  fades. The Dust option turns splashes off too.
- **Choosing an enemy** (`[game] choose_enemy_keys`; `[display] enemy_rings`):
  on a party member's turn in a fight, Tab chooses the nearest living enemy,
  then the next nearest (Shift+Tab goes back), names it in the combat log
  with the chance to hit, and moves the view to it if it is out of sight;
  Enter attacks it as a click on it would (walking up first if it is out of
  reach), even when another figure stands in front of it. A red ring on the
  floor marks the chosen enemy (thicker and redder); the rings can be off,
  only under the chosen one (the default), or under every enemy.

`-- --new-game --check-floor` checks that each living figure casts a shadow,
walks a member across the arena's sand and checks for dust that fades,
paints the cells it walks back over as water and checks that they splash
and raise no dust, then in a fight presses Tab (the nearest enemy, its ring drawn) and Enter
(an attack on it).

### Map environments

In the web viewer's map toolbar, choose **Environment: Indoors / Outdoors**.
Changes save immediately to [game/data/map_environments.json](game/data/map_environments.json),
not to the installed GFF files. The JSON maps game IDs to uppercase GFF filenames
and `"indoors"` / `"outdoors"` values, for example:

```json
{"dark_sun": {"RGN02.GFF": "indoors"}, "dark_sun_2": {}}
```

An absent entry defaults to outdoors, preserving existing behavior. The viewer
uses `GET` / `POST /api/gff/<name>/map-environment?game=<game_id>`; POST accepts
`{"environment":"indoors"}` (or `"outdoors"`). Godot reads the shared file
as `res://data/map_environments.json` whenever a region loads, so re-enter the
region to pick up viewer changes. The file is included in exports; rebuild an
export to ship updated classifications. The current engine consumes `dark_sun`;
the viewer keeps DS2 classifications separately.

### Map terrain

In the web viewer's map toolbar, choose **Paint: Water / Sand / Erase** and
drag over the map: the cells in the rectangle get that type (Erase clears
them), and a later stroke paints over an earlier one. Middle-drag still pans;
**Paint: Off** gives the left button back to panning. The **Terrain** debug
layer shows the painted cells (water blue, sand yellow) and is turned on when
a paint type is chosen; hovering a cell names its type. Each stroke saves
immediately to [game/data/map_terrain.json](game/data/map_terrain.json),
which maps game IDs to uppercase GFF filenames and their cells by type:

```json
{"dark_sun": {"RGN28.GFF": {"water": [[4, 3], [5, 3]], "sand": [[9, 6]]}}, "dark_sun_2": {}}
```

The viewer uses `GET` / `POST /api/gff/<name>/map-terrain?game=<game_id>`;
POST takes `{"cells": {"water": [[x, y], ...], "sand": [...]}}` and replaces
the map's cells (an empty `cells` removes the map). The types are
`TERRAIN_TYPES` in `src/config.py`. Godot reads the file as
`res://data/map_terrain.json` whenever a region loads, for `dark_sun` only,
and uses it for the floor's dust and splashes (see Dust above).

## Combat log

The combat log on the map follows [Obsidian Edition](https://github.com/daaki85/darksun-obsidian-edition)'s
combat log in the game (its PLAYER-GUIDE's quality of life table and its
DEVELOPMENT.md, "The combat log in the game"; drawn here with Godot, its
look and wording taken, no code). `CombatLog` (`DarkSun.Game`) turns the
fight's events into lines, and `CombatLogView` draws them:

- **The panel**: the game's stone, docked at the screen's lower right
  (180 × 78 of the 320 × 200 screen, its foot at row 198), in the game's
  font. The header says the round, whose turn it is and who still has one
  ("Round 2: Gerakis, then Cermak, Cilla"). The lines sit in a dark well,
  coloured by kind: hits green, misses grey, damage amber, saves and magic
  resistance blue, turns sand, the rest yellow. Each name is in its own
  colour (each party member's by party slot, monsters red). A party
  member's line has a filled pip, a monster's a hollow one. A round is a
  rule across the well.
- **How much it says** (the header's level chip): **Least** gives only
  what came of each roll ("Halton hits Slig for 6"). **Short**, the
  default, adds the dice ("Halton attacks Slig: 15 vs 15+ HIT, 6 damage
  (1d8)"; "Slig's save vs Sleep: 9 vs 14, failed"; "Slig now 24/30 HP
  (-6)"). **Detail** gives every term ("THAC0 20 - STR 1 - AC 5 = 14",
  the damage dice, the save's natural roll). `Battle` notes what this
  needs: `AttackRolled.Terms` (`ToHitTerms`, which add up to
  `ToHitNeeded`), `DamageRolled`, `SaveRolled` and `ResistanceRolled`.
- **Its size** (the header's − and +): collapsed to the newest line, docked,
  or expanded, with filters: All, Party (a party member's lines), Damage
  (hits, wounds, falls), Saves.
- **Reading back**: the mouse wheel over it, its scrollbar (click the track
  for a page, drag the handle), and LATEST in the header to come back to
  the newest.
- **L** shows and hides it, as does its own L button, in a fight or out of
  one (the last fight's lines stay). It waits under the game's screens and
  windows. Whether it is shown, its level and its size are kept in
  `[combat_log]` in the settings.
- Lines are written when the fight decides them, so the hit points are
  those of that moment. They are shown when the playback reaches them.
- The CAST, GUARD, WAIT, END TURN and AUTO buttons moved to the lower left,
  to leave the lower right to the log.

Unlike Obsidian Edition's log, it shows only fights, not the rolls made
out of them (thief skills, hit points at a new level), and it has no strip
below the map. `-- --new-game --check-combat` checks that every attack,
wound and save has its line and that the panel's buttons and L work.

## Notes

- Every project targets `net10.0`. `game/DarkSunEngine.csproj` repeats it,
  because Godot adds `net8.0` to a project that doesn't name a framework.
- Commit the `*.uid` files Godot creates next to scripts and resources; they
  keep references stable when files move.
