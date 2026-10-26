"""
Game library configuration, tag constants, and URL segment helpers.

All other modules in the server package import from here rather than
defining their own constants.
"""

import glob
import os
import tomllib

# ---------------------------------------------------------------------------
# Directory layout
# ---------------------------------------------------------------------------
SRC_DIR  = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.dirname(SRC_DIR)
WEB_DIR  = os.path.join(SRC_DIR, 'web')
DATA_DIR = os.path.join(SRC_DIR, 'data')
MAP_ENVIRONMENTS_PATH = os.path.join(BASE_DIR, 'engine', 'game', 'data', 'map_environments.json')
MAP_TERRAIN_PATH = os.path.join(BASE_DIR, 'engine', 'game', 'data', 'map_terrain.json')
# Floor types the viewer can paint on map cells; the engine splashes on water and raises dust on sand.
TERRAIN_TYPES = ('water', 'sand')
MAP_NAMES_PATH = os.path.join(SRC_DIR, 'json', 'map_names.json')


def _load_toml(filename: str) -> dict:
    """Load a TOML file from src/data/."""
    with open(os.path.join(DATA_DIR, filename), 'rb') as _f:
        return tomllib.load(_f)


# ---------------------------------------------------------------------------
# Supported games  (loaded from src/data/games.toml)
# ---------------------------------------------------------------------------
def _build_games() -> dict[str, dict]:
    raw = _load_toml('games.toml')
    _dir_keys = ('dir', 'sfx_dir', 'music_dir', 'cinematic_dir', 'xmi_dir')
    games: dict[str, dict] = {}
    for game_id, entry in raw.items():
        built: dict = {}
        for key, value in entry.items():
            built[key] = os.path.join(BASE_DIR, value) if key in _dir_keys else value
        # Fill in None for optional dir keys not present in TOML
        for key in _dir_keys:
            built.setdefault(key, None)
        games[game_id] = built
    return games


GAMES: dict[str, dict] = _build_games()

DEFAULT_GAME = 'dark_sun'


def get_game(game_id: str) -> dict:
    """Return the game config dict for *game_id*, falling back to dark_sun."""
    return GAMES.get(game_id, GAMES[DEFAULT_GAME])


def resolve_game_dir(game_id: str) -> str:
    """Return the GFF data directory for *game_id*, falling back to dark_sun."""
    return get_game(game_id)['dir']


def find_file_nocase(directory: str, filename: str) -> str | None:
    """Return the full path to *filename* inside *directory*, matching case-insensitively.

    Returns ``None`` when no match is found or the directory does not exist.
    """
    path = os.path.join(directory, filename)
    if os.path.isfile(path):
        return path
    if not os.path.isdir(directory):
        return None
    upper = filename.upper()
    match = next((f for f in os.listdir(directory) if f.upper() == upper), None)
    return os.path.join(directory, match) if match else None


def list_files_nocase(directory: str, ext: str) -> list[str]:
    """Return sorted, case-insensitive-deduplicated file paths for *ext* in *directory*.

    *ext* may include or omit the leading dot (e.g. ``'.voc'`` or ``'voc'``).
    Returns an empty list when the directory does not exist.
    """
    if not os.path.isdir(directory):
        return []
    bare = ext.upper().lstrip('*.')
    seen: set[str] = set()
    result: list[str] = []
    for filepath in sorted(
        glob.glob(os.path.join(directory, f'*.{bare}')) +
        glob.glob(os.path.join(directory, f'*.{bare.lower()}'))
    ):
        name = os.path.basename(filepath)
        if name.upper() not in seen:
            seen.add(name.upper())
            result.append(filepath)
    return result


# ---------------------------------------------------------------------------
# GFF resource-tag sets  (loaded from src/data/tags.toml)
# Labels derived from the canonical descriptions in src/data/gff_chunk_types.toml
# ---------------------------------------------------------------------------
_tags = _load_toml('tags.toml')
_chunk_types: dict[str, str] = _load_toml('gff_chunk_types.toml')['chunk_types']

# Tags that contain renderable image data
IMAGE_TAGS: set[str] = set(_tags['image_tags']['tags'])

# Tags that contain XMIDI music sequences
MUSIC_TAGS: set[str] = set(_tags['music_tags']['tags'])
MUSIC_TAG_LABELS: dict[str, str] = {t: _chunk_types[t] for t in MUSIC_TAGS if t in _chunk_types}

# Tags that contain raw Creative Voice File (VOC) audio
AUDIO_TAGS: set[str] = set(_tags['audio_tags']['tags'])
AUDIO_TAG_LABELS: dict[str, str] = {t: _chunk_types[t] for t in AUDIO_TAGS if t in _chunk_types}

# Tags that contain human-readable text
TEXT_TAGS: set[str] = set(_tags['text_tags']['tags'])

# ---------------------------------------------------------------------------
# Spell / icon offset constants  (loaded from src/data/spells.toml)
# (used by both the GFF spell route and the high-level /api/spells route)
# ---------------------------------------------------------------------------
_spells = _load_toml('spells.toml')

SPIN_ICON_OFFSET:    int = _spells['spin_icon_offset']
PSIONICS_SPIN_START: int = _spells['psionics_spin_start']
PSIONICS_SPIN_END:   int = _spells['psionics_spin_end']
PSIONICS_ICON_BASE:  int = _spells['psionics_icon_base']
TURN_UNDEAD_SPIN:    int = _spells['turn_undead_spin']
TURN_UNDEAD_VARIANTS: list[tuple[str, int]] = [
    (v['domain'], v['icon']) for v in _spells['turn_undead_variants']
]

# ---------------------------------------------------------------------------
# URL ↔ tag helpers
# ---------------------------------------------------------------------------

def tag_to_url_seg(tag: str) -> str:
    """Replace trailing space(s) with underscores for use in URL path segments."""
    return tag.rstrip(' ') + '_' * (4 - len(tag.rstrip()))


def url_seg_to_tag(seg: str) -> str:
    """Reverse of tag_to_url_seg: restore a trailing underscore as a space."""
    if len(seg) == 4 and seg[3] == '_':
        return seg[:3] + ' '
    return seg
