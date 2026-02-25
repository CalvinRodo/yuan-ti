"""
Region map routes.

Endpoints
---------
GET /api/gff/<name>/rmap/<number>   – decode an RMAP/MAP resource to JSON tile data
GET /api/gff/<name>/map-render      – render a full region map image as PNG
"""

import io
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from flask import Blueprint, jsonify, send_file, abort, request

from gff import NoSuchResourceError
from config import DEFAULT_GAME
from gff_cache import load_gff, find_palette

bp = Blueprint('maps', __name__)

# Auxiliary GFF names searched for OJFF/BMP/WALL resources during map rendering.
# DS1 uses SEGOBJEX.GFF; DS2 uses OBJEX.GFF.
_AUX_GFF_NAMES = ['SEGOBJEX.GFF', 'OBJEX.GFF', 'GPLDATA.GFF']


@bp.route('/api/gff/<name>/rmap/<int:number>')
def api_rmap(name: str, number: int):
    """Decode a Region Tile Map (RMAP / MAP) resource to JSON.

    Response: {width, height, tiles}  where tiles is a flat row-major
    array of 1-byte tile indices.
    """
    game_id = request.args.get('game', DEFAULT_GAME)
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
    """
    try:
        from PIL import Image as _PilImage  # noqa — validated here so error is clear
    except ImportError:
        abort(500, description='Pillow is not installed. Run: pip install pillow')

    game_id = request.args.get('game', DEFAULT_GAME)
    gff     = load_gff(name, game_id)

    # Load auxiliary GFFs silently — not all games have all of them
    aux_gffs = []
    for aux_name in _AUX_GFF_NAMES:
        try:
            aux_gffs.append(load_gff(aux_name, game_id))
        except Exception:
            pass

    pal_gff_name = request.args.get('pal_gff')
    pal_num_raw  = request.args.get('pal_num')
    pal_num      = int(pal_num_raw) if pal_num_raw is not None else None
    pal_tag_raw  = request.args.get('pal_tag', 'PAL')
    palette      = find_palette(gff, pal_gff_name, pal_num, pal_tag_raw, game_id)

    if palette is None:
        abort(400, description='No palette found. Supply pal_gff and pal_num.')

    try:
        from region_renderer import render_region
        img = render_region(gff, aux_gffs, palette)
    except Exception as exc:
        abort(500, description=str(exc))

    buf = io.BytesIO()
    img.save(buf, format='PNG', optimize=False)
    buf.seek(0)
    return send_file(buf, mimetype='image/png')
