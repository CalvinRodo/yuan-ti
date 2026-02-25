"""
Text resource parsing helpers.

Handles decoding of plain text resources (TEXT, MERR, SPIN) and the
structured NAME resource which packs multiple named entries with binary
separator sequences.
"""

import re


def decode_text(raw: bytes) -> str:
    """Decode a null-terminated latin-1 string, normalising line endings."""
    return raw.split(b'\x00')[0].decode('latin-1').replace('\r\n', '\n').replace('\r', '\n')


# NAME entry separator pattern.
#
# Each entry is:  name\x00 + path_fragment\x00 + 1-3 type bytes ending with \xff+
#
# Observed variants:
#   \x00 XX \x00 \xff           – 4 bytes (most common)
#   \x00 XX \x00 \xff \xff+     – extended
#   \x00 \xff \xff              – 3 bytes
#   \x00 \xff \xff \xff         – 4 bytes
_NAME_SEP_RE = re.compile(rb'\x00(?:.\x00\xff+|\xff{2,4})', re.DOTALL)


def parse_name_resource(raw: bytes) -> list:
    """Parse a NAME resource blob into a list of structured entry dicts.

    Each dict has:
      text      – display name (null-terminated C string)
      path      – ASCII path fragment between the two nulls (non-printable as \\xNN)
      sep_hex   – separator bytes as space-separated hex pairs
      raw_bytes – complete entry bytes (segment + separator) as list[int]
      seg_len   – byte length of the content portion
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
            text     = seg[:null_pos].decode('latin-1', errors='replace')
            path_raw = seg[null_pos + 1:]
        else:
            text     = seg.decode('latin-1', errors='replace')
            path_raw = b''

        # Render path bytes: printable ASCII as-is, others as \xNN
        path_str = ''.join(
            chr(b) if 32 <= b < 127 else f'\\x{b:02x}'
            for b in path_raw
        )

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
