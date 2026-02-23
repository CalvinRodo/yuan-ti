"""
Dark Sun GFF Viewer - Flask web server.
Serves a browser UI to explore GFF file contents and view images.

Usage:
    cd ..
    uv sync
    uv run python src/server.py

Then open http://localhost:5000
"""

import os
import io
import re
import glob
import sys

from flask import Flask, jsonify, send_file, abort, send_from_directory, request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gff import GffFile, NoSuchResourceError, GFF_CHUNK_TYPES
from image import ImageReading, Palette

# ---------------------------------------------------------------------------
# Paths & game library
# ---------------------------------------------------------------------------
SRC_DIR  = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.dirname(SRC_DIR)
WEB_DIR  = os.path.join(SRC_DIR, 'web')

GAMES: dict[str, dict] = {
    'dark_sun': {
        'id':        'dark_sun',
        'label':     'Dark Sun: Shattered Lands',
        'dir':       os.path.join(BASE_DIR, 'dark_sun'),
        'supported': True,
    },
    'dark_sun_2': {
        'id':        'dark_sun_2',
        'label':     'Dark Sun: Wake of the Ravager',
        'dir':       os.path.join(BASE_DIR, 'dark_sun_2', 'data'),
        'supported': True,
    },
    'al-qadim': {
        'id':        'al-qadim',
        'label':     "Al-Qadim: The Genie's Curse",
        'dir':       os.path.join(BASE_DIR, 'al-qadim', 'data'),
        'supported': False,
    },
}
DEFAULT_GAME = 'dark_sun'


def resolve_game_dir(game_id: str) -> str:
    """Return the GFF data directory for *game_id*, or abort if unsupported."""
    info = GAMES.get(game_id, GAMES[DEFAULT_GAME])
    if not info.get('supported', True):
        abort(400, description=f"Game '{game_id}' is not supported.")
    return info['dir']

# Tags that contain renderable image data
IMAGE_TAGS = {'BMP ', 'TILE', 'ICON'}

app = Flask(__name__, static_folder=WEB_DIR, static_url_path='')

# ---------------------------------------------------------------------------
# GFF cache (evicts oldest when capacity is exceeded)
# ---------------------------------------------------------------------------
_gff_cache: dict = {}
MAX_CACHE = 10


def load_gff(name: str, game_id: str = DEFAULT_GAME) -> GffFile:
    name = name.upper()
    cache_key = (game_id, name)
    if cache_key not in _gff_cache:
        path = os.path.join(resolve_game_dir(game_id), name)
        if not os.path.isfile(path):
            abort(404, description=f"GFF file not found: {name}")
        with open(path, 'rb') as fh:
            data = fh.read()
        if len(_gff_cache) >= MAX_CACHE:
            _gff_cache.pop(next(iter(_gff_cache)))
        _gff_cache[cache_key] = GffFile.create(data)
    return _gff_cache[cache_key]


def find_palette(gff: GffFile,
                 pal_gff_name: str | None = None,
                 pal_num: int | None = None,
                 pal_tag: str = 'PAL',
                 game_id: str = DEFAULT_GAME) -> Palette | None:
    """Locate a palette.

    Priority:
    1. Explicit pal_gff + pal_num + pal_tag query params.
    2. First PAL resource that lives in the same GFF.
    3. First PAL resource in RESOURCE.GFF.
    """
    # Normalise tag to exactly 4 characters (e.g. 'PAL' → 'PAL ', 'CPAL' → 'CPAL')
    tag4 = (pal_tag.strip() + '    ')[:4]

    if pal_gff_name is not None and pal_num is not None:
        for try_tag in (tag4, 'PAL '):
            try:
                pg = load_gff(pal_gff_name, game_id)
                return Palette.from_pal_data(pg.get_resource_data(try_tag, pal_num))
            except Exception:
                continue

    candidates = [gff]
    resource_gff_path = os.path.join(resolve_game_dir(game_id), 'RESOURCE.GFF')
    if os.path.isfile(resource_gff_path) and gff is not _gff_cache.get((game_id, 'RESOURCE.GFF')):
        try:
            candidates.append(load_gff('RESOURCE.GFF', game_id))
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

@app.route('/api/games')
def api_games():
    """Return the list of known games with metadata."""
    return jsonify([
        {
            'id':        info['id'],
            'label':     info['label'],
            'supported': info['supported'],
        }
        for info in GAMES.values()
    ])

