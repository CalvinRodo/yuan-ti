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
        pattern_name = saved.get('pattern_name', None)

        sheet_def  = SHEET_DEFS.get(fc)
        sheet_type = sheet_def['type']     if sheet_def else None
        sections   = sheet_def['sections'] if sheet_def else None

        # custom_sections: use saved value if present (even []); auto-seed from
        # sheet_def when the key is absent from metadata (never been edited).
        saved_custom = saved.get('custom_sections')   # None when key absent
        if saved_custom is None and sheet_def:
            custom_sections = [
                {
                    'id':       str(i + 1),
                    'name':     s['name'],
                    'frames':   list(s['frames']),
                    'mirrored': False,
                    'desc':     s.get('desc', ''),
                }
                for i, s in enumerate(sheet_def['sections'])
            ]
        else:
            custom_sections = saved_custom if saved_custom is not None else []

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
            'sections':        sections,
            'item_index':      item_index,
            'gff_name':        gff_name,
            'custom_sections': custom_sections,
            'pattern_name':    pattern_name,
            'plain_anchor':    saved.get('plain_anchor', None),
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
# Sprite sheet patterns
# ---------------------------------------------------------------------------

_PATTERNS_FILE = os.path.join(
    os.path.dirname(__file__), '..', 'json', 'sprite_patterns.json'
)


def _load_patterns() -> list[dict]:
    """Load all saved patterns; returns a list (may be empty)."""
    if not os.path.isfile(_PATTERNS_FILE):
        return []
    try:
        with open(_PATTERNS_FILE, 'r', encoding='utf-8') as fh:
            obj = json.load(fh)
        return obj.get('patterns', [])
    except Exception:
        return []


def _save_patterns_file(patterns: list[dict]) -> bool:
    """Persist the patterns list. Returns True on success."""
    os.makedirs(os.path.dirname(_PATTERNS_FILE), exist_ok=True)
    obj = {'version': 1, 'patterns': patterns}
    try:
        with open(_PATTERNS_FILE, 'w', encoding='utf-8') as fh:
            json.dump(obj, fh, indent=2)
        return True
    except Exception:
        return False


@bp.route('/api/sprite-patterns', methods=['GET'])
def api_sprite_patterns_get():
    """Return saved sprite sheet patterns.

    Query params:
      frame_count (optional int) — filter to patterns matching this frame count
    """
    patterns = _load_patterns()
    fc_filter = request.args.get('frame_count')
    if fc_filter is not None:
        try:
            fc = int(fc_filter)
            patterns = [p for p in patterns if p.get('frame_count') == fc]
        except ValueError:
            pass
    return jsonify(patterns)


@bp.route('/api/sprite-patterns', methods=['POST'])
def api_sprite_patterns_post():
    """Create, update, or delete a sprite sheet pattern.

    Body (save/update):
      { "action": "save", "pattern": { "id": "...", "name": "...", "frame_count": N,
                                        "sections": [...] } }
      When id is absent or empty a new id is generated.

    Body (delete):
      { "action": "delete", "id": "..." }
    """
    body   = request.get_json(silent=True) or {}
    action = body.get('action', 'save')
    patterns = _load_patterns()

    if action == 'delete':
        pat_id = body.get('id')
        if not pat_id:
            abort(400, description='Missing pattern id')
        patterns = [p for p in patterns if p.get('id') != pat_id]
        ok = _save_patterns_file(patterns)
        if not ok:
            abort(500, description='Failed to write patterns file')
        return jsonify({'ok': True, 'deleted': pat_id})

    # save / update
    pat = body.get('pattern')
    if not pat or not isinstance(pat, dict):
        abort(400, description='Missing pattern object')

    # Match by id first, then fall back to name so re-saving under the same
    # name updates the existing pattern rather than creating a duplicate.
    idx_by_id   = next((i for i, p in enumerate(patterns) if p.get('id') == pat.get('id')), None)
    idx_by_name = next((i for i, p in enumerate(patterns) if p.get('name') == pat.get('name')), None)

    if idx_by_id is not None:
        pat['id'] = patterns[idx_by_id]['id']
        patterns[idx_by_id] = pat
    elif idx_by_name is not None:
        # Same name → reuse existing id so clients can track it
        pat['id'] = patterns[idx_by_name]['id']
        patterns[idx_by_name] = pat
    else:
        if not pat.get('id'):
            existing_ids = {p['id'] for p in patterns}
            candidate = 1
            while str(candidate) in existing_ids:
                candidate += 1
            pat['id'] = str(candidate)
        patterns.append(pat)

    ok = _save_patterns_file(patterns)
    if not ok:
        abort(500, description='Failed to write patterns file')

    # Propagate updated sections to every sprite that uses this pattern name
    propagated = _propagate_pattern(pat)
    return jsonify({'ok': True, 'pattern': pat, 'propagated': propagated})


