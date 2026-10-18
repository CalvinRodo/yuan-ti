"""
Region map renderer for Dark Sun: Shattered Lands.
Python port of RegionTool.java by John Glassmyer.

Rendering layers (back → front):
  1. RMAP ground tiles  (TILE resources, one per cell)
  2. ETAB object sprites (OJFF → BMP resources)
  3. WALL sprites        (WALL resources keyed by GMAP wall-index)
"""

import struct
from typing import Dict, List, Optional, Tuple

from image import Palette, ImageFrame, ImageReading
from gff import GffFile

# ---------------------------------------------------------------------------
# Constants (matching RegionTool.java)
# ---------------------------------------------------------------------------
TILE_PIXEL_SIZE         = 16
DEFAULT_MAP_WIDTH       = 128
GMAP_WALL_INDEX_BITMASK = 31


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _get_resource(gffs: List[GffFile], tag: str, number: int) -> Optional[bytes]:
    """Try each GFF in order; return first match or None."""
    for gff in gffs:
        try:
            return gff.get_resource_data(tag, number)
        except Exception:
            pass
    return None


def _frame_to_pil(frame: ImageFrame, palette: Palette):
    """Convert an ImageFrame to a Pillow RGBA Image via the palette."""
    from PIL import Image

    w, h = frame.width, frame.height
    if w == 0 or h == 0:
        return None

    px = frame.pixels
    al = frame.alpha_mask
    data = bytearray(w * h * 4)
    for i in range(w * h):
        if al[i]:
            c = palette.get_color(px[i])
            data[i * 4]     = c.red
            data[i * 4 + 1] = c.green
            data[i * 4 + 2] = c.blue
            data[i * 4 + 3] = 255
        # else: stays (0, 0, 0, 0) — transparent
    return Image.frombytes('RGBA', (w, h), bytes(data))


# ---------------------------------------------------------------------------
# Region renderer
# ---------------------------------------------------------------------------