@app.route('/api/gffs')
def api_gffs():
    """Return sorted list of .GFF filenames, optionally filtered by capability.

    ?cap=maps    — only GFFs that match the RGN*.GFF naming pattern
    ?cap=images  — only GFFs that contain at least one BMP/TILE/ICON resource
    ?cap=spells  — only RESOURCE.GFF
    ?cap=texts   — only GFFs that contain at least one TEXT/MERR/SPIN resource
    ?game=<id>   — which game's GFF directory to use (default: dark_sun)
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    pattern = os.path.join(resolve_game_dir(game_id), '*.GFF')
    files = sorted(os.path.basename(f) for f in glob.glob(pattern))

    cap = request.args.get('cap', '').lower()
    if cap == 'maps':
        files = [f for f in files if f.upper().startswith('RGN')]
    elif cap == 'spells':
        files = [f for f in files if f.upper() == 'RESOURCE.GFF']
    elif cap == 'images':
        def has_images(name):
            try:
                return any(d.tag in IMAGE_TAGS
                           for d in load_gff(name, game_id).describe_resources())
            except Exception:
                return False
        files = [f for f in files if has_images(f)]
    elif cap == 'texts':
        def has_texts(name):
            try:
                return any(d.tag.strip() in TEXT_TAGS
                           for d in load_gff(name, game_id).describe_resources())
            except Exception:
                return False
        files = [f for f in files if has_texts(f)]

    return jsonify(files)


@app.route('/api/gff/<name>/resources')
def api_resources(name: str):
    """Return all resource descriptors for one GFF file."""
    game_id = request.args.get('game', DEFAULT_GAME)
    gff = load_gff(name, game_id)
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
            'is_rmap':     d.tag.strip() in ('RMAP', 'MAP'),
        })
    return jsonify(result)


@app.route('/api/gff/<name>/pals')
def api_pals(name: str):
    """List all PAL resources available: ones inside this GFF, then RESOURCE.GFF."""
    game_id = request.args.get('game', DEFAULT_GAME)
    results = []
    seen = set()

    def collect(gff_name: str):
        try:
            g = load_gff(gff_name, game_id)
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

    game_id = request.args.get('game', DEFAULT_GAME)
    tag = url_seg_to_tag(tag_url)

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


@app.route('/api/pals/all')
def api_pals_all():
    """Return every PAL and CPAL resource across all GFF files.

    Response items: {gff, tag, number}
    where gff is the uppercase filename and tag is 'PAL' or 'CPAL'.
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    pattern = os.path.join(resolve_game_dir(game_id), '*.GFF')
    all_files = sorted(os.path.basename(f) for f in glob.glob(pattern))
    results = []
    seen = set()
    for name in all_files:
        try:
            g = load_gff(name, game_id)
        except Exception:
            continue
        for d in g.describe_resources():
            tag_stripped = d.tag.strip()
            if tag_stripped in ('PAL', 'CPAL'):
                key = (name.upper(), tag_stripped, d.number)
                if key not in seen:
                    seen.add(key)
                    results.append({
                        'gff':    name.upper(),
                        'tag':    tag_stripped,
                        'number': d.number,
                    })
    return jsonify(results)


