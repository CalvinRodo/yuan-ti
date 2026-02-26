#!/usr/bin/env python3
"""
Pre-computation script — extract and render GFF resources to disk.

Run this once (or after game files change) to build a cache that the Flask
server can serve directly, skipping the expensive parsing / rendering on every
request.

Output tree
-----------
precomputed/
  <game_id>/
    gff/
      <GFF_FILENAME>.json      – resource manifest (tag, number, size, offset)
    maps/
      <GFF_STEM>.png           – rendered region map (composited PNG)
      <GFF_STEM>-<N>.json      – decoded RMAP/MAP tile data
    gamedata/
      items.json               – parsed IT1R item records
      monsters.json            – parsed RDFF monster records
      spells.json              – parsed SPIN spell records

Usage examples
--------------
  # Pre-compute everything for all games:
  uv run python3 src/precompute.py

  # Only render maps for Dark Sun 1, forcing re-generation:
  uv run python3 src/precompute.py --game dark_sun --step maps --force

  # Only extract GFF manifests:
  uv run python3 src/precompute.py --step gff
"""

import argparse
import json
import os
import sys

# Allow running from the repo root as well as the src/ directory.
SRC_DIR  = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.dirname(SRC_DIR)
sys.path.insert(0, SRC_DIR)

from config import GAMES
from gff import GffFile

PRECOMPUTED_DIR = os.path.join(BASE_DIR, 'precomputed')

# ---------------------------------------------------------------------------
# Low-level helpers (no Flask, no abort())
# ---------------------------------------------------------------------------

# Simple in-script GFF cache so each file is only read once per run.
_raw_gff_cache: dict[str, GffFile] = {}


def _load_raw_gff(path: str) -> GffFile | None:
    """Load a GFF file from an absolute *path* without Flask's abort()."""
    path = os.path.abspath(path)
    if path in _raw_gff_cache:
        return _raw_gff_cache[path]
    if not os.path.isfile(path):
        return None
    try:
        with open(path, 'rb') as fh:
            gff = GffFile.create(fh.read())
        _raw_gff_cache[path] = gff
        return gff
    except Exception as exc:
        print(f"    [!] Cannot read {path}: {exc}")
        return None


def _find_gff_in_dir(game_dir: str, filename: str) -> str | None:
    """Return the full path to *filename* in *game_dir* (case-insensitive)."""
    path = os.path.join(game_dir, filename)
    if os.path.isfile(path):
        return path
    upper = filename.upper()
    match = next((f for f in os.listdir(game_dir) if f.upper() == upper), None)
    return os.path.join(game_dir, match) if match else None


def _find_palette_standalone(game_dir: str, gff: GffFile):
    """Find a 256-colour palette without depending on Flask's load_gff.

    Lookup order
    ------------
    1. GPLDATA.GFF at the region number (GMAP resource number) — the correct
       source for region map rendering.
    2. First PAL resource in the GFF itself.
    3. First PAL resource in RESOURCE.GFF.
    """
    from image import Palette

    # 1. Region palette: GPLDATA.GFF PAL whose number == GMAP number.
    gpldata_path = _find_gff_in_dir(game_dir, 'GPLDATA.GFF')
    if gpldata_path:
        gpldata = _load_raw_gff(gpldata_path)
        if gpldata:
            for d in gff.describe_resources():
                if d.tag == 'GMAP':
                    try:
                        return Palette.from_pal_data(
                            gpldata.get_resource_data('PAL ', d.number))
                    except Exception:
                        pass
                    break  # GMAP found but PAL missing — fall through

    # 2. Try the GFF itself.
    for d in gff.describe_resources():
        if d.tag == 'PAL ':
            try:
                return Palette.from_pal_data(gff.get_resource_data('PAL ', d.number))
            except Exception:
                continue

    # 2. Fall back to RESOURCE.GFF in the same directory.
    res_path = _find_gff_in_dir(game_dir, 'RESOURCE.GFF')
    if res_path:
        res_gff = _load_raw_gff(res_path)
        if res_gff:
            for d in res_gff.describe_resources():
                if d.tag == 'PAL ':
                    try:
                        return Palette.from_pal_data(
                            res_gff.get_resource_data('PAL ', d.number))
                    except Exception:
                        continue
    return None


