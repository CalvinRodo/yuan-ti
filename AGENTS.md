# Agent Instructions

Before changing code, read [.github/copilot-instructions.md](.github/copilot-instructions.md) for the project architecture, game-format details, and conventions. Follow any relevant guidance in `.github/instructions/`; for engine work, also read `engine/README.md`.

## Working in this repository

- Keep changes focused and follow the existing module boundaries and patterns. Python server features belong in `src/routes/`; shared game configuration belongs in `src/config.py`.
- Use `uv run python3` for Python commands. Install Python dependencies with `uv sync`.
- The web viewer can be started with `make serve`. For engine changes, use `make engine-build` and `make engine-test`; run `make engine-compare` when changing file-format or image-decoding behavior.
- Do not modify installed game files or commit generated output under `out/` unless the task specifically requires it.
- Keep the games' format differences in mind. In particular, Al-Qadim uses `.LIB` files, not GFF, and its GFF-based support is incomplete.
