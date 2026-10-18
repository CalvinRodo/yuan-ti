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
| `tools/DarkSun.Dump` | Hashes every resource, image frame, palette and region render in the game files, for `make engine-compare`. |
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
make engine-compare # check the C# file-format code against the Python tools
```

`engine-compare` runs the repository's `tools/compare/gff_dump.py` (using `src/gff.py` and
`src/image.py`) and `tools/DarkSun.Dump` over the game files, and fails unless
the two outputs are identical. Run it after changing either side.

Headless checks: that the engine starts and finds the game files (exit code
1 if it doesn't), and that every region loads in the region viewer:

```
$GODOT --headless --path engine/game -- --check-install
$GODOT --headless --path engine/game -- --check-regions
```

In the region viewer: arrows, WASD or drag to scroll; mouse wheel or +/- to
zoom; PgUp/PgDn for the previous or next region; Esc to go back.

## Notes

- Every project targets `net10.0`. `game/DarkSunEngine.csproj` repeats it,
  because Godot adds `net8.0` to a project that doesn't name a framework.
- Commit the `*.uid` files Godot creates next to scripts and resources; they
  keep references stable when files move.