def render_region(
    rgn_gff:   GffFile,
    aux_gffs:  List[GffFile],
    palette:   Palette,
) -> 'PIL.Image.Image':
    """
    Render a region GFF file to a full-resolution RGB PIL Image.

    Parameters
    ----------
    rgn_gff   : The RGN*.GFF file for this region.
    aux_gffs  : Helper GFFs (e.g. SEGOBJEX.GFF / OBJEX.GFF, GPLDATA.GFF).
                They are searched in order for OJFF, BMP, WALL resources.
    palette   : Decoded palette (from Palette.from_pal_data).

    Returns
    -------
    A PIL Image of size (map_w * 16, map_h * 16).
    """
    from PIL import Image

    all_gffs = [rgn_gff] + aux_gffs

    # ---- Find GMAP (gives region number + passability flags) ----------------
    region_number: Optional[int] = None
    gmap_data:     Optional[bytes] = None
    for d in rgn_gff.describe_resources():
        if d.tag == 'GMAP':
            region_number = d.number
            try:
                gmap_data = rgn_gff.get_resource_data('GMAP', d.number)
            except Exception:
                pass
            break
    if region_number is None:
        raise ValueError("No GMAP resource found in region GFF")

    # ---- Find RMAP / MAP  ---------------------------------------------------
    rmap_data: Optional[bytes] = None
    for try_tag in ('RMAP', 'MAP '):
        rmap_data = _get_resource(all_gffs, try_tag, region_number)
        if rmap_data:
            break
    if rmap_data is None:
        raise ValueError("No RMAP/MAP resource found in region GFF")

    # ---- Determine map dimensions -------------------------------------------
    n  = len(rmap_data)
    map_w = DEFAULT_MAP_WIDTH
    map_h = (n + map_w - 1) // map_w

    canvas_w = map_w * TILE_PIXEL_SIZE
    canvas_h = map_h * TILE_PIXEL_SIZE

    canvas = Image.new('RGB', (canvas_w, canvas_h), (13, 12, 20))

    # ---- Paste helper (clips automatically for out-of-bounds coords) ---------
    def paste(frame: ImageFrame, sx: int, sy: int, mirrored: bool = False):
        if frame.width == 0 or frame.height == 0:
            return
        img = _frame_to_pil(frame, palette)
        if img is None:
            return
        if mirrored:
            img = img.transpose(Image.FLIP_LEFT_RIGHT)
        # Split alpha channel as paste mask so transparent pixels show through
        canvas.paste(img.convert('RGB'), (sx, sy), mask=img.split()[3])

    # ==========================================================================
    # Layer 1: RMAP ground tiles
    # ==========================================================================

    # Collect every TILE frame from all GFFs (first occurrence wins)
    tile_frames: Dict[int, ImageFrame] = {}
    for gff in all_gffs:
        for d in gff.describe_resources():
            if d.tag == 'TILE' and d.number not in tile_frames:
                try:
                    raw = gff.get_resource_data('TILE', d.number)
                    frames = ImageReading.extract_frames(raw)
                    if frames:
                        tile_frames[d.number] = frames[0]
                except Exception:
                    pass

    # Pre-render each unique tile to RGBA PIL once
    tile_imgs: Dict[int, object] = {}
    for num, frame in tile_frames.items():
        img = _frame_to_pil(frame, palette)
        if img is not None:
            tile_imgs[num] = img

    for my in range(map_h):
        for mx in range(map_w):
            tile_num = rmap_data[my * map_w + mx]
            img = tile_imgs.get(tile_num)
            if img is not None:
                sx = mx * TILE_PIXEL_SIZE
                sy = my * TILE_PIXEL_SIZE
                canvas.paste(img.convert('RGB'), (sx, sy), mask=img.split()[3])

    # ==========================================================================
    # Layer 2: ETAB object sprites
    # ==========================================================================

    etab_data = _get_resource(all_gffs, 'ETAB', region_number)
    if etab_data:
        # Cache: ojff_number -> (x_offset, y_offset, ImageFrame) or None.
        # The y offset includes OJFF byte 0x0A, as DSUN.EXE positions objects
        # (object_from_ojff, 1DF3:1073); ETAB byte 4 is not used for this.
        ojff_cache: Dict[int, Optional[Tuple[int, int, ImageFrame]]] = {}

        for off in range(0, len(etab_data) - 7, 8):
            ex       = struct.unpack_from('<h', etab_data, off)[0]
            ey       = struct.unpack_from('<h', etab_data, off + 2)[0]
            byte5    = etab_data[off + 5]
            mirrored = bool(byte5 & 0x80)
            ojff_num = struct.unpack_from('<h', etab_data, off + 6)[0]
            if ojff_num < 0:
                ojff_num = -ojff_num

            if ojff_num not in ojff_cache:
                ojff_raw = _get_resource(all_gffs, 'OJFF', ojff_num)
                if ojff_raw and len(ojff_raw) >= 0x10:
                    ox  = struct.unpack_from('<h', ojff_raw, 2)[0]
                    oy  = (struct.unpack_from('<h', ojff_raw, 4)[0]
                           + struct.unpack_from('<b', ojff_raw, 0xA)[0])
                    bmp = struct.unpack_from('<H', ojff_raw, 0xC)[0]
                    bd  = _get_resource(all_gffs, 'BMP ', bmp)
                    if bd:
                        frs = ImageReading.extract_frames(bd)
                        ojff_cache[ojff_num] = (ox, oy, frs[0]) if frs else None
                    else:
                        ojff_cache[ojff_num] = None
                else:
                    ojff_cache[ojff_num] = None

            ojff = ojff_cache.get(ojff_num)
            if ojff is None:
                continue
            ox, oy, frame = ojff
            paste(frame, ex - ox, ey - oy, mirrored=mirrored)

    # ==========================================================================
    # Layer 3: WALL sprites (from GMAP wall-index bytes)
    # ==========================================================================

    if gmap_data:
        wall_cache: Dict[int, Optional[ImageFrame]] = {}

        for ty in range(map_h):
            for tx in range(map_w):
                idx = ty * map_w + tx
                if idx >= len(gmap_data):
                    break
                gmap_byte  = gmap_data[idx]
                wall_index = gmap_byte & GMAP_WALL_INDEX_BITMASK
                if wall_index == 0:
                    continue

                wall_num = region_number * 100 + wall_index - 1
                if wall_num not in wall_cache:
                    wd = _get_resource(all_gffs, 'WALL', wall_num)
                    if wd:
                        frs = ImageReading.extract_frames(wd)
                        wall_cache[wall_num] = frs[0] if frs else None
                    else:
                        wall_cache[wall_num] = None

                frame = wall_cache.get(wall_num)
                if frame is None:
                    continue

                sx = tx * TILE_PIXEL_SIZE + 8 - frame.width  // 2
                sy = ty * TILE_PIXEL_SIZE + 16 - frame.height
                paste(frame, sx, sy)

    return canvas
