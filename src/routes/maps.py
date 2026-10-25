"""
Region map routes.

Endpoints
---------
GET /api/gff/<name>/rmap/<number>   – decode an RMAP/MAP resource to JSON tile data
GET /api/gff/<name>/map-render      – render a full region map image as PNG
GET/POST /api/gff/<name>/map-environment – read/save indoor or outdoor classification
GET /api/region-connections        – directed graph of scripted party transitions
GET/POST /api/gff/<name>/map-name   – read/save a custom map name
"""

import hashlib
import json
import os
import tempfile
from functools import lru_cache
from threading import Lock

from flask import Blueprint, jsonify, send_file, abort, request, current_app

from gff import NoSuchResourceError
from config import DEFAULT_GAME, GAMES, MAP_ENVIRONMENTS_PATH, MAP_NAMES_PATH, resolve_game_dir, list_files_nocase
from gff_cache import load_gff, find_palette
from disk_cache import load_precomputed_json, precomputed_png_path, precomputed_path, source_signature

bp = Blueprint('maps', __name__)

# Auxiliary GFF names searched for OJFF/BMP/WALL resources during map rendering.
# DS1 uses SEGOBJEX.GFF; DS2 uses OBJEX.GFF.
_AUX_GFF_NAMES = ['SEGOBJEX.GFF', 'OBJEX.GFF', 'GPLDATA.GFF']
_environment_lock = Lock()
_names_lock = Lock()
_render_lock = Lock()
_connections_lock = Lock()


def _map_sources(game_id):
    from config import SRC_DIR
    return source_signature([
        resolve_game_dir(game_id),
        *list_files_nocase(resolve_game_dir(game_id), '.GFF'),
        *(os.path.join(SRC_DIR, module) for module in (
            'config.py', 'gff.py', 'gff_cache.py', 'image.py', 'region_renderer.py',
            'region_connections.py', 'gpl_parser.py', 'routes/maps.py',
            'data/games.toml',
        )),
    ])


@lru_cache(maxsize=4)
def _region_graph(game_id, signature):
    from region_connections import region_connections

    key = hashlib.sha256(json.dumps(signature).encode()).hexdigest()
    cached = load_precomputed_json(game_id, 'maps', 'connections', f'{key}.json')
    if cached is not None:
        return cached
    nodes = []
    for path, _, _ in signature:
        name = os.path.basename(path)
        if not name.upper().startswith('RGN') or not name.upper().endswith('.GFF'):
            continue
        gff = load_gff(name, game_id)
        region = next((d.number for d in gff.describe_resources() if d.tag == 'GMAP'), None)
        if region is not None:
            nodes.append({'id': region, 'file': name})
    graph = region_connections(load_gff('GPLDATA.GFF', game_id), nodes)
    path = precomputed_path(game_id, 'maps', 'connections', f'{key}.json')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    _write_map_metadata(path, graph)
    return graph


def _write_map_metadata(path, data):
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode='w', encoding='utf-8', dir=os.path.dirname(path), delete=False,
        ) as fh:
            temporary = fh.name
            json.dump(data, fh, indent=2, sort_keys=True)
            fh.write('\n')
        os.replace(temporary, path)
    finally:
        if temporary and os.path.exists(temporary):
            os.unlink(temporary)


def _load_map_names():
    with open(MAP_NAMES_PATH, encoding='utf-8') as fh:
        return json.load(fh)


@bp.route('/api/gff/<name>/map-name', methods=['GET', 'POST'])
def api_map_name(name: str):
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description='Unknown game')
    name = name.upper()
    gff = load_gff(name, game_id)
    if not any(d.tag == 'GMAP' for d in gff.describe_resources()):
        abort(404, description='GFF contains no region')
    label = None
    if request.method == 'POST':
        body = request.get_json()
        label = body.get('name') if isinstance(body, dict) else None
        if not isinstance(label, str):
            abort(400, description='name must be a string')
        label = label.strip()
    try:
        with _names_lock:
            names = _load_map_names()
            maps = names.setdefault(game_id, {})
            if label is not None:
                if label:
                    maps[name] = label
                else:
                    maps.pop(name, None)
                _write_map_metadata(MAP_NAMES_PATH, names)
            return jsonify(name=maps.get(name, ''))
    except (OSError, ValueError):
        current_app.logger.exception('Map names could not be read or saved')
        abort(500, description='Map names could not be read or saved')


