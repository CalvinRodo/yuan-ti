"""
NPC / PC / Monster Sprite browser routes.

Scans BMP resources in SEGOBJEX.GFF (DS1) and provides:
  * A full BMP catalog with auto-classification by frame count
  * Persisted per-BMP metadata (type, name, linked sheets, notes) as JSON
  * Sprite-section definitions for known animation sheet layouts

Sprite sheet type conventions (Dark Sun 1 only, verified):
  walk15      - 15 frames: standing x3 (front/back/right) + walking x12
  attack9     - 9 frames: melee attack, 3 frames x 3 directions
  attack12    - 12 frames: weapon attack 3f x3dir + ranged 1f x3dir
  attack14    - 14 frames: melee 3f x3 + ranged 1f x3 + death x2
  combined24  - 24 frames: attack 4f x3 then walking 4f x3

Auto-type classification (overridden by saved metadata):
  1-3 frames  -> Scenery
  >3 frames, not a recognised animation count -> Effect
  9/12/14/15/24 frames -> NPC (defaults), PC when paired walk15+attack14

Endpoints
---------
GET  /api/bmp-catalog        - all BMP entries with type, frames, auto-classification
GET  /api/sprite-metadata    - raw saved metadata dict (game -> sprites dict)
POST /api/sprite-metadata    - save / merge metadata updates
"""

import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from flask import Blueprint, jsonify, request, abort

from config import DEFAULT_GAME, GAMES
from gff_cache import load_gff
from ds1_parsers import parse_monsters
from image import ImageReading

bp = Blueprint('sprites', __name__)

# ---------------------------------------------------------------------------
# Sprite sheet section definitions
# ---------------------------------------------------------------------------
SHEET_DEFS: dict[int, dict] = {
    15: {
        'type':  'walk15',
        'label': 'Walking',
        'sections': [
            {'name': 'Standing',    'frames': [0, 1, 2],        'loop': False,
             'desc': 'Front / Back / Right'},
            {'name': 'Walk Front',  'frames': [3, 4, 5, 6],     'loop': True,
             'desc': '4-frame forward walk loop'},
            {'name': 'Walk Back',   'frames': [7, 8, 9, 10],    'loop': True,
             'desc': '4-frame backward walk loop'},
            {'name': 'Walk Right',  'frames': [11, 12, 13, 14], 'loop': True,
             'desc': '4-frame rightward walk loop'},
        ],
    },
    9: {
        'type':  'attack9',
        'label': 'Attack (melee)',
        'sections': [
            {'name': 'Attack Front', 'frames': [0, 1, 2], 'loop': False,
             'desc': '3-frame melee - front'},
            {'name': 'Attack Back',  'frames': [3, 4, 5], 'loop': False,
             'desc': '3-frame melee - back'},
            {'name': 'Attack Right', 'frames': [6, 7, 8], 'loop': False,
             'desc': '3-frame melee - right'},
        ],
    },
    12: {
        'type':  'attack12',
        'label': 'Attack (weapon + ranged)',
        'sections': [
            {'name': 'Weapon Front', 'frames': [0, 1, 2],   'loop': False,
             'desc': '3-frame weapon - front'},
            {'name': 'Weapon Back',  'frames': [3, 4, 5],   'loop': False,
             'desc': '3-frame weapon - back'},
            {'name': 'Weapon Right', 'frames': [6, 7, 8],   'loop': False,
             'desc': '3-frame weapon - right'},
            {'name': 'Ranged',       'frames': [9, 10, 11], 'loop': False,
             'desc': '1 frame per direction (front / back / right)'},
        ],
    },
    14: {
        'type':  'attack14',
        'label': 'Attack + Ranged + Death',
        'sections': [
            {'name': 'Attack Front', 'frames': [0, 1, 2],   'loop': False,
             'desc': '3-frame melee - front'},
            {'name': 'Attack Back',  'frames': [3, 4, 5],   'loop': False,
             'desc': '3-frame melee - back'},
            {'name': 'Attack Right', 'frames': [6, 7, 8],   'loop': False,
             'desc': '3-frame melee - right'},
            {'name': 'Ranged',       'frames': [9, 10, 11], 'loop': False,
             'desc': '1 frame per direction (front / back / right)'},
            {'name': 'Death',        'frames': [12, 13],    'loop': False,
             'desc': '2-frame death animation'},
        ],
    },
    24: {
        'type':  'combined24',
        'label': 'Attack + Walk',
        'sections': [
            {'name': 'Attack Front', 'frames': [0, 1, 2, 3],      'loop': False,
             'desc': '4-frame melee - front'},
            {'name': 'Attack Back',  'frames': [4, 5, 6, 7],      'loop': False,
             'desc': '4-frame melee - back'},
            {'name': 'Attack Right', 'frames': [8, 9, 10, 11],    'loop': False,
             'desc': '4-frame melee - right'},
            {'name': 'Walk Front',   'frames': [12, 13, 14, 15],  'loop': True,
             'desc': '4-frame walk loop - front'},
            {'name': 'Walk Back',    'frames': [16, 17, 18, 19],  'loop': True,
             'desc': '4-frame walk loop - back'},
            {'name': 'Walk Right',   'frames': [20, 21, 22, 23],  'loop': True,
             'desc': '4-frame walk loop - right'},
        ],
    },
}

