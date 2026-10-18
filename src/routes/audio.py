"""
Audio and music routes.

GFF-embedded audio
------------------
GET /api/gff/<name>/music                       – list XMIDI sequence resources
GET /api/gff/<name>/audio/<tag_url>/<number>    – stream BVOC resource as MP3
GET /api/gff/<name>/midi/<tag_url>/<number>     – stream XMIDI resource as MIDI

Filesystem audio
----------------
GET /api/sfx                                    – list SFX tracks (GFF + loose VOC files)
GET /api/sfx-file/<filename>                    – stream a loose VOC file as MP3
GET /api/xmi-files                              – list filesystem XMI music files
GET /api/xmi-file/<filename>                    – stream an XMI file as MIDI
GET /api/ogg-files                              – list OGG music files
GET /api/ogg-file/<filename>                    – stream an OGG file directly
"""

import io
import os
import subprocess

from flask import Blueprint, jsonify, send_file, abort, request, Response

from gff import NoSuchResourceError
from config import (
    DEFAULT_GAME, GAMES, MUSIC_TAGS, MUSIC_TAG_LABELS, AUDIO_TAGS,
    get_game, find_file_nocase, list_files_nocase,
    tag_to_url_seg, url_seg_to_tag,
)
from gff_cache import load_gff
from disk_cache import precomputed_audio_path

bp = Blueprint('audio', __name__)


def _voc_to_mp3(voc_data: bytes) -> bytes:
    """Convert raw VOC bytes to MP3 via ffmpeg.  Calls ``abort(500)`` on failure."""
    proc = subprocess.run(
        ['ffmpeg', '-y', '-f', 'voc', '-i', 'pipe:0', '-f', 'mp3', 'pipe:1'],
        input=voc_data, capture_output=True,
    )
    if proc.returncode != 0:
        abort(500, description=f'ffmpeg conversion failed: {proc.stderr.decode(errors="replace")}')
    return proc.stdout


def _file_entry(filepath: str) -> dict:
    """Build a filesystem-audio list entry from an absolute path."""
    filename = os.path.basename(filepath)
    return {
        'filename': filename,
        'name':     os.path.splitext(filename)[0],
        'size':     os.path.getsize(filepath),
    }


# ---------------------------------------------------------------------------
# GFF-embedded music resources
# ---------------------------------------------------------------------------

@bp.route('/api/gff/<name>/music')
def api_music_list(name: str):
    """Return all XMIDI sequence resources in a GFF file."""
    game_id = request.args.get('game', DEFAULT_GAME)
    gff     = load_gff(name, game_id)
    result  = []
    for d in gff.describe_resources():
        stripped = d.tag.strip()
        if stripped in MUSIC_TAGS:
            result.append({
                'tag':       d.tag,
                'tag_url':   tag_to_url_seg(d.tag),
                'tag_label': MUSIC_TAG_LABELS.get(stripped, stripped),
                'number':    d.number,
                'size':      d.size,
                'type':      'midi',
            })
    return jsonify(result)


@bp.route('/api/gff/<name>/audio/<tag_url>/<int:number>')
def api_audio(name: str, tag_url: str, number: int):
    """Convert a BVOC Creative Voice File resource to MP3 via ffmpeg and stream it."""
    game_id = request.args.get('game', DEFAULT_GAME)
    tag     = url_seg_to_tag(tag_url)
    tag_s   = tag.strip()

    # Fast path: serve precomputed MP3 when available.
    stem   = os.path.splitext(name.upper())[0]
    cached = precomputed_audio_path(game_id, stem, f'{tag_s}-{number}.mp3')
    if cached is not None:
        return send_file(cached, mimetype='audio/mpeg',
                         download_name=f'{name}_{tag_s}_{number}.mp3')

    gff = load_gff(name, game_id)
    try:
        voc_data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)

    buf = io.BytesIO(_voc_to_mp3(voc_data))
    return send_file(buf, mimetype='audio/mpeg',
                     download_name=f'{name}_{tag_s}_{number}.mp3')


@bp.route('/api/gff/<name>/midi/<tag_url>/<int:number>')
def api_midi(name: str, tag_url: str, number: int):
    """Convert an XMIDI sequence resource to standard MIDI and stream it."""
    try:
        from xmi_tool import xmi_to_midi, XmiError  # noqa: F401
    except ImportError as exc:
        abort(500, description=f'xmi_tool not available: {exc}')

    game_id     = request.args.get('game', DEFAULT_GAME)
    track_index = int(request.args.get('track_index', '0'))
    tag         = url_seg_to_tag(tag_url)

    # Fast path: serve precomputed MIDI when available (track 0 only).
    if track_index == 0:
        stem   = os.path.splitext(name.upper())[0]
        tag_s  = tag.strip()
        cached = precomputed_audio_path(game_id, stem, f'{tag_s}-{number}.mid')
        if cached is not None:
            return send_file(cached, mimetype='audio/midi',
                             download_name=f'{name}_{tag_s}_{number}.mid')

    gff = load_gff(name, game_id)
    try:
        xmi_data = gff.get_resource_data(tag, number)
    except NoSuchResourceError:
        abort(404)

    try:
        midi_bytes = xmi_to_midi(xmi_data, track_index)
    except Exception as exc:
        abort(400, description=f'XMI conversion failed: {exc}')

    buf = io.BytesIO(midi_bytes)
    return send_file(buf, mimetype='audio/midi',
                     download_name=f'{name}_{tag.strip()}_{number}.mid')


