"""
Game library configuration, tag constants, and URL segment helpers.

All other modules in the server package import from here rather than
defining their own constants.
"""

import os

# ---------------------------------------------------------------------------
# Directory layout
# ---------------------------------------------------------------------------
SRC_DIR  = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.dirname(SRC_DIR)
WEB_DIR  = os.path.join(SRC_DIR, 'web')

# ---------------------------------------------------------------------------
# Supported games
# ---------------------------------------------------------------------------
GAMES: dict[str, dict] = {
    'dark_sun': {
        'id':             'dark_sun',
        'label':          'Dark Sun: Shattered Lands',
        'dir':            os.path.join(BASE_DIR, 'dark_sun'),
        'supported':      True,
        'sfx_dir':        None,   # BVOC lives inside GFF files only
        'music_dir':      None,
        'cinematic_dir':  None,   # DS1 has no standalone FLI files
    },
    'dark_sun_2': {
        'id':             'dark_sun_2',
        'label':          'Dark Sun: Wake of the Ravager',
        'dir':            os.path.join(BASE_DIR, 'dark_sun_2', 'data'),
        'supported':      True,
        'sfx_dir':        os.path.join(BASE_DIR, 'dark_sun_2', 'data'),
        'music_dir':      os.path.join(BASE_DIR, 'dark_sun_2', 'data', 'MUSIC'),
        'cinematic_dir':  os.path.join(BASE_DIR, 'dark_sun_2', 'data'),
    },
    'al-qadim': {
        'id':             'al-qadim',
        'label':          "Al-Qadim: The Genie's Curse",
        'dir':            os.path.join(BASE_DIR, 'al-qadim', 'data'),
        'supported':      True,
        'sfx_dir':        os.path.join(BASE_DIR, 'al-qadim', 'data', 'SFX'),
        'music_dir':      None,
        'xmi_dir':        os.path.join(BASE_DIR, 'al-qadim', 'data', 'SFX'),
        'cinematic_dir':  None,
    },
}

DEFAULT_GAME = 'dark_sun'


def resolve_game_dir(game_id: str) -> str:
    """Return the GFF data directory for *game_id*, falling back to dark_sun."""
    return GAMES.get(game_id, GAMES[DEFAULT_GAME])['dir']


# ---------------------------------------------------------------------------
# GFF resource-tag sets
# ---------------------------------------------------------------------------

# Tags that contain renderable image data
IMAGE_TAGS = {'BMP ', 'CBMP', 'TILE', 'ICON', 'PORT', 'WALL', 'FONT'}

# Tags that contain XMIDI music sequences
MUSIC_TAGS = {'GSEQ', 'LSEQ', 'CSEQ', 'PSEQ', 'MSEQ'}

MUSIC_TAG_LABELS = {
    'GSEQ': 'Global audio/sync sequence (shared background themes and sync data)',
    'LSEQ': 'Local audio sequence (region- or scene-specific music and ambience)',
    'CSEQ': 'Cinematic sequence (cutscene/scripted music and playback control)',
    'PSEQ': 'Playback sequence (short playback/playlist blocks used for cues)',
    'MSEQ': 'XMIDI music sequence (XMIDI/EVNT format — game music data)',
}

# Tags that contain raw Creative Voice File (VOC) audio
AUDIO_TAGS = {'BVOC'}

AUDIO_TAG_LABELS = {
    'BVOC': 'Background audio sample (Creative Voice File)',
}

# Tags that contain human-readable text
TEXT_TAGS = {'TEXT', 'MERR', 'SPIN', 'NAME'}

# ---------------------------------------------------------------------------
# Spell / icon offset constants  (used by both the GFF spell route and
# the high-level /api/spells game-data route)
# ---------------------------------------------------------------------------

# Most spells: ICON = SPIN_ICON_OFFSET + spin_number
SPIN_ICON_OFFSET = 21000

# Psionic powers (SPIN 139–172) map to ICON 3000–3033
PSIONICS_SPIN_START = 139
PSIONICS_SPIN_END   = 172
PSIONICS_ICON_BASE  = 3000

# Turn Undead (SPIN 1000) expands to one card per elemental domain
TURN_UNDEAD_SPIN = 1000
TURN_UNDEAD_VARIANTS = [
    ('AIR',   3044),
    ('EARTH', 3045),
    ('FIRE',  3046),
    ('WATER', 3047),
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