SPRITE_COUNTS = frozenset(SHEET_DEFS.keys())   # {9, 12, 14, 15, 24}

# Sprite types available for editing
SPRITE_TYPES = ['PC', 'NPC', 'Monster', 'Scenery', 'Effect', 'Spell', 'Item', 'Unknown']

# ---------------------------------------------------------------------------
# Metadata file helpers
# ---------------------------------------------------------------------------
_METADATA_PATHS: dict[str, str] = {
    'dark_sun': os.path.join(
        os.path.dirname(__file__), '..', 'json', 'sprite_metadata_dark_sun.json'
    ),
}


def _meta_path(game_id: str) -> str | None:
    return _METADATA_PATHS.get(game_id)


def _load_meta_file(game_id: str) -> dict:
    """Load the saved metadata JSON for game_id; returns dict (may be empty)."""
    path = _meta_path(game_id)
    if not path or not os.path.isfile(path):
        return {}
    try:
        with open(path, 'r', encoding='utf-8') as fh:
            obj = json.load(fh)
        return obj.get('sprites', {})
    except Exception:
        return {}


def _save_meta_file(game_id: str, sprites_dict: dict) -> bool:
    """Persist the metadata dict for game_id.  Returns True on success."""
    path = _meta_path(game_id)
    if not path:
        return False
    os.makedirs(os.path.dirname(path), exist_ok=True)
    obj = {'version': 1, 'game': game_id, 'sprites': sprites_dict}
    try:
        with open(path, 'w', encoding='utf-8') as fh:
            json.dump(obj, fh, indent=2)
        return True
    except Exception:
        return False


# ---------------------------------------------------------------------------
# Catalog cache  (invalidated on metadata save)
# ---------------------------------------------------------------------------
_catalog_cache: dict[str, list] = {}


def _invalidate_catalog(game_id: str) -> None:
    _catalog_cache.pop(game_id, None)


# ---------------------------------------------------------------------------
# Auto-type classification
# ---------------------------------------------------------------------------

def _auto_type(frame_count: int, *, is_pc_pair: bool = False) -> str:
    """Return an automatic sprite type based on frame count and pairing context."""
    if frame_count <= 0:
        return 'Unknown'
    if is_pc_pair:
        return 'PC'
    if frame_count <= 3:
        return 'Scenery'
    if frame_count in SPRITE_COUNTS:
        return 'NPC'
    return 'Effect'