# ---------------------------------------------------------------------------
# Filesystem SFX (loose VOC files)
# ---------------------------------------------------------------------------

@bp.route('/api/sfx')
def api_sfx_list():
    """Return all sound-effect tracks for a game.

    Combines GFF-embedded BVOC resources and loose *.VOC files in sfx_dir.
    Each item carries a 'source' field ('gff' or 'file').
    """
    game_id  = request.args.get('game', DEFAULT_GAME)
    gff_name = request.args.get('gff', 'RESOURCE.GFF').upper()
    game     = get_game(game_id)
    result   = []

    # GFF-embedded BVOC resources
    if os.path.isfile(os.path.join(game['dir'], gff_name)):
        try:
            gff = load_gff(gff_name, game_id)
            for d in gff.describe_resources():
                if d.tag.strip() in AUDIO_TAGS:
                    result.append({
                        'source':  'gff',
                        'gff':     gff_name,
                        'tag':     d.tag,
                        'tag_url': tag_to_url_seg(d.tag),
                        'number':  d.number,
                        'size':    d.size,
                        'name':    f'{d.tag.strip()} #{d.number}',
                    })
        except Exception:
            pass

    # Loose *.VOC files on the filesystem
    sfx_dir = game.get('sfx_dir')
    for filepath in list_files_nocase(sfx_dir or '', 'voc'):
        result.append({'source': 'file', **_file_entry(filepath)})

    return jsonify(result)


@bp.route('/api/sfx-file/<path:filename>')
def api_sfx_file(filename: str):
    """Convert a filesystem VOC file to MP3 via ffmpeg and stream it."""
    game_id = request.args.get('game', DEFAULT_GAME)
    sfx_dir = get_game(game_id).get('sfx_dir')
    if not sfx_dir or not os.path.isdir(sfx_dir):
        abort(404, description='No SFX directory configured for this game.')

    filepath = find_file_nocase(sfx_dir, filename)
    if filepath is None:
        abort(404, description=f'VOC file not found: {filename}')

    with open(filepath, 'rb') as fh:
        voc_data = fh.read()

    stem = os.path.splitext(os.path.basename(filepath))[0]
    buf  = io.BytesIO(_voc_to_mp3(voc_data))
    return send_file(buf, mimetype='audio/mpeg', download_name=f'{stem}.mp3')


# ---------------------------------------------------------------------------
# Filesystem XMI music
# ---------------------------------------------------------------------------

@bp.route('/api/xmi-files')
def api_xmi_files():
    """Return a sorted list of filesystem XMI music files in the game's xmi_dir."""
    game_id = request.args.get('game', DEFAULT_GAME)
    xmi_dir = get_game(game_id).get('xmi_dir') or ''
    return jsonify([_file_entry(p) for p in list_files_nocase(xmi_dir, 'xmi')])


@bp.route('/api/xmi-file/<path:filename>')
def api_xmi_file(filename: str):
    """Convert a filesystem XMI file to MIDI and serve it."""
    from xmi_tool import xmi_to_midi
    game_id = request.args.get('game', DEFAULT_GAME)
    xmi_dir = get_game(game_id).get('xmi_dir')
    if not xmi_dir or not os.path.isdir(xmi_dir):
        abort(404, description='No XMI directory configured for this game.')

    filepath = find_file_nocase(xmi_dir, filename)
    if filepath is None:
        abort(404, description=f'XMI file not found: {filename}')

    with open(filepath, 'rb') as fh:
        xmi_data = fh.read()

    track_index = int(request.args.get('track_index', 0))
    try:
        midi_data = xmi_to_midi(xmi_data, track_index)
    except Exception as exc:
        abort(500, description=f'XMI conversion failed: {exc}')

    return Response(midi_data, mimetype='audio/midi')


# ---------------------------------------------------------------------------
# Filesystem OGG music (Dark Sun 2)
# ---------------------------------------------------------------------------

@bp.route('/api/ogg-files')
def api_ogg_files():
    """Return a sorted list of OGG music files in the game's music_dir."""
    game_id   = request.args.get('game', DEFAULT_GAME)
    music_dir = get_game(game_id).get('music_dir') or ''
    return jsonify([_file_entry(p) for p in list_files_nocase(music_dir, 'ogg')])


@bp.route('/api/ogg-file/<path:filename>')
def api_ogg_file(filename: str):
    """Stream an OGG music file directly (no conversion required)."""
    game_id   = request.args.get('game', DEFAULT_GAME)
    music_dir = get_game(game_id).get('music_dir')
    if not music_dir or not os.path.isdir(music_dir):
        abort(404, description='No music directory configured for this game.')

    filepath = find_file_nocase(music_dir, filename)
    if filepath is None:
        abort(404, description=f'OGG file not found: {filename}')

    return send_file(filepath, mimetype='audio/ogg')
