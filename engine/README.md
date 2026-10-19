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
| `tools/DarkSun.ModTool` | Exports a resource into a mod folder (PNG, text or raw bytes) and lists what mods replace, for `make engine-mod-export` / `engine-mod-list`. |
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
```

Starting the game plays the intro (`CINE.GFF` scripts 1–8: the SSI and AD&D
logos, the title, the sword falling to Athas, the story scroll) with its
music. Any key or click skips it; `-- --no-intro` leaves it out. Then comes the original main menu:
Start Game (or S) plays the arrival cinematic and starts a new game, and
Exit (E) quits. The music changes as in the original: the menu's
theme (17), each region's own track (its `RDAT` record), with 5-second fades
between them. Create Characters (C) and Load Saved Game (L) aren't ported
yet. Arrow keys and Enter work too. Esc in the region viewer returns to the
menu. The start screen only appears when the game files can't be found.

The region viewer starts a new game where DSUN.EXE does: region 42
(`RGN2A.GFF`), the party at pixel (488, 152), after the game-start script
(`MAS 99`). PgUp/PgDn browse the other regions, with the party in the middle
of the map. The party is four placeholder sprites (any object whose `SCMD`
can walk). Entering a region
runs its script (and, once, the game-start script); characters reaching a
cell fire its move trigger; right-click an object to fire its talk-to, use,
look or pick-up trigger. Dialogue (text, portraits, Continue, Yes/No,
menus) opens in a window and pauses the script until answered; TELEPORT moves
the party (region exits: walk out through a map edge and answer Yes) and
creatures. Creatures (objects with an `RDFF` combat record) walk where their
scripts send them (HUNT, GO, GOXY, FOLLOW, FLEE, FETCH, WAIT), and run their
no-orders and line-of-sight scripts; RANGE, GETXY, GET_LOS and the other
queries read the world (`DarkSun.Game.World`). Items, combat and some
REQUEST codes are still only logged: F2 shows the script log. Click: the selected character (green ring) walks there;
Shift+click: the whole party; Tab: select
the next character; N: add a character; C: change the selected character's
sprite. Characters cover a footprint of cells (estimated from the sprite's
width until the game's size data is traced) and never overlap: one blocked by
another waits, goes round if that's short, or gives up after about 4 s.
Arrows, WASD or middle-drag scroll (the camera eases after the selected
character, and follows it again after the next click); mouse wheel or +/-
zoom (½, 1–4; 2, the default, shows what the original showed); PgUp/PgDn change region;
F1 shows blocked cells (red), cells that block line of sight (blue outline),
occupied cells (yellow) and move triggers (magenta); Esc goes back. Objects with an `SCMD` script
animate. `--check-regions` also walks the party 20 cells in every region and
fails if two characters ever overlap; `--check-exits` steps on every move
trigger and lists the region exits; `--check-start` starts a new game and
prints where the party stands and what the scripts did.

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
    ICON/2048/0.png                 frame 0 of ICON 2048 (the other frames stay)
    BMP/20029.png                   a whole image, in its own colours
    TEXT/12.txt                     text, UTF-8
    GPL/80.bin                      raw resource bytes: any tag (scripts, PAL, GMAP, GSEQ, …)
    RGN2A.GFF/ETAB/42.bin           only in that file; a file folder may also add resources
  _Disabled/                        names starting with _ or . are skipped
```

`make engine-mod-export` writes a resource in the right form to edit:
images are drawn in a palette (`--palette FILE:N` for the tool) and come back
in true colour; in cinematics they are matched to the colours on screen.
Changes take effect the next time the game starts.

Decoded images and palettes are cached for the session
(`Session.Assets`), with one texture per frame and palette, so returning to a
region or screen doesn't decode again.

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