# ---------------------------------------------------------------------------
# Catalog builder
# ---------------------------------------------------------------------------

def _build_catalog(game_id: str) -> list[dict]:
    """Build the full BMP catalog for game_id, merging auto-type with metadata."""
    if game_id in _catalog_cache:
        return _catalog_cache[game_id]

    game     = GAMES.get(game_id, GAMES[DEFAULT_GAME])
    game_dir = game['dir']

    gff      = None
    gff_name = 'SEGOBJEX.GFF'
    for candidate in ('SEGOBJEX.GFF', 'OBJEX.GFF'):
        p = os.path.join(game_dir, candidate)
        if not os.path.isfile(p):
            upper = candidate.upper()
            match = next(
                (f for f in os.listdir(game_dir) if f.upper() == upper), None
            )
            p = os.path.join(game_dir, match) if match else None
        if p and os.path.isfile(p):
            try:
                gff_name = os.path.basename(p)
                gff = load_gff(gff_name, game_id)
                break
            except Exception:
                continue

    if gff is None:
        return []

    descs   = gff.describe_resources()
    bmps    = sorted([d for d in descs if d.tag.strip() == 'BMP'], key=lambda d: d.number)
    bmp_nums = [d.number for d in bmps]
    bmp_set  = set(bmp_nums)

    # Frame counts
    fc_map: dict[int, int] = {}
    for d in bmps:
        try:
            raw    = gff.get_resource_data('BMP ', d.number)
            frames = ImageReading.extract_frames(raw)
            fc_map[d.number] = len(frames)
        except Exception:
            fc_map[d.number] = 0

    # RDFF monster names keyed by bmp_num
    monsters = parse_monsters(gff)
    bmp_to_rdff: dict[int, list[str]] = {}
    for m in monsters:
        bn = m.get('bmp_num')
        if bn:
            bmp_to_rdff.setdefault(bn, [])
            if m['name'] not in bmp_to_rdff[bn]:
                bmp_to_rdff[bn].append(m['name'])

    # Detect PC pairs: walk15 immediately followed by attack14
    pc_pair_nums: set[int] = set()
    for n in bmp_nums:
        if fc_map[n] == 15:
            nxt = n + 1
            if nxt in bmp_set and fc_map.get(nxt) == 14:
                pc_pair_nums.add(n)
                pc_pair_nums.add(nxt)

    saved_meta = _load_meta_file(game_id)

    catalog: list[dict] = []
    for n in bmp_nums:
        fc  = fc_map[n]
        key = str(n)

        auto_t = _auto_type(fc, is_pc_pair=(n in pc_pair_nums))

        saved      = saved_meta.get(key, {})
        eff_type   = saved.get('type')  or auto_t
        eff_name   = saved.get('name',  '') or ''
        linked     = saved.get('linked_bmps', [])
        notes      = saved.get('notes', '') or ''
        item_index = saved.get('item_index', None)

        sheet_def  = SHEET_DEFS.get(fc)
        sheet_type = sheet_def['type']     if sheet_def else None
        sections   = sheet_def['sections'] if sheet_def else None

        rdff_names = bmp_to_rdff.get(n, [])

        catalog.append({
            'bmp_num':     n,
            'frame_count': fc,
            'auto_type':   auto_t,
            'type':        eff_type,
            'name':        eff_name,
            'rdff_names':  rdff_names,
            'linked_bmps': linked,
            'notes':       notes,
            'sheet_type':  sheet_type,
            'sections':    sections,
            'item_index':  item_index,
            'gff_name':    gff_name,
        })

    _catalog_cache[game_id] = catalog
    return catalog


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------

@bp.route('/api/bmp-catalog')
def api_bmp_catalog():
    """Return the full BMP catalog for a game.

    Query params:  game (default: dark_sun)
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description=f'Unknown game: {game_id}')
    return jsonify(_build_catalog(game_id))


@bp.route('/api/sprite-metadata', methods=['GET'])
def api_sprite_metadata_get():
    """Return the raw saved metadata dict for a game.

    Query params:  game (default: dark_sun)
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    return jsonify(_load_meta_file(game_id))


