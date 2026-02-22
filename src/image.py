"""
Image reading for Dark Sun: Shattered Lands GFF assets.
Translated from Color.java, PixelRun.java, ImageFrame.java,
Palette.java, and ImageReading.java by John Glassmyer.

Supports three image formats found in GFF files:
 - Row-based RLE (default)
 - PLAN  (planar, direct symbol lookup, big-endian bit stream)
 - PLNR  (planar, RLE symbol lookup, big-endian bit stream)
"""

import struct
from dataclasses import dataclass
from functools import cached_property
from typing import Callable, Dict, List, Optional, Tuple

from bit_chomper import BitChomper


# ---------------------------------------------------------------------------
# Color
# ---------------------------------------------------------------------------
@dataclass
class Color:
    red: int
    green: int
    blue: int


# ---------------------------------------------------------------------------
# PixelRun
# ---------------------------------------------------------------------------
class PixelRun:
    def __init__(self, start_x: int, pixels: bytes):
        self.start_x = start_x
        self._pixels = bytes(pixels)

    def length(self) -> int:
        return len(self._pixels)

    def write_to(self, dest: bytearray, dest_pos: int):
        dest[dest_pos:dest_pos + len(self._pixels)] = self._pixels

    @property
    def pixels(self) -> bytes:
        return self._pixels


# ---------------------------------------------------------------------------
# ImageFrame
# ---------------------------------------------------------------------------
class ImageFrame:
    def __init__(self, width: int, height: int,
                 runs_by_row: Dict[int, List[PixelRun]]):
        self.width = width
        self.height = height
        self._runs_by_row = dict(runs_by_row)

    @cached_property
    def alpha_mask(self) -> bytearray:
        """
        Returns a flat bytearray bitmask (width*height bytes).
        1 = opaque, 0 = transparent.
        """
        mask = bytearray(self.width * self.height)
        for row, runs in self._runs_by_row.items():
            row_start = row * self.width
            for run in runs:
                for i in range(run.length()):
                    mask[row_start + run.start_x + i] = 1
        return mask

    @cached_property
    def pixels(self) -> bytearray:
        """
        Returns a flat bytearray of palette indices (width*height bytes).
        Transparent pixels are 0.
        """
        buf = bytearray(self.width * self.height)
        for row, runs in self._runs_by_row.items():
            row_start = row * self.width
            for run in runs:
                run.write_to(buf, row_start + run.start_x)
        return buf


# ---------------------------------------------------------------------------
# Palette
# ---------------------------------------------------------------------------
class Palette:
    BYTES_PER_COLOR = 3

    @classmethod
    def from_pal_data(cls, pal_data: bytes) -> 'Palette':
        """
        Build a palette from raw PAL resource bytes.
        Each color is 3 bytes (R, G, B) in the range 0-63; multiplied by 4
        to bring into 0-252.
        """
        n_colors = len(pal_data) // cls.BYTES_PER_COLOR
        colors: List[Color] = []
        for i in range(n_colors):
            base = i * cls.BYTES_PER_COLOR
            red   = 4 * pal_data[base]
            green = 4 * pal_data[base + 1]
            blue  = 4 * pal_data[base + 2]
            colors.append(Color(red, green, blue))
        return cls(colors)

    def __init__(self, colors: List[Color]):
        self._colors = colors

    def get_color(self, palette_index: int) -> Color:
        return self._colors[palette_index]

    def __len__(self):
        return len(self._colors)


# ---------------------------------------------------------------------------
# ImageReading
# ---------------------------------------------------------------------------
_NO_MORE_ROWS = 0xFF
_COLUMN_256_FLAG = 0x1
_LAST_RUN_FLAG = 0x80
_PLAN_TAG = b'PLAN'
_PLNR_TAG = b'PLNR'