@bp.route('/api/region-connections')
def api_region_connections():
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description='Unknown game')
    if game_id == 'al-qadim':
        abort(400, description='Region connections require GFF-based game data; Al-Qadim uses LIB files.')
    with _connections_lock:
        graph = _region_graph(game_id, _map_sources(game_id))
    try:
        names = _load_map_names().get(game_id, {})
    except (OSError, ValueError):
        current_app.logger.exception('Map names could not be read')
        abort(500, description='Map names could not be read')
    nodes = [
        {**node, 'name': names.get(node['file'].upper(), '') if node['file'] else ''}
        for node in graph['nodes']
    ]
    return jsonify({**graph, 'nodes': nodes})


@bp.route('/api/gff/<name>/map-environment', methods=['GET', 'POST'])
def api_map_environment(name: str):
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description='Unknown game')
    name = name.upper()
    gff = load_gff(name, game_id)
    if not any(d.tag in ('RMAP', 'MAP ') for d in gff.describe_resources()):
        abort(404, description='GFF contains no map')

    environment = None
    if request.method == 'POST':
        body = request.get_json()
        environment = body.get('environment') if isinstance(body, dict) else None
        if environment not in ('indoors', 'outdoors'):
            abort(400, description='environment must be indoors or outdoors')

    try:
        with _environment_lock:
            with open(MAP_ENVIRONMENTS_PATH, encoding='utf-8') as fh:
                environments = json.load(fh)
            maps = environments.setdefault(game_id, {})
            if environment is not None:
                maps[name] = environment
                _write_map_metadata(MAP_ENVIRONMENTS_PATH, environments)
            return jsonify(environment=maps.get(name, 'outdoors'), configured=name in maps)
    except (OSError, ValueError) as exc:
        current_app.logger.exception('Map environment file could not be read or saved')
        abort(500, description=f'Map environment file could not be read or saved: {exc}')


@bp.route('/api/gff/<name>/rmap/<int:number>')
def api_rmap(name: str, number: int):
    """Decode a Region Tile Map (RMAP / MAP) resource to JSON.

    Response: {width, height, tiles}  where tiles is a flat row-major
    array of 1-byte tile indices.
    """
    game_id = request.args.get('game', DEFAULT_GAME)

    # Fast path: serve from precomputed disk cache when available.
    stem    = os.path.splitext(name.upper())[0]
    cached  = load_precomputed_json(game_id, 'maps', f'{stem}-{number}.json')
    if cached is not None:
        return jsonify(cached)

    gff     = load_gff(name, game_id)

    # DS1 stores maps as 'RMAP'; DS2 uses 'MAP '
    raw = None
    for try_tag in ('RMAP', 'MAP '):
        try:
            raw = gff.get_resource_data(try_tag, number)
            break
        except NoSuchResourceError:
            continue
    if raw is None:
        abort(404)

    size = len(raw)
    sq   = int(size ** 0.5)
    if sq * sq == size:
        width = height = sq
    else:
        width  = size
        height = 1

    return jsonify({'width': width, 'height': height, 'tiles': list(raw)})


