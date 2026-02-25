"""
High-level game data routes.

These endpoints parse structured binary data from game GFF files and return
clean JSON suitable for the items / monsters / spells views in the browser UI.

Endpoints
---------
GET /api/games       – list of supported games with capabilities
GET /api/items       – item types from IT1R + NAME in GPLDATA.GFF
GET /api/monsters    – combat entities from RDFF in SEGOBJEX/OBJEX.GFF
GET /api/spells      – spell descriptions from SPIN in RESOURCE.GFF
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from flask import Blueprint, jsonify, abort, request

from config import (
    DEFAULT_GAME, GAMES,
    SPIN_ICON_OFFSET,
    PSIONICS_SPIN_START, PSIONICS_SPIN_END, PSIONICS_ICON_BASE,
    tag_to_url_seg,
)
from gff_cache import load_gff
from ds1_parsers import parse_items, parse_monsters, parse_spells

bp = Blueprint('gamedata', __name__)

# In-process cache so large SEGOBJEX scans aren't repeated per request.
_gamedata_cache: dict[tuple, list] = {}


def _try_load_gff(game_dir: str, filename: str, game_id: str):
    """Load a GFF by filename from game_dir, with case-insensitive fallback."""
    path = os.path.join(game_dir, filename)
    if not os.path.isfile(path):
        upper = filename.upper()
        match = next(
            (f for f in os.listdir(game_dir) if f.upper() == upper), None
        )
        if match is None:
            return None
        path = os.path.join(game_dir, match)
    try:
        return load_gff(os.path.basename(path), game_id)
    except Exception:
        return None


def _load_gamedata(kind: str, game_id: str) -> list:
    """Load and cache items / monsters / spells for *game_id*."""
    key = (kind, game_id)
    if key in _gamedata_cache:
        return _gamedata_cache[key]

    game     = GAMES.get(game_id, GAMES[DEFAULT_GAME])
    game_dir = game['dir']

    if kind == 'items':
        gff    = _try_load_gff(game_dir, 'GPLDATA.GFF', game_id)
        result = parse_items(gff) if gff else []
        for rec in result:
            rec['sprite_gff']     = 'GPLDATA.GFF'
            rec['sprite_tag_url'] = tag_to_url_seg('PORT')

    elif kind == 'monsters':
        # DS1 uses SEGOBJEX.GFF; DS2 uses OBJEX.GFF
        gff              = None
        sprite_gff_name  = None
        for candidate in ('SEGOBJEX.GFF', 'OBJEX.GFF'):
            gff = _try_load_gff(game_dir, candidate, game_id)
            if gff is not None:
                sprite_gff_name = candidate
                break
        result = parse_monsters(gff) if gff else []
        for rec in result:
            rec['sprite_gff']     = sprite_gff_name
            rec['sprite_tag_url'] = tag_to_url_seg('BMP ')

    elif kind == 'spells':
        gff = _try_load_gff(game_dir, 'RESOURCE.GFF', game_id)
        result = parse_spells(gff) if gff else []
        icon_numbers = (
            {d.number for d in gff.describe_resources() if d.tag.strip() == 'ICON'}
            if gff else set()
        )
        for rec in result:
            n = rec['number']
            if PSIONICS_SPIN_START <= n <= PSIONICS_SPIN_END:
                icon_n = PSIONICS_ICON_BASE + (n - PSIONICS_SPIN_START)
            else:
                icon_n = SPIN_ICON_OFFSET + n
            rec['icon_number']    = icon_n if icon_n in icon_numbers else None
            rec['sprite_gff']     = 'RESOURCE.GFF'
            rec['sprite_tag_url'] = 'ICON'

    else:
        result = []

    _gamedata_cache[key] = result
    return result


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------

@bp.route('/api/games')
def api_games():
    """Return the list of known games with metadata."""
    return jsonify([
        {
            'id':        info['id'],
            'label':     info['label'],
            'supported': info['supported'],
            'has_sfx':   bool(info.get('sfx_dir') and os.path.isdir(info['sfx_dir'])),
            'has_ogg':   bool(info.get('music_dir') and os.path.isdir(info['music_dir'])),
        }
        for info in GAMES.values()
    ])


@bp.route('/api/items')
def api_items():
    """Return all item types from IT1R + NAME resources in GPLDATA.GFF."""
    game_id = request.args.get('game', DEFAULT_GAME)
    return jsonify(_load_gamedata('items', game_id))


@bp.route('/api/monsters')
def api_monsters():
    """Return all combat entities from RDFF resources in SEGOBJEX/OBJEX.GFF."""
    game_id = request.args.get('game', DEFAULT_GAME)
    return jsonify(_load_gamedata('monsters', game_id))


@bp.route('/api/spells')
def api_gamedata_spells():
    """Return all spell descriptions from SPIN resources in RESOURCE.GFF."""
    game_id = request.args.get('game', DEFAULT_GAME)
    return jsonify(_load_gamedata('spells', game_id))


# Mapping of game_id → relative path (from this file's directory) to the
# manually-extracted monster lore JSON produced from the PDF manual.
_MONSTER_LORE_FILES: dict[str, str] = {
    'dark_sun': os.path.join(os.path.dirname(__file__), '..', 'json',
                             'monsters_with_details_ds1.json'),
}


@bp.route('/api/monster-lore')
def api_monster_lore():
    """Return monster lore entries extracted from the printed game manual.

    Currently only Dark Sun 1 has a lore file.  Other games return [].
    """
    import json as _json
    game_id = request.args.get('game', DEFAULT_GAME)
    path    = _MONSTER_LORE_FILES.get(game_id)
    if not path or not os.path.isfile(path):
        return jsonify([])
    with open(path, 'r', encoding='utf-8') as fh:
        data = _json.load(fh)
    return jsonify(data)
