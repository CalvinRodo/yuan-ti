"""
Dark Sun GFF Viewer - Flask web server.
Serves a browser UI to explore GFF file contents and view images.

Usage:
  cd src
  pip install flask pillow
  python server.py

Then open http://localhost:5000
"""

import os
import io
import glob
import sys

from flask import Flask, jsonify, send_file, abort, send_from_directory, request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gff import GffFile, NoSuchResourceError, GFF_CHUNK_TYPES
from image import ImageReading, Palette

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
SRC_DIR  = os.path.dirname(os.path.abspath(__file__))
GAME_DIR = os.path.dirname(SRC_DIR)
WEB_DIR  = os.path.join(SRC_DIR, 'web')

# Tags that contain renderable image data
IMAGE_TAGS = {'BMP ', 'TILE', 'ICON'}

app = Flask(__name__, static_folder=WEB_DIR, static_url_path='')

# ---------------------------------------------------------------------------
# GFF cache (evicts oldest when capacity is exceeded)
# ---------------------------------------------------------------------------
_gff_cache: dict = {}
MAX_CACHE = 10


def load_gff(name: str) -> GffFile:
    name = name.upper()
    if name not in _gff_cache:
        path = os.path.join(GAME_DIR, name)
        if not os.path.isfile(path):
            abort(404, description=f"GFF file not found: {name}")
        with open(path, 'rb') as fh:
            data = fh.read()
        if len(_gff_cache) >= MAX_CACHE:
            _gff_cache.pop(next(iter(_gff_cache)))
        _gff_cache[name] = GffFile.create(data)
    return _gff_cache[name]


def find_palette(gff: GffFile,
                 pal_gff_name: str | None = None,
                 pal_num: int | None = None) -> Palette | None:
    """Locate a palette.  Priority:
    1. Explicit pal_gff + pal_num query params.
    2. First PAL resource that lives in the same GFF.
    3. First PAL resource in RESOURCE.GFF.
    """
    if pal_gff_name is not None and pal_num is not None:
        try:
            pg = load_gff(pal_gff_name)
            return Palette.from_pal_data(pg.get_resource_data('PAL ', pal_num))
        except Exception:
            pass  # fall through to auto-detect

    candidates = [gff]
    resource_gff_path = os.path.join(GAME_DIR, 'RESOURCE.GFF')
    if os.path.isfile(resource_gff_path) and gff is not _gff_cache.get('RESOURCE.GFF'):
        try:
            candidates.append(load_gff('RESOURCE.GFF'))
        except Exception:
            pass

    for candidate in candidates:
        for desc in candidate.describe_resources():
            if desc.tag == 'PAL ':
                try:
                    return Palette.from_pal_data(
                        candidate.get_resource_data('PAL ', desc.number))
                except Exception:
                    continue
    return None


def tag_to_url_seg(tag: str) -> str:
    """Replace trailing space with underscore for use in URL path segments."""
    return tag.rstrip(' ') + '_' * (4 - len(tag.rstrip()))


def url_seg_to_tag(seg: str) -> str:
    """Reverse of tag_to_url_seg: restore trailing space."""
    if len(seg) == 4 and seg[3] == '_':
        return seg[:3] + ' '
    return seg


# ---------------------------------------------------------------------------
# Routes — static
# ---------------------------------------------------------------------------

@app.route('/')
def index():
    return send_from_directory(WEB_DIR, 'index.html')


# ---------------------------------------------------------------------------
# Routes — API
# ---------------------------------------------------------------------------

@app.route('/api/gffs')
def api_gffs():
    """Return sorted list of all .GFF filenames in the game directory."""
    pattern = os.path.join(GAME_DIR, '*.GFF')
    files = sorted([os.path.basename(f) for f in glob.glob(pattern)])
    return jsonify(files)


@app.route('/api/gff/<name>/resources')
def api_resources(name: str):
    """Return all resource descriptors for one GFF file."""
    gff = load_gff(name)
    result = []
    for d in gff.describe_resources():
        result.append({
            'tag':         d.tag,
            'tag_url':     tag_to_url_seg(d.tag),
            'number':      d.number,
            'size':        d.size,
            'offset':      d.offset,
            'description': GFF_CHUNK_TYPES.get(d.tag, 'Unknown'),
            'is_image':    d.tag in IMAGE_TAGS,
        })
    return jsonify(result)


@app.route('/api/gff/<name>/pals')
def api_pals(name: str):
    """List all PAL resources available: ones inside this GFF, then RESOURCE.GFF."""
    results = []
    seen = set()

    def collect(gff_name: str):
        try:
            g = load_gff(gff_name)
        except Exception:
            return
        for d in g.describe_resources():
            if d.tag == 'PAL ':
                key = (gff_name.upper(), d.number)
                if key not in seen:
                    seen.add(key)
                    results.append({'gff': gff_name.upper(), 'number': d.number})

    collect(name)
    if name.upper() != 'RESOURCE.GFF':
        collect('RESOURCE.GFF')

    return jsonify(results)


@app.route('/api/gff/<name>/image/<tag_url>/<int:number>')
def api_image(name: str, tag_url: str, number: int):
    """Render an image resource as PNG.

    Query parameters
    ----------------
    pal_gff   : GFF filename that holds the palette  (optional)
    pal_num   : PAL resource number to use           (optional)
    frame     : specific frame index; -1 = sprite-sheet of all frames (default -1)
    """
    try:
        from PIL import Image as PilImage
    except ImportError:
        abort(500, description="Pillow is not installed. Run: pip install pillow")

    tag = url_seg_to_tag(tag_url)

    gff = load_gff(name)
    try:
        image_data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)

    pal_gff_name = request.args.get('pal_gff')
    pal_num_raw  = request.args.get('pal_num')
    pal_num      = int(pal_num_raw) if pal_num_raw is not None else None
    frame_idx    = int(request.args.get('frame', '-1'))

    palette = find_palette(gff, pal_gff_name, pal_num)

    if palette is None:
        # Grey placeholder so the UI doesn't break
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
                    fi = frame_to_pil_image(frame, palette)
                    sheet.paste(fi, (x_off, 0))
                    x_off += frame.width
                img = sheet

    buf = io.BytesIO()
    img.save(buf, format='PNG')
    buf.seek(0)
    return send_file(buf, mimetype='image/png')


@app.route('/api/gff/<name>/raw/<tag_url>/<int:number>')
def api_raw(name: str, tag_url: str, number: int):
    """Return the raw bytes of any resource as application/octet-stream."""
    tag = url_seg_to_tag(tag_url)
    gff = load_gff(name)
    try:
        data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)
    buf = io.BytesIO(data)
    return send_file(buf, mimetype='application/octet-stream')


@app.route('/api/gff/<name>/image/<tag_url>/<int:number>/info')
def api_image_info(name: str, tag_url: str, number: int):
    """Return frame count and dimensions for an image resource."""
    tag = url_seg_to_tag(tag_url)
    gff = load_gff(name)
    try:
        image_data = gff.get_resource_data(tag, number)
        frames = ImageReading.extract_frames(image_data)
    except Exception as exc:
        return jsonify({'error': str(exc), 'frames': []})

    return jsonify({
        'frames': [{'index': i, 'width': f.width, 'height': f.height}
                   for i, f in enumerate(frames)]
    })


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == '__main__':
    print(f"Game directory : {GAME_DIR}")
    print(f"Web directory  : {WEB_DIR}")
    print(f"Open           : http://localhost:5000")
    app.run(debug=True, port=5000)