@bp.route('/api/gff/<name>/map-render')
def api_map_render(name: str):
    """Render a full region map image as PNG.

    Composites RMAP ground tiles, ETAB object sprites, and WALL sprites into
    a single image.  Automatically loads SEGOBJEX/OBJEX/GPLDATA as auxiliary
    GFFs because assets may span multiple files.

    Query parameters
    ----------------
    game    : game id (default: dark_sun)
    pal_gff : GFF filename that holds the palette (optional)
    pal_num : PAL resource number (optional)
    pal_tag : 'PAL' or 'CPAL' (default: 'PAL')
    thumbnail : '1' for a cached preview fitting within 192 x 112 pixels
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    if game_id not in GAMES:
        abort(404, description='Unknown game')
    name = name.upper()
    if os.path.basename(name) != name:
        abort(404)
    pal_gff_name = request.args.get('pal_gff')
    pal_num_raw = request.args.get('pal_num')
    try:
        pal_num = int(pal_num_raw) if pal_num_raw is not None else None
    except ValueError:
        abort(400, description='pal_num must be an integer')
    pal_tag = request.args.get('pal_tag', 'PAL')
    signature = _map_sources(game_id)

    if not pal_gff_name and pal_num_raw is None:
        stem = os.path.splitext(name)[0]
        cached = precomputed_png_path(game_id, 'maps', f'{stem}.png')
        if cached is not None and os.stat(cached).st_mtime_ns >= max(
            (mtime for _, mtime, _ in signature if mtime is not None), default=0,
        ):
            return _map_image_response(cached)

    key = hashlib.sha256(json.dumps(
        [name, pal_gff_name, pal_num, pal_tag, signature],
    ).encode()).hexdigest()
    path = precomputed_path(game_id, 'maps', 'rendered', f'{key}.png')
    if not os.path.isfile(path):
        with _render_lock:
            if not os.path.isfile(path):
                img = _render_map(name, game_id, pal_gff_name, pal_num, pal_tag)
                temporary = None
                try:
                    os.makedirs(os.path.dirname(path), exist_ok=True)
                    with tempfile.NamedTemporaryFile(dir=os.path.dirname(path), delete=False) as fh:
                        temporary = fh.name
                        img.save(fh, format='PNG', optimize=False)
                    os.replace(temporary, path)
                except OSError:
                    current_app.logger.exception('Rendered map could not be cached')
                    abort(500, description='Rendered map could not be cached')
                finally:
                    if temporary and os.path.exists(temporary):
                        os.unlink(temporary)
    return _map_image_response(path)


def _map_image_response(path):
    if request.args.get('thumbnail') == '1':
        from PIL import Image

        key = hashlib.sha256(json.dumps(source_signature([path])).encode()).hexdigest()
        thumbnail = os.path.join(os.path.dirname(path), 'thumbnails', f'{key}.png')
        if not os.path.isfile(thumbnail):
            with _render_lock:
                if not os.path.isfile(thumbnail):
                    temporary = None
                    try:
                        os.makedirs(os.path.dirname(thumbnail), exist_ok=True)
                        with Image.open(path) as image:
                            image.thumbnail((192, 112), Image.Resampling.LANCZOS)
                            with tempfile.NamedTemporaryFile(dir=os.path.dirname(thumbnail), delete=False) as fh:
                                temporary = fh.name
                                image.save(fh, format='PNG')
                        os.replace(temporary, thumbnail)
                    except OSError:
                        current_app.logger.exception('Map thumbnail could not be generated')
                        abort(500, description='Map thumbnail could not be generated')
                    finally:
                        if temporary and os.path.exists(temporary):
                            os.unlink(temporary)
        path = thumbnail
    return send_file(path, mimetype='image/png', conditional=True, max_age=0)


def _render_map(name, game_id, pal_gff_name, pal_num, pal_tag_raw):
    try:
        from PIL import Image as _PilImage  # noqa — validated here so error is clear
    except ImportError:
        abort(500, description='Pillow is not installed. Run: pip install pillow')

    gff     = load_gff(name, game_id)

    # Not all games have every auxiliary file.
    aux_gffs = []
    for aux_name in _AUX_GFF_NAMES:
        if os.path.isfile(os.path.join(resolve_game_dir(game_id), aux_name)):
            aux_gffs.append(load_gff(aux_name, game_id))

    # When no explicit palette is supplied, derive it from the region number:
    # the GMAP resource number in the region GFF equals the region number, and
    # the matching PAL lives in GPLDATA.GFF at that same number.
    if pal_gff_name is None and pal_num is None:
        for d in gff.describe_resources():
            if d.tag == 'GMAP':
                pal_gff_name = 'GPLDATA.GFF'
                pal_num      = d.number
                break

    palette      = find_palette(gff, pal_gff_name, pal_num, pal_tag_raw, game_id)

    if palette is None:
        abort(400, description='No palette found. Supply pal_gff and pal_num.')

    try:
        from region_renderer import render_region
        return render_region(gff, aux_gffs, palette)
    except Exception as exc:
        current_app.logger.exception('Region map rendering failed')
        abort(500, description=str(exc))