@app.route('/api/gff/<name>/rmap/<int:number>')
def api_rmap(name: str, number: int):
    """Return a Region Tile Map resource as JSON.

    Response
    --------
    {
      "width":  <int>,   # columns (sqrt of size when perfectly square)
      "height": <int>,   # rows
      "tiles":  [<int>, ...]  # flat array of 1-byte tile numbers, row-major
    }
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    gff = load_gff(name, game_id)
    # Dark Sun 1 uses 'RMAP', Dark Sun 2 uses 'MAP '
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


# Auxiliary GFF names to search for OJFF/BMP/WALL resources
_AUX_GFF_NAMES = ['SEGOBJEX.GFF', 'OBJEX.GFF', 'GPLDATA.GFF']


@app.route('/api/gff/<name>/map-render')
def api_map_render(name: str):
    """Render a full region map image as PNG.

    Loads the region GFF, auto-discovers SEGOBJEX/OBJEX/GPLDATA helper GFFs,
    and composites RMAP tiles + ETAB objects + WALL sprites into one PNG.

    Query parameters
    ----------------
    game    : game id (default: dark_sun)
    pal_gff : GFF filename that holds the palette (optional)
    pal_num : PAL resource number (optional)
    pal_tag : 'PAL' or 'CPAL' (default: 'PAL')
    """
    try:
        from PIL import Image as _PilImage  # noqa — checked here so error is clear
    except ImportError:
        abort(500, description='Pillow is not installed. Run: pip install pillow')

    game_id = request.args.get('game', DEFAULT_GAME)
    gff     = load_gff(name, game_id)

    # Load auxiliary GFFs (silently skip those that don't exist)
    aux_gffs = []
    for aux_name in _AUX_GFF_NAMES:
        try:
            aux_gffs.append(load_gff(aux_name, game_id))
        except Exception:
            pass

    # Resolve palette
    pal_gff_name = request.args.get('pal_gff')
    pal_num_raw  = request.args.get('pal_num')
    pal_num      = int(pal_num_raw) if pal_num_raw is not None else None
    pal_tag_raw  = request.args.get('pal_tag', 'PAL')
    palette      = find_palette(gff, pal_gff_name, pal_num, pal_tag_raw, game_id)

    if palette is None:
        abort(400, description='No palette found. Supply pal_gff and pal_num.')

    # Render
    try:
        from region_renderer import render_region
        img = render_region(gff, aux_gffs, palette)
    except Exception as exc:
        abort(500, description=str(exc))

    buf = io.BytesIO()
    img.save(buf, format='PNG', optimize=False)
    buf.seek(0)
    return send_file(buf, mimetype='image/png')


SPIN_ICON_OFFSET = 21000

# Psionic powers: SPIN 139–172 use ICON 3000–3033
# SPIN 139 (Detonate) → ICON 3000, SPIN 172 (Thought Shield) → ICON 3033
PSIONICS_SPIN_START  = 139
PSIONICS_SPIN_END    = 172          # inclusive
PSIONICS_ICON_BASE   = 3000         # icon = PSIONICS_ICON_BASE + (spin - PSIONICS_SPIN_START)

# Turn Undead (SPIN 1000) → one entry per elemental cleric domain
# Icons 3044–3047 correspond to Air / Earth / Fire / Water
TURN_UNDEAD_SPIN = 1000
TURN_UNDEAD_VARIANTS = [
    ('AIR',   3044),
    ('EARTH', 3045),
    ('FIRE',  3046),
    ('WATER', 3047),
]


@app.route('/api/gff/<name>/spells')
def api_spells(name: str):
    """Return parsed SPIN (spell information) resources.

    Each entry:
      number       – SPIN resource number (synthetic for Turn Undead variants)
      name         – spell name
      description  – description text (stripped)
      icon_number  – ICON resource number, or None

    Icon lookup rules:
      • SPIN 1–137, 173–248 → ICON 21000 + spin_number
      • SPIN 139–172 (psionics) → ICON 3000 + (spin_number − 139)
      • SPIN 1000 (Turn Undead) → expanded to 4 domain variants (icons 3044–3047)
      • All others (SPIN 249–255 stat increases, etc.) → no icon
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    gff = load_gff(name, game_id)
    icon_numbers = {
        d.number for d in gff.describe_resources() if d.tag.strip() == 'ICON'
    }
    result = []
    for d in gff.describe_resources():
        if d.tag.strip() != 'SPIN':
            continue

        try:
            raw  = gff.get_resource_data('SPIN', d.number)
            text = raw.decode('latin-1').strip()
        except Exception:
            text = ''

        # Split "Name: Description"
        if ': ' in text:
            spell_name, desc = text.split(': ', 1)
        elif ':' in text:
            spell_name, desc = text.split(':', 1)
        else:
            spell_name, desc = text, ''
        spell_name = spell_name.strip()
        desc       = desc.strip()

        # --- Turn Undead: expand to one card per elemental domain ---
        if d.number == TURN_UNDEAD_SPIN:
            for domain, icon_n in TURN_UNDEAD_VARIANTS:
                result.append({
                    'number':      icon_n,          # unique synthetic id per variant
                    'name':        f'TURN UNDEAD - {domain}',
                    'description': desc,
                    'icon_number': icon_n if icon_n in icon_numbers else None,
                })
            continue

        # --- Psionic powers: offset into 3000-range icon block ---
        if PSIONICS_SPIN_START <= d.number <= PSIONICS_SPIN_END:
            icon_n = PSIONICS_ICON_BASE + (d.number - PSIONICS_SPIN_START)
        else:
            icon_n = SPIN_ICON_OFFSET + d.number

        result.append({
            'number':      d.number,
            'name':        spell_name,
            'description': desc,
            'icon_number': icon_n if icon_n in icon_numbers else None,
        })

    result.sort(key=lambda r: r['name'])
    return jsonify(result)


TEXT_TAGS = {'TEXT', 'MERR', 'SPIN', 'NAME'}


def _decode_text(raw: bytes) -> str:
    """Decode a single null-terminated latin-1 string."""
    return raw.split(b'\x00')[0].decode('latin-1').replace('\r\n', '\n').replace('\r', '\n')


# Separator pattern for NAME resources:
# Each entry is: name\x00 + path_fragment\x00 + 1-3 type bytes ending with \xff+
# Observed variants:
#   \x00 XX \x00 \xff           – 4 bytes (most common)
#   \x00 XX \x00 \xff \xff+     – extended, e.g. 00 d5 00 ff ff ff
#   \x00 \xff \xff              – 3 bytes
#   \x00 \xff \xff \xff         – 4 bytes
# The trailing \xff+ handles extended separators that would otherwise
# leave stray \xff bytes at the start of the next entry's name.
_NAME_SEP_RE = re.compile(rb'\x00(?:.\x00\xff+|\xff{2,4})', re.DOTALL)


