"""
GFF file loading and caching helpers.

Provides load_gff() (with a simple LRU-style eviction cache) and
find_palette() which searches for a usable PAL resource in order of
specificity: explicit params → same GFF → RESOURCE.GFF.
"""

import os
from flask import abort

from gff import GffFile
from image import Palette
from config import DEFAULT_GAME, resolve_game_dir, find_file_nocase

# ---------------------------------------------------------------------------
# In-process GFF cache
# Evicts the oldest entry when the maximum size is reached.
# ---------------------------------------------------------------------------
_gff_cache: dict = {}
MAX_CACHE = 30


def load_gff(name: str, game_id: str = DEFAULT_GAME) -> GffFile:
    """Load (and cache) a GFF file from the game's data directory."""
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


def try_load_gff(game_dir: str, filename: str, game_id: str) -> GffFile | None:
    """Load a GFF from *game_dir* with case-insensitive filename lookup.

    Returns ``None`` when the file cannot be found or fails to parse.
    """
    path = find_file_nocase(game_dir, filename)
    if path is None:
        return None
    try:
        return load_gff(os.path.basename(path), game_id)
    except Exception:
        return None


def find_palette(gff: GffFile,
                 pal_gff_name: str | None = None,
                 pal_num: int | None = None,
                 pal_tag: str = 'PAL',
                 game_id: str = DEFAULT_GAME) -> Palette | None:
    """Locate a 256-colour VGA palette from one of three sources (in order):

    1. Explicit pal_gff + pal_num query params.
    2. First PAL resource in the same GFF.
    3. First PAL resource in RESOURCE.GFF.

    For region map rendering the caller should supply
    ``pal_gff='GPLDATA.GFF'`` and ``pal_num=<region_number>`` (the GMAP
    resource number from the region GFF) so that source 1 is used.

    Returns None if no palette can be found.
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
