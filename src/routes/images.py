"""
Image rendering routes.

Endpoints
---------
GET /api/gff/<name>/image/<tag_url>/<number>        – render image resource as PNG
GET /api/gff/<name>/image/<tag_url>/<number>/info   – frame count and dimensions
GET /api/gff/<name>/pal-png/<tag_url>/<number>      – render palette as a colour-swatch PNG
"""

import io
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from flask import Blueprint, jsonify, send_file, abort, request

from gff import NoSuchResourceError
from image import ImageReading, Palette
from config import DEFAULT_GAME, url_seg_to_tag
from gff_cache import load_gff, find_palette

bp = Blueprint('images', __name__)


@bp.route('/api/gff/<name>/image/<tag_url>/<int:number>')
def api_image(name: str, tag_url: str, number: int):
    """Render a single image resource (or all frames as a sprite-sheet) as PNG.

    Query parameters
    ----------------
    pal_gff  : GFF filename that holds the palette   (optional)
    pal_num  : PAL resource number                   (optional)
    pal_tag  : 'PAL' or 'CPAL'                       (default: 'PAL')
    frame    : specific frame index; -1 = sprite-sheet of all frames (default)
    game     : game id                               (default: dark_sun)
    """
    try:
        from PIL import Image as PilImage
    except ImportError:
        abort(500, description="Pillow is not installed. Run: pip install pillow")

    game_id = request.args.get('game', DEFAULT_GAME)
    tag     = url_seg_to_tag(tag_url)

    gff = load_gff(name, game_id)
    try:
        image_data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)

    pal_gff_name = request.args.get('pal_gff')
    pal_num_raw  = request.args.get('pal_num')
    pal_num      = int(pal_num_raw) if pal_num_raw is not None else None
    pal_tag_raw  = request.args.get('pal_tag', 'PAL')
    frame_idx    = int(request.args.get('frame', '-1'))

    palette = find_palette(gff, pal_gff_name, pal_num, pal_tag_raw, game_id)

    if palette is None:
        # Grey placeholder so the UI doesn't break when no palette is available
        img = PilImage.new('RGBA', (32, 32), (80, 80, 80, 255))
    else:
        try:
            frames = ImageReading.extract_frames(image_data)
        except Exception as exc:
            abort(400, description=str(exc))

        if not frames:
            img = PilImage.new('RGBA', (1, 1), (0, 0, 0, 0))
        else:
            from image_tool import frame_to_pil_image

            if 0 <= frame_idx < len(frames):
                img = frame_to_pil_image(frames[frame_idx], palette)
            else:
                # Horizontal sprite-sheet of all frames
                total_w = max(sum(f.width for f in frames), 1)
                max_h   = max((f.height for f in frames), default=1)
                sheet   = PilImage.new('RGBA', (total_w, max_h), (0, 0, 0, 0))
                x_off   = 0
                for frame in frames:
                    sheet.paste(frame_to_pil_image(frame, palette), (x_off, 0))
                    x_off += frame.width
                img = sheet

    buf = io.BytesIO()
    img.save(buf, format='PNG')
    buf.seek(0)
    return send_file(buf, mimetype='image/png')


@bp.route('/api/gff/<name>/image/<tag_url>/<int:number>/info')
def api_image_info(name: str, tag_url: str, number: int):
    """Return frame count and dimensions for an image resource."""
    game_id = request.args.get('game', DEFAULT_GAME)
    tag     = url_seg_to_tag(tag_url)
    gff     = load_gff(name, game_id)
    try:
        image_data = gff.get_resource_data(tag, number)
        frames     = ImageReading.extract_frames(image_data)
    except Exception as exc:
        return jsonify({'error': str(exc), 'frames': []})

    return jsonify({
        'frames': [{'index': i, 'width': f.width, 'height': f.height}
                   for i, f in enumerate(frames)]
    })


@bp.route('/api/gff/<name>/pal-png/<tag_url>/<int:number>')
def api_pal_png(name: str, tag_url: str, number: int):
    """Render a PAL or CPAL resource as a 512×512 colour-swatch PNG grid.

    The grid is 16 columns × 16 rows of 32×32 pixel swatches.

    Query parameters
    ----------------
    game      : game id (default: dark_sun)
    bit_depth : 6, 8, or auto (default: auto — detects from channel values)
    """
    try:
        from PIL import Image as PilImage, ImageDraw
    except ImportError:
        abort(500, description="Pillow is not installed. Run: pip install pillow")

    game_id       = request.args.get('game', DEFAULT_GAME)
    bit_depth_raw = request.args.get('bit_depth', 'auto')
    if bit_depth_raw in ('6', '8'):
        bit_depth: str | int = int(bit_depth_raw)
    elif bit_depth_raw == 'auto':
        bit_depth = 'auto'
    else:
        abort(400, description="bit_depth must be 6, 8, or auto")

    tag = url_seg_to_tag(tag_url)
    gff = load_gff(name, game_id)
    try:
        raw = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)

    try:
        palette = Palette.from_pal_data(raw, bit_depth=bit_depth)
    except Exception as exc:
        abort(400, description=str(exc))

    swatch  = 32
    columns = 16
    rows    = (len(palette) + columns - 1) // columns
    img     = PilImage.new('RGB', (columns * swatch, rows * swatch), (0, 0, 0))
    draw    = ImageDraw.Draw(img)

    for idx in range(len(palette)):
        c   = palette.get_color(idx)
        col = idx % columns
        row = idx // columns
        x0, y0 = col * swatch, row * swatch
        draw.rectangle((x0, y0, x0 + swatch - 1, y0 + swatch - 1),
                       fill=(c.red, c.green, c.blue))

    buf = io.BytesIO()
    img.save(buf, format='PNG')
    buf.seek(0)
    return send_file(buf, mimetype='image/png')
