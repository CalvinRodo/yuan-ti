"""
GFF file browser routes.

Endpoints
---------
GET /api/gffs                                    – list GFF files for a game
GET /api/gff/<name>/resources                    – list all resources in a GFF
GET /api/gff/<name>/pals                         – list available palettes for a GFF
GET /api/pals/all                                – all PAL/CPAL resources across all GFFs
GET /api/gff/<name>/raw/<tag_url>/<number>       – raw bytes of any resource
"""

import glob
import io
import os
import struct

from flask import Blueprint, jsonify, send_file, abort, request

from gff import GFF_CHUNK_TYPES, NoSuchResourceError
from config import (
    DEFAULT_GAME, resolve_game_dir,
    IMAGE_TAGS, MUSIC_TAGS, AUDIO_TAGS, TEXT_TAGS,
    tag_to_url_seg, url_seg_to_tag,
)
from gff_cache import load_gff

bp = Blueprint('gff', __name__)


@bp.route('/api/gffs')
def api_gffs():
    """Return sorted list of .GFF filenames, optionally filtered by capability.

    ?cap=maps    — only GFFs whose name starts with RGN
    ?cap=images  — only GFFs containing at least one image resource
    ?cap=spells  — only RESOURCE.GFF
    ?cap=texts   — only GFFs containing TEXT/MERR/SPIN/NAME resources
    ?cap=music   — only GFFs containing XMIDI sequence resources
    ?cap=sfx     — only GFFs containing BVOC audio resources
    ?game=<id>   — which game's directory to use (default: dark_sun)
    """
    game_id = request.args.get('game', DEFAULT_GAME)
    pattern = os.path.join(resolve_game_dir(game_id), '*.GFF')
    files   = sorted(os.path.basename(f) for f in glob.glob(pattern))

    cap = request.args.get('cap', '').lower()

    def has_tags(name, tags):
        try:
            return any(d.tag.strip() in tags
                       for d in load_gff(name, game_id).describe_resources())
        except Exception:
            return False

    if cap == 'maps':
        files = [f for f in files if f.upper().startswith('RGN')]
    elif cap == 'spells':
        files = [f for f in files if f.upper() == 'RESOURCE.GFF']
    elif cap == 'images':
        files = [f for f in files if has_tags(f, {t.strip() for t in IMAGE_TAGS})]
    elif cap == 'texts':
        files = [f for f in files if has_tags(f, TEXT_TAGS)]
    elif cap == 'music':
        files = [f for f in files if has_tags(f, MUSIC_TAGS)]
    elif cap == 'sfx':
        files = [f for f in files if has_tags(f, AUDIO_TAGS)]

    resp = jsonify(files)
    resp.headers['Cache-Control'] = 'public, max-age=300'
    return resp


@bp.route('/api/gff/<name>/resources')
def api_resources(name: str):
    """Return all resource descriptors for one GFF file."""
    game_id = request.args.get('game', DEFAULT_GAME)
    gff     = load_gff(name, game_id)
    result  = []
    for d in gff.describe_resources():
        is_img = d.tag in IMAGE_TAGS
        frame_count = None
        if is_img:
            try:
                raw = gff.get_resource_data(d.tag, d.number)
                n = struct.unpack_from('<H', raw, 4)[0]
                frame_count = max(1, n) if n > 0 else 1
            except Exception:
                frame_count = 1
        result.append({
            'tag':         d.tag,
            'tag_url':     tag_to_url_seg(d.tag),
            'number':      d.number,
            'size':        d.size,
            'offset':      d.offset,
            'description': GFF_CHUNK_TYPES.get(d.tag, 'Unknown'),
            'is_image':    is_img,
            'is_rmap':     d.tag.strip() in ('RMAP', 'MAP'),
            'frame_count': frame_count,
        })
    resp = jsonify(result)
    resp.headers['Cache-Control'] = 'public, max-age=300'
    return resp


@bp.route('/api/gff/<name>/pals')
def api_pals(name: str):
    """List all PAL resources available: ones in this GFF, then RESOURCE.GFF."""
    game_id = request.args.get('game', DEFAULT_GAME)
    results = []
    seen    = set()

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


@bp.route('/api/pals/all')
def api_pals_all():
    """Return every PAL and CPAL resource across all GFF files.

    Response items: {gff, tag, number}
    """
    game_id   = request.args.get('game', DEFAULT_GAME)
    pattern   = os.path.join(resolve_game_dir(game_id), '*.GFF')
    all_files = sorted(os.path.basename(f) for f in glob.glob(pattern))
    results   = []
    seen      = set()

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


@bp.route('/api/gff/<name>/raw/<tag_url>/<int:number>')
def api_raw(name: str, tag_url: str, number: int):
    """Return the raw bytes of any resource as application/octet-stream."""
    game_id = request.args.get('game', DEFAULT_GAME)
    tag     = url_seg_to_tag(tag_url)
    gff     = load_gff(name, game_id)
    try:
        data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)
    return send_file(io.BytesIO(data), mimetype='application/octet-stream')
