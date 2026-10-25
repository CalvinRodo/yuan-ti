"""
Disk-based precomputed result cache.

Precomputed files live under  precomputed/<game_id>/  and are written by
:mod:`precompute`. Map PNGs and connections are also cached on demand.
Server routes use these helpers to skip expensive parsing or rendering
when a cached result already exists.

Directory layout (mirrors precompute.py output)
------------------------------------------------
precomputed/
  <game_id>/
    gff/<GFF_FILENAME>.json      – resource manifest
    maps/<GFF_STEM>.png          – rendered region map
    maps/rendered/<HASH>.png     – on-demand map, keyed by inputs and palette
    maps/connections/<HASH>.json – scripted region connections, keyed by inputs
    maps/<GFF_STEM>-<N>.json     – decoded RMAP tile data
    gamedata/items.json
    gamedata/monsters.json
    gamedata/spells.json
"""

import json
import os

from config import BASE_DIR

PRECOMPUTED_DIR = os.path.join(BASE_DIR, 'precomputed')


def source_signature(paths) -> tuple:
    """Fingerprint input files without reading their contents."""
    result = []
    for path in paths:
        try:
            stat = os.stat(path)
        except FileNotFoundError:
            result.append((path, None, None))
        else:
            result.append((path, stat.st_mtime_ns, stat.st_size))
    return tuple(result)


def precomputed_path(game_id: str, *parts: str) -> str:
    """Return the absolute path to a precomputed artefact (may not exist yet)."""
    return os.path.join(PRECOMPUTED_DIR, game_id, *parts)


def precomputed_exists(game_id: str, *parts: str) -> bool:
    """Return ``True`` if the precomputed file is present on disk."""
    return os.path.isfile(precomputed_path(game_id, *parts))


def load_precomputed_json(game_id: str, *parts: str):
    """Load and return a precomputed JSON artefact, or ``None`` if absent.

    Example
    -------
    >>> data = load_precomputed_json('dark_sun', 'gamedata', 'monsters.json')
    >>> if data is not None:
    ...     return jsonify(data)   # fast path
    """
    path = precomputed_path(game_id, *parts)
    if not os.path.isfile(path):
        return None
    with open(path, 'r', encoding='utf-8') as fh:
        return json.load(fh)


def precomputed_png_path(game_id: str, *parts: str) -> str | None:
    """Return the path to a precomputed PNG if it exists, else ``None``."""
    path = precomputed_path(game_id, *parts)
    return path if os.path.isfile(path) else None


def precomputed_image_path(
    game_id: str,
    gff_stem: str,
    tag: str,
    number: int,
    frame: int = -1,
) -> str | None:
    """Return the path to a precomputed image PNG, or ``None`` if not found.

    Naming convention written by :func:`precompute.step_images`:

    * Single-frame resource  →  ``<TAG>-<N>.png``
    * Multi-frame, specific frame  →  ``<TAG>-<N>-f<F>.png``
    * Multi-frame, spritesheet     →  ``<TAG>-<N>-sheet.png``

    Lookup order for ``frame >= 0``:
      1. per-frame file (``-f<F>.png``)
      2. single-frame fallback (``<TAG>-<N>.png``)

    Lookup order for ``frame == -1`` (sheet):
      1. spritesheet file (``-sheet.png``)
      2. single-frame fallback (``<TAG>-<N>.png``)
    """
    tag_s = tag.strip()
    base  = f'{tag_s}-{number}'

    if frame >= 0:
        candidates = [
            precomputed_path(game_id, 'images', gff_stem, f'{base}-f{frame}.png'),
            precomputed_path(game_id, 'images', gff_stem, f'{base}.png'),
        ]
    else:
        candidates = [
            precomputed_path(game_id, 'images', gff_stem, f'{base}-sheet.png'),
            precomputed_path(game_id, 'images', gff_stem, f'{base}.png'),
        ]

    return next((p for p in candidates if os.path.isfile(p)), None)


def precomputed_audio_path(game_id: str, *parts: str) -> str | None:
    """Return the path to a precomputed audio file, or ``None`` if not found.

    *parts* are relative to ``precomputed/<game_id>/audio/``.

    Examples::

        precomputed_audio_path('dark_sun', 'RESOURCE', 'BVOC-1.mp3')
        precomputed_audio_path('dark_sun', 'RESOURCE', 'GSEQ-0.mid')
        precomputed_audio_path('dark_sun_2', 'music', 'SONG1.mid')
    """
    path = precomputed_path(game_id, 'audio', *parts)
    return path if os.path.isfile(path) else None