def _out_path(game_id: str, *parts: str) -> str:
    """Build an absolute output path and ensure its parent directory exists."""
    path = os.path.join(PRECOMPUTED_DIR, game_id, *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    return path


def _write_json(path: str, data, *, force: bool) -> bool:
    """Write *data* as JSON to *path*.  Returns True if the file was written."""
    if os.path.isfile(path) and not force:
        return False
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(data, fh, indent=2)
    return True


def _all_gff_files(game_dir: str) -> list[str]:
    """Return sorted list of .GFF filenames in *game_dir*."""
    try:
        return sorted(f for f in os.listdir(game_dir) if f.upper().endswith('.GFF'))
    except FileNotFoundError:
        return []


# ---------------------------------------------------------------------------
# Step: gff  – extract per-GFF resource manifests
# ---------------------------------------------------------------------------

def step_gff(game_id: str, game: dict, force: bool) -> None:
    """Write a JSON manifest (tag / number / size / offset) for every GFF."""
    game_dir = game['dir']

    gff_files = _all_gff_files(game_dir)
    if not gff_files:
        print(f"  [skip] no GFF files found in {game_dir}")
        return

    print(f"  {len(gff_files)} GFF file(s) in {game_dir}")
    written = skipped = errors = 0

    for filename in gff_files:
        out = _out_path(game_id, 'gff', filename + '.json')
        if os.path.isfile(out) and not force:
            skipped += 1
            continue

        gff = _load_raw_gff(os.path.join(game_dir, filename))
        if gff is None:
            errors += 1
            continue

        descs = [
            {'tag': d.tag, 'number': d.number, 'size': d.size, 'offset': d.offset}
            for d in gff.describe_resources()
        ]
        _write_json(out, {
            'filename':       filename,
            'game_id':        game_id,
            'resource_count': len(descs),
            'resources':      descs,
        }, force=True)
        written += 1

    print(f"  manifests: {written} written, {skipped} skipped, {errors} errors")


# ---------------------------------------------------------------------------
# Step: maps  – decode RMAP/MAP resources and render composite PNG images
# ---------------------------------------------------------------------------

def step_maps(game_id: str, game: dict, force: bool) -> None:
    """Decode every RMAP/MAP resource to JSON and render each region as a PNG."""
    game_dir = game['dir']

    gff_files = _all_gff_files(game_dir)
    if not gff_files:
        print(f"  [skip] no GFF files found in {game_dir}")
        return

    # Pre-load auxiliary GFFs used for sprite look-up during rendering.
    aux_gffs: list[GffFile] = []
    for aux_name in ('SEGOBJEX.GFF', 'OBJEX.GFF', 'GPLDATA.GFF'):
        aux_path = _find_gff_in_dir(game_dir, aux_name)
        if aux_path:
            aux = _load_raw_gff(aux_path)
            if aux:
                aux_gffs.append(aux)

    json_written = json_skipped = png_written = png_skipped = png_errors = 0

    for filename in gff_files:
        gff = _load_raw_gff(os.path.join(game_dir, filename))
        if gff is None:
            continue

        # Collect RMAP or MAP resources in this GFF.
        rmap_descs = []
        rmap_tag   = None
        for tag in ('RMAP', 'MAP '):
            found = [d for d in gff.describe_resources() if d.tag == tag]
            if found:
                rmap_descs = found
                rmap_tag   = tag
                break

        if not rmap_descs:
            continue

        stem = os.path.splitext(filename)[0]
        print(f"  {filename}: {len(rmap_descs)} {rmap_tag.strip()} resource(s)")

        for d in rmap_descs:
            # ------------------------------------------------------------------
            # JSON tile data
            # ------------------------------------------------------------------
            json_path = _out_path(game_id, 'maps', f"{stem}-{d.number}.json")
            if not os.path.isfile(json_path) or force:
                try:
                    raw  = gff.get_resource_data(d.tag, d.number)
                    size = len(raw)
                    sq   = int(size ** 0.5)
                    if sq * sq == size:
                        width = height = sq
                    else:
                        width, height = size, 1
                    _write_json(json_path, {
                        'gff':    filename,
                        'tag':    d.tag,
                        'number': d.number,
                        'width':  width,
                        'height': height,
                        'tiles':  list(raw),
                    }, force=True)
                    json_written += 1
                except Exception as exc:
                    print(f"    [!] tile-JSON failed for {filename}#{d.number}: {exc}")
            else:
                json_skipped += 1

        # ------------------------------------------------------------------
        # PNG render — one composite per region GFF (render_region picks the
        # GMAP resource to determine which RMAP number to use, so it is a
        # single call per GFF rather than one per RMAP resource).
        # ------------------------------------------------------------------
        png_path = _out_path(game_id, 'maps', f"{stem}.png")
        if not os.path.isfile(png_path) or force:
            try:
                palette = _find_palette_standalone(game_dir, gff)
                if palette is None:
                    print(f"    [!] no palette for {filename} — skipping PNG render")
                    png_errors += 1
                    continue
                from region_renderer import render_region
                img = render_region(gff, aux_gffs, palette)
                img.save(png_path)
                print(f"    rendered → {png_path}")
                png_written += 1
            except Exception as exc:
                print(f"    [!] render failed for {filename}: {exc}")
                png_errors += 1
        else:
            png_skipped += 1

    print(f"  tile-JSON : {json_written} written, {json_skipped} skipped")
    print(f"  map-PNG   : {png_written} rendered, {png_skipped} skipped, {png_errors} errors")


# ---------------------------------------------------------------------------
# Step: gamedata  – items, monsters, spells
# ---------------------------------------------------------------------------

def step_gamedata(game_id: str, game: dict, force: bool) -> None:
    """Parse and cache items, monsters, and spells as JSON."""
    from ds1_parsers import parse_items, parse_monsters, parse_spells
    from config import (
        SPIN_ICON_OFFSET,
        PSIONICS_SPIN_START, PSIONICS_SPIN_END, PSIONICS_ICON_BASE,
        tag_to_url_seg,
    )

    game_dir = game['dir']

    def try_load(filename: str) -> GffFile | None:
        path = _find_gff_in_dir(game_dir, filename)
        return _load_raw_gff(path) if path else None

    # ------------------------------------------------------------------
    # Items
    # ------------------------------------------------------------------
    items_path = _out_path(game_id, 'gamedata', 'items.json')
    if not os.path.isfile(items_path) or force:
        gff = try_load('GPLDATA.GFF')
        if gff:
            result = parse_items(gff)
            for rec in result:
                rec['sprite_gff']     = 'GPLDATA.GFF'
                rec['sprite_tag_url'] = tag_to_url_seg('PORT')
            _write_json(items_path, result, force=True)
            print(f"  wrote {items_path}  ({len(result)} items)")
        else:
            print(f"  [skip] GPLDATA.GFF not found for {game_id}")
    else:
        print(f"  [skip] items.json already exists (use --force to regenerate)")

    # ------------------------------------------------------------------
    # Monsters
    # ------------------------------------------------------------------
    monsters_path = _out_path(game_id, 'gamedata', 'monsters.json')
    if not os.path.isfile(monsters_path) or force:
        gff              = None
        sprite_gff_name  = None
        for candidate in ('SEGOBJEX.GFF', 'OBJEX.GFF'):
            gff = try_load(candidate)
            if gff is not None:
                sprite_gff_name = candidate
                break
        if gff:
            result = parse_monsters(gff)
            for rec in result:
                rec['sprite_gff']     = sprite_gff_name
                rec['sprite_tag_url'] = tag_to_url_seg('BMP ')
            _write_json(monsters_path, result, force=True)
            print(f"  wrote {monsters_path}  ({len(result)} monsters)")
        else:
            print(f"  [skip] SEGOBJEX/OBJEX.GFF not found for {game_id}")
    else:
        print(f"  [skip] monsters.json already exists (use --force to regenerate)")

    # ------------------------------------------------------------------
    # Spells
    # ------------------------------------------------------------------
    spells_path = _out_path(game_id, 'gamedata', 'spells.json')
    if not os.path.isfile(spells_path) or force:
        gff = try_load('RESOURCE.GFF')
        if gff:
            result     = parse_spells(gff)
            icon_nums  = {d.number for d in gff.describe_resources()
                          if d.tag.strip() == 'ICON'}
            for rec in result:
                n = rec['number']
                if PSIONICS_SPIN_START <= n <= PSIONICS_SPIN_END:
                    icon_n = PSIONICS_ICON_BASE + (n - PSIONICS_SPIN_START)
                else:
                    icon_n = SPIN_ICON_OFFSET + n
                rec['icon_number']    = icon_n if icon_n in icon_nums else None
                rec['sprite_gff']     = 'RESOURCE.GFF'
                rec['sprite_tag_url'] = 'ICON'
            _write_json(spells_path, result, force=True)
            print(f"  wrote {spells_path}  ({len(result)} spells)")
        else:
            print(f"  [skip] RESOURCE.GFF not found for {game_id}")
    else:
        print(f"  [skip] spells.json already exists (use --force to regenerate)")


# ---------------------------------------------------------------------------
# Audio helpers (VOC → MP3, XMI → MIDI, FLI → MP4)
# ---------------------------------------------------------------------------

def _has_ffmpeg() -> bool:
    """Return True if ffmpeg is on PATH."""
    import subprocess
    try:
        subprocess.run(['ffmpeg', '-version'], capture_output=True, check=True)
        return True
    except (FileNotFoundError, subprocess.CalledProcessError):
        return False


def _voc_to_mp3(voc_data: bytes, out_path: str) -> bool:
    """Convert VOC bytes to an MP3 file via ffmpeg. Returns True on success."""
    import subprocess
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    result = subprocess.run(
        ['ffmpeg', '-y', '-f', 'voc', '-i', 'pipe:0', '-f', 'mp3', out_path],
        input=voc_data, capture_output=True,
    )
    return result.returncode == 0


def _xmi_to_midi_file(xmi_data: bytes, out_path: str, track_index: int = 0) -> bool:
    """Convert XMI bytes to a MIDI file. Returns True on success."""
    try:
        from xmi_tool import xmi_to_midi
        midi_bytes = xmi_to_midi(xmi_data, track_index)
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        with open(out_path, 'wb') as fh:
            fh.write(midi_bytes)
        return True
    except Exception:
        return False


def _fli_to_mp4(fli_path: str, out_path: str) -> bool:
    """Convert a FLI/FLIC animation to MP4 via ffmpeg. Returns True on success."""
    import subprocess
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    result = subprocess.run(
        ['ffmpeg', '-y', '-i', fli_path,
         '-c:v', 'libx264', '-pix_fmt', 'yuv420p', out_path],
        capture_output=True,
    )
    return result.returncode == 0


# ---------------------------------------------------------------------------
# Step: images  – render all image resources as PNG files
# ---------------------------------------------------------------------------

def step_images(game_id: str, game: dict, force: bool) -> None:
    """Decode every image resource in every GFF to PNG (per-frame + spritesheet)."""
    from config import IMAGE_TAGS
    from image import ImageReading
    from image_tool import frame_to_pil_image

    game_dir  = game['dir']
    gff_files = _all_gff_files(game_dir)
    if not gff_files:
        print(f"  [skip] no GFF files found in {game_dir}")
        return

    total_written = total_skipped = total_errors = 0

    for filename in gff_files:
        gff = _load_raw_gff(os.path.join(game_dir, filename))
        if gff is None:
            continue

        img_descs = [d for d in gff.describe_resources() if d.tag in IMAGE_TAGS]
        if not img_descs:
            continue

        palette = _find_palette_standalone(game_dir, gff)
        if palette is None:
            print(f"  [skip] no palette for {filename}")
            continue

        stem = os.path.splitext(filename)[0]
        print(f"  {filename}: {len(img_descs)} image resource(s)")

        for d in img_descs:
            tag_s = d.tag.strip()
            base  = f'{tag_s}-{d.number}'

            try:
                raw    = gff.get_resource_data(d.tag, d.number)
                frames = ImageReading.extract_frames(raw)
            except Exception as exc:
                print(f"    [!] decode failed {base}: {exc}")
                total_errors += 1
                continue

            if not frames:
                continue

            if len(frames) == 1:
                # Single-frame: save as TAG-N.png (serves both sheet and frame=0)
                out = _out_path(game_id, 'images', stem, f'{base}.png')
                if not os.path.isfile(out) or force:
                    try:
                        frame_to_pil_image(frames[0], palette).save(out)
                        total_written += 1
                    except Exception as exc:
                        print(f"    [!] save failed {base}: {exc}")
                        total_errors += 1
                else:
                    total_skipped += 1
            else:
                try:
                    from PIL import Image as PilImage
                except ImportError:
                    print("  [!] Pillow not installed — skipping image extract")
                    return

                # Individual frames: TAG-N-fF.png
                for fi, frame in enumerate(frames):
                    out = _out_path(game_id, 'images', stem, f'{base}-f{fi}.png')
                    if not os.path.isfile(out) or force:
                        try:
                            frame_to_pil_image(frame, palette).save(out)
                            total_written += 1
                        except Exception as exc:
                            print(f"    [!] frame {fi} failed {base}: {exc}")
                            total_errors += 1
                    else:
                        total_skipped += 1

                # Spritesheet: TAG-N-sheet.png
                out = _out_path(game_id, 'images', stem, f'{base}-sheet.png')
                if not os.path.isfile(out) or force:
                    try:
                        total_w = max(sum(f.width for f in frames), 1)
                        max_h   = max((f.height for f in frames), default=1)
                        sheet   = PilImage.new('RGBA', (total_w, max_h), (0, 0, 0, 0))
                        x = 0
                        for frame in frames:
                            sheet.paste(frame_to_pil_image(frame, palette), (x, 0))
                            x += frame.width
                        sheet.save(out)
                        total_written += 1
                    except Exception as exc:
                        print(f"    [!] sheet failed {base}: {exc}")
                        total_errors += 1
                else:
                    total_skipped += 1

    print(f"  images: {total_written} written, {total_skipped} skipped, "
          f"{total_errors} errors")


# ---------------------------------------------------------------------------
# Step: audio  – extract BVOC/XMI from GFFs; convert loose VOC/XMI/FLI files
# ---------------------------------------------------------------------------

def step_audio(game_id: str, game: dict, force: bool) -> None:
    """Extract audio/music/video: VOC + MP3, XMI + MIDI, FLI + MP4."""
    import glob as _glob
    import shutil
    from config import MUSIC_TAGS, AUDIO_TAGS

    game_dir   = game['dir']
    has_ffmpeg = _has_ffmpeg()
    if not has_ffmpeg:
        print("  [warn] ffmpeg not found — MP3/MP4 conversion will be skipped. "
              "Run: make install-ffmpeg")

    # ------------------------------------------------------------------
    # GFF-embedded audio (BVOC → VOC + MP3 ; XMIDI → XMI + MIDI)
    # ------------------------------------------------------------------
    gff_files = _all_gff_files(game_dir)
    voc_w = mp3_w = xmi_w = mid_w = au_skip = au_err = 0

    for filename in gff_files:
        gff = _load_raw_gff(os.path.join(game_dir, filename))
        if gff is None:
            continue

        audio_descs = [
            d for d in gff.describe_resources()
            if d.tag.strip() in AUDIO_TAGS or d.tag.strip() in MUSIC_TAGS
        ]
        if not audio_descs:
            continue

        stem = os.path.splitext(filename)[0]

        for d in audio_descs:
            tag_s = d.tag.strip()
            base  = f'{tag_s}-{d.number}'

            try:
                data = gff.get_resource_data(d.tag, d.number)
            except Exception:
                au_err += 1
                continue

            if tag_s in AUDIO_TAGS:   # BVOC
                # Raw VOC bytes
                voc_out = _out_path(game_id, 'audio', stem, f'{base}.voc')
                if not os.path.isfile(voc_out) or force:
                    with open(voc_out, 'wb') as fh:
                        fh.write(data)
                    voc_w += 1
                else:
                    au_skip += 1

                # MP3 via ffmpeg
                if has_ffmpeg:
                    mp3_out = _out_path(game_id, 'audio', stem, f'{base}.mp3')
                    if not os.path.isfile(mp3_out) or force:
                        if _voc_to_mp3(data, mp3_out):
                            mp3_w += 1
                        else:
                            au_err += 1
                    else:
                        au_skip += 1

            elif tag_s in MUSIC_TAGS:  # XMIDI
                # Raw XMI bytes
                xmi_out = _out_path(game_id, 'audio', stem, f'{base}.xmi')
                if not os.path.isfile(xmi_out) or force:
                    with open(xmi_out, 'wb') as fh:
                        fh.write(data)
                    xmi_w += 1
                else:
                    au_skip += 1

                # MIDI via xmi_tool
                mid_out = _out_path(game_id, 'audio', stem, f'{base}.mid')
                if not os.path.isfile(mid_out) or force:
                    if _xmi_to_midi_file(data, mid_out):
                        mid_w += 1
                    else:
                        au_err += 1
                else:
                    au_skip += 1

    if voc_w or mp3_w or xmi_w or mid_w or au_err:
        print(f"  GFF audio: {voc_w} VOC, {mp3_w} MP3, "
              f"{xmi_w} XMI, {mid_w} MIDI; "
              f"{au_err} errors, {au_skip} skipped")

    # ------------------------------------------------------------------
    # Loose SFX (VOC files)
    # ------------------------------------------------------------------
    sfx_dir = game.get('sfx_dir')
    if sfx_dir and os.path.isdir(sfx_dir):
        voc_paths = sorted(
            set(os.path.normcase(p)
                for p in _glob.glob(os.path.join(sfx_dir, '*.VOC')) +
                         _glob.glob(os.path.join(sfx_dir, '*.voc')))
        )
        seen: set[str] = set()
        sfx_voc = sfx_mp3 = sfx_err = 0
        for voc_path in voc_paths:
            uname = os.path.basename(voc_path).upper()
            if uname in seen:
                continue
            seen.add(uname)
            out_voc = _out_path(game_id, 'audio', 'sfx', uname)
            if not os.path.isfile(out_voc) or force:
                shutil.copy2(voc_path, out_voc)
                sfx_voc += 1
            if has_ffmpeg:
                out_mp3 = _out_path(game_id, 'audio', 'sfx',
                                    os.path.splitext(uname)[0] + '.mp3')
                if not os.path.isfile(out_mp3) or force:
                    with open(voc_path, 'rb') as fh:
                        raw = fh.read()
                    if _voc_to_mp3(raw, out_mp3):
                        sfx_mp3 += 1
                    else:
                        sfx_err += 1
        print(f"  sfx: {sfx_voc} VOC copied, {sfx_mp3} MP3 converted, "
              f"{sfx_err} errors")

    # ------------------------------------------------------------------
    # Loose XMI music files
    # ------------------------------------------------------------------
    # music_dir holds OGG for DS2; xmi_dir is the XMI source
    for dir_key in ('music_dir', 'xmi_dir'):
        xmi_dir = game.get(dir_key)
        if not xmi_dir or not os.path.isdir(xmi_dir):
            continue
        seen_xmi: set[str] = set()
        xmi_cnt = mid_cnt = xmi_err = 0
        for xmi_path in sorted(
            _glob.glob(os.path.join(xmi_dir, '*.XMI')) +
            _glob.glob(os.path.join(xmi_dir, '*.xmi'))
        ):
            uname = os.path.basename(xmi_path).upper()
            if uname in seen_xmi:
                continue
            seen_xmi.add(uname)
            with open(xmi_path, 'rb') as fh:
                data = fh.read()
            out_xmi = _out_path(game_id, 'audio', 'music', uname)
            if not os.path.isfile(out_xmi) or force:
                with open(out_xmi, 'wb') as fh:
                    fh.write(data)
                xmi_cnt += 1
            out_mid = _out_path(game_id, 'audio', 'music',
                                os.path.splitext(uname)[0] + '.mid')
            if not os.path.isfile(out_mid) or force:
                if _xmi_to_midi_file(data, out_mid):
                    mid_cnt += 1
                else:
                    xmi_err += 1
        if xmi_cnt or mid_cnt or xmi_err:
            print(f"  music ({dir_key}): {xmi_cnt} XMI copied, "
                  f"{mid_cnt} MIDI converted, {xmi_err} errors")

    # ------------------------------------------------------------------
    # Cinematics (FLI/FLIC → copy + MP4)
    # ------------------------------------------------------------------
    cin_dir = game.get('cinematic_dir')
    if cin_dir and os.path.isdir(cin_dir):
        seen_fli: set[str] = set()
        fli_cnt = mp4_cnt = fli_err = 0
        for fli_path in sorted(
            _glob.glob(os.path.join(cin_dir, '*.FLI')) +
            _glob.glob(os.path.join(cin_dir, '*.fli')) +
            _glob.glob(os.path.join(cin_dir, '*.FLC')) +
            _glob.glob(os.path.join(cin_dir, '*.flc'))
        ):
            uname = os.path.basename(fli_path).upper()
            if uname in seen_fli:
                continue
            seen_fli.add(uname)
            out_fli = _out_path(game_id, 'audio', 'cinematics', uname)
            if not os.path.isfile(out_fli) or force:
                shutil.copy2(fli_path, out_fli)
                fli_cnt += 1
            if has_ffmpeg:
                out_mp4 = _out_path(game_id, 'audio', 'cinematics',
                                    os.path.splitext(uname)[0] + '.mp4')
                if not os.path.isfile(out_mp4) or force:
                    if _fli_to_mp4(fli_path, out_mp4):
                        mp4_cnt += 1
                    else:
                        fli_err += 1
        print(f"  cinematics: {fli_cnt} FLI copied, "
              f"{mp4_cnt} MP4 converted, {fli_err} errors")


# ---------------------------------------------------------------------------
# Step registry & CLI
# ---------------------------------------------------------------------------

STEPS: dict[str, callable] = {
    'gff':      step_gff,
    'maps':     step_maps,
    'gamedata': step_gamedata,
    'images':   step_images,
    'audio':    step_audio,
}


def main() -> None:
    parser = argparse.ArgumentParser(
        description='Pre-compute GFF resources to disk for faster server responses.',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        '--game', default='all',
        help=(
            'Game to process (default: all).  '
            'One of: ' + ', '.join(GAMES) + ', all'
        ),
    )
    parser.add_argument(
        '--step', default='all',
        help=(
            'Processing step to run (default: all).  '
            'One of: ' + ', '.join(STEPS) + ', all'
        ),
    )
    parser.add_argument(
        '--force', action='store_true',
        help='Re-generate output even when it already exists on disk.',
    )
    args = parser.parse_args()

    # Resolve games list
    if args.game == 'all':
        games_to_run = list(GAMES.items())
    elif args.game in GAMES:
        games_to_run = [(args.game, GAMES[args.game])]
    else:
        print(f"Unknown game '{args.game}'. Choose from: {', '.join(GAMES)}, all")
        sys.exit(1)

    # Resolve steps list
    if args.step == 'all':
        steps_to_run = list(STEPS.items())
    elif args.step in STEPS:
        steps_to_run = [(args.step, STEPS[args.step])]
    else:
        print(f"Unknown step '{args.step}'. Choose from: {', '.join(STEPS)}, all")
        sys.exit(1)

    print(f"Output directory : {PRECOMPUTED_DIR}")
    print(f"Games            : {', '.join(g for g, _ in games_to_run)}")
    print(f"Steps            : {', '.join(s for s, _ in steps_to_run)}")
    print(f"Force            : {args.force}")
    print()

    for game_id, game in games_to_run:
        print(f"=== {game['label']} ({game_id}) ===")
        for step_name, step_fn in steps_to_run:
            print(f"\n  -- {step_name} --")
            step_fn(game_id, game, args.force)
        print()

    print("Done.")


if __name__ == '__main__':
    main()
