"""
Cinematic (FLI/FLC) routes.

Endpoints
---------
GET /api/cinematics                 – list FLI/FLC cinematic files for a game
GET /api/cinematic/<filename>       – convert FLI/FLC to MP4 and stream it
"""

import glob
import io
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))

from flask import Blueprint, jsonify, send_file, abort, request

from config import DEFAULT_GAME, GAMES

bp = Blueprint('cinematics', __name__)

# In-memory cache: (game_id, FILENAME_UPPER) → mp4 bytes
_cinematic_cache: dict[tuple, bytes] = {}


@bp.route('/api/cinematics')
def api_cinematics():
    """Return a sorted list of FLI/FLC cinematic files for the current game."""
    game_id  = request.args.get('game', DEFAULT_GAME)
    game     = GAMES.get(game_id, GAMES[DEFAULT_GAME])
    cine_dir = game.get('cinematic_dir')
    if not cine_dir or not os.path.isdir(cine_dir):
        return jsonify([])

    results = []
    seen    = set()
    for filepath in sorted(glob.glob(os.path.join(cine_dir, '*.[Ff][Ll][Ii]')) +
                            glob.glob(os.path.join(cine_dir, '*.[Ff][Ll][Cc]'))):
        name = os.path.basename(filepath)
        if name.upper() in seen:
            continue
        seen.add(name.upper())
        results.append({
            'filename': name,
            'name':     os.path.splitext(name)[0],
            'size':     os.path.getsize(filepath),
        })

    results.sort(key=lambda r: r['name'])
    return jsonify(results)


@bp.route('/api/cinematic/<path:filename>')
def api_cinematic(filename: str):
    """Convert a FLI/FLC file to MP4 via ffmpeg and stream it.

    The result is cached in memory so subsequent requests are instant.
    """
    import subprocess
    game_id  = request.args.get('game', DEFAULT_GAME)
    game     = GAMES.get(game_id, GAMES[DEFAULT_GAME])
    cine_dir = game.get('cinematic_dir')
    if not cine_dir or not os.path.isdir(cine_dir):
        abort(404, description='No cinematic directory for this game.')

    filepath = os.path.join(cine_dir, filename)
    if not os.path.isfile(filepath):
        upper = filename.upper()
        match = next((f for f in os.listdir(cine_dir) if f.upper() == upper), None)
        if match is None:
            abort(404, description=f'Cinematic not found: {filename}')
        filepath = os.path.join(cine_dir, match)

    cache_key = (game_id, os.path.basename(filepath).upper())
    if cache_key not in _cinematic_cache:
        proc = subprocess.run(
            [
                'ffmpeg', '-y',
                '-i', filepath,
                '-vf', 'scale=trunc(iw/2)*2:trunc(ih/2)*2',  # h264 requires even dimensions
                '-c:v', 'libx264', '-preset', 'fast', '-crf', '23',
                '-c:a', 'aac',
                '-movflags', 'frag_keyframe+empty_moov',       # streamable fragmented MP4
                '-f', 'mp4',
                'pipe:1',
            ],
            capture_output=True,
        )
        if proc.returncode != 0:
            abort(500, description=f'ffmpeg failed: {proc.stderr.decode(errors="replace")}')
        _cinematic_cache[cache_key] = proc.stdout

    stem = os.path.splitext(os.path.basename(filepath))[0]
    buf  = io.BytesIO(_cinematic_cache[cache_key])
    return send_file(buf, mimetype='video/mp4', download_name=f'{stem}.mp4')