@bp.route('/api/sprite-metadata', methods=['POST'])
def api_sprite_metadata_post():
    """Save / merge sprite metadata.

    Body: { "sprites": { "<bmp_num>": { type, name, linked_bmps, notes } },
            "_replace_all": false }

    When _replace_all is false (default), the incoming sprites dict is merged
    into the existing file and bidirectional link maintenance is applied.
    Pass "_replace_all": true to atomically replace the entire sprites dict.
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    body    = request.get_json(silent=True) or {}

    incoming  = body.get('sprites', {})
    replace_all = bool(body.get('_replace_all', False))

    if replace_all:
        merged = incoming
    else:
        merged = _load_meta_file(game_id)
        for k, v in incoming.items():
            if v is None:
                # Deletion: also clean up back-references
                old_links = set(merged.get(k, {}).get('linked_bmps', []))
                for ln in old_links:
                    sk = str(ln)
                    if sk in merged:
                        pl = set(merged[sk].get('linked_bmps', []))
                        pl.discard(int(k))
                        merged[sk]['linked_bmps'] = sorted(pl)
                merged.pop(k, None)
            else:
                old_links = set(merged.get(k, {}).get('linked_bmps', []))
                new_links = set(v.get('linked_bmps', old_links))

                # Links added: add back-reference
                for ln in new_links - old_links:
                    sk = str(ln)
                    peer = merged.setdefault(sk, {})
                    pl   = set(peer.get('linked_bmps', []))
                    pl.add(int(k))
                    peer['linked_bmps'] = sorted(pl)

                # Links removed: remove back-reference
                for ln in old_links - new_links:
                    sk = str(ln)
                    if sk in merged:
                        pl = set(merged[sk].get('linked_bmps', []))
                        pl.discard(int(k))
                        merged[sk]['linked_bmps'] = sorted(pl)

                # Merge fields into existing entry (preserves fields not in patch)
                old_entry = merged.get(k, {})
                merged[k] = {**old_entry, **v}

                # Enforce item_index uniqueness: clear it from any other sprite
                new_idx = v.get('item_index')
                if new_idx is not None:
                    for other_k in list(merged.keys()):
                        if other_k != k and merged[other_k].get('item_index') == new_idx:
                            merged[other_k].pop('item_index', None)

    ok = _save_meta_file(game_id, merged)
    _invalidate_catalog(game_id)

    if not ok:
        abort(500, description='Failed to write metadata file')

    return jsonify({'ok': True, 'saved': len(merged)})


@bp.route('/api/sprite-types')
def api_sprite_types():
    """Return the list of valid sprite type values."""
    return jsonify(SPRITE_TYPES)


@bp.route('/api/item-sprite-links')
def api_item_sprite_links():
    """Return a mapping of item_index → bmp_num for all linked item sprites.

    Query params:  game (default: dark_sun)
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description=f'Unknown game: {game_id}')
    catalog  = _build_catalog(game_id)
    gff_name = catalog[0]['gff_name'] if catalog else 'SEGOBJEX.GFF'
    links    = {'_gff_name': gff_name}
    for e in catalog:
        if e.get('item_index') is not None:
            links[str(e['item_index'])] = e['bmp_num']
    return jsonify(links)


# ---------------------------------------------------------------------------
# Legacy endpoint
# ---------------------------------------------------------------------------

@bp.route('/api/npc-sprites')
def api_npc_sprites():
    """Legacy: sprite-type BMP entries only (deprecated, use /api/bmp-catalog)."""
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in ('dark_sun',):
        return jsonify([])
    catalog = _build_catalog(game_id)
    return jsonify([e for e in catalog if e['sheet_type'] is not None])
