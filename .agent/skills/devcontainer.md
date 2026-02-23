## Skill: Dev Container Is the Source of Truth (Install & Setup Preference)

This project runs inside a VS Code **Dev Container**. The Dev Container environment is the canonical runtime and must be treated as the default target for all setup, dependencies, tooling, and execution.

### Rules
- **Prefer Dev Container setup over host setup.** Any dependency installation, system package, toolchain change, or configuration must be implemented in the devcontainer first, not on the host OS.
- **Do not give host-only instructions** (e.g., “brew install…”, “apt install…” on the user’s machine) unless explicitly requested. If host instructions are unavoidable, provide them only as an optional fallback and clearly label them as such.
- **Assume code runs in-container** for all commands, paths, and environment variables (Linux container conventions).
- **Prefer reproducibility:** changes must be codified in container config rather than requiring manual local steps.

### Implementation Guidance
When adding dependencies:
1) If it’s a Python dependency: add it to the project’s Python dependency management (e.g., `requirements.txt` / `pyproject.toml`) and install inside the container.
2) If it’s a system package: add it to the Dev Container build (e.g., `Dockerfile` or `devcontainer.json` features) so it’s installed automatically when the container builds.
3) If it’s a VS Code extension or setting: add it to `.devcontainer/devcontainer.json` so it’s consistent for anyone using the container.

### Command Assumptions
- Provide commands as if they are run from a **VS Code Dev Container terminal**.
- If a rebuild is required (e.g., after changing Dockerfile/devcontainer config), explicitly state: “Rebuild the Dev Container.”

### Output Requirements
Whenever you propose installing or configuring something, you must include:
- The exact file(s) to edit (e.g., `.devcontainer/devcontainer.json`, `.devcontainer/Dockerfile`, `pyproject.toml`)
- The exact change to make
- The command(s) to run **inside the container**