def _propagate_pattern(pat: dict) -> int:
    """Re-apply updated pattern sections to all sprites whose pattern_name matches.

    Returns the number of sprite entries updated across all games.
    """
    import copy as _copy
    pat_name = pat.get('name')
    new_sections = pat.get('sections', [])
    if not pat_name:
        return 0
    total = 0
    for game_id in list(_METADATA_PATHS.keys()):
        sprites = _load_meta_file(game_id)
        changed = False
        for entry in sprites.values():
            if entry.get('pattern_name') == pat_name:
                # Re-stamp section IDs so they are consistent
                id_map = {s.get('id', ''): str(i + 1)
                          for i, s in enumerate(new_sections)}
                new_cs = []
                for i, s in enumerate(new_sections):
                    sec = _copy.deepcopy(s)
                    sec['id'] = str(i + 1)
                    if sec.get('mirrorOf'):
                        sec['mirrorOf'] = id_map.get(sec['mirrorOf'], sec['mirrorOf'])
                    new_cs.append(sec)
                entry['custom_sections'] = new_cs
                changed = True
                total += 1
        if changed:
            _save_meta_file(game_id, sprites)
            _invalidate_catalog(game_id)
    return total


# ---------------------------------------------------------------------------
# Monster ↔ Sprite linking
# ---------------------------------------------------------------------------

_MONSTER_LINK_PATHS: dict[str, str] = {
    'dark_sun': os.path.join(
        os.path.dirname(__file__), '..', 'json', 'monster_sprite_links_dark_sun.json'
    ),
}


def _monster_links_path(game_id: str) -> str | None:
    return _MONSTER_LINK_PATHS.get(game_id)


def _load_monster_links(game_id: str) -> dict[str, list[int]]:
    """Load monster → BMP links; returns {rdff_id_str: [bmp_num, ...]}."""
    path = _monster_links_path(game_id)
    if not path or not os.path.isfile(path):
        return {}
    try:
        with open(path, 'r', encoding='utf-8') as fh:
            obj = json.load(fh)
        return obj.get('links', {})
    except Exception:
        return {}


def _save_monster_links(game_id: str, links: dict[str, list[int]]) -> bool:
    """Persist the monster → BMP links dict.  Returns True on success."""
    path = _monster_links_path(game_id)
    if not path:
        return False
    os.makedirs(os.path.dirname(path), exist_ok=True)
    obj = {'version': 1, 'game': game_id, 'links': links}
    try:
        with open(path, 'w', encoding='utf-8') as fh:
            json.dump(obj, fh, indent=2)
        return True
    except Exception:
        return False


@bp.route('/api/monster-sprite-links', methods=['GET'])
def api_monster_sprite_links_get():
    """Return monster → BMP link mapping for a game.

    Query params:  game (default: dark_sun)
    Response: { "<rdff_id>": [bmp1, bmp2, ...], "_gff_name": "SEGOBJEX.GFF" }
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description=f'Unknown game: {game_id}')
    links = _load_monster_links(game_id)
    catalog = _build_catalog(game_id)
    gff_name = catalog[0]['gff_name'] if catalog else 'SEGOBJEX.GFF'
    return jsonify({**links, '_gff_name': gff_name})


@bp.route('/api/monster-sprite-links', methods=['POST'])
def api_monster_sprite_links_post():
    """Add or remove a monster → BMP link.

    Body: { "rdff_id": <int>, "bmp_num": <int>, "action": "link" | "unlink" }
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description=f'Unknown game: {game_id}')

    body    = request.get_json(silent=True) or {}
    rdff_id = body.get('rdff_id')
    bmp_num = body.get('bmp_num')
    action  = body.get('action', 'link')

    if rdff_id is None or bmp_num is None:
        abort(400, description='Missing rdff_id or bmp_num')
    try:
        rdff_id = int(rdff_id)
        bmp_num = int(bmp_num)
    except (TypeError, ValueError):
        abort(400, description='rdff_id and bmp_num must be integers')

    links = _load_monster_links(game_id)
    key   = str(rdff_id)

    if action == 'link':
        existing = links.get(key, [])
        if bmp_num not in existing:
            existing.append(bmp_num)
        links[key] = existing
    elif action == 'unlink':
        existing = links.get(key, [])
        existing = [b for b in existing if b != bmp_num]
        if existing:
            links[key] = existing
        else:
            links.pop(key, None)
    else:
        abort(400, description=f'Unknown action: {action}')

    ok = _save_monster_links(game_id, links)
    if not ok:
        abort(500, description='Failed to write monster sprite links file')

    return jsonify({'ok': True, 'rdff_id': rdff_id, 'links': links.get(key, [])})


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