def _parse_name_resource(raw: bytes) -> list:
    """Parse a NAME resource into structured entries.

    Each entry: {text, path, sep_hex, raw_bytes, seg_len}
      text      – null-terminated display name
      path      – ASCII path fragment between the two nulls
      sep_hex   – separator bytes rendered as space-separated hex
      raw_bytes – complete bytes for this entry (segment + separator) as list[int]
      seg_len   – byte length of the segment portion (raw_bytes[:seg_len] = content)
    """
    entries = []
    sep_spans = [(m.start(), m.end()) for m in _NAME_SEP_RE.finditer(raw)]
    starts = [0] + [end for _, end in sep_spans]
    ends   = [start for start, _ in sep_spans] + [len(raw)]
    seps   = [raw[s:e] for s, e in sep_spans] + [b'']

    for seg_start, seg_end, sep in zip(starts, ends, seps):
        seg = raw[seg_start:seg_end]
        if not seg:
            continue
        null_pos = seg.find(b'\x00')
        if null_pos >= 0:
            text = seg[:null_pos].decode('latin-1', errors='replace')
            path_raw = seg[null_pos + 1:]
        else:
            text = seg.decode('latin-1', errors='replace')
            path_raw = b''

        # Decode path as mixed ASCII/hex inline — non-printable become \xNN
        path_parts = []
        for b in path_raw:
            if 32 <= b < 127:
                path_parts.append(chr(b))
            else:
                path_parts.append(f'\\x{b:02x}')
        path_str = ''.join(path_parts)

        # sep includes the leading \x00 (the path-fragment null terminator);
        # encode the full separator as space-delimited hex pairs
        sep_hex = ' '.join(f'{b:02x}' for b in sep) if sep else ''

        if text.strip():
            entries.append({
                'text':      text,
                'path':      path_str,
                'sep_hex':   sep_hex,
                'raw_bytes': list(seg) + list(sep),
                'seg_len':   len(seg),
            })

    return entries


@app.route('/api/gff/<name>/texts')
def api_texts(name: str):
    """Return decoded text content for all TEXT, MERR, SPIN, and NAME resources.

    Each entry: {tag, number, size, content}
    For NAME resources, content is a list of {text, path, sep_hex} entry objects.
    For all other tags, content is a single string.
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    gff = load_gff(name, game_id)
    result = []
    for d in gff.describe_resources():
        if d.tag.strip() not in TEXT_TAGS:
            continue
        try:
            raw = gff.get_resource_data(d.tag, d.number)
            if d.tag.strip() == 'NAME':
                content = _parse_name_resource(raw)
            else:
                content = _decode_text(raw)
        except Exception:
            content = [] if d.tag.strip() == 'NAME' else ''
        result.append({
            'tag':     d.tag.strip(),
            'number':  d.number,
            'size':    d.size,
            'content': content,
        })
    return jsonify(result)


@app.route('/api/gff/<name>/pal-png/<tag_url>/<int:number>')
def api_pal_png(name: str, tag_url: str, number: int):
    """Render a PAL or CPAL resource as a PNG palette grid.

    Returns a 512×512 PNG: 16 columns × 16 rows of 32×32 pixel color swatches.

    Query parameters
    ----------------
    game      : game id (default: dark_sun)
    bit_depth : 6, 8, or auto (default: auto — detects from channel values)
    """
    try:
        from PIL import Image as PilImage, ImageDraw
    except ImportError:
        abort(500, description="Pillow is not installed. Run: pip install pillow")

    game_id        = request.args.get('game', DEFAULT_GAME)
    bit_depth_raw  = request.args.get('bit_depth', 'auto')
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


@app.route('/api/gff/<name>/raw/<tag_url>/<int:number>')
def api_raw(name: str, tag_url: str, number: int):
    """Return the raw bytes of any resource as application/octet-stream."""
    game_id = request.args.get('game', DEFAULT_GAME)
    tag = url_seg_to_tag(tag_url)
    gff = load_gff(name, game_id)
    try:
        data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)
    buf = io.BytesIO(data)
    return send_file(buf, mimetype='application/octet-stream')


@app.route('/api/gff/<name>/image/<tag_url>/<int:number>/info')
def api_image_info(name: str, tag_url: str, number: int):
    """Return frame count and dimensions for an image resource."""
    game_id = request.args.get('game', DEFAULT_GAME)
    tag = url_seg_to_tag(tag_url)
    gff = load_gff(name, game_id)
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
    print(f"Games          : {', '.join(GAMES.keys())}")
    print(f"Web directory  : {WEB_DIR}")
    print(f"Open           : http://localhost:5000")
    app.run(debug=True, port=5000)