class ImageReading:

    @staticmethod
    def extract_frames(image_bytes: bytes) -> List[ImageFrame]:
        """
        Parse a BMP/image resource and return all frames.
        """
        # skip file size (int32), read frame count (int16)
        n_frames = struct.unpack_from('<H', image_bytes, 4)[0]
        frames: List[ImageFrame] = []
        for i in range(n_frames):
            frame_offset = struct.unpack_from('<I', image_bytes, 6 + i * 4)[0]
            frames.append(ImageReading._read_frame(image_bytes, frame_offset))
        return frames

    @staticmethod
    def _read_frame(image_bytes: bytes, frame_start: int) -> ImageFrame:
        # Check for planar format: byte at frame_start+4 == 0xFF followed by tag
        if image_bytes[frame_start + 4] == 0xFF and \
                len(image_bytes) >= frame_start + 5 + 4:
            tag = image_bytes[frame_start + 5:frame_start + 9]
            if tag == _PLAN_TAG:
                return ImageReading._read_planar_frame(image_bytes, frame_start,
                                                       _plan_symbol_source)
            elif tag == _PLNR_TAG:
                return ImageReading._read_planar_frame(image_bytes, frame_start,
                                                       _plnr_symbol_source)

        return ImageReading._read_row_based_frame(image_bytes, frame_start)

    @staticmethod
    def _read_row_based_frame(image_bytes: bytes, frame_start: int) -> ImageFrame:
        """RLE row-based format — the primary format in Dark Sun 1."""
        width  = struct.unpack_from('<H', image_bytes, frame_start)[0]
        height = struct.unpack_from('<H', image_bytes, frame_start + 2)[0]

        pos = frame_start + 4
        runs_by_row: Dict[int, List[PixelRun]] = {}
        data = image_bytes

        while len(runs_by_row) < height:
            row_number = data[pos]
            pos += 1

            if row_number == _NO_MORE_ROWS:
                break
            if row_number >= height:
                raise ValueError(
                    f"row number 0x{row_number:X} >= frame height 0x{height:X}")

            runs: List[PixelRun] = []
            while True:
                start_x    = data[pos]; pos += 1
                flags      = data[pos]; pos += 1
                uncomp_len = data[pos]; pos += 1
                comp_len   = data[pos]; pos += 1

                if flags & _COLUMN_256_FLAG:
                    start_x += 256

                compressed = data[pos:pos + comp_len]
                pos += comp_len

                uncompressed = ImageReading._uncompress_pixels(
                    compressed, uncomp_len)
                runs.append(PixelRun(start_x, uncompressed))

                if flags & _LAST_RUN_FLAG:
                    break

            runs_by_row[row_number] = runs

        return ImageFrame(width, height, runs_by_row)

    @staticmethod
    def _uncompress_pixels(compressed: bytes, uncompressed_length: int) -> bytes:
        """
        Decode the in-row RLE:
        - even code byte: (code//2 + 1) literal bytes follow
        - odd  code byte: (code//2 + 1) repetitions of the next byte
        """
        out = bytearray(uncompressed_length)
        i_out = 0
        i_in  = 0
        while i_in < len(compressed):
            code = compressed[i_in]
            i_in += 1
            run_len = code // 2 + 1
            if code % 2 == 0:
                # literal run
                out[i_out:i_out + run_len] = compressed[i_in:i_in + run_len]
                i_in  += run_len
                i_out += run_len
            else:
                # repeated byte
                byte_val = compressed[i_in]
                i_in += 1
                for _ in range(run_len):
                    out[i_out] = byte_val
                    i_out += 1
        return bytes(out)

    @staticmethod
    def _read_planar_frame(image_bytes: bytes, frame_start: int,
                           symbol_source_factory: Callable) -> ImageFrame:
        """
        Planar (PLAN / PLNR) format.
        Layout after width(2) + height(2):
          byte 4: 0xFF (already checked)
          bytes 5-8: 'PLAN' or 'PLNR'
          byte 9: bitsPerSymbol (0 = empty frame)
          bytes 10..(10 + dictionarySize - 1): pixel value dictionary
          rest: bit-packed symbol stream (big-endian)
        """
        width  = struct.unpack_from('<H', image_bytes, frame_start)[0]
        height = struct.unpack_from('<H', image_bytes, frame_start + 2)[0]

        bits_per_symbol = image_bytes[frame_start + 9]
        if bits_per_symbol == 0:
            return ImageFrame(width, height, {})

        dict_size = 1 << bits_per_symbol
        dictionary = list(image_bytes[frame_start + 10:frame_start + 10 + dict_size])

        code_start = frame_start + 10 + dict_size
        chomper = BitChomper(image_bytes, code_start, 0, big_endian=True)
        symbol_source = symbol_source_factory(chomper, bits_per_symbol)

        runs_by_row: Dict[int, List[PixelRun]] = {}
        for y in range(height):
            builder = _PixelRunsBuilder()
            for x in range(width):
                pixel = _next_planar_pixel(symbol_source, dictionary)
                if pixel is not None:
                    builder.record(x, pixel)
            builder.finish_run()
            runs = builder.get_runs()
            if runs:
                runs_by_row[y] = runs

        return ImageFrame(width, height, runs_by_row)


# ---------------------------------------------------------------------------
# Symbol sources for planar decoding
# ---------------------------------------------------------------------------
def _plan_symbol_source(chomper: BitChomper, bits_per_symbol: int):
    """PLAN: each call reads exactly bits_per_symbol bits."""
    def source():
        return chomper.chomp(bits_per_symbol)
    return source


def _plnr_symbol_source(chomper: BitChomper, bits_per_symbol: int):
    """PLNR: RLE-compressed symbol stream."""
    state = {'last': 0, 'remaining': 0}

    def source():
        if state['remaining'] == 0:
            first = chomper.chomp(bits_per_symbol)
            if first == 0:
                second = chomper.chomp(bits_per_symbol)
                if second == 0:
                    state['last'] = 0
                    state['remaining'] = 1
                else:
                    # keep last value, repeat (second + 2) more times
                    state['remaining'] = second + 2
            else:
                state['last'] = first
                state['remaining'] = 1
        state['remaining'] -= 1
        return state['last']
    return source


def _next_planar_pixel(symbol_source, dictionary: List[int]) -> Optional[int]:
    """Return the palette index for the next pixel, or None (transparent)."""
    symbol = symbol_source()
    value = dictionary[symbol]
    return None if value == 0 else value


# ---------------------------------------------------------------------------
# Helper: pixel runs builder
# ---------------------------------------------------------------------------
class _PixelRunsBuilder:
    def __init__(self):
        self._runs: List[PixelRun] = []
        self._buf = bytearray()
        self._start_x: Optional[int] = None

    def record(self, x: int, pixel: int):
        if (self._start_x is None or
                x != self._start_x + len(self._buf)):
            self.finish_run()
            self._start_x = x
        self._buf.append(pixel)

    def finish_run(self):
        if self._start_x is not None:
            self._runs.append(PixelRun(self._start_x, bytes(self._buf)))
            self._buf = bytearray()
            self._start_x = None

    def get_runs(self) -> List[PixelRun]:
        return list(self._runs)
